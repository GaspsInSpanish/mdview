using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace MdView.App;

public sealed class InstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\mdview-singleton";
    private readonly ManualResetEventSlim ownerReady = new(false);
    private readonly ManualResetEventSlim releaseOwner = new(false);
    private readonly Thread ownerThread;
    private Exception? ownerFailure;
    private bool disposed;

    public InstanceCoordinator(string? stateDirectory = null)
    {
        StateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mdview");
        HandshakePath = Path.Combine(StateDirectory, "instance.json");
        ownerThread = new Thread(OwnMutex) { IsBackground = true, Name = "mdview mutex owner" };
        ownerThread.Start();
        ownerReady.Wait();
        if (ownerFailure is not null)
        {
            throw new InvalidOperationException("mdview could not acquire its single-instance coordinator.", ownerFailure);
        }
    }

    public bool IsPrimary { get; private set; }
    public string StateDirectory { get; }
    public string HandshakePath { get; }

    private void OwnMutex()
    {
        using var mutex = new Mutex(false, MutexName);
        try
        {
            try
            {
                IsPrimary = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                IsPrimary = true;
            }
            finally
            {
                ownerReady.Set();
            }

            if (IsPrimary)
            {
                releaseOwner.Wait();
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception)
        {
            ownerFailure = exception;
            ownerReady.Set();
        }
    }

    public void WriteHandshake(int port)
    {
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can write the handshake.");
        }

        Directory.CreateDirectory(StateDirectory);
        var value = new Handshake(port, Environment.ProcessId,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown");
        var temporaryPath = Path.Combine(StateDirectory, $"instance.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            File.Move(temporaryPath, HandshakePath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public async Task<bool> TryForwardAsync(string path, TimeSpan retryWindow, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + retryWindow;
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        do
        {
            if (TryReadLiveHandshake(out var handshake))
            {
                try
                {
                    using var content = new StringContent(path);
                    using var response = await client.PostAsync(
                        $"http://127.0.0.1:{handshake.Port}/open", content, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
        }
        while (true);

        return false;
    }

    private bool TryReadLiveHandshake(out Handshake handshake)
    {
        handshake = default!;
        try
        {
            var parsed = JsonSerializer.Deserialize<Handshake>(File.ReadAllText(HandshakePath));
            if (parsed is null || parsed.Port is < 1 or > 65535 || parsed.Pid <= 0)
            {
                return false;
            }

            using var process = Process.GetProcessById(parsed.Pid);
            if (process.HasExited)
            {
                return false;
            }

            handshake = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public void DeleteHandshake()
    {
        if (IsPrimary)
        {
            try { File.Delete(HandshakePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DeleteHandshake();
        releaseOwner.Set();
        ownerThread.Join();
        ownerReady.Dispose();
        releaseOwner.Dispose();
    }

    private sealed record Handshake(int Port, int Pid, string Version);
}
