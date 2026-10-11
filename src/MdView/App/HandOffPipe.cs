using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using MdView.Serving;

namespace MdView.App;

public static class HandOffPipeName
{
    public static string Current()
    {
        var user = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        var safeUser = string.Concat(user.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
        return $"mdview-{Process.GetCurrentProcess().SessionId}-{safeUser}";
    }
}

public sealed class HandOffPipeServer : IDisposable
{
    public const int MaximumPathBytes = 32 * 1024;
    private readonly DocumentHost host;
    private readonly Func<RegisteredDocument, bool>? accept;
    private readonly string pipeName;
    private readonly TimeSpan readTimeout;
    private readonly Func<string, PipeOptions, NamedPipeServerStream> pipeFactory;
    private readonly Action<string>? reportError;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> openPipes = new();
    private readonly ConcurrentDictionary<Task, byte> clientTasks = new();
    private readonly Task acceptLoop;
    private static readonly TimeSpan PersistentFailure = TimeSpan.FromSeconds(4);
    private int failureReported;
    private int disposed;

    public HandOffPipeServer(DocumentHost host, string? pipeName = null, TimeSpan? readTimeout = null,
        Func<RegisteredDocument, bool>? accept = null, Action<string>? reportError = null,
        Func<string, PipeOptions, NamedPipeServerStream>? pipeFactory = null,
        Func<CancellationToken, Task>? acceptLoopOverride = null)
    {
        this.host = host;
        this.accept = accept;
        this.pipeName = pipeName ?? HandOffPipeName.Current();
        this.readTimeout = readTimeout ?? TimeSpan.FromSeconds(2);
        this.reportError = reportError;
        this.pipeFactory = pipeFactory ?? CreatePipe;
        acceptLoop = Task.Run(() => acceptLoopOverride?.Invoke(shutdown.Token) ?? AcceptLoopAsync(shutdown.Token));
    }

    private static NamedPipeServerStream CreatePipe(string name, PipeOptions options) =>
        new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = await CreateListenerAsync(firstInstance: true, cancellationToken).ConfigureAwait(false);
        while (listener is not null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connected = listener;
                listener = await CreateListenerAsync(firstInstance: false, cancellationToken).ConfigureAwait(false);
                if (listener is null)
                {
                    ClosePipe(connected);
                    return;
                }

                var task = Task.Run(() => ProcessClientAndCloseAsync(connected, cancellationToken));
                clientTasks.TryAdd(task, 0);
                _ = task.ContinueWith(completed =>
                {
                    clientTasks.TryRemove(completed, out _);
                    _ = completed.Exception; // Observed; a single client's failure is not the user's problem.
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception)
            {
                // A client that connects and closes early (or a pipe torn down mid-accept) is routine.
                if (listener is not null) ClosePipe(listener);
                listener = await CreateListenerAsync(firstInstance: false, cancellationToken).ConfigureAwait(false);
            }
        }
        if (listener is not null) ClosePipe(listener);
    }

    private async Task<NamedPipeServerStream?> CreateListenerAsync(bool firstInstance,
        CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromMilliseconds(100);
        DateTime? failingSince = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var options = PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous;
                if (firstInstance) options |= PipeOptions.FirstPipeInstance;
                var listener = pipeFactory(pipeName, options);
                openPipes.TryAdd(listener, 0);
                // The outage, if any, is over: a later one may warn again.
                Interlocked.Exchange(ref failureReported, 0);
                return listener;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
            catch (Exception exception)
            {
                // Brief failures are routine (taking over from an exiting instance, a burst using up
                // instances); warn only when hand-off has been unavailable for a while.
                failingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - failingSince.Value >= PersistentFailure) ReportFailure(exception);
                try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 1000));
            }
        }
        return null;
    }

    private async Task ProcessClientAndCloseAsync(NamedPipeServerStream pipe, CancellationToken shutdownToken)
    {
        try { await ProcessClientAsync(pipe).ConfigureAwait(false); }
        catch (Exception) when (!shutdownToken.IsCancellationRequested) { /* One client's failure: drop it. */ }
        finally { ClosePipe(pipe); }
    }

    private async Task ProcessClientAsync(NamedPipeServerStream pipe)
    {
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(readTimeout);
        var header = new byte[sizeof(int)];
        try
        {
            if (!await ReadExactlyAsync(pipe, header, timeout.Token).ConfigureAwait(false)) return;
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > MaximumPathBytes)
            {
                await ReplyAsync(pipe, false, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var payload = new byte[length];
            if (!await ReadExactlyAsync(pipe, payload, timeout.Token).ConfigureAwait(false)) return;
            string path;
            try { path = new UTF8Encoding(false, true).GetString(payload); }
            catch (DecoderFallbackException)
            {
                await ReplyAsync(pipe, false, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await ReplyAsync(pipe, host.OpenFromHandOff(path, accept), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static async Task ReplyAsync(Stream stream, bool accepted, CancellationToken token)
    {
        await stream.WriteAsync(new[] { accepted ? (byte)1 : (byte)0 }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private void ClosePipe(NamedPipeServerStream pipe)
    {
        openPipes.TryRemove(pipe, out _);
        try { pipe.Dispose(); } catch (Exception) { /* Closing an already-broken pipe. */ }
    }

    private void ReportFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref failureReported, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try { reportError?.Invoke($"mdview hand-off is temporarily unavailable: {exception.Message}"); }
                catch (Exception) { }
            });
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        try
        {
            var tasks = clientTasks.Keys.Append(acceptLoop).ToArray();
            _ = Task.WaitAll(tasks, TimeSpan.FromMilliseconds(500));
        }
        catch (Exception) { /* Shutting down: a faulted loop has nothing left to tell the user. */ }
        foreach (var pipe in openPipes.Keys) ClosePipe(pipe);
        if (acceptLoop.IsCompleted) shutdown.Dispose();
    }
}

public enum HandOffResult { Unavailable, ConnectedNoReply, Rejected, Accepted }

public static class HandOffPipeClient
{
    public static async Task<bool> TrySendAsync(string path, string? pipeName = null,
        TimeSpan? connectTimeout = null, CancellationToken cancellationToken = default) =>
        await TrySendDetailedAsync(path, pipeName, connectTimeout, null, cancellationToken).ConfigureAwait(false) ==
            HandOffResult.Accepted;

    public static async Task<HandOffResult> TrySendDetailedAsync(string path, string? pipeName = null,
        TimeSpan? connectTimeout = null, TimeSpan? replyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var payload = Encoding.UTF8.GetBytes(path);
        if (payload.Length > HandOffPipeServer.MaximumPathBytes) return HandOffResult.Rejected;
        using var client = new NamedPipeClientStream(".", pipeName ?? HandOffPipeName.Current(),
            PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectBudget.CancelAfter(connectTimeout ?? TimeSpan.FromMilliseconds(400));
        try { await client.ConnectAsync(connectBudget.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return HandOffResult.Unavailable;
        }

        using var replyBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        replyBudget.CancelAfter(replyTimeout ?? TimeSpan.FromSeconds(2));
        try
        {
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await client.WriteAsync(header, replyBudget.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, replyBudget.Token).ConfigureAwait(false);
            await client.FlushAsync(replyBudget.Token).ConfigureAwait(false);
            var reply = new byte[1];
            if (await client.ReadAsync(reply, replyBudget.Token).ConfigureAwait(false) != 1)
                return HandOffResult.ConnectedNoReply;
            return reply[0] == 1 ? HandOffResult.Accepted : HandOffResult.Rejected;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return HandOffResult.ConnectedNoReply;
        }
    }
}
