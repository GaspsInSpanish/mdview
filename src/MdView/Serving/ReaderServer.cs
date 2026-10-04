using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;

namespace MdView.Serving;

public sealed class ReaderServer : IDisposable
{
    private const int FirstPort = 7717;
    private const int LastPort = 7817;
    private readonly ConcurrentDictionary<long, SseClient> clients = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly object lifecycleGate = new();
    private HttpListener? listener;
    private Task? acceptLoop;
    private long nextClientId;
    private bool disposed;

    public ReaderServer(DocumentRegistry? registry = null, ThemeConfigStore? themeConfig = null,
        IFileDialogProvider? fileDialogs = null)
    {
        Host = new DocumentHost(() => $"http://127.0.0.1:{Port}", registry, themeConfig, fileDialogs);
        Host.DocumentOpened += value => Notify(DocumentOpened, value);
        Host.ModalCommandStarted += () => Notify(ModalCommandStarted);
        Host.ModalCommandCompleted += () => Notify(ModalCommandCompleted);
        Host.ExitRequested += () => Notify(ExitRequested);
        Host.DocumentChanged += BroadcastReload;
        Host.ThemeChanged += BroadcastTheme;
    }

    public DocumentHost Host { get; }
    public int Port { get; private set; }
    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<RegisteredDocument>? DocumentOpened;
    public event Action? ModalCommandStarted;
    public event Action? ModalCommandCompleted;
    public event Action? ExitRequested;

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
        var responseStarted = false;
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (context.Request.HttpMethod == "GET" && segments.Length == 2 && segments[0] == "events" &&
                Host.TryGetDocument(segments[1], out var document))
            {
                await ServeEventsAsync(context, document).ConfigureAwait(false);
                return;
            }

            if (!IsHostRoute(context.Request.HttpMethod, segments))
            {
                responseStarted = true;
                await WriteAndCloseAsync(context.Response, PlainText(HttpStatusCode.NotFound, "Not Found"))
                    .ConfigureAwait(false);
                return;
            }

            var origin = context.Request.Headers["Origin"];
            if (RequiresOrigin(segments) &&
                !string.Equals(origin, $"http://127.0.0.1:{Port}", StringComparison.Ordinal))
            {
                responseStarted = true;
                await WriteAndCloseAsync(context.Response, PlainText(HttpStatusCode.Forbidden, "Forbidden"))
                    .ConfigureAwait(false);
                return;
            }

            byte[] body = [];
            if (context.Request.HttpMethod == "POST")
            {
                var maximumBytes = DocumentHost.MaximumBodyBytes(path);
                if (context.Request.ContentLength64 > maximumBytes)
                {
                    responseStarted = true;
                    await WriteAndCloseAsync(context.Response,
                        PlainText(HttpStatusCode.RequestEntityTooLarge, "Too Large")).ConfigureAwait(false);
                    return;
                }

                body = await ReadBodyAsync(context.Request.InputStream, maximumBytes, shutdown.Token)
                    .ConfigureAwait(false);
                if (body.LongLength > maximumBytes)
                {
                    responseStarted = true;
                    await WriteAndCloseAsync(context.Response,
                        PlainText(HttpStatusCode.RequestEntityTooLarge, "Too Large")).ConfigureAwait(false);
                    return;
                }
            }

            var request = new HostRequest(context.Request.HttpMethod, path, origin, body);
            var response = await Host.HandleAsync(request, shutdown.Token).ConfigureAwait(false);
            responseStarted = true;
            await WriteResponseAsync(context.Response, response).ConfigureAwait(false);
            try
            {
                context.Response.Close();
            }
            catch (ObjectDisposedException) { }
            Host.CompleteResponse(response);
        }
        catch (Exception)
        {
            if (!responseStarted)
            {
                try
                {
                    await WriteResponseAsync(context.Response,
                        PlainText(HttpStatusCode.InternalServerError, "Internal Server Error")).ConfigureAwait(false);
                }
                catch (HttpListenerException) { }
                catch (InvalidOperationException) { }
            }

            try
            {
                context.Response.Close();
            }
            catch (ObjectDisposedException) { }
        }
    }

    private bool IsHostRoute(string method, string[] segments)
    {
        if (method == "GET")
        {
            return segments.Length == 2 && segments[0] == "d" && Host.TryGetDocument(segments[1], out _);
        }

        if (method != "POST")
        {
            return false;
        }

        if (segments.Length == 1)
        {
            return segments[0] is "open" or "toggle" or "theme";
        }

        return segments.Length == 2 && segments[0] is "command" or "source" or "render" or "save" &&
            Host.TryGetDocument(segments[1], out _);
    }

    private static bool RequiresOrigin(string[] segments) =>
        segments[0] is "toggle" or "theme" or "command" or "source" or "render" or "save";

    private static async Task<byte[]> ReadBodyAsync(Stream input, long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        while (body.Length <= maximumBytes)
        {
            var remaining = maximumBytes + 1 - body.Length;
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            body.Write(buffer, 0, count);
        }

        return body.ToArray();
    }

    private static HostResponse PlainText(HttpStatusCode status, string message) => new(
        (int)status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message),
        new Dictionary<string, string>());

    private static async Task WriteAndCloseAsync(HttpListenerResponse target, HostResponse source)
    {
        await WriteResponseAsync(target, source).ConfigureAwait(false);
        target.Close();
    }

    private static async Task WriteResponseAsync(HttpListenerResponse target, HostResponse source)
    {
        target.StatusCode = source.StatusCode;
        if (source.ContentType.Length != 0)
        {
            target.ContentType = source.ContentType;
        }
        foreach (var header in source.Headers)
        {
            target.Headers[header.Key] = header.Value;
        }
        if (source.Body.Length != 0)
        {
            target.ContentLength64 = source.Body.Length;
            await target.OutputStream.WriteAsync(source.Body).ConfigureAwait(false);
        }
    }

    private async Task ServeEventsAsync(HttpListenerContext context, RegisteredDocument document)
    {
        var client = new SseClient(Interlocked.Increment(ref nextClientId), document.Id, context.Response);
        clients.TryAdd(client.Id, client);
        var connected = false;
        try
        {
            context.Response.StatusCode = 200;
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
                    await client.WriteAsync($"event: {message.Event}\ndata: {message.Data}\n\n", shutdown.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
        catch (IOException) { }
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

    private void BroadcastReload(string id)
    {
        foreach (var client in clients.Values)
        {
            if (client.DocumentId == id)
            {
                client.Messages.Writer.TryWrite(new("reload", "reload"));
            }
        }
    }

    private void BroadcastTheme(string theme)
    {
        foreach (var client in clients.Values)
        {
            client.Messages.Writer.TryWrite(new("theme", theme));
        }
    }

    private static void Notify(Action? action)
    {
        if (action is null)
        {
            return;
        }

        foreach (Action handler in action.GetInvocationList())
        {
            try { handler(); } catch { }
        }
    }

    private static void Notify<T>(Action<T>? action, T value)
    {
        if (action is null)
        {
            return;
        }

        foreach (Action<T> handler in action.GetInvocationList())
        {
            try { handler(value); } catch { }
        }
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

        Host.Dispose();
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }

        shutdown.Dispose();
    }

    private sealed record SseMessage(string Event, string Data);
    private sealed class SseClient(long id, string documentId, HttpListenerResponse response) : IDisposable
    {
        private bool disposed;
        internal long Id { get; } = id;
        internal string DocumentId { get; } = documentId;
        internal Channel<SseMessage> Messages { get; } = Channel.CreateUnbounded<SseMessage>();

        internal async Task WriteAsync(string message, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
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
