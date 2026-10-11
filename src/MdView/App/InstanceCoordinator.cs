namespace MdView.App;

public sealed class InstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\mdview-singleton";
    private readonly ManualResetEventSlim ownerReady = new(false);
    private readonly ManualResetEventSlim releaseOwner = new(false);
    private readonly AutoResetEvent acquireRequested = new(false);
    private readonly AutoResetEvent acquireCompleted = new(false);
    private readonly Thread ownerThread;
    private Exception? ownerFailure;
    private volatile bool isPrimary;
    private bool disposed;

    public InstanceCoordinator(string? stateDirectory = null)
    {
        StateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mdview");
        ownerThread = new Thread(OwnMutex) { IsBackground = true, Name = "mdview mutex owner" };
        ownerThread.Start();
        ownerReady.Wait();
        if (ownerFailure is not null)
        {
            throw new InvalidOperationException("mdview could not acquire its single-instance coordinator.", ownerFailure);
        }
    }

    public bool IsPrimary => isPrimary;
    public string StateDirectory { get; }

    private void OwnMutex()
    {
        using var mutex = new Mutex(false, MutexName);
        try
        {
            try
            {
                isPrimary = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                isPrimary = true;
            }
            finally
            {
                ownerReady.Set();
            }

            while (!isPrimary)
            {
                var signal = WaitHandle.WaitAny([releaseOwner.WaitHandle, acquireRequested]);
                if (signal == 0) return;
                try
                {
                    isPrimary = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    isPrimary = true;
                }
                finally
                {
                    acquireCompleted.Set();
                }
            }

            releaseOwner.Wait();
            mutex.ReleaseMutex();
        }
        catch (Exception exception)
        {
            ownerFailure = exception;
            ownerReady.Set();
            acquireCompleted.Set();
        }
    }

    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (isPrimary) return true;
        acquireRequested.Set();
        acquireCompleted.WaitOne();
        if (ownerFailure is not null)
            throw new InvalidOperationException("mdview could not acquire its single-instance coordinator.", ownerFailure);
        return isPrimary;
    }

    public void DeleteLegacyHandshake()
    {
        // Compatibility cleanup for pre-W3A releases; remove after the next release cycle.
        if (!IsPrimary) return;
        var path = Path.Combine(StateDirectory, "instance.json");
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        releaseOwner.Set();
        ownerThread.Join();
        ownerReady.Dispose();
        releaseOwner.Dispose();
        acquireRequested.Dispose();
        acquireCompleted.Dispose();
    }
}
