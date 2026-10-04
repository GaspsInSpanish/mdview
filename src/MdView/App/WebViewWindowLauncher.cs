using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace MdView.App;

public sealed class WebViewWindowLauncher : IBrowserLauncher
{
    private const string WindowClassName = "MdViewWebViewWindow";
    private readonly IBrowserLauncher fallback;
    private readonly Action<string>? reportMessage;
    private readonly ConcurrentQueue<Action> work = new();
    private readonly ManualResetEventSlim threadReady = new();
    private readonly Dictionary<nint, WindowState> windows = [];
    private readonly WebViewNative.WindowProcedure windowProcedure;
    private readonly Thread uiThread;
    private nint dispatcherWindow;
    private Exception? threadFailure;
    private CoreWebView2Environment? environment;
    private Task<CoreWebView2Environment>? environmentTask;
    private Exception? environmentFailure;
    private Action? pendingShutdown;
    private bool exitPending;
    private int unavailableReported;

    public WebViewWindowLauncher(IBrowserLauncher fallback, Action<string>? reportMessage = null)
    {
        this.fallback = fallback;
        this.reportMessage = reportMessage;
        windowProcedure = WindowProc;
        uiThread = new Thread(RunMessageLoop) { IsBackground = true, Name = "mdview WebView2 UI" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
    }

    public bool TryLaunch(string url, out string? error)
    {
        threadReady.Wait();
        if (threadFailure is not null)
        {
            return TryFallback(url, UnavailableMessage(
                $"WebView2 was unavailable ({threadFailure.Message}), so the browser was used."),
                out error);
        }

        var launch = new LaunchWork();
        work.Enqueue(() =>
        {
            if (launch.TryClaim()) OpenWindowAsync(url);
        });
        if (!WebViewNative.PostMessageW(dispatcherWindow, WebViewNative.WmApp, 0, 0))
        {
            if (launch.TryClaim())
            {
                return TryFallback(url,
                    UnavailableMessage("WebView2 was unavailable because its UI thread could not be reached, so the browser was used."),
                    out error);
            }
        }

        error = null;
        return true;
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
            reportMessage?.Invoke($"The WebView2 window thread failed: {exception.Message}");
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

    private async void OpenWindowAsync(string url)
    {
        nint window = 0;
        WindowState? state = null;
        try
        {
            if (environmentFailure is not null) throw environmentFailure;
            environmentTask ??= CreateEnvironmentAsync();
            environment ??= await environmentTask.WaitAsync(TimeSpan.FromSeconds(15));

            var scale = WebViewNative.GetDpiForSystem() / 96.0;
            window = CreateWindow("mdview", WebViewNative.WsOverlappedWindow,
                (int)Math.Round(1000 * scale), (int)Math.Round(760 * scale));
            state = new WindowState(new Uri(url).GetLeftPart(UriPartial.Authority));
            windows.Add(window, state);
            WebViewNative.ShowWindow(window, WebViewNative.SwShow);
            WebViewNative.SetForegroundWindow(window);

            state.Controller = await environment.CreateCoreWebView2ControllerAsync(window);
            if (state.Destroyed)
            {
                try { state.Controller.Close(); }
                catch (Exception exception) { SafeReport($"The WebView2 controller could not close cleanly: {exception.Message}"); }
                return;
            }
            state.WebView = state.Controller.CoreWebView2;
            state.WebView.Settings.AreDevToolsEnabled = false;
            state.WebView.Settings.IsStatusBarEnabled = false;
            state.WebView.Settings.IsZoomControlEnabled = true;
            ResizeController(window, state);
            state.WebView.DocumentTitleChanged += (_, _) => UpdateTitle(window, state);
            state.WebView.NavigationStarting += (_, eventArgs) => HandleNavigation(state, eventArgs);
            state.WebView.NewWindowRequested += (_, eventArgs) => HandleNewWindow(state, eventArgs);
            state.WebView.ProcessFailed += (_, eventArgs) => HandleProcessFailed(window, state, eventArgs);
            state.WebView.Navigate(url);
            state.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception exception)
        {
            if (state?.Destroyed == true) return;
            if (environment is null) environmentFailure = exception;
            if (window != 0) WebViewNative.DestroyWindow(window);
            FallbackAfterQueuedLaunch(url, exception);
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
        if (IsOwnOrigin(eventArgs.Uri, state.Origin)) return;
        eventArgs.Cancel = true;
        OpenExternalIfAllowed(eventArgs.Uri);
    }

    private void HandleNewWindow(WindowState state, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (IsOwnOrigin(eventArgs.Uri, state.Origin))
        {
            state.WebView?.Navigate(eventArgs.Uri);
            return;
        }

        OpenExternalIfAllowed(eventArgs.Uri);
    }

    private void OpenExternalIfAllowed(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            reportMessage?.Invoke($"The link could not be opened: {exception.Message}");
        }
    }

    private static bool IsOwnOrigin(string target, string origin) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);

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

            if (!windows.TryGetValue(window, out var state))
                return WebViewNative.DefWindowProcW(window, message, wParam, lParam);

            switch (message)
            {
                case WebViewNative.WmSetFocus:
                    state.Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    return 0;
                case WebViewNative.WmActivate when (ushort)(wParam & 0xffff) != WebViewNative.WaInactive:
                    state.Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    return 0;
                case WebViewNative.WmMove:
                    state.Controller?.NotifyParentWindowPositionChanged();
                    return 0;
                case WebViewNative.WmSize:
                    if (state.Controller is not null)
                        state.Controller.IsVisible = wParam != WebViewNative.SizeMinimized;
                    ResizeController(window, state);
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
        state.Destroyed = true;
        try { state.Controller?.Close(); }
        catch (Exception exception) { SafeReport($"The WebView2 controller could not close cleanly: {exception.Message}"); }
        finally { windows.Remove(window); }

        if (exitPending && windows.Count == 0)
        {
            exitPending = false;
            var shutdown = pendingShutdown;
            pendingShutdown = null;
            shutdown?.Invoke();
        }
    }

    private static void ResizeController(nint window, WindowState state)
    {
        if (state.Controller is null || !WebViewNative.GetClientRect(window, out var rect)) return;
        state.Controller.Bounds = new System.Drawing.Rectangle(0, 0,
            Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
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

    private void FallbackAfterQueuedLaunch(string url, Exception webViewError)
    {
        var prefix = UnavailableMessage($"WebView2 was unavailable ({webViewError.Message}), so the browser was used.");
        if (TryFallback(url, prefix, out var error))
        {
            if (error is not null) reportMessage?.Invoke(error);
        }
        else
            reportMessage?.Invoke(error ?? prefix ?? "WebView2 was unavailable and the fallback browser could not be opened.");
    }

    private string? UnavailableMessage(string message) =>
        Interlocked.Exchange(ref unavailableReported, 1) == 0 ? message : null;

    private bool TryFallback(string url, string? prefix, out string? error)
    {
        var launched = fallback.TryLaunch(url, out var fallbackError);
        error = (prefix, fallbackError) switch
        {
            (null, null) => null,
            (null, not null) => fallbackError,
            (not null, null) => prefix,
            _ => $"{prefix} {fallbackError}"
        };
        return launched;
    }

    private sealed class WindowState(string origin)
    {
        internal string Origin { get; } = origin;
        internal string Title { get; set; } = "mdview";
        internal CoreWebView2Controller? Controller { get; set; }
        internal CoreWebView2? WebView { get; set; }
        internal bool ClosePending { get; set; }
        internal bool Destroyed { get; set; }
        internal bool ProcessFailureReported { get; set; }
    }

    private sealed class LaunchWork
    {
        private int claimed;
        internal bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
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
