using MdView.Serving;

namespace MdView.App;

public sealed class Lifecycle : IDisposable
{
    private readonly DocumentHost host;
    private readonly IDisposable handOffServer;
    private readonly ILifecycleSignals signals;
    private readonly InstanceCoordinator coordinator;
    private readonly IBrowserLauncher browserLauncher;
    private readonly TimeSpan gracePeriod;
    private readonly TimeSpan startupTimeout;
    private readonly Action<string>? reportMessage;
    private readonly string documentOrigin;
    private readonly object gate = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<LaunchTicket, CancellationTokenSource> pendingLaunches = [];
    // Only for launchers that cannot hold a ticket (the legacy browser launcher and the probe stubs): their
    // connects are matched to launches first-in, first-out. Ticketed launchers resolve their own tickets.
    private readonly Queue<LaunchTicket> implicitLaunches = new();
    private CancellationTokenSource? graceTimer;
    private int activeClients;
    private int activeModalCommands;
    private bool shuttingDown;

    public Lifecycle(
        DocumentHost host,
        IDisposable handOffServer,
        ILifecycleSignals signals,
        InstanceCoordinator coordinator,
        IBrowserLauncher browserLauncher,
        TimeSpan? gracePeriod = null,
        TimeSpan? startupTimeout = null,
        Action<string>? reportMessage = null,
        string documentOrigin = DocumentHost.Origin)
    {
        this.host = host;
        this.handOffServer = handOffServer;
        this.signals = signals;
        this.coordinator = coordinator;
        this.browserLauncher = browserLauncher;
        this.gracePeriod = gracePeriod ?? TimeSpan.Zero;
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        this.reportMessage = reportMessage;
        this.documentOrigin = documentOrigin.TrimEnd('/');

        signals.ClientConnected += OnClientConnected;
        signals.ClientDisconnected += OnClientDisconnected;
        signals.DocumentOpened += OnDocumentOpened;
        signals.ModalCommandStarted += OnModalCommandStarted;
        signals.ModalCommandCompleted += OnModalCommandCompleted;
        signals.ExitRequested += OnExitRequested;
        // Give the initial launch the same bounded setup window as every later launch.
        // A zero production grace must not race TryLaunch immediately after construction.
        lock (gate) graceTimer = StartTimer(this.startupTimeout, GraceExpired);
    }

    public Task Completion => completion.Task;
    internal bool IsShuttingDown { get { lock (gate) return shuttingDown; } }

    public bool TryLaunch(RegisteredDocument document)
    {
        var ticket = BeginLaunch(implicitResolution: browserLauncher is not IQueuedWindowLauncher);
        if (ticket is null) return false;

        var url = DocumentUrl(document);
        if (browserLauncher is IQueuedWindowLauncher ticketed)
        {
            // A ticketed launcher resolves the ticket and tells the user about a failure itself.
            if (ticketed.TryLaunch(url, ticket, out _)) return true;
            ticket.Resolve(LaunchOutcome.Abandoned);
            Shutdown();
            return false;
        }

        if (browserLauncher.TryLaunch(url, out var error))
        {
            if (error is not null)
            {
                reportMessage?.Invoke(error);
            }

            return true;
        }

        ticket.Resolve(LaunchOutcome.Abandoned);
        reportMessage?.Invoke(error ?? "The mdview window could not be opened.");
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
            foreach (var timer in pendingLaunches.Values)
            {
                timer.Cancel();
                timer.Dispose();
            }
            pendingLaunches.Clear();
        }

        try
        {
            try
            {
                signals.ClientConnected -= OnClientConnected;
                signals.ClientDisconnected -= OnClientDisconnected;
                signals.DocumentOpened -= OnDocumentOpened;
                signals.ModalCommandStarted -= OnModalCommandStarted;
                signals.ModalCommandCompleted -= OnModalCommandCompleted;
                signals.ExitRequested -= OnExitRequested;
            }
            catch (Exception exception) { ReportShutdownFailure("disconnect lifecycle signals", exception); }

            try { handOffServer.Dispose(); }
            catch (Exception exception) { ReportShutdownFailure("stop hand-offs", exception); }
            try { host.Dispose(); }
            catch (Exception exception) { ReportShutdownFailure("dispose document host", exception); }
            try { coordinator.Dispose(); }
            catch (Exception exception) { ReportShutdownFailure("release the single-instance mutex", exception); }
        }
        finally { completion.TrySetResult(); }
    }

    private void ReportShutdownFailure(string operation, Exception exception) =>
        reportMessage?.Invoke($"mdview could not {operation}: {exception.Message}");

    private string DocumentUrl(RegisteredDocument document) =>
        $"{documentOrigin}/d/{Uri.EscapeDataString(document.Id)}";

    private void OnExitRequested() => browserLauncher.RequestExit(Shutdown);

    private void OnDocumentOpened(RegisteredDocument document)
    {
        _ = TryAcceptHandOff(document);
    }

    public bool TryAcceptHandOff(RegisteredDocument document)
    {
        var ticket = BeginLaunch(implicitResolution: browserLauncher is not IQueuedWindowLauncher);
        if (ticket is null) return false;

        var url = DocumentUrl(document);
        if (browserLauncher is IQueuedWindowLauncher queued)
        {
            queued.QueueLaunch(url, ticket, error =>
            {
                if (error is not null) reportMessage?.Invoke(error);
            });
        }
        else
        {
            _ = Task.Run(() =>
            {
                if (!browserLauncher.TryLaunch(url, out var error))
                {
                    ticket.Resolve(LaunchOutcome.Abandoned);
                    reportMessage?.Invoke(error ?? "The mdview window could not be opened.");
                }
            });
        }

        return true;
    }

    /// <summary>
    /// Counts one expected reader for a requested launch, until the returned ticket resolves (any outcome).
    /// A launch that does not resolve within the startup timeout resolves itself as abandoned.
    /// Returns null once shutdown has begun.
    /// </summary>
    private LaunchTicket? BeginLaunch(bool implicitResolution)
    {
        var ticket = new LaunchTicket(OnLaunchResolved);
        lock (gate)
        {
            if (shuttingDown) return null;

            CancelTimer(ref graceTimer);
            pendingLaunches.Add(ticket, StartTimer(startupTimeout, () => ticket.Resolve(LaunchOutcome.Abandoned)));
            if (implicitResolution) implicitLaunches.Enqueue(ticket);
        }

        return ticket;
    }

    private void OnLaunchResolved(LaunchTicket ticket)
    {
        lock (gate)
        {
            if (pendingLaunches.Remove(ticket, out var timer))
            {
                timer.Cancel();
                timer.Dispose();
            }

            if (shuttingDown) return;
            ArmGraceIfIdle();
        }
    }

    private void OnClientConnected()
    {
        LaunchTicket? implicitTicket = null;
        lock (gate)
        {
            if (shuttingDown)
            {
                return;
            }

            activeClients++;
            CancelTimer(ref graceTimer);
            while (implicitTicket is null && implicitLaunches.TryDequeue(out var candidate))
            {
                if (!candidate.IsResolved) implicitTicket = candidate;
            }
        }

        // A launcher that cannot hold a ticket learns of a connection only through this signal.
        implicitTicket?.Resolve(LaunchOutcome.Connected);
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
            ArmGraceIfIdle();
        }
    }

    private void OnModalCommandStarted()
    {
        lock (gate)
        {
            if (shuttingDown) return;
            activeModalCommands++;
            CancelTimer(ref graceTimer);
        }
    }

    private void OnModalCommandCompleted()
    {
        lock (gate)
        {
            if (shuttingDown || activeModalCommands == 0) return;
            activeModalCommands--;
            ArmGraceIfIdle();
        }
    }

    /// <summary>Caller holds <see cref="gate"/>. Starts the exit grace once nothing can keep the process alive.</summary>
    private void ArmGraceIfIdle()
    {
        if (activeClients != 0 || activeModalCommands != 0 || pendingLaunches.Count != 0) return;
        CancelTimer(ref graceTimer);
        graceTimer = StartTimer(gracePeriod, GraceExpired);
    }

    private void GraceExpired()
    {
        lock (gate)
        {
            if (shuttingDown || activeClients != 0 || activeModalCommands != 0 || pendingLaunches.Count != 0)
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

    private static async Task RunTimerAsync(TimeSpan delay, Action callback, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
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
