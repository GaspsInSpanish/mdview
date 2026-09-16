namespace MdView.Serving;

/// <summary>Watches one document's directory and signals after stable file updates.</summary>
public sealed class DocumentWatcher : IDisposable
{
    private readonly string path;
    private readonly Func<Task> onReload;
    private readonly FileSystemWatcher watcher;
    private readonly object gate = new();
    private CancellationTokenSource? debounceCancellation;
    private bool disposed;

    public DocumentWatcher(string path, Func<Task> onReload)
    {
        this.path = path;
        this.onReload = onReload;
        watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += OnRenamed;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs eventArgs) => ScheduleReload();

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) => ScheduleReload();

    private void ScheduleReload()
    {
        CancellationToken token;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
            debounceCancellation = new CancellationTokenSource();
            token = debounceCancellation.Token;
        }

        _ = DebounceAndNotifyAsync(token);
    }

    private async Task DebounceAndNotifyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
            if (await CanReadFileAsync(cancellationToken).ConfigureAwait(false))
            {
                await onReload().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer filesystem event replaced this pending notification.
        }
    }

    private async Task<bool> CanReadFileAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                _ = stream.Length;
                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
        }

        watcher.Dispose();
    }
}
