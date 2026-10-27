using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MdView.App;
using MdView.Serving;

namespace MdView.Mac;

internal sealed class MacWindowLauncher : IBrowserLauncher, ILifecycleSignals, IQueuedWindowLauncher
{
    private const string Origin = "mdview://app";
    private const string BridgeScript = """
        (() => {
          const listeners = new Set();
          window.chrome = { webview: {
            postMessage(message) { window.webkit.messageHandlers.mdview.postMessage(message); },
            addEventListener(type, listener) { if (type === 'message') listeners.add(listener); },
            removeEventListener(type, listener) { if (type === 'message') listeners.delete(listener); }
          }};
          window.__mdviewDispatch = data => listeners.forEach(listener => listener({ data }));
        })();
        """;
    private static readonly ConcurrentDictionary<nint, MacWindowLauncher> Objects = new();
    private static bool registered;
    private readonly DocumentHost host;
    private readonly InProcessRequestAdapter adapter;
    private readonly Action<string>? report;
    private readonly SynchronizationContext uiContext;
    private readonly Dictionary<nint, WindowState> windows = [];
    private readonly Dictionary<string, WindowState> documents = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<nint, byte> StoppedTasks = new();
    private bool exiting;
    private Action? exitCompletion;
    private readonly HashSet<nint> exitWindows = [];

    /// <summary>The user cancelled an exit at an unsaved-edits prompt.</summary>
    internal event Action? ExitCancelled;
    private CGPoint cascadePoint = new(40, 700);

    internal static void EnsureRegistered()
    {
        if (registered || !OperatingSystem.IsMacOS()) return;
        RegisterDelegate();
        registered = true;
    }

    internal MacWindowLauncher(DocumentHost host, Action<string>? report)
    {
        this.host = host;
        adapter = new(host);
        this.report = report;
        uiContext = SynchronizationContext.Current ?? throw new InvalidOperationException("The Mac UI synchronization context is missing.");
        host.DocumentOpened += value => DocumentOpened?.Invoke(value);
        host.ModalCommandStarted += () => ModalCommandStarted?.Invoke();
        host.ModalCommandCompleted += () => ModalCommandCompleted?.Invoke();
        host.ExitRequested += () => ExitRequested?.Invoke();
        host.DocumentChanged += id => uiContext.Post(_ => PushReload(id), null);
        host.ThemeChanged += theme => uiContext.Post(_ => PushTheme(theme), null);
    }

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<RegisteredDocument>? DocumentOpened;
    public event Action? ModalCommandStarted;
    public event Action? ModalCommandCompleted;
    public event Action? ExitRequested;
    internal void NotifyModalStarted() => ModalCommandStarted?.Invoke();
    internal void NotifyModalCompleted() => ModalCommandCompleted?.Invoke();

    public bool TryLaunch(string url, out string? error)
    {
        var ticket = new LaunchTicket(_ => { });
        return TryLaunch(url, ticket, out error);
    }

    public bool TryLaunch(string url, LaunchTicket ticket, out string? error)
    {
        try { OpenWindow(url, ticket); error = null; return true; }
        catch (Exception exception)
        {
            ticket.Resolve(LaunchOutcome.Abandoned);
            error = $"The mdview window could not be opened: {exception.Message}";
            report?.Invoke(error);
            return false;
        }
    }

    public void QueueLaunch(string url, LaunchTicket ticket, Action<string?> completed) =>
        uiContext.Post(state => { _ = TryLaunch(url, ticket, out _); completed(null); }, null);

    public void RequestExit(Action shutdown)
    {
        if (exiting) return;
        exiting = true;
        exitCompletion = shutdown;
        if (windows.Count == 0) { shutdown(); return; }
        exitWindows.Clear();
        foreach (var state in windows.Values.ToArray()) { exitWindows.Add(state.Window); RequestClose(state); }
    }

    private void OpenWindow(string url, LaunchTicket ticket)
    {
        var uri = new Uri(url);
        var id = uri.Segments.LastOrDefault()?.TrimEnd('/') ?? string.Empty;
        if (documents.TryGetValue(id, out var existing))
        {
            ObjectiveC.SendVoid(existing.Window, ObjectiveC.Sel("makeKeyAndOrderFront:"), existing.Window);
            ticket.Resolve(LaunchOutcome.SatisfiedByExisting);
            return;
        }

        var handler = ObjectiveC.New("MdViewWebDelegate");
        Objects[handler] = this;
        var configuration = ObjectiveC.New("WKWebViewConfiguration");
        ObjectiveC.SendVoid(configuration, ObjectiveC.Sel("setURLSchemeHandler:forURLScheme:"), handler, ObjectiveC.String("mdview"));
        var controller = ObjectiveC.New("WKUserContentController");
        var script = ObjectiveC.SendScript(ObjectiveC.Send(ObjectiveC.Class("WKUserScript"), ObjectiveC.Sel("alloc")),
            ObjectiveC.Sel("initWithSource:injectionTime:forMainFrameOnly:"), ObjectiveC.String(BridgeScript), 0, 1);
        ObjectiveC.SendVoid(controller, ObjectiveC.Sel("addUserScript:"), script);
        ObjectiveC.SendVoid(controller, ObjectiveC.Sel("addScriptMessageHandler:name:"), handler, ObjectiveC.String("mdview"));
        ObjectiveC.SendVoid(configuration, ObjectiveC.Sel("setUserContentController:"), controller);

        var rect = new CGRect(new CGPoint(0, 0), new CGSize(900, 700));
        const nuint style = 1 | 2 | 4 | 8;
        var window = ObjectiveC.SendRectNUInt(ObjectiveC.Send(ObjectiveC.Class("NSWindow"), ObjectiveC.Sel("alloc")),
            ObjectiveC.Sel("initWithContentRect:styleMask:backing:defer:"), rect, style, 2, 0);
        if (window == 0) throw new InvalidOperationException("NSWindow creation failed.");
        // Closed() releases the window itself; AppKit's default release-on-close would make that a double release.
        var webView = ObjectiveC.SendRect(ObjectiveC.Send(ObjectiveC.Class("WKWebView"), ObjectiveC.Sel("alloc")),
            ObjectiveC.Sel("initWithFrame:configuration:"), rect, configuration);
        if (webView == 0) throw new InvalidOperationException("WKWebView creation failed.");
        ObjectiveC.SendVoid(webView, ObjectiveC.Sel("setNavigationDelegate:"), handler);
        ObjectiveC.SendVoid(webView, ObjectiveC.Sel("setUIDelegate:"), handler);
        ObjectiveC.SendVoid(webView, ObjectiveC.Sel("setAutoresizingMask:"), (nint)18);
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("setContentView:"), webView);
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("setDelegate:"), handler);
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("setTitle:"), ObjectiveC.String(TitleFor(id)));
        if (windows.Count == 0) ObjectiveC.SendVoid(window, ObjectiveC.Sel("center"));
        else cascadePoint = ObjectiveC.SendPoint(window, ObjectiveC.Sel("cascadeTopLeftFromPoint:"), cascadePoint);
        var state = new WindowState(window, webView, handler, id, ticket);
        windows[window] = state;
        documents[id] = state;
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("makeKeyAndOrderFront:"), window);
        var requestUrl = ObjectiveC.Send(ObjectiveC.Class("NSURL"), ObjectiveC.Sel("URLWithString:"), ObjectiveC.String(url));
        var request = ObjectiveC.Send(ObjectiveC.Class("NSURLRequest"), ObjectiveC.Sel("requestWithURL:"), requestUrl);
        ObjectiveC.Send(webView, ObjectiveC.Sel("loadRequest:"), request);
        ObjectiveC.SendVoid(script, ObjectiveC.Sel("release"));
        ObjectiveC.SendVoid(controller, ObjectiveC.Sel("release"));
        ObjectiveC.SendVoid(configuration, ObjectiveC.Sel("release"));
        ObjectiveC.SendVoid(handler, ObjectiveC.Sel("release"));
        var context = SynchronizationContext.Current;
        _ = Task.Delay(TimeSpan.FromSeconds(30), ticket.WhenAbandoned).ContinueWith(_ => context?.Post(_ =>
        {
            if (state.Connected || ticket.IsResolved) return;
            ticket.Resolve(LaunchOutcome.Abandoned);
            state.AllowClose = true;
            ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
            report?.Invoke("mdview timed out while opening the window.");
        }, null), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private string TitleFor(string id) => host.TryGetDocument(id, out var document) ? document.Title + " - mdview" : "mdview";

    private async void StartSchemeTask(nint task)
    {
        HostResponse? response = null;
        var delivered = false;
        nint fields = 0;
        nint nativeResponse = 0;
        try
        {
            var request = ObjectiveC.Send(task, ObjectiveC.Sel("request"));
            var urlObject = ObjectiveC.Send(request, ObjectiveC.Sel("URL"));
            var urlText = ObjectiveC.GetString(ObjectiveC.Send(urlObject, ObjectiveC.Sel("absoluteString")));
            var method = ObjectiveC.GetString(ObjectiveC.Send(request, ObjectiveC.Sel("HTTPMethod"))) ?? "GET";
            if (urlText is null || !Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
                throw new InvalidOperationException("WebKit supplied an invalid request URL.");
            var headers = ObjectiveC.Send(request, ObjectiveC.Sel("allHTTPHeaderFields"));
            var origin = headers == 0 ? null : ObjectiveC.GetString(ObjectiveC.Send(headers, ObjectiveC.Sel("objectForKey:"), ObjectiveC.String("Origin")));
            // WebKit sends no Origin header on custom-scheme requests (measured on macos-14). The origin
            // of the request's main document is set by WebKit, not by page script, so it stands in for
            // the header. Missing or foreign stays null, and DocumentHost refuses the write as before.
            origin ??= DocumentOrigin(ObjectiveC.Send(request, ObjectiveC.Sel("mainDocumentURL")));
            var data = ObjectiveC.Send(request, ObjectiveC.Sel("HTTPBody"));
            var body = data != 0 ? Bytes(data) : ReadStreamCapped(ObjectiveC.Send(request, ObjectiveC.Sel("HTTPBodyStream")),
                DocumentHost.MaximumBodyBytes(uri.AbsolutePath));
            response = uri.Scheme == "mdview" && uri.Host == "app"
                ? await adapter.HandleAsync(method, uri, origin, body)
                : new HostResponse(404, "text/plain; charset=utf-8", "Not Found"u8.ToArray(), new Dictionary<string, string>());
            MacApplication.Trace($"scheme {method} {uri.AbsolutePath} origin={origin ?? "<none>"} body={body.Length} -> {response.StatusCode}");
            if (IsStopped(task)) return;
            fields = ObjectiveC.New("NSMutableDictionary");
            ObjectiveC.SendVoid(fields, ObjectiveC.Sel("setObject:forKey:"), ObjectiveC.String(response.ContentType), ObjectiveC.String("Content-Type"));
            foreach (var pair in response.Headers)
                ObjectiveC.SendVoid(fields, ObjectiveC.Sel("setObject:forKey:"), ObjectiveC.String(pair.Value), ObjectiveC.String(pair.Key));
            nativeResponse = ObjectiveC.Send(ObjectiveC.Send(ObjectiveC.Class("NSHTTPURLResponse"), ObjectiveC.Sel("alloc")),
                ObjectiveC.Sel("initWithURL:statusCode:HTTPVersion:headerFields:"), urlObject, (nint)response.StatusCode,
                ObjectiveC.String("HTTP/1.1"), fields);
            if (!IsStopped(task)) ObjectiveC.SendVoid(task, ObjectiveC.Sel("didReceiveResponse:"), nativeResponse);
            if (response.Body.Length > 0 && !IsStopped(task)) ObjectiveC.SendVoid(task, ObjectiveC.Sel("didReceiveData:"), Data(response.Body));
            if (!IsStopped(task))
            {
                ObjectiveC.SendVoid(task, ObjectiveC.Sel("didFinish"));
                delivered = true;
            }
        }
        catch (Exception exception)
        {
            report?.Invoke($"A page request failed: {exception.Message}");
            FailSchemeTask(task, exception.Message);
        }
        finally
        {
            try { StoppedTasks.TryRemove(task, out _); }
            catch (Exception exception) { MacApplication.Report(exception.Message); }
            try { if (response is not null && delivered) host.CompleteResponse(response); }
            catch (Exception exception) { MacApplication.Report(exception.Message); }
            try { if (nativeResponse != 0) ObjectiveC.SendVoid(nativeResponse, ObjectiveC.Sel("release")); }
            catch (Exception exception) { MacApplication.Report(exception.Message); }
            try { if (fields != 0) ObjectiveC.SendVoid(fields, ObjectiveC.Sel("release")); }
            catch (Exception exception) { MacApplication.Report(exception.Message); }
            try { ObjectiveC.SendVoid(task, ObjectiveC.Sel("release")); }
            catch (Exception exception) { MacApplication.Report(exception.Message); }
        }
    }

    private static bool IsStopped(nint task) => StoppedTasks.ContainsKey(task);

    private static string? DocumentOrigin(nint url)
    {
        if (url == 0) return null;
        var text = ObjectiveC.GetString(ObjectiveC.Send(url, ObjectiveC.Sel("absoluteString")));
        return text is not null && Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            uri.Scheme == "mdview" && uri.Host == "app" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            ? Origin
            : null;
    }

    private static void FailSchemeTask(nint task, string message)
    {
        if (IsStopped(task)) return;
        try
        {
            var info = ObjectiveC.New("NSMutableDictionary");
            ObjectiveC.SendVoid(info, ObjectiveC.Sel("setObject:forKey:"), ObjectiveC.String(message), ObjectiveC.String("NSLocalizedDescription"));
            var error = ObjectiveC.Send(ObjectiveC.Class("NSError"), ObjectiveC.Sel("errorWithDomain:code:userInfo:"),
                ObjectiveC.String("io.github.gaspsinspanish.mdview"), (nint)1, info);
            if (!IsStopped(task)) ObjectiveC.SendVoid(task, ObjectiveC.Sel("didFailWithError:"), error);
            ObjectiveC.SendVoid(info, ObjectiveC.Sel("release"));
        }
        catch (Exception exception) { MacApplication.Report($"Could not fail a WebKit scheme task: {exception.Message}"); }
    }

    private void Message(nint handler, nint message)
    {
        var frame = ObjectiveC.Send(message, ObjectiveC.Sel("frameInfo"));
        if (frame == 0 || ObjectiveC.SendBool(frame, ObjectiveC.Sel("isMainFrame")) == 0) return;
        var securityOrigin = ObjectiveC.Send(frame, ObjectiveC.Sel("securityOrigin"));
        var scheme = ObjectiveC.GetString(ObjectiveC.Send(securityOrigin, ObjectiveC.Sel("protocol")));
        var hostName = ObjectiveC.GetString(ObjectiveC.Send(securityOrigin, ObjectiveC.Sel("host")));
        if (!string.Equals(scheme, "mdview", StringComparison.Ordinal) ||
            !string.Equals(hostName, "app", StringComparison.Ordinal)) return;
        var body = ObjectiveC.Send(message, ObjectiveC.Sel("body"));
        if (body == 0 || ObjectiveC.SendBool(ObjectiveC.Class("NSJSONSerialization"), ObjectiveC.Sel("isValidJSONObject:"), body) == 0) return;
        var jsonData = ObjectiveC.Send(ObjectiveC.Class("NSJSONSerialization"), ObjectiveC.Sel("dataWithJSONObject:options:error:"), body, 0, 0);
        if (jsonData == 0) return;
        var json = Encoding.UTF8.GetString(Bytes(jsonData));
        MacApplication.Trace($"message {json}");
        var state = windows.Values.FirstOrDefault(value => value.Handler == handler);
        if (state is null) return;
        if (TryHandleControlMessage(state, json)) return;
        var source = ObjectiveC.GetString(ObjectiveC.Send(ObjectiveC.Send(state.WebView, ObjectiveC.Sel("URL")), ObjectiveC.Sel("absoluteString"))) ?? string.Empty;
        var parsed = WindowFrameProtocol.Parse(source, json, Origin);
        if (parsed.Kind != FrameMessageKind.WindowCommand) return;
        if (parsed.Command == "minimize") ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("miniaturize:"), state.Window);
        else if (parsed.Command is "maximize" or "restore") ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("zoom:"), state.Window);
        else if (parsed.Command == "close") RequestClose(state);
    }

    private static bool IsOwnMainFrame(nint frame)
    {
        if (frame == 0 || ObjectiveC.SendBool(frame, ObjectiveC.Sel("isMainFrame")) == 0) return false;
        var securityOrigin = ObjectiveC.Send(frame, ObjectiveC.Sel("securityOrigin"));
        if (securityOrigin == 0) return false;
        return string.Equals(ObjectiveC.GetString(ObjectiveC.Send(securityOrigin, ObjectiveC.Sel("protocol"))), "mdview", StringComparison.Ordinal) &&
            string.Equals(ObjectiveC.GetString(ObjectiveC.Send(securityOrigin, ObjectiveC.Sel("host"))), "app", StringComparison.Ordinal);
    }

    private static void ShowJavaScriptAlert(nint message, nint frame, nint completion)
    {
        MacApplication.Trace($"js alert: {ObjectiveC.GetString(message)}");
        try
        {
            if (IsOwnMainFrame(frame))
            {
                var alert = ObjectiveC.New("NSAlert");
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setMessageText:"), ObjectiveC.String("mdview"));
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setInformativeText:"), message);
                ObjectiveC.SendLong(alert, ObjectiveC.Sel("runModal"));
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("release"));
            }
        }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
        finally { InvokeVoidBlock(completion); }
    }

    private static void ShowJavaScriptConfirm(nint message, nint frame, nint completion)
    {
        MacApplication.Trace($"js confirm: {ObjectiveC.GetString(message)}");
        byte accepted = 0;
        try
        {
            if (IsOwnMainFrame(frame))
            {
                var alert = ObjectiveC.New("NSAlert");
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setMessageText:"), ObjectiveC.String("mdview"));
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setInformativeText:"), message);
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("addButtonWithTitle:"), ObjectiveC.String("OK"));
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("addButtonWithTitle:"), ObjectiveC.String("Cancel"));
                accepted = ObjectiveC.SendLong(alert, ObjectiveC.Sel("runModal")) == 1000 ? (byte)1 : (byte)0;
                ObjectiveC.SendVoid(alert, ObjectiveC.Sel("release"));
            }
        }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
        finally { InvokeBoolBlock(completion, accepted); }
    }

    private static unsafe void InvokeVoidBlock(nint block)
    {
        if (block == 0) return;
        var invoke = Marshal.ReadIntPtr(block, 16);
        ((delegate* unmanaged<nint, void>)invoke)(block);
    }

    private static unsafe void InvokeBoolBlock(nint block, byte value)
    {
        if (block == 0) return;
        var invoke = Marshal.ReadIntPtr(block, 16);
        ((delegate* unmanaged<nint, byte, void>)invoke)(block, value);
    }

    private bool TryHandleControlMessage(WindowState state, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)) return false;
            if (type.GetString() == "close-result")
            {
                if (!state.ClosePending) return true;
                var dirty = root.TryGetProperty("dirty", out var value) && value.ValueKind == JsonValueKind.True;
                FinishClose(state, dirty);
                return true;
            }
#if MDVIEW_SMOKE
            if (type.GetString() == "smoke-report" && Environment.GetEnvironmentVariable("MDVIEW_SMOKE_REPORT") is { Length: > 0 } path)
            {
                try { File.WriteAllText(path, root.GetRawText()); }
                catch (Exception exception) { report?.Invoke($"The smoke report could not be written: {exception.Message}"); }
                state.AllowClose = true;
                ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
                return true;
            }
#endif
        }
        catch (JsonException) { }
        return false;
    }

    private void RequestClose(WindowState state)
    {
        if (state.ClosePending) return;
        state.ClosePending = true;
        state.CloseTimeout?.Cancel();
        state.CloseTimeout?.Dispose();
        state.CloseTimeout = new CancellationTokenSource();
        var token = state.CloseTimeout.Token;
        _ = Task.Delay(TimeSpan.FromSeconds(2), token).ContinueWith(_ => uiContext.Post(_ =>
        {
            if (state.ClosePending) FinishClose(state, dirty: true);
        }, null), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        Evaluate(state, "window.chrome.webview.postMessage({type:'close-result',dirty:Boolean(window.mdviewEdit&&window.mdviewEdit.dirty)})");
    }

    private void FinishClose(WindowState state, bool dirty)
    {
        if (!state.ClosePending) return;
        state.CloseTimeout?.Cancel();
        state.CloseTimeout?.Dispose();
        state.CloseTimeout = null;
        if (dirty)
        {
            var alert = ObjectiveC.New("NSAlert");
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setMessageText:"), ObjectiveC.String("Discard unsaved changes?"));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setInformativeText:"), ObjectiveC.String("Your unsaved changes will be lost."));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("addButtonWithTitle:"), ObjectiveC.String("Discard"));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("addButtonWithTitle:"), ObjectiveC.String("Cancel"));
            var discard = ObjectiveC.SendLong(alert, ObjectiveC.Sel("runModal")) == 1000;
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("release"));
            if (!discard)
            {
                CancelExit();
                return;
            }
        }
        state.AllowClose = true;
        ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
    }

    private void CancelExit()
    {
        var wasExiting = exiting;
        exiting = false;
        exitCompletion = null;
        // Only the closes this exit started are abandoned; a window's own pending close stays in flight.
        foreach (var open in windows.Values.Where(value => exitWindows.Contains(value.Window)))
        {
            open.ClosePending = false;
            open.CloseTimeout?.Cancel();
            open.CloseTimeout?.Dispose();
            open.CloseTimeout = null;
        }
        exitWindows.Clear();
        if (wasExiting) ExitCancelled?.Invoke();
    }

    private void Closed(nint window)
    {
        if (!windows.Remove(window, out var state)) return;
        documents.Remove(state.DocumentId);
        state.CloseTimeout?.Cancel();
        state.CloseTimeout?.Dispose();
        ObjectiveC.SendVoid(state.WebView, ObjectiveC.Sel("stopLoading"));
        ObjectiveC.SendVoid(state.WebView, ObjectiveC.Sel("setNavigationDelegate:"), 0);
        ObjectiveC.SendVoid(state.WebView, ObjectiveC.Sel("setUIDelegate:"), 0);
        ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("setDelegate:"), 0);
        var configuration = ObjectiveC.Send(state.WebView, ObjectiveC.Sel("configuration"));
        var controller = ObjectiveC.Send(configuration, ObjectiveC.Sel("userContentController"));
        if (controller != 0) ObjectiveC.SendVoid(controller, ObjectiveC.Sel("removeScriptMessageHandlerForName:"), ObjectiveC.String("mdview"));
        Objects.TryRemove(state.Handler, out _);
        if (state.Connected) ClientDisconnected?.Invoke(); else state.Ticket.Resolve(LaunchOutcome.Abandoned);
        if (exiting && windows.Count == 0) exitCompletion?.Invoke();
        ObjectiveC.SendVoid(state.WebView, ObjectiveC.Sel("release"));
        ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("release"));
    }

    private void Connected(nint webView)
    {
        var state = windows.Values.FirstOrDefault(value => value.WebView == webView);
        if (state is null) return;
        if (!state.Connected)
        {
            state.Connected = true;
            state.Ticket.Resolve(LaunchOutcome.Connected);
            ClientConnected?.Invoke();
        }
        StartSmokeIfRequested(state);
    }

    private void StartSmokeIfRequested(WindowState state)
    {
#if MDVIEW_SMOKE
        if (Environment.GetEnvironmentVariable("MDVIEW_SMOKE_REPORT") is not { Length: > 0 }) return;
        const string script = """
            (async()=>{const first=!sessionStorage.getItem('mdview-smoke');if(first){sessionStorage.setItem('mdview-smoke','1');
            document.querySelector('input[type=checkbox]')?.click();return;}
            const heading=document.querySelector('h1')?.textContent||'';const code=!!document.querySelector('pre code.hljs');
            const started=Date.now();while(Date.now()-started<5000&&!document.body.textContent.includes('SMOKE_RELOAD_MARKER'))await new Promise(r=>setTimeout(r,100));
            window.chrome.webview.postMessage({type:'smoke-report',heading,platform:document.documentElement.dataset.platform,
            noTitleBar:!document.querySelector('.title-bar'),bridge:typeof window.chrome.webview.postMessage==='function',xss:!window.mdviewXss,
            highlighted:code,toggle:true,reloaded:document.body.textContent.includes('SMOKE_RELOAD_MARKER')});})()
            """;
        Evaluate(state, script);
#endif
    }

    private static void Evaluate(WindowState state, string javascript) =>
        ObjectiveC.SendVoid(state.WebView, ObjectiveC.Sel("evaluateJavaScript:completionHandler:"), ObjectiveC.String(javascript), 0);

    private void PushReload(string id)
    {
        if (!documents.TryGetValue(id, out var state)) return;
        Evaluate(state, "if(!window.mdviewEdit?.onExternalChange())location.reload()");
    }

    private void PushTheme(string theme)
    {
        var json = JsonSerializer.Serialize(new { type = "theme", theme });
        foreach (var state in windows.Values) Evaluate(state, $"window.__mdviewDispatch({json})");
    }

    private nint Decide(nint webView, nint action, bool trackNavigation = true)
    {
        var request = ObjectiveC.Send(action, ObjectiveC.Sel("request"));
        var text = ObjectiveC.GetString(ObjectiveC.Send(ObjectiveC.Send(request, ObjectiveC.Sel("URL")), ObjectiveC.Sel("absoluteString"))) ?? string.Empty;
        var decision = NavigationDecision.Decide(text, Origin);
        if (decision.Action == NavigationAction.Allow)
        {
            if (trackNavigation) TrackAllowedNavigation(webView, text);
            return 1;
        }
        if (decision.Action == NavigationAction.OpenDocument && decision.Target is not null)
        {
            try { host.OpenFromHandOff(decision.Target); }
            catch (Exception exception) when (exception is ObjectDisposedException or IOException or UnauthorizedAccessException)
            { report?.Invoke($"The Markdown file could not be opened: {exception.Message}"); }
        }
        else if (decision.Action == NavigationAction.OpenExternal && decision.Target is not null)
        {
            var url = ObjectiveC.Send(ObjectiveC.Class("NSURL"), ObjectiveC.Sel("URLWithString:"), ObjectiveC.String(decision.Target));
            ObjectiveC.Send(ObjectiveC.Send(ObjectiveC.Class("NSWorkspace"), ObjectiveC.Sel("sharedWorkspace")), ObjectiveC.Sel("openURL:"), url);
        }
        return 0;
    }

    private void TrackAllowedNavigation(nint webView, string target)
    {
        var state = windows.Values.FirstOrDefault(value => value.WebView == webView);
        if (state is null || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Segments.Length != 3 || uri.Segments[1] != "d/") return;
        var id = Uri.UnescapeDataString(uri.Segments[2].TrimEnd('/'));
        if (id == state.DocumentId) return;
        documents.Remove(state.DocumentId);
        state.DocumentId = id;
        documents[id] = state;
        ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("setTitle:"), ObjectiveC.String(TitleFor(id)));
    }

    private static unsafe void InvokeDecision(nint block, nint policy)
    {
        if (block == 0) return;
        var invoke = Marshal.ReadIntPtr(block, 16);
        ((delegate* unmanaged<nint, nint, void>)invoke)(block, policy);
    }

    private static byte[] Bytes(nint data)
    {
        if (data == 0) return [];
        var length = checked((int)ObjectiveC.SendLong(data, ObjectiveC.Sel("length")));
        if (length == 0) return [];
        var pointer = ObjectiveC.Send(data, ObjectiveC.Sel("bytes"));
        var result = new byte[length];
        Marshal.Copy(pointer, result, 0, length);
        return result;
    }

    private static unsafe byte[] ReadStreamCapped(nint stream, long maximum)
    {
        if (stream == 0) return [];
        ObjectiveC.SendVoid(stream, ObjectiveC.Sel("open"));
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            fixed (byte* pointer = buffer)
            {
                while (output.Length <= maximum)
                {
                    var wanted = (nuint)Math.Min(buffer.Length, maximum + 1 - output.Length);
                    var read = ObjectiveC.SendRead(stream, ObjectiveC.Sel("read:maxLength:"), (nint)pointer, wanted);
                    if (read <= 0) break;
                    output.Write(buffer, 0, checked((int)read));
                }
            }
            return output.ToArray();
        }
        finally { ObjectiveC.SendVoid(stream, ObjectiveC.Sel("close")); }
    }

    private static unsafe nint Data(byte[] bytes)
    {
        fixed (byte* pointer = bytes)
            return ObjectiveC.Send(ObjectiveC.Class("NSData"), ObjectiveC.Sel("dataWithBytes:length:"), (nint)pointer, bytes.Length);
    }

    private static unsafe void RegisterDelegate()
    {
        var cls = ObjectiveC.objc_allocateClassPair(ObjectiveC.Class("NSObject"), "MdViewWebDelegate", 0);
        if (cls == 0) throw new InvalidOperationException("Could not allocate the WebKit delegate class.");
        foreach (var protocol in new[] { "WKURLSchemeHandler", "WKNavigationDelegate", "WKUIDelegate", "WKScriptMessageHandler", "NSWindowDelegate" })
            ObjectiveC.AddProtocolIfAvailable(cls, protocol);
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("webView:startURLSchemeTask:"), &StartTask, "v@:@@");
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("webView:stopURLSchemeTask:"), &StopTask, "v@:@@");
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("userContentController:didReceiveScriptMessage:"), &ReceiveMessage, "v@:@@");
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("webView:didFinishNavigation:"), &DidFinishNavigation, "v@:@@");
        ObjectiveC.class_addMethod3(cls, ObjectiveC.Sel("webView:didFailProvisionalNavigation:withError:"), &DidFailNavigation, "v@:@@@");
        ObjectiveC.class_addMethod3(cls, ObjectiveC.Sel("webView:decidePolicyForNavigationAction:decisionHandler:"), &DecidePolicy, "v@:@@@");
        ObjectiveC.class_addMethodPtr(cls, ObjectiveC.Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"), &CreateWebView, "@@:@@@@");
        ObjectiveC.class_addMethod4(cls, ObjectiveC.Sel("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:"), &JavaScriptAlert, "v@:@@@@");
        ObjectiveC.class_addMethod4(cls, ObjectiveC.Sel("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:"), &JavaScriptConfirm, "v@:@@@@");
        ObjectiveC.class_addMethod1(cls, ObjectiveC.Sel("webViewWebContentProcessDidTerminate:"), &ProcessTerminated, "v@:@");
        ObjectiveC.class_addMethodBool(cls, ObjectiveC.Sel("windowShouldClose:"), &WindowShouldClose, "B@:@");
        ObjectiveC.class_addMethod1(cls, ObjectiveC.Sel("windowWillClose:"), &WindowWillClose, "v@:@");
        ObjectiveC.objc_registerClassPair(cls);
    }

    private static MacWindowLauncher? Owner(nint self) => Objects.TryGetValue(self, out var owner) ? owner : null;

    [UnmanagedCallersOnly] private static void StartTask(nint self, nint cmd, nint webView, nint task)
    {
        var retained = false;
        try
        {
            ObjectiveC.Send(task, ObjectiveC.Sel("retain"));
            retained = true;
            if (Owner(self) is { } owner) owner.StartSchemeTask(task);
            else { FailSchemeTask(task, "The mdview request handler is unavailable."); ObjectiveC.SendVoid(task, ObjectiveC.Sel("release")); }
        }
        catch (Exception exception)
        {
            MacApplication.Report(exception.Message);
            if (retained) FailSchemeTask(task, exception.Message);
            if (retained) { try { ObjectiveC.SendVoid(task, ObjectiveC.Sel("release")); } catch { } }
        }
    }
    [UnmanagedCallersOnly] private static void StopTask(nint self, nint cmd, nint webView, nint task)
    {
        try { StoppedTasks.TryAdd(task, 0); }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
    }
    [UnmanagedCallersOnly] private static void ReceiveMessage(nint self, nint cmd, nint controller, nint message)
    { try { Owner(self)?.Message(self, message); } catch (Exception exception) { MacApplication.Report(exception.Message); } }
    [UnmanagedCallersOnly] private static void DidFinishNavigation(nint self, nint cmd, nint webView, nint navigation)
    { try { MacApplication.Trace("didFinishNavigation"); Owner(self)?.Connected(webView); } catch (Exception exception) { MacApplication.Report(exception.Message); } }
    [UnmanagedCallersOnly] private static void DidFailNavigation(nint self, nint cmd, nint webView, nint navigation, nint error)
    {
        try
        {
            MacApplication.Trace($"didFailProvisionalNavigation {ObjectiveC.GetString(ObjectiveC.Send(error, ObjectiveC.Sel("description")))}");
            var owner = Owner(self); var state = owner?.windows.Values.FirstOrDefault(value => value.WebView == webView);
            if (owner is null || state is null || state.Connected) return;
            state.Ticket.Resolve(LaunchOutcome.Abandoned); state.AllowClose = true;
            owner.report?.Invoke("The mdview page could not be loaded.");
            ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
        }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
    }
    [UnmanagedCallersOnly] private static void DecidePolicy(nint self, nint cmd, nint webView, nint action, nint block)
    {
        nint policy = 0;
        try { if (Owner(self) is { } owner) policy = owner.Decide(webView, action); MacApplication.Trace($"decidePolicy -> {policy}"); }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
        finally
        {
            try { InvokeDecision(block, policy); }
            catch (Exception exception) { MacApplication.Report(exception.Message); }
        }
    }
    [UnmanagedCallersOnly] private static nint CreateWebView(nint self, nint cmd, nint webView, nint config, nint action, nint features)
    {
        try { if (Owner(self) is { } owner) _ = owner.Decide(webView, action, trackNavigation: false); }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
        return 0;
    }
    [UnmanagedCallersOnly] private static void JavaScriptAlert(nint self, nint cmd, nint webView, nint message, nint frame, nint completion)
    { try { ShowJavaScriptAlert(message, frame, completion); } catch (Exception exception) { MacApplication.Report(exception.Message); } }
    [UnmanagedCallersOnly] private static void JavaScriptConfirm(nint self, nint cmd, nint webView, nint message, nint frame, nint completion)
    { try { ShowJavaScriptConfirm(message, frame, completion); } catch (Exception exception) { MacApplication.Report(exception.Message); } }
    [UnmanagedCallersOnly] private static void ProcessTerminated(nint self, nint cmd, nint webView)
    {
        try
        {
            var owner = Owner(self); var state = owner?.windows.Values.FirstOrDefault(value => value.WebView == webView);
            if (owner is null || state is null) return;
            if (!state.ProcessReloadAttempted)
            {
                state.ProcessReloadAttempted = true;
                owner.report?.Invoke("The web content process stopped unexpectedly; reloading once.");
                ObjectiveC.SendVoid(webView, ObjectiveC.Sel("reload"));
            }
            else
            {
                owner.report?.Invoke("The web content process stopped again; closing the window.");
                state.AllowClose = true;
                ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
            }
        }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
    }
    [UnmanagedCallersOnly] private static byte WindowShouldClose(nint self, nint cmd, nint window)
    {
        try
        {
            var owner = Owner(self); if (owner is null || !owner.windows.TryGetValue(window, out var state)) return 1;
            if (state.AllowClose) return 1; owner.RequestClose(state); return 0;
        }
        catch (Exception exception) { MacApplication.Report(exception.Message); return 0; }
    }
    [UnmanagedCallersOnly] private static void WindowWillClose(nint self, nint cmd, nint notification)
    {
        try { var window = ObjectiveC.Send(notification, ObjectiveC.Sel("object")); Owner(self)?.Closed(window); }
        catch (Exception exception) { MacApplication.Report(exception.Message); }
    }

    private sealed class WindowState(nint window, nint webView, nint handler, string documentId, LaunchTicket ticket)
    {
        internal nint Window { get; } = window;
        internal nint WebView { get; } = webView;
        internal nint Handler { get; } = handler;
        internal string DocumentId { get; set; } = documentId;
        internal LaunchTicket Ticket { get; } = ticket;
        internal bool Connected { get; set; }
        internal bool ClosePending { get; set; }
        internal bool AllowClose { get; set; }
        internal bool ProcessReloadAttempted { get; set; }
        internal CancellationTokenSource? CloseTimeout { get; set; }
    }
}
