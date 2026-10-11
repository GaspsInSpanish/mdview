using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using MdView.Serving;

namespace MdView.App;

public sealed class WebViewWindowLauncher : IBrowserLauncher, ILifecycleSignals, IQueuedWindowLauncher
{
    private const string WindowClassName = "MdViewWebViewWindow";
    private const string RuntimeUnavailableMessage = LaunchMessages.RuntimeUnavailable;
    private readonly DocumentHost host;
    private readonly InProcessRequestAdapter adapter;
    private readonly Action<string>? reportMessage;
    private readonly ConcurrentQueue<Action> work = new();
    private readonly ManualResetEventSlim threadReady = new();
    private readonly Dictionary<nint, WindowState> windows = [];
    private readonly OpeningDocumentGuard openingDocuments = new();
    private readonly PendingLaunchSet pendingLaunches = new();
    private readonly WindowPlacementStore placementStore = new();
    private readonly WebViewNative.WindowProcedure windowProcedure;
    private readonly Thread uiThread;
    private nint dispatcherWindow;
    private Exception? threadFailure;
    private CoreWebView2Environment? environment;
    private Task<CoreWebView2Environment>? environmentTask;
    private Exception? environmentFailure;
    private Action? pendingShutdown;
    private bool exitPending;
    private int runtimeMessageShown;

    public WebViewWindowLauncher(DocumentHost host, Action<string>? reportMessage = null)
    {
        this.host = host;
        adapter = new InProcessRequestAdapter(host);
        this.reportMessage = reportMessage;
        host.DocumentOpened += document => Notify(DocumentOpened, document);
        host.ModalCommandStarted += () => Notify(ModalCommandStarted);
        host.ModalCommandCompleted += () => Notify(ModalCommandCompleted);
        host.ExitRequested += () => Notify(ExitRequested);
        host.DocumentChanged += id => PostToUi(() => PushReload(id));
        host.ThemeChanged += theme => PostToUi(() => PushTheme(theme));
        windowProcedure = WindowProc;
        uiThread = new Thread(RunMessageLoop) { IsBackground = true, Name = "mdview WebView2 UI" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
    }

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<RegisteredDocument>? DocumentOpened;
    public event Action? ModalCommandStarted;
    public event Action? ModalCommandCompleted;
    public event Action? ExitRequested;

    public bool TryLaunch(string url, out string? error)
    {
        var ticket = new LaunchTicket(_ => { });
        var launched = TryLaunch(url, ticket, out error);
        if (!launched) ticket.Resolve(LaunchOutcome.Abandoned);
        return launched;
    }

    public bool TryLaunch(string url, LaunchTicket ticket, out string? error)
    {
        if (Environment.CurrentManagedThreadId == uiThread.ManagedThreadId)
        {
            error = "The mdview window cannot synchronously launch another window from its UI thread.";
            ticket.Resolve(LaunchOutcome.Abandoned);
            SafeReport(error);
            return false;
        }
        threadReady.Wait();
        var launch = new LaunchRequest(url, ticket);
        if (threadFailure is not null || !Enqueue(launch))
        {
            error = RuntimeUnavailableMessage;
            ticket.Resolve(LaunchOutcome.Abandoned);
            ShowFirstLaunchFailure(error);
            return false;
        }
        try { error = launch.Completion.Task.WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            error = RuntimeUnavailableMessage;
            launch.Abort(error);
        }
        // Shown here, on the caller's thread: the caller exits the process as soon as this returns false,
        // and the UI thread is a background thread that would take a posted box down with it.
        if (error is not null) ShowFirstLaunchFailure(error);
        return error is null;
    }

    public void QueueLaunch(string url, LaunchTicket ticket, Action<string?> completed)
    {
        var launch = new LaunchRequest(url, ticket, completed);
        threadReady.Wait();
        if (threadFailure is not null || !Enqueue(launch)) launch.Abort(RuntimeUnavailableMessage);
    }

    private bool Enqueue(LaunchRequest launch)
    {
        work.Enqueue(() => OpenWindowAsync(launch));
        return WebViewNative.PostMessageW(dispatcherWindow, WebViewNative.WmApp, 0, 0);
    }

    public void RequestExit(Action shutdown)
    {
        work.Enqueue(() => BeginExit(shutdown));
        if (!WebViewNative.PostMessageW(dispatcherWindow, WebViewNative.WmApp, 0, 0))
        {
            reportMessage?.Invoke("mdview could not ask its windows to close because the UI thread was unavailable.");
        }
    }

    private void RunMessageLoop()
    {
        try
        {
            var instance = WebViewNative.GetModuleHandleW(null);
            var windowClass = new WebViewNative.WindowClass
            {
                Size = (uint)Marshal.SizeOf<WebViewNative.WindowClass>(),
                WindowProcedure = windowProcedure,
                Instance = instance,
                Cursor = WebViewNative.LoadCursorW(0, WebViewNative.IdcArrow),
                Background = WebViewNative.ColorWindow + 1,
                ClassName = WindowClassName
            };
            if (WebViewNative.RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW failed");
            }

            dispatcherWindow = CreateWindow("mdview", 0, 0, 0, WebViewNative.HwndMessage);
            SynchronizationContext.SetSynchronizationContext(
                new WindowSynchronizationContext(dispatcherWindow, work, ReportCallbackFailure));
            threadReady.Set();
            while (WebViewNative.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                WebViewNative.TranslateMessage(ref message);
                WebViewNative.DispatchMessageW(ref message);
            }
        }
        catch (Exception exception)
        {
            threadFailure = exception;
            threadReady.Set();
        }
    }

    private nint CreateWindow(string title, uint style, int width, int height, nint parent = 0)
    {
        var window = WebViewNative.CreateWindowExW(0, WindowClassName, title, style,
            WebViewNative.CwUseDefault, WebViewNative.CwUseDefault, width, height, parent, 0,
            WebViewNative.GetModuleHandleW(null), 0);
        return window != 0
            ? window
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed");
    }

    private async void OpenWindowAsync(LaunchRequest launch)
    {
        nint window = 0;
        WindowState? state = null;
        OpeningDocumentGuard.Lease? lease = null;
        IDisposable? registration = null;
        var inRuntimeStage = false;
        try
        {
            if (exitPending) launch.AbandonQuietly();
            if (launch.Ticket.IsResolved) return;
            registration = pendingLaunches.Register(launch.AbandonQuietly);
            var documentId = DocumentIdFromUrl(launch.Url);
            var existing = windows.FirstOrDefault(pair => pair.Value.DocumentId == documentId);
            if (existing.Key != 0)
            {
                WebViewNative.ShowWindow(existing.Key, WebViewNative.SwShow);
                WebViewNative.SetForegroundWindow(existing.Key);
                launch.SatisfyByExisting();
                return;
            }
            lease = openingDocuments.TryEnter(documentId);
            if (lease is null)
            {
                launch.SatisfyByExisting();
                return;
            }
            if (environmentFailure is not null) throw environmentFailure;
            inRuntimeStage = true;
            environmentTask ??= CreateEnvironmentAsync();
            environment ??= await environmentTask.WaitAsync(TimeSpan.FromSeconds(15));
            inRuntimeStage = false;
            if (launch.Ticket.IsResolved) return;

            var scale = WebViewNative.GetDpiForSystem() / 96.0;
            window = CreateWindow("mdview", WebViewNative.WsOverlappedWindow | WebViewNative.WsClipChildren,
                (int)Math.Round(1000 * scale), (int)Math.Round(760 * scale));
            var uri = new Uri(launch.Url);
            state = new WindowState(uri.GetLeftPart(UriPartial.Authority), documentId, launch);
            windows.Add(window, state);
            _ = WindowPlacementRules.TryApply(() => RestorePlacement(window));
            var created = state;
            var createdWindow = window;
            launch.Ticket.WhenAbandoned.Register(() => PostToUi(() => CloseIfUnconnected(createdWindow, created)));
            WebViewNative.ShowWindow(window, WebViewNative.SwShow);
            WebViewNative.SetForegroundWindow(window);

            inRuntimeStage = true;
            state.Controller = await environment.CreateCoreWebView2ControllerAsync(window)
                .WaitAsync(TimeSpan.FromSeconds(15));
            inRuntimeStage = false;
            if (state.Destroyed || launch.Ticket.IsResolved)
            {
                try { state.Controller.Close(); }
                catch (Exception exception) { SafeReport($"The WebView2 controller could not close cleanly: {exception.Message}"); }
                if (!state.Destroyed) WebViewNative.DestroyWindow(window);
                return;
            }
            state.WebView = state.Controller.CoreWebView2;
            state.WebView.Settings.AreDevToolsEnabled = false;
            state.WebView.Settings.IsStatusBarEnabled = false;
            state.WebView.Settings.IsZoomControlEnabled = true;
            state.WebView.Settings.IsNonClientRegionSupportEnabled = true;
            ResizeController(window, state);
            state.WebView.DocumentTitleChanged += (_, _) => UpdateTitle(window, state);
            state.WebView.NavigationStarting += (_, eventArgs) => HandleNavigation(state, eventArgs);
            state.WebView.NewWindowRequested += (_, eventArgs) => HandleNewWindow(state, eventArgs);
            state.WebView.ProcessFailed += (_, eventArgs) => HandleProcessFailed(window, state, eventArgs);
            state.WebView.NavigationCompleted += (_, eventArgs) => HandleNavigationCompleted(window, state, eventArgs);
            state.WebView.WebMessageReceived += (_, eventArgs) => HandleWebMessage(window, state, eventArgs);
            state.WebView.AddWebResourceRequestedFilter($"{DocumentHost.Origin}/*", CoreWebView2WebResourceContext.All);
            state.WebView.WebResourceRequested += (_, eventArgs) => HandleWebResourceRequestedAsync(eventArgs);
            state.WebView.Navigate(launch.Url);
            state.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            launch.Complete(null);
        }
        catch (Exception exception)
        {
            var runtimeMissing = inRuntimeStage || environmentFailure is not null;
            if (environment is null) environmentFailure ??= exception;
            if (state?.Destroyed != true)
            {
                launch.Complete(runtimeMissing
                    ? RuntimeUnavailableMessage
                    : $"mdview could not open the window: {exception.Message}");
            }
            // Destroying the window resolves the ticket as abandoned; this covers the no-window case.
            launch.Ticket.Resolve(LaunchOutcome.Abandoned);
            if (window != 0 && state?.Destroyed != true) WebViewNative.DestroyWindow(window);
        }
        finally
        {
            lease?.Dispose();
            registration?.Dispose();
        }
    }

    private void CloseIfUnconnected(nint window, WindowState state)
    {
        if (!state.Connected && !state.Destroyed) WebViewNative.DestroyWindow(window);
    }

    private async void HandleWebResourceRequestedAsync(CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        var deferral = eventArgs.GetDeferral();
        var flowStarted = false;
        try
        {
            var request = eventArgs.Request;
            var origin = request.Headers.Contains("Origin") ? request.Headers.GetHeader("Origin") : null;
            var uri = new Uri(request.Uri);
            var body = InProcessRequestAdapter.ReadCapped(request.Content,
                DocumentHost.MaximumBodyBytes(uri.AbsolutePath));
            flowStarted = true;
            await InProcessRequestFlow.RunAsync(
                () => adapter.HandleAsync(request.Method, uri, origin, body),
                response => eventArgs.Response = environment!.CreateWebResourceResponse(
                    new MemoryStream(response.Body, writable: false), response.StatusCode,
                    ReasonPhrase(response.StatusCode), InProcessRequestAdapter.BuildHeaderString(response)),
                deferral.Complete,
                host.CompleteResponse);
        }
        catch (Exception exception)
        {
            if (!flowStarted) deferral.Complete();
            SafeReport($"A page request failed: {exception.Message}");
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden",
        404 => "Not Found", 409 => "Conflict", 413 => "Payload Too Large",
        500 => "Internal Server Error", 503 => "Service Unavailable", _ => "Response"
    };

    private void HandleNavigationCompleted(nint window, WindowState state, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess) PushWindowState(window, state, force: true);
        if (eventArgs.IsSuccess && !state.Connected)
        {
            state.Connected = true;
            Notify(ClientConnected);
            state.Launch.Ticket.Resolve(LaunchOutcome.Connected);
        }
    }

    private void HandleWebMessage(nint window, WindowState state, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        var message = WindowFrameProtocol.Parse(eventArgs.Source, eventArgs.WebMessageAsJson, DocumentHost.Origin);
        if (message.Kind == FrameMessageKind.WindowCommand)
        {
            switch (message.Command)
            {
                case "minimize": WebViewNative.ShowWindow(window, WebViewNative.SwMinimize); break;
                case "maximize": WebViewNative.ShowWindow(window, WebViewNative.IsZoomed(window) ? WebViewNative.SwRestore : WebViewNative.SwMaximize); break;
                case "restore": WebViewNative.ShowWindow(window, WebViewNative.SwRestore); break;
                case "close": WebViewNative.PostMessageW(window, WebViewNative.WmClose, 0, 0); break;
            }
        }
        else if (message.Kind == FrameMessageKind.FrameColors)
        {
            state.FrameColors = message.Colors!;
            InvalidateFrameBand(window);
        }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userDataFolder = Path.Combine(localAppData, "mdview", "WebView2");
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder);
    }

    private void UpdateTitle(nint window, WindowState state)
    {
        if (state.WebView is null) return;
        state.Title = string.IsNullOrWhiteSpace(state.WebView.DocumentTitle) ? "mdview" : state.WebView.DocumentTitle;
        _ = WebViewNative.SetWindowTextW(window, state.Title);
    }

    private void HandleNavigation(WindowState state, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        var decision = NavigationDecision.Decide(eventArgs.Uri, state.Origin);
        if (decision.Action == NavigationAction.Allow)
        {
            var id = DocumentIdFromUrl(eventArgs.Uri);
            if (id.Length != 0) state.DocumentId = id;
            return;
        }
        eventArgs.Cancel = true;
        ApplyNavigationDecision(decision);
    }

    private void HandleNewWindow(WindowState state, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        var decision = NavigationDecision.Decide(eventArgs.Uri, state.Origin);
        if (decision.Action == NavigationAction.Allow)
        {
            state.WebView?.Navigate(eventArgs.Uri);
            return;
        }

        ApplyNavigationDecision(decision);
    }

    private void ApplyNavigationDecision(NavigationDecision decision)
    {
        if (decision.Action == NavigationAction.OpenDocument && decision.Target is not null)
            host.OpenFromHandOff(decision.Target);
        else if (decision.Action == NavigationAction.OpenExternal && decision.Target is not null)
            OpenExternal(decision.Target);
    }

    private void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            reportMessage?.Invoke($"The link could not be opened: {exception.Message}");
        }
    }

    private void HandleProcessFailed(nint window, WindowState state, CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        try
        {
            switch (eventArgs.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                    ReportProcessFailureOnce(state, "The WebView2 renderer failed and the page is being reloaded.");
                    state.WebView?.Reload();
                    break;
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    ReportProcessFailureOnce(state, "The WebView2 browser process exited; this window will close.");
                    WebViewNative.DestroyWindow(window);
                    break;
            }
        }
        catch (Exception exception)
        {
            ReportProcessFailureOnce(state, $"The WebView2 failure could not be recovered: {exception.Message}");
            WebViewNative.DestroyWindow(window);
        }
    }

    private void ReportProcessFailureOnce(WindowState state, string message)
    {
        if (!state.ProcessFailureReported)
        {
            state.ProcessFailureReported = true;
            reportMessage?.Invoke(message);
        }
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == WebViewNative.WmApp)
            {
                DrainWork();
                return 0;
            }

            // WM_NCCALCSIZE is sent inside CreateWindowExW, before the WindowState can be registered.
            // Handle both forms: the first one, during creation, has wParam FALSE, and leaving it to
            // DefWindowProc gives the new window a standard caption until its first resize.
            if (message == WebViewNative.WmNcCalcSize)
            {
                if (wParam != 0) ApplyMaximizedBounds(window, lParam);
                return 0;
            }

            if (!windows.TryGetValue(window, out var state))
                return WebViewNative.DefWindowProcW(window, message, wParam, lParam);

            switch (message)
            {
                case WebViewNative.WmNcHitTest:
                    return HitTestFrame(window, lParam);
                case WebViewNative.WmEraseBackground:
                    return 1;
                case WebViewNative.WmPaint:
                    PaintFrame(window, state);
                    return 0;
                case WebViewNative.WmSetFocus:
                    state.Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    return 0;
                case WebViewNative.WmActivate:
                    state.Active = (ushort)(wParam & 0xffff) != WebViewNative.WaInactive;
                    if (state.Active) state.Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    PushWindowState(window, state);
                    return 0;
                case WebViewNative.WmMove:
                    state.Controller?.NotifyParentWindowPositionChanged();
                    return 0;
                case WebViewNative.WmSize:
                    if (state.Controller is not null)
                        state.Controller.IsVisible = wParam != WebViewNative.SizeMinimized;
                    ResizeController(window, state);
                    InvalidateFrameBand(window);
                    PushWindowState(window, state);
                    return 0;
                case WebViewNative.WmDpiChanged:
                    ApplyDpiChange(window, lParam);
                    return 0;
                case WebViewNative.WmClose:
                    BeginClose(window, state, forExit: false);
                    return 0;
                case WebViewNative.WmDestroy:
                    DestroyState(window, state);
                    return 0;
                default:
                    return WebViewNative.DefWindowProcW(window, message, wParam, lParam);
            }
        }
        catch (Exception exception)
        {
            SafeReport($"The mdview window encountered an error: {exception.Message}");
            return 0;
        }
    }

    private static void ApplyMaximizedBounds(nint window, nint proposedRectangle)
    {
        if (!WebViewNative.IsZoomed(window)) return;
        var proposed = Marshal.PtrToStructure<WebViewNative.Rect>(proposedRectangle);
        var monitor = WebViewNative.MonitorFromRect(ref proposed, WebViewNative.MonitorDefaultToNearest);
        var info = new WebViewNative.MonitorInfo { Size = (uint)Marshal.SizeOf<WebViewNative.MonitorInfo>() };
        if (monitor != 0 && WebViewNative.GetMonitorInfoW(monitor, ref info))
        {
            var adjusted = AutoHideTaskbarInsets.Apply(
                new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
                HasAutoHideTaskbar(info.Monitor, WebViewNative.AbeLeft),
                HasAutoHideTaskbar(info.Monitor, WebViewNative.AbeTop),
                HasAutoHideTaskbar(info.Monitor, WebViewNative.AbeRight),
                HasAutoHideTaskbar(info.Monitor, WebViewNative.AbeBottom));
            Marshal.StructureToPtr(new WebViewNative.Rect(adjusted.Left, adjusted.Top, adjusted.Right, adjusted.Bottom),
                proposedRectangle, false);
        }
    }

    private static bool HasAutoHideTaskbar(WebViewNative.Rect monitor, uint edge)
    {
        var data = new WebViewNative.AppBarData
        {
            Size = (uint)Marshal.SizeOf<WebViewNative.AppBarData>(),
            Edge = edge,
            Rectangle = monitor
        };
        return WebViewNative.SHAppBarMessage(WebViewNative.AbmGetAutoHideBarEx, ref data) != 0;
    }

    private static nint HitTestFrame(nint window, nint lParam)
    {
        // Windows 11 snap layouts require HTMAXBUTTON from WM_NCHITTEST. A page-drawn button is in
        // the WebView2 child and cannot supply that parent non-client result; Windows 10 snapping is retained.
        if (!WebViewNative.GetWindowRect(window, out var rect)) return WindowFrameHitTest.Client;
        var x = unchecked((short)((long)lParam & 0xffff)) - rect.Left;
        var y = unchecked((short)(((long)lParam >> 16) & 0xffff)) - rect.Top;
        return WindowFrameHitTest.Test(x, y, rect.Right - rect.Left, rect.Bottom - rect.Top,
            (int)WebViewNative.GetDpiForWindow(window), WebViewNative.IsZoomed(window));
    }

    private static void PaintFrame(nint window, WindowState state)
    {
        var dc = WebViewNative.BeginPaint(window, out var paint);
        try
        {
            if (dc == 0 || !WebViewNative.GetClientRect(window, out var client)) return;
            var colors = state.FrameColors;
            // WS_CLIPCHILDREN keeps this off the WebView2 child, so fill the whole client: until the child
            // exists, and at the growing edge during a live resize, the window shows the frame face rather
            // than black or stale pixels. Invalidation stays limited to the band.
            Fill(dc, new(0, 0, client.Right, client.Bottom), colors.Face);
            if (WebViewNative.IsZoomed(window)) return;
            DrawEdges(dc, client.Right, client.Bottom, 0, colors.HiInner, colors.LoOuter);
            DrawEdges(dc, client.Right, client.Bottom, 1, colors.HiOuter, colors.LoInner);
        }
        finally { if (dc != 0) WebViewNative.EndPaint(window, ref paint); }
    }

    private static void DrawEdges(nint dc, int width, int height, int inset, string topLeft, string bottomRight)
    {
        Fill(dc, new(inset, inset, width - inset, inset + 1), topLeft);
        Fill(dc, new(inset, inset, inset + 1, height - inset), topLeft);
        Fill(dc, new(inset, height - inset - 1, width - inset, height - inset), bottomRight);
        Fill(dc, new(width - inset - 1, inset, width - inset, height - inset), bottomRight);
    }

    private static void Fill(nint dc, WebViewNative.Rect rect, string color)
    {
        var rgb = uint.Parse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        var brush = WebViewNative.CreateSolidBrush((rgb & 0xff) << 16 | (rgb & 0xff00) | (rgb >> 16));
        try { if (brush != 0) WebViewNative.FillRect(dc, ref rect, brush); }
        finally { if (brush != 0) WebViewNative.DeleteObject(brush); }
    }

    private void PushWindowState(nint window, WindowState state, bool force = false)
    {
        if (state.WebView is null) return;
        var maximized = WebViewNative.IsZoomed(window);
        if (!force && state.LastPostedMaximized == maximized && state.LastPostedActive == state.Active) return;
        state.WebView.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
            { type = "window-state", maximized, active = state.Active }));
        state.LastPostedMaximized = maximized;
        state.LastPostedActive = state.Active;
    }

    private static void InvalidateFrameBand(nint window)
    {
        if (WebViewNative.IsZoomed(window) || !WebViewNative.GetClientRect(window, out var client)) return;
        var band = ResizeBand(window);
        var rectangles = new[]
        {
            new WebViewNative.Rect(0, 0, client.Right, Math.Min(band, client.Bottom)),
            new WebViewNative.Rect(0, band, Math.Min(band, client.Right), Math.Max(band, client.Bottom - band)),
            new WebViewNative.Rect(Math.Max(0, client.Right - band), band, client.Right, Math.Max(band, client.Bottom - band)),
            new WebViewNative.Rect(0, Math.Max(0, client.Bottom - band), client.Right, client.Bottom)
        };
        foreach (var rectangle in rectangles)
        {
            var value = rectangle;
            WebViewNative.InvalidateRect(window, ref value, false);
        }
    }

    private void DrainWork()
    {
        while (work.TryDequeue(out var callback))
        {
            try { callback(); }
            catch (Exception exception) { ReportCallbackFailure(exception); }
        }
    }

    private void ReportCallbackFailure(Exception exception) =>
        SafeReport($"The mdview UI thread encountered an error: {exception.Message}");

    private void SafeReport(string message)
    {
        try { reportMessage?.Invoke(message); }
        catch { /* Reporting must not tear down the native message loop. */ }
    }

    private static void ApplyDpiChange(nint window, nint suggestedRectangle)
    {
        var rect = Marshal.PtrToStructure<WebViewNative.Rect>(suggestedRectangle);
        if (!WebViewNative.SetWindowPos(window, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            WebViewNative.SwpNoZOrder | WebViewNative.SwpNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed");
        }
    }

    private void DestroyState(nint window, WindowState state)
    {
        SavePlacement(window);
        state.Destroyed = true;
        try { state.Controller?.Close(); }
        catch (Exception exception) { SafeReport($"The WebView2 controller could not close cleanly: {exception.Message}"); }
        finally { windows.Remove(window); }
        if (state.Connected)
        {
            Notify(ClientDisconnected);
        }
        else
        {
            // Closed by hand or failed before connecting: not an error in itself, only an abandoned launch.
            state.Launch.Complete(null);
            state.Launch.Ticket.Resolve(LaunchOutcome.Abandoned);
        }

        if (exitPending && windows.Count == 0)
        {
            exitPending = false;
            var shutdown = pendingShutdown;
            pendingShutdown = null;
            shutdown?.Invoke();
        }
    }

    private void RestorePlacement(nint window)
    {
        var saved = placementStore.Read();
        if (saved is null || !WindowPlacementRules.CanRestore(saved.Value, IntersectsMonitor)) return;

        var rect = ToRect(saved.Value);
        var monitor = WebViewNative.MonitorFromRect(ref rect, WebViewNative.MonitorDefaultToNull);
        var info = new WebViewNative.MonitorInfo { Size = (uint)Marshal.SizeOf<WebViewNative.MonitorInfo>() };
        if (monitor == 0 || !WebViewNative.GetMonitorInfoW(monitor, ref info)) return;

        var occupied = new List<(int X, int Y)>();
        foreach (var existing in windows.Keys.Where(handle => handle != window))
        {
            var existingPlacement = EmptyNativePlacement();
            var existingMonitor = WebViewNative.MonitorFromWindow(existing, WebViewNative.MonitorDefaultToNearest);
            var existingInfo = new WebViewNative.MonitorInfo { Size = (uint)Marshal.SizeOf<WebViewNative.MonitorInfo>() };
            if (WebViewNative.GetWindowPlacement(existing, ref existingPlacement) && existingMonitor != 0 &&
                WebViewNative.GetMonitorInfoW(existingMonitor, ref existingInfo))
            {
                occupied.Add(WindowPlacementRules.WorkspaceToScreen(
                    existingPlacement.NormalPosition.Left, existingPlacement.NormalPosition.Top,
                    existingInfo.Monitor.Left, existingInfo.Monitor.Top,
                    existingInfo.Work.Left, existingInfo.Work.Top));
            }
        }
        var dpi = WebViewNative.GetDpiForWindow(window);
        var offset = Math.Max(1, (int)Math.Round(32 * dpi / 96.0));
        var restored = WindowPlacementRules.Cascade(saved.Value, occupied, offset,
            info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        // A subsequent WM_DPICHANGED may apply Windows' suggested rectangle on mixed-DPI desktops.
        if (!WebViewNative.SetWindowPos(window, 0, restored.X, restored.Y, restored.Width, restored.Height,
            WebViewNative.SwpNoZOrder | WebViewNative.SwpNoActivate)) return;
        if (!restored.Maximized) return;

        // GetWindowPlacement converts the screen rectangle applied above to the workspace coordinates it requires.
        var placement = EmptyNativePlacement();
        if (!WebViewNative.GetWindowPlacement(window, ref placement)) return;
        placement.ShowCommand = WebViewNative.SwShowMaximized;
        _ = WebViewNative.SetWindowPlacement(window, ref placement);
    }

    private void SavePlacement(nint window)
    {
        try
        {
            var placement = EmptyNativePlacement();
            if (!WebViewNative.GetWindowPlacement(window, ref placement)) return;
            var rect = placement.NormalPosition;
            var monitor = WebViewNative.MonitorFromWindow(window, WebViewNative.MonitorDefaultToNearest);
            var info = new WebViewNative.MonitorInfo { Size = (uint)Marshal.SizeOf<WebViewNative.MonitorInfo>() };
            if (monitor == 0 || !WebViewNative.GetMonitorInfoW(monitor, ref info)) return;
            // WINDOWPLACEMENT is workspace-relative; config.json stores screen coordinates.
            var (x, y) = WindowPlacementRules.WorkspaceToScreen(rect.Left, rect.Top,
                info.Monitor.Left, info.Monitor.Top, info.Work.Left, info.Work.Top);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < WindowPlacementRules.MinimumWidth || height < WindowPlacementRules.MinimumHeight) return;
            var restoreMaximized = placement.ShowCommand == WebViewNative.SwShowMaximized ||
                (placement.Flags & WebViewNative.WindowPlacementRestoreToMaximized) != 0;
            placementStore.Write(new SavedWindowPlacement(x, y, width, height, restoreMaximized));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool IntersectsMonitor(int x, int y, int width, int height)
    {
        var rect = new WebViewNative.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        return WebViewNative.MonitorFromRect(ref rect, WebViewNative.MonitorDefaultToNull) != 0;
    }

    private static WebViewNative.Rect ToRect(SavedWindowPlacement placement) => new()
    {
        Left = placement.X,
        Top = placement.Y,
        Right = placement.X + placement.Width,
        Bottom = placement.Y + placement.Height
    };

    private static WebViewNative.WindowPlacement EmptyNativePlacement() => new()
    {
        Length = (uint)Marshal.SizeOf<WebViewNative.WindowPlacement>()
    };

    private static int ResizeBand(nint window) => Math.Max(4, (int)Math.Ceiling(4 * WebViewNative.GetDpiForWindow(window) / 96.0));

    private static void ResizeController(nint window, WindowState state)
    {
        if (state.Controller is null || !WebViewNative.GetClientRect(window, out var rect)) return;
        var band = WebViewNative.IsZoomed(window) ? 0 : ResizeBand(window);
        state.Controller.Bounds = new System.Drawing.Rectangle(band, band,
            Math.Max(0, rect.Right - rect.Left - 2 * band), Math.Max(0, rect.Bottom - rect.Top - 2 * band));
    }

    private async void BeginClose(nint window, WindowState state, bool forExit)
    {
        if (state.ClosePending) return;
        state.ClosePending = true;
        try
        {
            var dirty = false;
            try
            {
                if (state.WebView is not null)
                {
                    var query = state.WebView.ExecuteScriptAsync(
                        "Boolean(window.mdviewEdit && window.mdviewEdit.dirty)");
                    if (await Task.WhenAny(query, Task.Delay(TimeSpan.FromSeconds(2))) == query)
                        dirty = string.Equals(await query, "true", StringComparison.Ordinal);
                    else
                        dirty = true;
                }
            }
            catch
            {
                // A failed page query must never trap the user in the window.
            }

            if (forExit && !exitPending)
            {
                state.ClosePending = false;
                return;
            }

            if (dirty)
            {
                var title = state.Title.TrimStart('*').Trim();
                if (WebViewNative.MessageBox(window,
                    $"Discard your unsaved changes to {title} and close?", "mdview",
                    WebViewNative.MbYesNo | WebViewNative.MbIconWarning) != WebViewNative.IdYes)
                {
                    state.ClosePending = false;
                    if (exitPending) CancelExit();
                    return;
                }
            }

            if (forExit && !exitPending)
            {
                state.ClosePending = false;
                return;
            }
            WebViewNative.DestroyWindow(window);
        }
        catch (Exception exception)
        {
            reportMessage?.Invoke($"The mdview window could not be closed cleanly: {exception.Message}");
            WebViewNative.DestroyWindow(window);
        }
    }

    private void BeginExit(Action shutdown)
    {
        if (exitPending) return;
        pendingLaunches.AbortAll();
        if (windows.Count == 0)
        {
            shutdown();
            return;
        }

        exitPending = true;
        pendingShutdown = shutdown;
        foreach (var pair in windows.ToArray()) BeginClose(pair.Key, pair.Value, forExit: true);
    }

    private void CancelExit()
    {
        exitPending = false;
        pendingShutdown = null;
    }

    private void PostToUi(Action callback)
    {
        work.Enqueue(callback);
        if (dispatcherWindow != 0) WebViewNative.PostMessageW(dispatcherWindow, WebViewNative.WmApp, 0, 0);
    }

    private void PushReload(string id)
    {
        foreach (var state in windows.Values.Where(value => value.DocumentId == id))
            state.WebView?.PostWebMessageAsJson("{\"type\":\"reload\"}");
    }

    private void PushTheme(string theme)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { type = "theme", theme });
        foreach (var state in windows.Values) state.WebView?.PostWebMessageAsJson(json);
    }

    private void ShowFirstLaunchFailure(string error)
    {
        if (LaunchMessages.Classify(error) == LaunchFailurePrompt.RuntimeDownload) ShowRuntimeMessage();
        else SafeReport(error);
    }

    private void ShowRuntimeMessage()
    {
        if (Interlocked.Exchange(ref runtimeMessageShown, 1) != 0) return;
        if (WebViewNative.MessageBox(0, LaunchMessages.RuntimePrompt, "mdview",
            WebViewNative.MbYesNo | WebViewNative.MbIconWarning) == WebViewNative.IdYes)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                    { UseShellExecute = true });
            }
            catch { }
        }
    }

    private static void Notify(Action? action)
    {
        if (action is null) return;
        foreach (Action handler in action.GetInvocationList()) try { handler(); } catch { }
    }

    private static void Notify<T>(Action<T>? action, T value)
    {
        if (action is null) return;
        foreach (Action<T> handler in action.GetInvocationList()) try { handler(value); } catch { }
    }

    private static string DocumentIdFromUrl(string url)
    {
        var uri = new Uri(url);
        return uri.AbsolutePath.StartsWith("/d/", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(uri.AbsolutePath[3..]) : string.Empty;
    }

    private sealed class WindowState(string origin, string documentId, LaunchRequest launch)
    {
        internal LaunchRequest Launch { get; } = launch;
        internal string Origin { get; } = origin;
        internal string DocumentId { get; set; } = documentId;
        internal string Title { get; set; } = "mdview";
        internal CoreWebView2Controller? Controller { get; set; }
        internal CoreWebView2? WebView { get; set; }
        internal bool ClosePending { get; set; }
        internal bool Destroyed { get; set; }
        internal bool ProcessFailureReported { get; set; }
        internal bool Connected { get; set; }
        internal bool Active { get; set; } = true;
        internal FrameColors FrameColors { get; set; } = new("#C9C5B9", "#FFFFFF", "#E7E3D9", "#A8A499", "#88847A");
        internal bool? LastPostedMaximized { get; set; }
        internal bool? LastPostedActive { get; set; }
    }

    private sealed class LaunchRequest
    {
        private readonly Action<string?>? completed;
        private int done;

        internal LaunchRequest(string url, LaunchTicket ticket, Action<string?>? completed = null)
        {
            Url = url;
            Ticket = ticket;
            this.completed = completed;
            // An abandoned launch that never told its caller anything has failed to start.
            ticket.WhenAbandoned.Register(() =>
                Complete(completed is null ? RuntimeUnavailableMessage : LaunchMessages.QueuedTimeout));
        }

        internal string Url { get; }
        internal LaunchTicket Ticket { get; }
        internal TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Tells the caller how the launch went; only the first call has any effect.</summary>
        internal void Complete(string? error)
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            Completion.TrySetResult(error);
            try { completed?.Invoke(error); }
            catch { /* A caller's callback must not break the launch path. */ }
        }

        internal void SatisfyByExisting()
        {
            Complete(null);
            Ticket.Resolve(LaunchOutcome.SatisfiedByExisting);
        }

        internal void AbandonQuietly()
        {
            Complete(null);
            Ticket.Resolve(LaunchOutcome.Abandoned);
        }

        internal void Abort(string error)
        {
            Complete(error);
            Ticket.Resolve(LaunchOutcome.Abandoned);
        }
    }

    private sealed class WindowSynchronizationContext(
        nint window,
        ConcurrentQueue<Action> work,
        Action<Exception> reportFailure) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            work.Enqueue(() =>
            {
                try { callback(state); }
                catch (Exception exception) { reportFailure(exception); }
            });
            WebViewNative.PostMessageW(window, WebViewNative.WmApp, 0, 0);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
