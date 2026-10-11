using MdView.Serving;

namespace MdView.App;

public interface ILifecycleSignals
{
    /// <summary>A window's page loaded successfully: a reader is now live.</summary>
    event Action? ClientConnected;
    /// <summary>A window whose page had connected went away.</summary>
    event Action? ClientDisconnected;
    event Action<RegisteredDocument>? DocumentOpened;
    event Action? ModalCommandStarted;
    event Action? ModalCommandCompleted;
    event Action? ExitRequested;
}

/// <summary>How a requested launch ended. Exactly one outcome is ever recorded per launch.</summary>
public enum LaunchOutcome
{
    /// <summary>The launch's window completed its first successful navigation.</summary>
    Connected,
    /// <summary>The window was destroyed before connecting, creation failed, or the launch timed out.</summary>
    Abandoned,
    /// <summary>An existing or already-opening window for the document was focused instead.</summary>
    SatisfiedByExisting
}

/// <summary>
/// One requested launch. <see cref="Lifecycle"/> counts it as an expected reader until it resolves; resolving
/// is idempotent, so a late or repeated resolution can never release another launch's expectation.
/// </summary>
public sealed class LaunchTicket
{
    private readonly Action<LaunchTicket> resolved;
    private readonly CancellationTokenSource abandoned = new();
    private int outcome = -1;

    internal LaunchTicket(Action<LaunchTicket> resolved) => this.resolved = resolved;

    public bool IsResolved => Volatile.Read(ref outcome) >= 0;

    public LaunchOutcome? Outcome => Volatile.Read(ref outcome) is var value and >= 0 ? (LaunchOutcome)value : null;

    /// <summary>Cancelled when the launch resolves as <see cref="LaunchOutcome.Abandoned"/>.</summary>
    public CancellationToken WhenAbandoned => abandoned.Token;

    /// <summary>Records the outcome. Returns false, changing nothing, if the launch already resolved.</summary>
    public bool Resolve(LaunchOutcome result)
    {
        if (Interlocked.CompareExchange(ref outcome, (int)result, -1) != -1) return false;
        resolved(this);
        if (result == LaunchOutcome.Abandoned) abandoned.Cancel();
        return true;
    }
}

public interface IQueuedWindowLauncher
{
    /// <summary>
    /// Blocking first launch. The launcher resolves <paramref name="ticket"/> and reports a failure to the
    /// user itself, so the caller must not report <paramref name="error"/> again.
    /// </summary>
    bool TryLaunch(string url, LaunchTicket ticket, out string? error);

    /// <summary>Non-blocking launch. <paramref name="completed"/> receives the failure message, or null once the window is being created.</summary>
    void QueueLaunch(string url, LaunchTicket ticket, Action<string?> completed);
}

/// <summary>Tracks documents whose window is still being created, so a repeat request cannot open a second one.</summary>
public sealed class OpeningDocumentGuard
{
    private readonly HashSet<string> opening = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>Claims the document, or returns null if another lease holds it.</summary>
    public Lease? TryEnter(string documentId)
    {
        lock (gate) return opening.Add(documentId) ? new Lease(this, documentId) : null;
    }

    public bool IsOpening(string documentId)
    {
        lock (gate) return opening.Contains(documentId);
    }

    /// <summary>Releases only the claim this lease made.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly OpeningDocumentGuard owner;
        private readonly string documentId;
        private int released;

        internal Lease(OpeningDocumentGuard owner, string documentId)
        {
            this.owner = owner;
            this.documentId = documentId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lock (owner.gate) owner.opening.Remove(documentId);
        }
    }
}

public enum LaunchFailurePrompt
{
    /// <summary>Yes/No box offering the WebView2 Runtime download page.</summary>
    RuntimeDownload,
    /// <summary>Plain warning box.</summary>
    Warning
}

public static class LaunchMessages
{
    /// <summary>Runtime, environment or controller failure, and the first launch's timeout.</summary>
    public const string RuntimeUnavailable =
        "mdview needs the Microsoft Edge WebView2 Runtime, which was not found or could not start.";

    public const string RuntimePrompt = RuntimeUnavailable + " Open the download page?";

    /// <summary>A queued launch whose window was not ready in time; this is no evidence the runtime is missing.</summary>
    public const string QueuedTimeout = "mdview timed out while opening the window.";

    /// <summary>Decides how a first-launch failure is shown to the user before the process exits.</summary>
    public static LaunchFailurePrompt Classify(string error) =>
        string.Equals(error, RuntimeUnavailable, StringComparison.Ordinal)
            ? LaunchFailurePrompt.RuntimeDownload
            : LaunchFailurePrompt.Warning;
}

/// <summary>Launches still being set up, so File → Exit can abort them rather than leave their windows behind.</summary>
public sealed class PendingLaunchSet
{
    private readonly List<Action> aborts = [];
    private readonly object gate = new();

    public int Count
    {
        get { lock (gate) return aborts.Count; }
    }

    public IDisposable Register(Action abort)
    {
        lock (gate) aborts.Add(abort);
        return new Registration(this, abort);
    }

    public void AbortAll()
    {
        Action[] snapshot;
        lock (gate) snapshot = [.. aborts];
        foreach (var abort in snapshot)
        {
            try { abort(); }
            catch { /* One failing abort must not strand the others. */ }
        }
    }

    private sealed class Registration(PendingLaunchSet owner, Action abort) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate) owner.aborts.Remove(abort);
        }
    }
}
