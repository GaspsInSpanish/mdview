using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Security.Cryptography;
using MdView.Rendering;

namespace MdView.Serving;

/// <summary>Serves explicitly registered documents through a loopback-only HTTP listener.</summary>
public sealed class ReaderServer : IDisposable
{
    private const int FirstPort = 7717;
    private const int LastPort = 7817;
    private const long MaximumEditBytes = 64L * 1024 * 1024;

    private readonly DocumentRegistry registry;
    private readonly ThemeConfigStore themeConfig;
    private readonly IFileDialogProvider? fileDialogs;
    private readonly ConcurrentDictionary<string, Lazy<DocumentWatcher>> watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, SseClient> clients = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly object lifecycleGate = new();
    private HttpListener? listener;
    private Task? acceptLoop;
    private long nextClientId;
    private bool disposed;
    private readonly string writeToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private readonly ConcurrentDictionary<string, byte[]> suppressedHashes = new(StringComparer.Ordinal);

    public ReaderServer(DocumentRegistry? registry = null, ThemeConfigStore? themeConfig = null,
        IFileDialogProvider? fileDialogs = null)
    {
        this.registry = registry ?? new DocumentRegistry();
        this.themeConfig = themeConfig ?? new ThemeConfigStore();
        this.fileDialogs = fileDialogs;
    }

    /// <summary>The loopback TCP port chosen when the listener starts.</summary>
    public int Port { get; private set; }

    /// <summary>Raised after an SSE connection has completed its initial response write.</summary>
    public event Action? ClientConnected;

    /// <summary>Raised after an established SSE connection has been removed.</summary>
    public event Action? ClientDisconnected;

    /// <summary>Raised after POST /open registers a document.</summary>
    public event Action<RegisteredDocument>? DocumentOpened;

    public event Action? ModalCommandStarted;
    public event Action? ModalCommandCompleted;
    public event Action? ExitRequested;

    /// <summary>Starts the listener, selecting the first available port from 7717 through 7817.</summary>
    public void Start()
    {
        lock (lifecycleGate)
        {
            ThrowIfDisposed();
            if (listener is not null)
            {
                return;
            }

            HttpListenerException? lastBindError = null;
            for (var port = FirstPort; port <= LastPort; port++)
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                    listener = candidate;
                    Port = port;
                    acceptLoop = AcceptLoopAsync(candidate, shutdown.Token);
                    return;
                }
                catch (HttpListenerException exception)
                {
                    lastBindError = exception;
                    candidate.Close();
                }
            }

            throw new InvalidOperationException(
                $"No loopback port was available between {FirstPort} and {LastPort}.",
                lastBindError);
        }
    }

    /// <summary>Registers a document and ensures its directory is watched for updates.</summary>
    public RegisteredDocument RegisterDocument(string path)
    {
        var document = registry.Register(path);
        var watcher = watchers.GetOrAdd(
            document.Id,
            _ => new Lazy<DocumentWatcher>(
                () => new DocumentWatcher(document.Path, () => BroadcastReloadAsync(document.Id)),
                LazyThreadSafetyMode.ExecutionAndPublication));
        _ = watcher.Value;
        return document;
    }

    private async Task AcceptLoopAsync(HttpListener activeListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await activeListener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                _ = ProcessContextAsync(context);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessContextAsync(HttpListenerContext context)
    {
        try
        {
            var segments = GetPathSegments(context.Request);
            if (context.Request.HttpMethod == "GET" && TryGetDocumentRoute(segments, "d", out var document))
            {
                await ServeDocumentAsync(context, document).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && TryGetDocumentRoute(segments, "events", out document))
            {
                await ServeEventsAsync(context, document).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && segments.Length == 1 && segments[0] == "open")
            {
                await OpenDocumentAsync(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && segments.Length == 1 && segments[0] == "toggle")
            {
                await ToggleAsync(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && segments.Length == 1 && segments[0] == "theme")
            {
                await SetThemeAsync(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && TryGetDocumentRoute(segments, "command", out document))
            {
                await ExecuteCommandAsync(context, document).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && TryGetDocumentRoute(segments, "source", out document))
            {
                await BeginEditAsync(context, document).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && TryGetDocumentRoute(segments, "render", out document))
            {
                await RenderEditAsync(context, document).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && TryGetDocumentRoute(segments, "save", out document))
            {
                await SaveEditAsync(context, document).ConfigureAwait(false);
                return;
            }

            await WriteNotFoundAsync(context.Response).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.InternalServerError, "Internal Server Error").ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // The client disconnected before it could receive an error response.
            }
            catch (ObjectDisposedException)
            {
                // The client disconnected before it could receive an error response.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private bool TryGetDocumentRoute(string[] segments, string route, out RegisteredDocument document)
    {
        if (segments.Length == 2 && segments[0] == route && registry.TryGet(segments[1], out document))
        {
            return true;
        }

        document = null!;
        return false;
    }

    private static string[] GetPathSegments(HttpListenerRequest request) =>
        request.Url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];

    private async Task ServeDocumentAsync(HttpListenerContext context, RegisteredDocument document)
    {
        try
        {
            var bytes = await DocumentEditService.ReadBytesAsync(document.Path).ConfigureAwait(false);
            if (bytes is null)
            {
                await WriteNotFoundAsync(context.Response).ConfigureAwait(false);
                return;
            }

            var source = DocumentEditService.Decode(bytes);
            var nonce = Renderer.CreateNonce();
            var contentSecurityPolicy = Renderer.CreateContentSecurityPolicy(nonce);
            var theme = themeConfig.ReadTheme();
            var html = Renderer.RenderDocument(document.Kind, source.Text, document.Title, document.Id, writeToken, nonce, theme, source.Hash);
            var page = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = contentSecurityPolicy;
            context.Response.ContentLength64 = page.Length;
            await context.Response.OutputStream.WriteAsync(page).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task OpenDocumentAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var path = await reader.ReadToEndAsync().ConfigureAwait(false);
            var document = RegisterAndNotify(path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = document.Id });
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Unsupported document path.").ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Unsupported document path.").ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private RegisteredDocument RegisterAndNotify(string path)
    {
        var document = RegisterDocument(path);
        Notify(DocumentOpened, document);
        return document;
    }

    private async Task ToggleAsync(HttpListenerContext context)
    {
        try
        {
            var origin = context.Request.Headers["Origin"];
            if (!string.Equals(origin, $"http://127.0.0.1:{Port}", StringComparison.Ordinal))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
                return;
            }

            var request = await JsonSerializer.DeserializeAsync<ToggleRequest>(context.Request.InputStream).ConfigureAwait(false);
            if (request is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Token ?? string.Empty), Encoding.UTF8.GetBytes(writeToken)) ||
                !registry.TryGet(request.Id ?? string.Empty, out var document))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
                return;
            }

            if (!TaskToggleService.TryToggle(document.Path, request.Line, request.Checked, out var hash))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Conflict, "Conflict").ConfigureAwait(false);
                return;
            }

            suppressedHashes[document.Id] = hash;
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }
        catch (JsonException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
        }
        finally { context.Response.Close(); }
    }

    private async Task SetThemeAsync(HttpListenerContext context)
    {
        try
        {
            var origin = context.Request.Headers["Origin"];
            if (!string.Equals(origin, $"http://127.0.0.1:{Port}", StringComparison.Ordinal))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
                return;
            }

            var request = await JsonSerializer.DeserializeAsync<ThemeRequest>(context.Request.InputStream).ConfigureAwait(false);
            if (request is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.Token ?? string.Empty), Encoding.UTF8.GetBytes(writeToken)))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
                return;
            }
            if (!ThemeConfigStore.TryParse(request.Theme, out var theme))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
                return;
            }

            themeConfig.WriteTheme(theme);
            BroadcastTheme(ThemeConfigStore.ToWireValue(theme));
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }
        catch (JsonException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
        }
        finally { context.Response.Close(); }
    }

    private async Task ExecuteCommandAsync(HttpListenerContext context, RegisteredDocument currentDocument)
    {
        var exitRequested = false;
        try
        {
            var origin = context.Request.Headers["Origin"];
            if (!string.Equals(origin, $"http://127.0.0.1:{Port}", StringComparison.Ordinal))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
                return;
            }

            var request = await JsonSerializer.DeserializeAsync<CommandRequest>(context.Request.InputStream).ConfigureAwait(false);
            if (request is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.Token ?? string.Empty), Encoding.UTF8.GetBytes(writeToken)))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
                return;
            }
            if (request.Command is not ("file.open" or "file.save-as" or "file.new" or "file.exit"))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
                return;
            }
            if (request.Command == "file.exit")
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                exitRequested = true;
                return;
            }
            if (fileDialogs is null)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.ServiceUnavailable, "File dialogs unavailable.").ConfigureAwait(false);
                return;
            }

            Notify(ModalCommandStarted);
            try
            {
                var directory = Path.GetDirectoryName(currentDocument.Path)!;
                if (request.Command == "file.open")
                {
                    var selected = await fileDialogs.ShowOpenAsync(directory, shutdown.Token).ConfigureAwait(false);
                    if (selected is not null) _ = RegisterAndNotify(NormalizeDialogPath(selected, appendMarkdown: false));
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    return;
                }

                var suggestedName = request.Command == "file.new" ? "Untitled.md" : Path.GetFileName(currentDocument.Path);
                var destination = await fileDialogs.ShowSaveAsAsync(directory, suggestedName, shutdown.Token).ConfigureAwait(false);
                if (destination is null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    return;
                }
                destination = NormalizeDialogPath(destination, appendMarkdown: true);
                if (request.Command == "file.new")
                {
                    await File.WriteAllBytesAsync(destination, [], shutdown.Token).ConfigureAwait(false);
                    _ = RegisterAndNotify(destination);
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    return;
                }

                if (!string.Equals(currentDocument.Path, destination, StringComparison.OrdinalIgnoreCase))
                    File.Copy(currentDocument.Path, destination, overwrite: true);
                var savedDocument = RegisterDocument(destination);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = savedDocument.Id });
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            finally { Notify(ModalCommandCompleted); }
        }
        catch (JsonException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or NotSupportedException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Unsupported file selection.").ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
            if (exitRequested) Notify(ExitRequested);
        }
    }

    /// <summary>
    /// Entering edit mode. Returns the text the page will splice edits into, the hash a save
    /// must match, and a fresh render whose block ranges index into exactly that text — the
    /// page may be stale (a checkbox toggle rewrites the file without reloading it).
    /// </summary>
    private async Task BeginEditAsync(HttpListenerContext context, RegisteredDocument document)
    {
        try
        {
            if (await ReadTrustedEditRequestAsync(context).ConfigureAwait(false) is null) return;
            var bytes = await DocumentEditService.ReadBytesAsync(document.Path).ConfigureAwait(false);
            if (bytes is null)
            {
                await WriteNotFoundAsync(context.Response).ConfigureAwait(false);
                return;
            }
            var source = DocumentEditService.Decode(bytes);
            if (!source.Editable)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.UnsupportedMediaType,
                    "This file is not UTF-8 text, so it cannot be edited without changing its encoding.").ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(context.Response, new
            {
                text = source.Text,
                hash = source.Hash,
                newline = source.Newline,
                html = Renderer.RenderBody(document.Kind, source.Text, document.Id)
            }).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
        }
        finally { context.Response.Close(); }
    }

    /// <summary>Renders unsaved text for the page. Writes nothing.</summary>
    private async Task RenderEditAsync(HttpListenerContext context, RegisteredDocument document)
    {
        try
        {
            var request = await ReadTrustedEditRequestAsync(context).ConfigureAwait(false);
            if (request is null) return;
            if (request.Text is null)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(context.Response, new { html = Renderer.RenderBody(document.Kind, request.Text, document.Id) })
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
        }
        finally { context.Response.Close(); }
    }

    private async Task SaveEditAsync(HttpListenerContext context, RegisteredDocument document)
    {
        try
        {
            var request = await ReadTrustedEditRequestAsync(context).ConfigureAwait(false);
            if (request is null) return;
            if (request.Text is null || (request.Hash is null && !request.Force))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
                return;
            }
            var outcome = DocumentEditService.TrySave(document.Path, request.Text, request.Hash, request.Force, out var hash);
            if (outcome == SaveOutcome.Conflict)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Conflict, "The file changed on disk.").ConfigureAwait(false);
                return;
            }
            if (outcome == SaveOutcome.NotEditable)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.UnsupportedMediaType, "Not UTF-8 text.").ConfigureAwait(false);
                return;
            }
            suppressedHashes[document.Id] = Convert.FromHexString(hash);
            await WriteJsonAsync(context.Response, new { hash }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest, "Bad Request").ConfigureAwait(false);
        }
        finally { context.Response.Close(); }
    }

    /// <summary>
    /// Same gate as every other write path: our own origin and the per-process token. On
    /// failure the 4xx is already written and this returns null.
    /// </summary>
    private async Task<EditRequest?> ReadTrustedEditRequestAsync(HttpListenerContext context)
    {
        if (!string.Equals(context.Request.Headers["Origin"], $"http://127.0.0.1:{Port}", StringComparison.Ordinal))
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
            return null;
        }
        if (context.Request.ContentLength64 > MaximumEditBytes)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.RequestEntityTooLarge, "Too Large").ConfigureAwait(false);
            return null;
        }
        var request = await JsonSerializer.DeserializeAsync<EditRequest>(context.Request.InputStream).ConfigureAwait(false);
        if (request is null || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Token ?? string.Empty), Encoding.UTF8.GetBytes(writeToken)))
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
            return null;
        }
        return request;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string NormalizeDialogPath(string path, bool appendMarkdown)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Dialog returned a relative path.", nameof(path));
        var normalized = Path.GetFullPath(path);
        return appendMarkdown && !Path.HasExtension(normalized) ? normalized + ".md" : normalized;
    }

    private async Task ServeEventsAsync(HttpListenerContext context, RegisteredDocument document)
    {
        var client = new SseClient(Interlocked.Increment(ref nextClientId), document.Id, context.Response);
        clients.TryAdd(client.Id, client);
        var connected = false;

        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            context.Response.KeepAlive = true;
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await client.WriteAsync(": connected\n\n", shutdown.Token).ConfigureAwait(false);
            Notify(ClientConnected);
            connected = true;

            while (!shutdown.IsCancellationRequested)
            {
                var available = client.Messages.Reader.WaitToReadAsync(shutdown.Token).AsTask();
                var keepAlive = Task.Delay(TimeSpan.FromSeconds(5), shutdown.Token);
                if (await Task.WhenAny(available, keepAlive).ConfigureAwait(false) == keepAlive)
                {
                    await client.WriteAsync(": keep-alive\n\n", shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                if (!await available.ConfigureAwait(false))
                {
                    break;
                }

                while (client.Messages.Reader.TryRead(out var message))
                {
                    await client.WriteAsync($"event: {message.Event}\ndata: {message.Data}\n\n", shutdown.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (HttpListenerException)
        {
            // The browser/client disconnected.
        }
        catch (IOException)
        {
            // The browser/client disconnected.
        }
        finally
        {
            clients.TryRemove(client.Id, out _);
            client.Dispose();
            if (connected)
            {
                Notify(ClientDisconnected);
            }
        }
    }

    private static void Notify(Action? notification)
    {
        if (notification is null)
        {
            return;
        }

        foreach (Action handler in notification.GetInvocationList())
        {
            try { handler(); } catch { /* Observers must not break the server. */ }
        }
    }

    private static void Notify<T>(Action<T>? notification, T value)
    {
        if (notification is null)
        {
            return;
        }

        foreach (Action<T> handler in notification.GetInvocationList())
        {
            try { handler(value); } catch { /* Observers must not break the request. */ }
        }
    }

    private Task BroadcastReloadAsync(string documentId)
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
        foreach (var client in clients.Values)
        {
            if (client.DocumentId == documentId)
            {
                client.Messages.Writer.TryWrite(new SseMessage("reload", "reload"));
            }
        }

        return Task.CompletedTask;
    }

    private void BroadcastTheme(string theme)
    {
        foreach (var client in clients.Values)
            client.Messages.Writer.TryWrite(new SseMessage("theme", theme));
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

    private sealed record SseMessage(string Event, string Data);

    private static Task WriteNotFoundAsync(HttpListenerResponse response) =>
        WriteStatusAsync(response, HttpStatusCode.NotFound, "Not Found");

    private static async Task WriteStatusAsync(HttpListenerResponse response, HttpStatusCode status, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        response.StatusCode = (int)status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(ReaderServer));
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            shutdown.Cancel();
            listener?.Close();
            listener = null;
        }

        foreach (var watcher in watchers.Values)
        {
            if (watcher.IsValueCreated)
            {
                watcher.Value.Dispose();
            }
        }

        foreach (var client in clients.Values)
        {
            client.Dispose();
        }

        shutdown.Dispose();
    }

    private sealed class SseClient(long id, string documentId, HttpListenerResponse response) : IDisposable
    {
        private readonly HttpListenerResponse response = response;
        private bool disposed;

        internal long Id { get; } = id;
        internal string DocumentId { get; } = documentId;
        internal Channel<SseMessage> Messages { get; } = Channel.CreateUnbounded<SseMessage>();

        internal async Task WriteAsync(string message, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Messages.Writer.TryComplete();
            response.Close();
        }
    }
}
