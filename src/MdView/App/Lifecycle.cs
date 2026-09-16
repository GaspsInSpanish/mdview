using MdView.Serving;

namespace MdView.App;

public sealed class Lifecycle : IDisposable
{
    private readonly ReaderServer server;
    private readonly InstanceCoordinator coordinator;
    private readonly IBrowserLauncher browserLauncher;
    private readonly TimeSpan gracePeriod;
    private readonly TimeSpan startupTimeout;
    private readonly Action<string>? reportMessage;
    private readonly object gate = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? graceTimer;
    private CancellationTokenSource? startupTimer;
    private int activeClients;
    private int activeModalCommands;
    private bool clientExpected;
    private bool shuttingDown;

    public Lifecycle(
        ReaderServer server,
        InstanceCoordinator coordinator,
        IBrowserLauncher browserLauncher,
        TimeSpan? gracePeriod = null,
        TimeSpan? startupTimeout = null,
        Action<string>? reportMessage = null)
    {
        this.server = server;
        this.coordinator = coordinator;
        this.browserLauncher = browserLauncher;
        this.gracePeriod = gracePeriod ?? TimeSpan.FromSeconds(8);
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        this.reportMessage = reportMessage;

        server.ClientConnected += OnClientConnected;
        server.ClientDisconnected += OnClientDisconnected;
        server.DocumentOpened += OnDocumentOpened;
        server.ModalCommandStarted += OnModalCommandStarted;
        server.ModalCommandCompleted += OnModalCommandCompleted;
        server.ExitRequested += Shutdown;
        ArmStartupGuard();
    }

    public Task Completion => completion.Task;

    public bool TryLaunch(RegisteredDocument document)
    {
        var url = DocumentUrl(document);
        if (browserLauncher.TryLaunch(url, out var error))
        {
            if (error is not null)
            {
                reportMessage?.Invoke(error);
            }

            return true;
        }

        reportMessage?.Invoke(error ?? "The browser could not be opened.");
        Shutdown();
        return false;
    }

    public void Shutdown()
    {
        lock (gate)
        {
            if (shuttingDown)
            {
                return;
            }

            shuttingDown = true;
            CancelTimer(ref graceTimer);
            CancelTimer(ref startupTimer);
        }

        server.ClientConnected -= OnClientConnected;
        server.ClientDisconnected -= OnClientDisconnected;
        server.DocumentOpened -= OnDocumentOpened;
        server.ModalCommandStarted -= OnModalCommandStarted;
        server.ModalCommandCompleted -= OnModalCommandCompleted;
        server.ExitRequested -= Shutdown;
        coordinator.DeleteHandshake();
        server.Dispose();
        coordinator.Dispose();
        completion.TrySetResult();
    }

    private string DocumentUrl(RegisteredDocument document) =>
        $"http://127.0.0.1:{server.Port}/d/{Uri.EscapeDataString(document.Id)}";

    private void OnDocumentOpened(RegisteredDocument document)
    {
        lock (gate)
        {
            if (shuttingDown)
            {
                return;
            }

            CancelTimer(ref graceTimer);
            ArmStartupGuard();
        }

        TryLaunch(document);
    }

    private void OnClientConnected()
    {
        lock (gate)
        {
            if (shuttingDown)
            {
                return;
            }

            activeClients++;
            clientExpected = false;
            CancelTimer(ref startupTimer);
            CancelTimer(ref graceTimer);
        }
    }

    private void OnClientDisconnected()
    {
        lock (gate)
        {
            if (shuttingDown || activeClients == 0)
            {
                return;
            }

            activeClients--;
            if (activeClients == 0 && activeModalCommands == 0)
            {
                CancelTimer(ref graceTimer);
                graceTimer = StartTimer(gracePeriod, GraceExpired);
            }
        }
    }

    private void OnModalCommandStarted()
    {
        lock (gate)
        {
            if (shuttingDown) return;
            activeModalCommands++;
            CancelTimer(ref startupTimer);
            CancelTimer(ref graceTimer);
        }
    }

    private void OnModalCommandCompleted()
    {
        lock (gate)
        {
            if (shuttingDown || activeModalCommands == 0) return;
            activeModalCommands--;
            if (activeModalCommands == 0 && activeClients == 0)
            {
                CancelTimer(ref graceTimer);
                graceTimer = StartTimer(gracePeriod, GraceExpired);
            }
        }
    }

    private void StartupExpired()
    {
        lock (gate)
        {
            if (shuttingDown || activeClients != 0 || activeModalCommands != 0 || !clientExpected)
            {
                return;
            }
        }

        Shutdown();
    }

    private void GraceExpired()
    {
        lock (gate)
        {
            if (shuttingDown || activeClients != 0 || activeModalCommands != 0 || clientExpected)
            {
                return;
            }
        }

        Shutdown();
    }

    private static CancellationTokenSource StartTimer(TimeSpan delay, Action callback)
    {
        var cancellation = new CancellationTokenSource();
        _ = RunTimerAsync(delay, callback, cancellation.Token);
        return cancellation;
    }

    private void ArmStartupGuard()
    {
        clientExpected = true;
        CancelTimer(ref startupTimer);
        startupTimer = StartTimer(startupTimeout, StartupExpired);
    }

    private static async Task RunTimerAsync(TimeSpan delay, Action callback, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            callback();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void CancelTimer(ref CancellationTokenSource? timer)
    {
        timer?.Cancel();
        timer?.Dispose();
        timer = null;
    }

    public void Dispose() => Shutdown();
}
