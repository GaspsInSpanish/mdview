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
    private static readonly nint DelegateClass = RegisterDelegate();
    private readonly DocumentHost host;
    private readonly InProcessRequestAdapter adapter;
    private readonly Action<string>? report;
    private readonly SynchronizationContext uiContext;
    private readonly Dictionary<nint, WindowState> windows = [];
    private readonly Dictionary<string, WindowState> documents = new(StringComparer.Ordinal);
    private readonly HashSet<nint> stoppedTasks = [];
    private bool exiting;
    private Action? exitCompletion;

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
        uiContext.Post(_ => completed(TryLaunch(url, ticket, out var error) ? null : error), null);

    public void RequestExit(Action shutdown)
    {
        if (exiting) return;
        exiting = true;
        exitCompletion = shutdown;
        if (windows.Count == 0) { shutdown(); return; }
        foreach (var state in windows.Values.ToArray()) RequestClose(state);
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
        var webView = ObjectiveC.SendRect(ObjectiveC.Send(ObjectiveC.Class("WKWebView"), ObjectiveC.Sel("alloc")),
            ObjectiveC.Sel("initWithFrame:configuration:"), rect, configuration);
        if (webView == 0) throw new InvalidOperationException("WKWebView creation failed.");
        ObjectiveC.SendVoid(webView, ObjectiveC.Sel("setNavigationDelegate:"), handler);
        ObjectiveC.SendVoid(webView, ObjectiveC.Sel("setUIDelegate:"), handler);
        ObjectiveC.SendVoid(webView, ObjectiveC.Sel("setAutoresizingMask:"), (nint)18);
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("setContentView:"), webView);
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("setDelegate:"), handler);
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("setTitle:"), ObjectiveC.String(TitleFor(id)));
        var state = new WindowState(window, webView, handler, id, ticket);
        windows[window] = state;
        documents[id] = state;
        ObjectiveC.SendVoid(window, ObjectiveC.Sel("makeKeyAndOrderFront:"), window);
        var requestUrl = ObjectiveC.Send(ObjectiveC.Class("NSURL"), ObjectiveC.Sel("URLWithString:"), ObjectiveC.String(url));
        var request = ObjectiveC.Send(ObjectiveC.Class("NSURLRequest"), ObjectiveC.Sel("requestWithURL:"), requestUrl);
        ObjectiveC.Send(webView, ObjectiveC.Sel("loadRequest:"), request);
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
        try
        {
            var request = ObjectiveC.Send(task, ObjectiveC.Sel("request"));
            var urlObject = ObjectiveC.Send(request, ObjectiveC.Sel("URL"));
            var urlText = ObjectiveC.GetString(ObjectiveC.Send(urlObject, ObjectiveC.Sel("absoluteString")));
            var method = ObjectiveC.GetString(ObjectiveC.Send(request, ObjectiveC.Sel("HTTPMethod"))) ?? "GET";
            if (urlText is null || !Uri.TryCreate(urlText, UriKind.Absolute, out var uri)) return;
            var headers = ObjectiveC.Send(request, ObjectiveC.Sel("allHTTPHeaderFields"));
            var origin = headers == 0 ? null : ObjectiveC.GetString(ObjectiveC.Send(headers, ObjectiveC.Sel("objectForKey:"), ObjectiveC.String("Origin")));
            var data = ObjectiveC.Send(request, ObjectiveC.Sel("HTTPBody"));
            var body = data != 0 ? Bytes(data) : ReadStreamCapped(ObjectiveC.Send(request, ObjectiveC.Sel("HTTPBodyStream")),
                DocumentHost.MaximumBodyBytes(uri.AbsolutePath));
            response = await adapter.HandleAsync(method, uri, origin, body);
            if (stoppedTasks.Contains(task)) return;
            var fields = ObjectiveC.New("NSMutableDictionary");
            ObjectiveC.SendVoid(fields, ObjectiveC.Sel("setObject:forKey:"), ObjectiveC.String(response.ContentType), ObjectiveC.String("Content-Type"));
            foreach (var pair in response.Headers)
                ObjectiveC.SendVoid(fields, ObjectiveC.Sel("setObject:forKey:"), ObjectiveC.String(pair.Value), ObjectiveC.String(pair.Key));
            var nativeResponse = ObjectiveC.Send(ObjectiveC.Send(ObjectiveC.Class("NSHTTPURLResponse"), ObjectiveC.Sel("alloc")),
                ObjectiveC.Sel("initWithURL:statusCode:HTTPVersion:headerFields:"), urlObject, (nint)response.StatusCode,
                ObjectiveC.String("HTTP/1.1"), fields);
            ObjectiveC.SendVoid(task, ObjectiveC.Sel("didReceiveResponse:"), nativeResponse);
            if (response.Body.Length > 0) ObjectiveC.SendVoid(task, ObjectiveC.Sel("didReceiveData:"), Data(response.Body));
            ObjectiveC.SendVoid(task, ObjectiveC.Sel("didFinish"));
        }
        catch (Exception exception) { report?.Invoke($"A page request failed: {exception.Message}"); }
        finally
        {
            stoppedTasks.Remove(task);
            if (response is not null) host.CompleteResponse(response);
        }
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
        var jsonData = ObjectiveC.Send(ObjectiveC.Class("NSJSONSerialization"), ObjectiveC.Sel("dataWithJSONObject:options:error:"), body, 0, 0);
        var json = Encoding.UTF8.GetString(Bytes(jsonData));
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

    private bool TryHandleControlMessage(WindowState state, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)) return false;
            if (type.GetString() == "close-result")
            {
                var dirty = root.TryGetProperty("dirty", out var value) && value.ValueKind == JsonValueKind.True;
                FinishClose(state, dirty);
                return true;
            }
            if (type.GetString() == "smoke-report" && Environment.GetEnvironmentVariable("MDVIEW_SMOKE_REPORT") is { Length: > 0 } path)
            {
                File.WriteAllText(path, root.GetRawText());
                state.AllowClose = true;
                ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private void RequestClose(WindowState state)
    {
        if (state.ClosePending) return;
        state.ClosePending = true;
        Evaluate(state, "window.chrome.webview.postMessage({type:'close-result',dirty:Boolean(window.mdviewEdit&&window.mdviewEdit.dirty)})");
    }

    private void FinishClose(WindowState state, bool dirty)
    {
        if (dirty)
        {
            var alert = ObjectiveC.New("NSAlert");
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setMessageText:"), ObjectiveC.String("Discard unsaved changes?"));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("setInformativeText:"), ObjectiveC.String("Your unsaved changes will be lost."));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("addButtonWithTitle:"), ObjectiveC.String("Discard"));
            ObjectiveC.SendVoid(alert, ObjectiveC.Sel("addButtonWithTitle:"), ObjectiveC.String("Cancel"));
            if (ObjectiveC.SendLong(alert, ObjectiveC.Sel("runModal")) != 1000)
            {
                state.ClosePending = false;
                exiting = false;
                exitCompletion = null;
                return;
            }
        }
        state.AllowClose = true;
        ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
    }

    private void Closed(nint window)
    {
        if (!windows.Remove(window, out var state)) return;
        documents.Remove(state.DocumentId);
        Objects.TryRemove(state.Handler, out _);
        if (state.Connected) ClientDisconnected?.Invoke(); else state.Ticket.Resolve(LaunchOutcome.Abandoned);
        if (exiting && windows.Count == 0) exitCompletion?.Invoke();
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

    private void Decide(nint action, nint block)
    {
        var request = ObjectiveC.Send(action, ObjectiveC.Sel("request"));
        var text = ObjectiveC.GetString(ObjectiveC.Send(ObjectiveC.Send(request, ObjectiveC.Sel("URL")), ObjectiveC.Sel("absoluteString"))) ?? string.Empty;
        var decision = NavigationDecision.Decide(text, Origin);
        if (decision.Action == NavigationAction.OpenDocument && decision.Target is not null)
            host.OpenFromHandOff(decision.Target);
        else if (decision.Action == NavigationAction.OpenExternal && decision.Target is not null)
        {
            var url = ObjectiveC.Send(ObjectiveC.Class("NSURL"), ObjectiveC.Sel("URLWithString:"), ObjectiveC.String(decision.Target));
            ObjectiveC.Send(ObjectiveC.Send(ObjectiveC.Class("NSWorkspace"), ObjectiveC.Sel("sharedWorkspace")), ObjectiveC.Sel("openURL:"), url);
        }
        InvokeDecision(block, decision.Action == NavigationAction.Allow ? 1 : 0);
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

    private static unsafe nint RegisterDelegate()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        var cls = ObjectiveC.objc_allocateClassPair(ObjectiveC.Class("NSObject"), "MdViewWebDelegate", 0);
        ObjectiveC.class_addProtocol(cls, ObjectiveC.objc_getProtocol("WKURLSchemeHandler"));
        ObjectiveC.class_addProtocol(cls, ObjectiveC.objc_getProtocol("WKNavigationDelegate"));
        ObjectiveC.class_addProtocol(cls, ObjectiveC.objc_getProtocol("WKUIDelegate"));
        ObjectiveC.class_addProtocol(cls, ObjectiveC.objc_getProtocol("WKScriptMessageHandler"));
        ObjectiveC.class_addProtocol(cls, ObjectiveC.objc_getProtocol("NSWindowDelegate"));
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("webView:startURLSchemeTask:"), &StartTask, "v@:@@");
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("webView:stopURLSchemeTask:"), &StopTask, "v@:@@");
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("userContentController:didReceiveScriptMessage:"), &ReceiveMessage, "v@:@@");
        ObjectiveC.class_addMethod2(cls, ObjectiveC.Sel("webView:didFinishNavigation:"), &DidFinishNavigation, "v@:@@");
        ObjectiveC.class_addMethod3(cls, ObjectiveC.Sel("webView:didFailProvisionalNavigation:withError:"), &DidFailNavigation, "v@:@@@");
        ObjectiveC.class_addMethod3(cls, ObjectiveC.Sel("webView:decidePolicyForNavigationAction:decisionHandler:"), &DecidePolicy, "v@:@@@");
        ObjectiveC.class_addMethodPtr(cls, ObjectiveC.Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"), &CreateWebView, "@@:@@@@");
        ObjectiveC.class_addMethod1(cls, ObjectiveC.Sel("webViewWebContentProcessDidTerminate:"), &ProcessTerminated, "v@:@");
        ObjectiveC.class_addMethodBool(cls, ObjectiveC.Sel("windowShouldClose:"), &WindowShouldClose, "B@:@");
        ObjectiveC.class_addMethod1(cls, ObjectiveC.Sel("windowWillClose:"), &WindowWillClose, "v@:@");
        ObjectiveC.objc_registerClassPair(cls);
        return cls;
    }

    private static MacWindowLauncher? Owner(nint self) => Objects.TryGetValue(self, out var owner) ? owner : null;

    [UnmanagedCallersOnly] private static void StartTask(nint self, nint cmd, nint webView, nint task) => Owner(self)?.StartSchemeTask(task);
    [UnmanagedCallersOnly] private static void StopTask(nint self, nint cmd, nint webView, nint task) { if (Owner(self) is { } owner) owner.stoppedTasks.Add(task); }
    [UnmanagedCallersOnly] private static void ReceiveMessage(nint self, nint cmd, nint controller, nint message) => Owner(self)?.Message(self, message);
    [UnmanagedCallersOnly] private static void DidFinishNavigation(nint self, nint cmd, nint webView, nint navigation) => Owner(self)?.Connected(webView);
    [UnmanagedCallersOnly] private static void DidFailNavigation(nint self, nint cmd, nint webView, nint navigation, nint error)
    {
        var owner = Owner(self); var state = owner?.windows.Values.FirstOrDefault(value => value.WebView == webView);
        if (owner is null || state is null || state.Connected) return;
        state.Ticket.Resolve(LaunchOutcome.Abandoned); state.AllowClose = true;
        owner.report?.Invoke("The mdview page could not be loaded.");
        ObjectiveC.SendVoid(state.Window, ObjectiveC.Sel("performClose:"), state.Window);
    }
    [UnmanagedCallersOnly] private static void DecidePolicy(nint self, nint cmd, nint webView, nint action, nint block) => Owner(self)?.Decide(action, block);
    [UnmanagedCallersOnly] private static nint CreateWebView(nint self, nint cmd, nint webView, nint config, nint action, nint features) { Owner(self)?.Decide(action, 0); return 0; }
    [UnmanagedCallersOnly] private static void ProcessTerminated(nint self, nint cmd, nint webView)
    {
        var owner = Owner(self); var state = owner?.windows.Values.FirstOrDefault(value => value.WebView == webView);
        if (owner is null || state is null) return;
        if (!state.ProcessFailureReported) { state.ProcessFailureReported = true; owner.report?.Invoke("The web content process stopped unexpectedly; reloading."); }
        ObjectiveC.SendVoid(webView, ObjectiveC.Sel("reload"));
    }
    [UnmanagedCallersOnly] private static byte WindowShouldClose(nint self, nint cmd, nint window)
    {
        var owner = Owner(self); if (owner is null || !owner.windows.TryGetValue(window, out var state)) return 1;
        if (state.AllowClose) return 1; owner.RequestClose(state); return 0;
    }
    [UnmanagedCallersOnly] private static void WindowWillClose(nint self, nint cmd, nint notification)
    {
        var window = ObjectiveC.Send(notification, ObjectiveC.Sel("object")); Owner(self)?.Closed(window);
    }

    private sealed class WindowState(nint window, nint webView, nint handler, string documentId, LaunchTicket ticket)
    {
        internal nint Window { get; } = window;
        internal nint WebView { get; } = webView;
        internal nint Handler { get; } = handler;
        internal string DocumentId { get; } = documentId;
        internal LaunchTicket Ticket { get; } = ticket;
        internal bool Connected { get; set; }
        internal bool ClosePending { get; set; }
        internal bool AllowClose { get; set; }
        internal bool ProcessFailureReported { get; set; }
    }
}
