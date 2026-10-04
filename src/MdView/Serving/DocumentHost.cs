using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MdView.Rendering;

namespace MdView.Serving;

public sealed record HostRequest(string Method, string Path, string? Origin, byte[] Body);

public sealed record HostResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers)
{
    internal bool ExitAfterResponse { get; init; }
}

/// <summary>Handles registered documents independently of any network transport.</summary>
public sealed class DocumentHost : IDisposable
{
    public const long MaximumStandardBodyBytes = 64L * 1024;
    public const long MaximumEditBodyBytes = 64L * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>();

    private readonly DocumentRegistry registry;
    private readonly ThemeConfigStore themeConfig;
    private readonly IFileDialogProvider? fileDialogs;
    private readonly Func<string> expectedOrigin;
    private readonly ConcurrentDictionary<string, Lazy<DocumentWatcher>> watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> suppressedHashes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly string writeToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private bool disposed;

    public DocumentHost(Func<string> expectedOrigin, DocumentRegistry? registry = null,
        ThemeConfigStore? themeConfig = null, IFileDialogProvider? fileDialogs = null)
    {
        this.expectedOrigin = expectedOrigin ?? throw new ArgumentNullException(nameof(expectedOrigin));
        this.registry = registry ?? new DocumentRegistry();
        this.themeConfig = themeConfig ?? new ThemeConfigStore();
        this.fileDialogs = fileDialogs;
    }

    public event Action<RegisteredDocument>? DocumentOpened;
    public event Action? ModalCommandStarted;
    public event Action? ModalCommandCompleted;
    public event Action? ExitRequested;
    public event Action<string>? DocumentChanged;
    public event Action<string>? ThemeChanged;

    public RegisteredDocument RegisterDocument(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var document = registry.Register(path);
        var watcher = watchers.GetOrAdd(document.Id, _ => new Lazy<DocumentWatcher>(
            () => new DocumentWatcher(document.Path, () => OnDocumentChangedAsync(document.Id)),
            LazyThreadSafetyMode.ExecutionAndPublication));
        _ = watcher.Value;
        return document;
    }

    public bool TryGetDocument(string id, out RegisteredDocument document) =>
        registry.TryGet(id, out document!);

    /// <summary>
    /// Returns the request body limit for a supported POST path. Transports must reject
    /// larger bodies before constructing a <see cref="HostRequest"/>.
    /// </summary>
    public static long MaximumBodyBytes(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 && segments[0] is "source" or "render" or "save"
            ? MaximumEditBodyBytes
            : MaximumStandardBodyBytes;
    }

    /// <summary>
    /// Handles one transport-neutral request. Every transport must call
    /// <see cref="CompleteResponse"/> after the returned response has been delivered;
    /// otherwise a successful File → Exit command will not raise <see cref="ExitRequested"/>.
    /// </summary>
    public async Task<HostResponse> HandleAsync(HostRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (request.Method == "POST" && IsHandledPostRoute(segments) &&
                request.Body.LongLength > MaximumBodyBytes(request.Path))
            {
                return Status(HttpStatusCode.RequestEntityTooLarge, "Too Large");
            }

            request = StripUtf8Bom(request);
            if (request.Method == "GET" && TryGetDocumentRoute(segments, "d", out var document))
                return await ServeDocumentAsync(document).ConfigureAwait(false);
            if (request.Method == "POST" && segments.Length == 1 && segments[0] == "open")
                return await OpenDocumentAsync(request).ConfigureAwait(false);
            if (request.Method == "POST" && segments.Length == 1 && segments[0] == "toggle")
                return await ToggleAsync(request).ConfigureAwait(false);
            if (request.Method == "POST" && segments.Length == 1 && segments[0] == "theme")
                return await SetThemeAsync(request).ConfigureAwait(false);
            if (request.Method == "POST" && TryGetDocumentRoute(segments, "command", out document))
                return await ExecuteCommandAsync(request, document, cancellationToken).ConfigureAwait(false);
            if (request.Method == "POST" && TryGetDocumentRoute(segments, "source", out document))
                return await BeginEditAsync(request, document).ConfigureAwait(false);
            if (request.Method == "POST" && TryGetDocumentRoute(segments, "render", out document))
                return await RenderEditAsync(request, document).ConfigureAwait(false);
            if (request.Method == "POST" && TryGetDocumentRoute(segments, "save", out document))
                return await SaveEditAsync(request, document).ConfigureAwait(false);
            return Status(HttpStatusCode.NotFound, "Not Found");
        }
        catch (Exception)
        {
            return Status(HttpStatusCode.InternalServerError, "Internal Server Error");
        }
    }

    private bool IsHandledPostRoute(string[] segments)
    {
        if (segments.Length == 1)
        {
            return segments[0] is "open" or "toggle" or "theme";
        }

        return segments.Length == 2 && segments[0] is "command" or "source" or "render" or "save" &&
            registry.TryGet(segments[1], out _);
    }

    /// <summary>
    /// Completes the host response lifecycle after a transport has delivered the response.
    /// Every response returned by <see cref="HandleAsync"/> must be passed here; File → Exit
    /// is deliberately raised only at this point.
    /// </summary>
    public void CompleteResponse(HostResponse response)
    {
        if (response.ExitAfterResponse) Notify(ExitRequested);
    }

    private bool TryGetDocumentRoute(string[] segments, string route, out RegisteredDocument document)
    {
        if (segments.Length == 2 && segments[0] == route && registry.TryGet(segments[1], out document!))
            return true;
        document = null!;
        return false;
    }

    private async Task<HostResponse> ServeDocumentAsync(RegisteredDocument document)
    {
        var bytes = await DocumentEditService.ReadBytesAsync(document.Path).ConfigureAwait(false);
        if (bytes is null) return Status(HttpStatusCode.NotFound, "Not Found");
        var source = DocumentEditService.Decode(bytes);
        var nonce = Renderer.CreateNonce();
        var csp = Renderer.CreateContentSecurityPolicy(nonce);
        var html = Renderer.RenderDocument(document.Kind, source.Text, document.Title, document.Id,
            writeToken, nonce, themeConfig.ReadTheme(), source.Hash);
        return new((int)HttpStatusCode.OK, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html),
            new Dictionary<string, string> { ["Content-Security-Policy"] = csp });
    }

    private Task<HostResponse> OpenDocumentAsync(HostRequest request)
    {
        try
        {
            var document = RegisterAndNotify(Encoding.UTF8.GetString(request.Body));
            return Task.FromResult(Json(new { id = document.Id }));
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException)
        {
            return Task.FromResult(Status(HttpStatusCode.BadRequest, "Unsupported document path."));
        }
    }

    private RegisteredDocument RegisterAndNotify(string path)
    {
        var document = RegisterDocument(path);
        Notify(DocumentOpened, document);
        return document;
    }

    private Task<HostResponse> ToggleAsync(HostRequest hostRequest)
    {
        try
        {
            if (!TrustedOrigin(hostRequest)) return Task.FromResult(Status(HttpStatusCode.Forbidden, "Forbidden"));
            var request = JsonSerializer.Deserialize<ToggleRequest>(hostRequest.Body);
            if (request is null || !TokenMatches(request.Token) ||
                !registry.TryGet(request.Id ?? string.Empty, out var document))
                return Task.FromResult(Status(HttpStatusCode.Forbidden, "Forbidden"));
            if (!TaskToggleService.TryToggle(document.Path, request.Line, request.Checked, out var hash))
                return Task.FromResult(Status(HttpStatusCode.Conflict, "Conflict"));
            suppressedHashes[document.Id] = hash;
            return Task.FromResult(Empty(HttpStatusCode.NoContent));
        }
        catch (JsonException) { return Task.FromResult(Status(HttpStatusCode.BadRequest, "Bad Request")); }
    }

    private Task<HostResponse> SetThemeAsync(HostRequest hostRequest)
    {
        try
        {
            if (!TrustedOrigin(hostRequest)) return Task.FromResult(Status(HttpStatusCode.Forbidden, "Forbidden"));
            var request = JsonSerializer.Deserialize<ThemeRequest>(hostRequest.Body);
            if (request is null || !TokenMatches(request.Token))
                return Task.FromResult(Status(HttpStatusCode.Forbidden, "Forbidden"));
            if (!ThemeConfigStore.TryParse(request.Theme, out var theme))
                return Task.FromResult(Status(HttpStatusCode.BadRequest, "Bad Request"));
            themeConfig.WriteTheme(theme);
            Notify(ThemeChanged, ThemeConfigStore.ToWireValue(theme));
            return Task.FromResult(Empty(HttpStatusCode.NoContent));
        }
        catch (JsonException) { return Task.FromResult(Status(HttpStatusCode.BadRequest, "Bad Request")); }
    }

    private async Task<HostResponse> ExecuteCommandAsync(HostRequest hostRequest,
        RegisteredDocument currentDocument, CancellationToken cancellationToken)
    {
        try
        {
            if (!TrustedOrigin(hostRequest)) return Status(HttpStatusCode.Forbidden, "Forbidden");
            var request = JsonSerializer.Deserialize<CommandRequest>(hostRequest.Body);
            if (request is null || !TokenMatches(request.Token)) return Status(HttpStatusCode.Forbidden, "Forbidden");
            if (request.Command is not ("file.open" or "file.save-as" or "file.new" or "file.exit"))
                return Status(HttpStatusCode.BadRequest, "Bad Request");
            if (request.Command == "file.exit")
                return Empty(HttpStatusCode.NoContent) with { ExitAfterResponse = true };
            if (fileDialogs is null) return Status(HttpStatusCode.ServiceUnavailable, "File dialogs unavailable.");

            Notify(ModalCommandStarted);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
                var directory = Path.GetDirectoryName(currentDocument.Path)!;
                if (request.Command == "file.open")
                {
                    var selected = await fileDialogs.ShowOpenAsync(directory, linked.Token).ConfigureAwait(false);
                    if (selected is not null) _ = RegisterAndNotify(NormalizeDialogPath(selected, false));
                    return Empty(HttpStatusCode.NoContent);
                }
                var suggestedName = request.Command == "file.new" ? "Untitled.md" : Path.GetFileName(currentDocument.Path);
                var destination = await fileDialogs.ShowSaveAsAsync(directory, suggestedName, linked.Token).ConfigureAwait(false);
                if (destination is null) return Empty(HttpStatusCode.NoContent);
                destination = NormalizeDialogPath(destination, true);
                if (request.Command == "file.new")
                {
                    await File.WriteAllBytesAsync(destination, [], linked.Token).ConfigureAwait(false);
                    _ = RegisterAndNotify(destination);
                    return Empty(HttpStatusCode.NoContent);
                }
                if (!string.Equals(currentDocument.Path, destination, StringComparison.OrdinalIgnoreCase))
                    File.Copy(currentDocument.Path, destination, overwrite: true);
                var saved = RegisterDocument(destination);
                return Json(new { id = saved.Id });
            }
            finally { Notify(ModalCommandCompleted); }
        }
        catch (JsonException) { return Status(HttpStatusCode.BadRequest, "Bad Request"); }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or NotSupportedException)
        { return Status(HttpStatusCode.BadRequest, "Unsupported file selection."); }
    }

    private async Task<HostResponse> BeginEditAsync(HostRequest hostRequest, RegisteredDocument document)
    {
        try
        {
            var trusted = ReadTrustedEditRequest(hostRequest);
            if (trusted.Error is not null) return trusted.Error;
            var bytes = await DocumentEditService.ReadBytesAsync(document.Path).ConfigureAwait(false);
            if (bytes is null) return Status(HttpStatusCode.NotFound, "Not Found");
            var source = DocumentEditService.Decode(bytes);
            if (!source.Editable) return Status(HttpStatusCode.UnsupportedMediaType,
                "This file is not UTF-8 text, so it cannot be edited without changing its encoding.");
            return Json(new { text = source.Text, hash = source.Hash, newline = source.Newline,
                html = Renderer.RenderBody(document.Kind, source.Text, document.Id) });
        }
        catch (JsonException) { return Status(HttpStatusCode.BadRequest, "Bad Request"); }
    }

    private Task<HostResponse> RenderEditAsync(HostRequest hostRequest, RegisteredDocument document)
    {
        try
        {
            var trusted = ReadTrustedEditRequest(hostRequest);
            if (trusted.Error is not null) return Task.FromResult(trusted.Error);
            if (trusted.Request!.Text is null) return Task.FromResult(Status(HttpStatusCode.BadRequest, "Bad Request"));
            return Task.FromResult(Json(new { html = Renderer.RenderBody(document.Kind, trusted.Request.Text, document.Id) }));
        }
        catch (JsonException) { return Task.FromResult(Status(HttpStatusCode.BadRequest, "Bad Request")); }
    }

    private Task<HostResponse> SaveEditAsync(HostRequest hostRequest, RegisteredDocument document)
    {
        try
        {
            var trusted = ReadTrustedEditRequest(hostRequest);
            if (trusted.Error is not null) return Task.FromResult(trusted.Error);
            var request = trusted.Request!;
            if (request.Text is null || (request.Hash is null && !request.Force))
                return Task.FromResult(Status(HttpStatusCode.BadRequest, "Bad Request"));
            var outcome = DocumentEditService.TrySave(document.Path, request.Text, request.Hash, request.Force, out var hash);
            if (outcome == SaveOutcome.Conflict)
                return Task.FromResult(Status(HttpStatusCode.Conflict, "The file changed on disk."));
            if (outcome == SaveOutcome.NotEditable)
                return Task.FromResult(Status(HttpStatusCode.UnsupportedMediaType, "Not UTF-8 text."));
            suppressedHashes[document.Id] = Convert.FromHexString(hash);
            return Task.FromResult(Json(new { hash }));
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException)
        { return Task.FromResult(Status(HttpStatusCode.BadRequest, "Bad Request")); }
    }

    private (EditRequest? Request, HostResponse? Error) ReadTrustedEditRequest(HostRequest hostRequest)
    {
        if (!TrustedOrigin(hostRequest)) return (null, Status(HttpStatusCode.Forbidden, "Forbidden"));
        if (hostRequest.Body.LongLength > MaximumEditBodyBytes)
            return (null, Status(HttpStatusCode.RequestEntityTooLarge, "Too Large"));
        var request = JsonSerializer.Deserialize<EditRequest>(hostRequest.Body);
        if (request is null || !TokenMatches(request.Token))
            return (null, Status(HttpStatusCode.Forbidden, "Forbidden"));
        return (request, null);
    }

    private bool TrustedOrigin(HostRequest request) =>
        string.Equals(request.Origin, expectedOrigin(), StringComparison.Ordinal);

    private static HostRequest StripUtf8Bom(HostRequest request)
    {
        if (request.Body.Length < 3 || request.Body[0] != 0xef || request.Body[1] != 0xbb || request.Body[2] != 0xbf)
        {
            return request;
        }

        return request with { Body = request.Body[3..] };
    }

    private bool TokenMatches(string? token) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(token ?? string.Empty), Encoding.UTF8.GetBytes(writeToken));

    private Task OnDocumentChangedAsync(string documentId)
    {
        if (suppressedHashes.TryGetValue(documentId, out var expected))
        {
            if (registry.TryGet(documentId, out var document) && File.Exists(document.Path) &&
                CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(File.ReadAllBytes(document.Path))))
            {
                suppressedHashes.TryRemove(documentId, out _);
                return Task.CompletedTask;
            }
            suppressedHashes.TryRemove(documentId, out _);
        }
        Notify(DocumentChanged, documentId);
        return Task.CompletedTask;
    }

    private static HostResponse Json(object value) => new((int)HttpStatusCode.OK,
        "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value), NoHeaders);
    private static HostResponse Empty(HttpStatusCode status) => new((int)status, string.Empty, [], NoHeaders);
    private static HostResponse Status(HttpStatusCode status, string message) => new((int)status,
        "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message), NoHeaders);

    private static string NormalizeDialogPath(string path, bool appendMarkdown)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Dialog returned a relative path.", nameof(path));
        var normalized = Path.GetFullPath(path);
        return appendMarkdown && !Path.HasExtension(normalized) ? normalized + ".md" : normalized;
    }

    private static void Notify(Action? notification)
    {
        if (notification is null) return;
        foreach (Action handler in notification.GetInvocationList())
            try { handler(); } catch { }
    }

    private static void Notify<T>(Action<T>? notification, T value)
    {
        if (notification is null) return;
        foreach (Action<T> handler in notification.GetInvocationList())
            try { handler(value); } catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shutdown.Cancel();
        foreach (var watcher in watchers.Values)
            if (watcher.IsValueCreated) watcher.Value.Dispose();
        shutdown.Dispose();
    }

    private sealed record ToggleRequest(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("checked")] bool Checked,
        [property: JsonPropertyName("token")] string? Token);
    private sealed record ThemeRequest(
        [property: JsonPropertyName("theme")] string? Theme,
        [property: JsonPropertyName("token")] string? Token);
    private sealed record CommandRequest(
        [property: JsonPropertyName("command")] string? Command,
        [property: JsonPropertyName("token")] string? Token);
    private sealed record EditRequest(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("force")] bool Force);
}
