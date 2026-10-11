using System.Net;
using System.Text;

namespace MdView.Serving;

public sealed class ReaderServer : IDisposable
{
    private const int FirstPort = 7717;
    private const int LastPort = 7817;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object lifecycleGate = new();
    private HttpListener? listener;
    private bool disposed;

    public ReaderServer(DocumentRegistry? registry = null, ThemeConfigStore? themeConfig = null,
        IFileDialogProvider? fileDialogs = null) =>
        Host = new DocumentHost(() => DocumentHost.Origin, registry, themeConfig, fileDialogs);

    public DocumentHost Host { get; }
    public int Port { get; private set; }

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listener is not null) return;
            HttpListenerException? lastBindError = null;
            for (var port = FirstPort; port <= LastPort; port++)
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start(); listener = candidate; Port = port;
                    _ = AcceptLoopAsync(candidate, shutdown.Token); return;
                }
                catch (HttpListenerException exception)
                { lastBindError = exception; candidate.Close(); }
            }
            throw new InvalidOperationException(
                $"No loopback port was available between {FirstPort} and {LastPort}.", lastBindError);
        }
    }

    public RegisteredDocument RegisterDocument(string path) => Host.RegisterDocument(path);

    private async Task AcceptLoopAsync(HttpListener activeListener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var context = await activeListener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
                _ = ProcessContextAsync(context);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    private async Task ProcessContextAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (context.Request.HttpMethod != "POST" || path != "/open")
            { await WriteAsync(context.Response, PlainText(HttpStatusCode.NotFound, "Not Found")); return; }

            if (!string.IsNullOrEmpty(context.Request.Headers["Origin"]))
            { await WriteAsync(context.Response, PlainText(HttpStatusCode.Forbidden, "Forbidden")); return; }

            const long maximum = DocumentHost.MaximumStandardBodyBytes;
            if (context.Request.ContentLength64 > maximum)
            { await WriteAsync(context.Response, PlainText(HttpStatusCode.RequestEntityTooLarge, "Too Large")); return; }
            var body = await ReadBodyAsync(context.Request.InputStream, maximum, shutdown.Token).ConfigureAwait(false);
            if (body.LongLength > maximum)
            { await WriteAsync(context.Response, PlainText(HttpStatusCode.RequestEntityTooLarge, "Too Large")); return; }
            var response = await Host.HandleAsync(new HostRequest("POST", path, null, body), shutdown.Token)
                .ConfigureAwait(false);
            await WriteAsync(context.Response, response).ConfigureAwait(false);
            Host.CompleteResponse(response);
        }
        catch (Exception)
        {
            try { await WriteAsync(context.Response, PlainText(HttpStatusCode.InternalServerError, "Internal Server Error")); }
            catch (Exception) { }
        }
        finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream input, long maximum, CancellationToken token)
    {
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        while (body.Length <= maximum)
        {
            var remaining = maximum + 1 - body.Length;
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token)
                .ConfigureAwait(false);
            if (count == 0) break;
            body.Write(buffer, 0, count);
        }
        return body.ToArray();
    }

    private static HostResponse PlainText(HttpStatusCode status, string message) => new(
        (int)status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message), new Dictionary<string, string>());

    private static async Task WriteAsync(HttpListenerResponse target, HostResponse source)
    {
        target.StatusCode = source.StatusCode; target.ContentType = source.ContentType;
        foreach (var header in source.Headers) target.Headers[header.Key] = header.Value;
        target.ContentLength64 = source.Body.Length;
        if (source.Body.Length != 0) await target.OutputStream.WriteAsync(source.Body).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true; shutdown.Cancel(); listener?.Close(); listener = null;
        }
        Host.Dispose(); shutdown.Dispose();
    }
}
