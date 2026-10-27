using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MdView.App;
using MdView.Rendering;
using MdView.Serving;

if (args.Length == 2 && args[0] == "--lifecycle")
{
    return await RunLifecycleChecksAsync(Path.GetFullPath(args[1]));
}

if (args.Length == 1 && args[0] == "--toggle")
{
    return await RunToggleChecksAsync();
}

if (args.Length == 1 && args[0] == "--security")
{
    return await RunSecurityChecksAsync();
}

if (args.Length == 1 && args[0] == "--theme")
{
    return await RunThemeChecksAsync();
}

if (args.Length == 1 && args[0] == "--files")
{
    return await RunFileChecksAsync();
}

if (args.Length == 1 && args[0] == "--handoff")
{
    return await RunHandOffChecksAsync();
}

if (args.Length == 1 && args[0] == "--browser")
{
    return await RunBrowserChecksAsync();
}

if (args.Length == 1 && args[0] == "--edit")
{
    return await RunEditChecksAsync();
}

if (args.Length == 1 && args[0] == "--transport")
{
    return await RunTransportChecksAsync();
}

if (args.Length == 1 && args[0] == "--placement")
{
    return await RunPlacementChecksAsync();
}

if (args.Length == 1 && args[0] == "--frame")
{
    return await RunFrameChecksAsync();
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ServeProbe <input.md> | ServeProbe --lifecycle <input.md> | ServeProbe --toggle | ServeProbe --security | ServeProbe --theme | ServeProbe --placement | ServeProbe --frame | ServeProbe --files | ServeProbe --edit | ServeProbe --transport | ServeProbe --browser | ServeProbe --handoff");
    return 1;
}

using (var host = new DocumentHost(() => DocumentHost.Origin))
{
    var document = host.RegisterDocument(args[0]);
    Console.WriteLine($"id={document.Id}");

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    try { await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token); }
    catch (OperationCanceledException) { }
}

return 0;

static async Task<int> RunFrameChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("frame-message-validation", () =>
    {
        const string origin = "https://mdview.example";
        const string macOrigin = "mdview://app";
        Ensure(NavigationDecision.Decide(macOrigin + "/d/test", macOrigin).Action == NavigationAction.Allow,
            "The custom Mac origin was not classified as same-origin navigation.");
        foreach (var command in new[] { "minimize", "maximize", "restore", "close" })
            Ensure(WindowFrameProtocol.Parse(origin + "/d/x", JsonSerializer.Serialize(new { type = "window", command }), origin).Kind == FrameMessageKind.WindowCommand,
                $"Window command {command} was rejected.");
        var valid = "{\"type\":\"frame-colors\",\"face\":\"#C9C5B9\",\"hiOuter\":\"#FFFFFF\",\"hiInner\":\"#E7E3D9\",\"loInner\":\"#A8A499\",\"loOuter\":\"#88847A\"}";
        Ensure(WindowFrameProtocol.Parse(origin + "/", valid, origin).Kind == FrameMessageKind.FrameColors, "Valid frame colors were rejected.");
        foreach (var invalid in new[] { valid.Replace("#FFFFFF", "red"), valid.Replace("#FFFFFF", "#FFF"), valid.Replace("#FFFFFF", "#GGGGGG"), valid.Replace(",\"loOuter\":\"#88847A\"", ""), valid[..^1] + ",\"evil\":\"<script>\"}", "{\"type\":\"window\",\"command\":\"script\"}", "{\"type\":7}" })
            Ensure(WindowFrameProtocol.Parse(origin + "/", invalid, origin).Kind == FrameMessageKind.Ignore, $"Invalid message was accepted: {invalid}");
        Ensure(WindowFrameProtocol.Parse("https://attacker.example/", valid, origin).Kind == FrameMessageKind.Ignore, "Foreign source was accepted.");
        return Task.CompletedTask;
    });
    failed |= !await RunCheckAsync("frame-hit-testing", () =>
    {
        var cases = new[] { (1, 50, WindowFrameHitTest.Left), (198, 50, WindowFrameHitTest.Right), (100, 1, WindowFrameHitTest.Top),
            (100, 98, WindowFrameHitTest.Bottom), (1, 1, WindowFrameHitTest.TopLeft), (198, 1, WindowFrameHitTest.TopRight),
            (1, 98, WindowFrameHitTest.BottomLeft), (198, 98, WindowFrameHitTest.BottomRight), (100, 50, WindowFrameHitTest.Client) };
        foreach (var (x, y, expected) in cases) Ensure(WindowFrameHitTest.Test(x, y, 200, 100, 96, false) == expected, $"Hit test failed at {x},{y}.");
        foreach (var (x, y, _) in cases) Ensure(WindowFrameHitTest.Test(x, y, 200, 100, 192, true) == WindowFrameHitTest.Client, "Maximized hit test returned resize code.");
        Ensure(WindowFrameHitTest.Test(7, 30, 400, 200, 192, false) == WindowFrameHitTest.Left, "DPI-scaled band was not used.");
        return Task.CompletedTask;
    });
    failed |= !await RunCheckAsync("frame-auto-hide-insets", () =>
    {
        var work = new FrameRectangle(10, 20, 1930, 1060);
        Ensure(AutoHideTaskbarInsets.Apply(work, false, false, false, false) == work,
            "A work area without an auto-hide taskbar changed.");
        Ensure(AutoHideTaskbarInsets.Apply(work, false, false, false, true) == new FrameRectangle(10, 20, 1930, 1059),
            "Bottom auto-hide taskbar did not reserve one pixel.");
        Ensure(AutoHideTaskbarInsets.Apply(work, false, true, false, false) == new FrameRectangle(10, 21, 1930, 1060),
            "Top auto-hide taskbar did not reserve one pixel.");
        Ensure(AutoHideTaskbarInsets.Apply(work, true, false, false, false) == new FrameRectangle(11, 20, 1930, 1060),
            "Left auto-hide taskbar did not reserve one pixel.");
        Ensure(AutoHideTaskbarInsets.Apply(work, false, false, true, false) == new FrameRectangle(10, 20, 1929, 1060),
            "Right auto-hide taskbar did not reserve one pixel.");
        return Task.CompletedTask;
    });
    return failed ? 1 : 0;
}

static async Task<int> RunToggleChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("basic-toggle-both-directions", CheckBasicToggleAsync);
    failed |= !await RunCheckAsync("line-endings-bom-whitespace", CheckPreservationAsync);
    failed |= !await RunCheckAsync("indentation-and-ordered-items", CheckListFormsAsync);
    failed |= !await RunCheckAsync("stale-state-conflict", CheckStaleStateAsync);
    failed |= !await RunCheckAsync("code-block-and-prose-immunity", CheckImmunityAsync);
    failed |= !await RunCheckAsync("token-enforcement", CheckTokenEnforcementAsync);
    failed |= !await RunCheckAsync("echo-suppression-and-external-reload", CheckEchoSuppressionAsync);
    failed |= !await RunCheckAsync("one-based-line-numbers", CheckLineNumbersAsync);
    failed |= !await RunCheckAsync("uri-sanitizer-regression", CheckUriSanitizerAsync);
    return failed ? 1 : 0;
}

static async Task<int> RunTransportChecksAsync()
{
    // Reload/theme delivery through chrome.webview and page.js requires the PM's Windows black-box run.
    var failed = false;
    failed |= !await RunCheckAsync("transport-body-cap", async () =>
    {
        using var host = new DocumentHost(() => DocumentHost.Origin);
        var adapter = new InProcessRequestAdapter(host);
        var response = await adapter.HandleAsync("POST", new Uri($"{DocumentHost.Origin}/toggle"),
            DocumentHost.Origin, new byte[DocumentHost.MaximumStandardBodyBytes + 1]);
        Ensure(response.StatusCode == 413, "The adapter did not reject a body over its route cap.");
    });
    failed |= !await RunCheckAsync("transport-origin-and-headers", async () =>
    {
        var directory = CreateProbeDirectory("transport");
        try
        {
            var path = Path.Combine(directory, "page.md");
            await File.WriteAllTextAsync(path, "# page\n");
            using var host = new DocumentHost(() => DocumentHost.Origin);
            var document = host.RegisterDocument(path);
            var adapter = new InProcessRequestAdapter(host);
            var response = await adapter.HandleAsync("GET", new Uri($"{DocumentHost.Origin}/d/{document.Id}"),
                null, []);
            var headers = InProcessRequestAdapter.BuildHeaderString(response);
            Ensure(headers.Contains("Content-Security-Policy: ", StringComparison.Ordinal),
                "The transport header string omitted the CSP header.");
            var noContentHeaders = InProcessRequestAdapter.BuildHeaderString(
                new HostResponse(204, "", [], new Dictionary<string, string>()));
            Ensure(!noContentHeaders.Contains("Content-Type:", StringComparison.Ordinal),
                "The transport emitted an empty Content-Type header for a 204 response.");
            var noOrigin = await adapter.HandleAsync("POST", new Uri($"{DocumentHost.Origin}/theme"), null,
                "{}"u8.ToArray());
            Ensure(noOrigin.StatusCode == 403, "A missing Origin was not passed to the host as null.");
            var open = await adapter.HandleAsync("POST", new Uri($"{DocumentHost.Origin}/open"), null, []);
            Ensure(open.StatusCode == 404, "The in-process transport exposed the HTTP-only /open route.");
        }
        finally { TryDeleteDirectory(directory); }
    });
    failed |= !await RunCheckAsync("transport-complete-after-delivery", async () =>
    {
        await using var fixture = await ToggleFixture.CreateAsync("# exit\n");
        var exited = false;
        fixture.Server.Host.ExitRequested += () => exited = true;
        var adapter = new InProcessRequestAdapter(fixture.Server.Host);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { command = "file.exit", token = fixture.Token });
        await InProcessRequestFlow.RunAsync(
            () => adapter.HandleAsync("POST", new Uri($"{DocumentHost.Origin}/command/{fixture.Id}"),
                DocumentHost.Origin, body),
            _ => Ensure(!exited, "CompleteResponse ran before response delivery."),
            () => Ensure(!exited, "CompleteResponse ran before deferral completion."),
            fixture.Server.Host.CompleteResponse);
        Ensure(exited, "CompleteResponse did not run after response delivery.");
    });
    failed |= !await RunCheckAsync("transport-deferral-completed-when-handler-throws", async () =>
    {
        var completed = false;
        try
        {
            await InProcessRequestFlow.RunAsync(
                () => throw new InvalidOperationException("handler failed"),
                _ => { }, () => completed = true, _ => { });
        }
        catch (InvalidOperationException) { }
        Ensure(completed, "A throwing handler left the WebView2 deferral uncompleted, so the page request would hang.");
    });
    failed |= !await RunCheckAsync("transport-ui-context-delivery", async () =>
    {
        using var context = new PumpSynchronizationContext();
        var callingThread = Environment.CurrentManagedThreadId;
        await context.RunAsync(() => InProcessRequestFlow.RunAsync(
            async () =>
            {
                await Task.Delay(20);
                return new HostResponse(204, "", [], new Dictionary<string, string>());
            },
            _ => Ensure(Environment.CurrentManagedThreadId == callingThread,
                "Response delivery resumed off the calling synchronization-context thread."),
            () => Ensure(Environment.CurrentManagedThreadId == callingThread,
                "Deferral completion resumed off the calling synchronization-context thread."),
            _ => Ensure(Environment.CurrentManagedThreadId == callingThread,
                "Host completion resumed off the calling synchronization-context thread.")));
    });
    failed |= !await RunCheckAsync("transport-navigation-decisions", CheckNavigationDecisionsAsync);
    return failed ? 1 : 0;
}

static async Task CheckNavigationDecisionsAsync()
{
    var directory = CreateProbeDirectory("navigation");
    try
    {
        var documentPath = Path.Combine(directory, "drop.md");
        await File.WriteAllTextAsync(documentPath, "# dropped\n");
        var decision = NavigationDecision.Decide(new Uri(documentPath).AbsoluteUri, DocumentHost.Origin);
        Ensure(decision.Action == NavigationAction.OpenDocument &&
            string.Equals(Path.GetFullPath(decision.Target!), Path.GetFullPath(documentPath), StringComparison.Ordinal),
            "A supported existing file URI was not converted to its local path.");

        var unsupported = Path.Combine(directory, "drop.txt");
        await File.WriteAllTextAsync(unsupported, "text");
        Ensure(NavigationDecision.Decide(new Uri(unsupported).AbsoluteUri, DocumentHost.Origin).Action == NavigationAction.Ignore,
            "An unsupported file extension was accepted.");
        Ensure(NavigationDecision.Decide(new Uri(Path.Combine(directory, "missing.md")).AbsoluteUri,
            DocumentHost.Origin).Action == NavigationAction.Ignore, "A nonexistent file was accepted.");
        Ensure(NavigationDecision.Decide($"{DocumentHost.Origin}/d/id", DocumentHost.Origin).Action == NavigationAction.Allow,
            "The private origin was not allowed.");
        Ensure(NavigationDecision.Decide("https://example.com", DocumentHost.Origin).Action == NavigationAction.OpenExternal,
            "HTTPS was not external.");
        Ensure(NavigationDecision.Decide("mailto:reader@example.com", DocumentHost.Origin).Action == NavigationAction.OpenExternal,
            "mailto was not external.");
        foreach (var target in new[] { "javascript:alert(1)", "data:text/plain,x", "ms-settings:display" })
            Ensure(NavigationDecision.Decide(target, DocumentHost.Origin).Action == NavigationAction.Ignore,
                $"A forbidden scheme was accepted: {target}");
        // A file: URI with a host must be refused before any filesystem call: on Windows File.Exists on
        // \\server\share contacts that server with the user's credentials. A recording stub proves no
        // existence check happens (a real check would just say "missing" here and prove nothing).
        foreach (var target in new[] { "file://attacker.invalid/share/a.md", "file:///%5C%5Cattacker.invalid/share/a.md",
            "file://127.0.0.1/c$/a.md", "file:///%5C%5C%3F%5CUNC%5Cattacker.invalid%5Cshare%5Ca.md", "file:////attacker.invalid/share/a.md",
            "file:///%5C%3F%3F%5CUNC%5Cattacker.invalid%5Cshare%5Ca.md" })
        {
            var touched = new List<string>();
            var hostile = NavigationDecision.Decide(target, DocumentHost.Origin, path => { touched.Add(path); return true; });
            Ensure(hostile.Action == NavigationAction.Ignore && touched.Count == 0,
                $"A file URI with a host reached the filesystem or was opened: {target} (checked: {string.Join(", ", touched)})");
        }
        if (!OperatingSystem.IsWindows())
        {
            foreach (var target in new[] { "file:///net/server/share/a.md", "file:///Network/Servers/server/share/a.md" })
            {
                var touched = false;
                var remote = NavigationDecision.Decide(target, DocumentHost.Origin, _ => { touched = true; return true; });
                Ensure(remote.Action == NavigationAction.Ignore && !touched,
                    $"A macOS automount path reached the filesystem: {target}");
            }
        }
        // Repeated separators after a drive letter are a local path on Windows (C:\\\x is C:\x), not a UNC
        // smuggle; what matters is that the filesystem only ever sees the normalised local form.
        if (OperatingSystem.IsWindows())
        {
            var seen = new List<string>();
            NavigationDecision.Decide("file:///C:/%5C%5Cattacker.invalid%5Cshare%5Ca.md", DocumentHost.Origin,
                path => { seen.Add(path); return false; });
            Ensure(seen.All(path => path.Length > 2 && path[1] == ':' && !path.Contains(@"\\", StringComparison.Ordinal)),
                $"A drive-letter path reached the filesystem un-normalised: {string.Join(", ", seen)}");
        }
        foreach (var target in new[] { "https://user@mdview.example/d/id", "https://mdview.example:444/d/id" })
            Ensure(NavigationDecision.Decide(target, DocumentHost.Origin).Action != NavigationAction.Allow,
                $"An origin authority trick was allowed: {target}");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task<int> RunPlacementChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("placement-round-trip", CheckPlacementRoundTripAsync);
    failed |= !await RunCheckAsync("placement-shared-config-concurrency", CheckPlacementConcurrencyAsync);
    failed |= !await RunCheckAsync("placement-malformed-values", CheckMalformedPlacementsAsync);
    failed |= !await RunCheckAsync("placement-unreadable-config-preserved", CheckUnreadablePlacementConfigAsync);
    failed |= !await RunCheckAsync("placement-monitor-and-cascade-rules", CheckPlacementRulesAsync);
    return failed ? 1 : 0;
}

static Task CheckPlacementRoundTripAsync()
{
    var directory = CreateProbeDirectory("placement-roundtrip");
    try
    {
        var store = new WindowPlacementStore(directory);
        var expected = new SavedWindowPlacement(101, -42, 900, 650, true);
        store.Write(expected);
        Ensure(store.Read() == expected, "The saved placement did not round-trip.");
    }
    finally { TryDeleteDirectory(directory); }
    return Task.CompletedTask;
}

static async Task CheckPlacementConcurrencyAsync()
{
    var directory = CreateProbeDirectory("placement-concurrency");
    try
    {
        var configPath = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(configPath,
            "{\"theme\":\"light\",\"unknown\":{\"path\":\"C:\\\\tools\\\\brave.exe\",\"values\":[1,true,null]}}");
        var theme = new ThemeConfigStore(directory);
        var placement = new WindowPlacementStore(directory);
        var expected = new SavedWindowPlacement(40, 50, 800, 600, false);
        var themeWrites = Task.Run(() => { for (var i = 0; i < 50; i++) theme.WriteTheme(ThemePreference.Dark); });
        var placementWrites = Task.Run(() => { for (var i = 0; i < 50; i++) placement.Write(expected); });
        await Task.WhenAll(themeWrites, placementWrites);
        Ensure(theme.ReadTheme() == ThemePreference.Dark, "The interleaved theme write was lost.");
        Ensure(placement.Read() == expected, "The interleaved placement write was lost.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var unknown = json.RootElement.GetProperty("unknown");
        Ensure(unknown.GetProperty("path").GetString() == "C:\\tools\\brave.exe" &&
            unknown.GetProperty("values")[1].GetBoolean(), "An unknown nested config value changed.");

        theme.WriteTheme(ThemePreference.Light);
        Ensure(placement.Read() == expected, "A later theme write removed the placement.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckMalformedPlacementsAsync()
{
    var directory = CreateProbeDirectory("placement-malformed");
    try
    {
        var configPath = Path.Combine(directory, "config.json");
        var invalid = new[]
        {
            "null", "[]", "{\"coordinateSpace\":\"screen\",\"x\":1}",
            "{\"coordinateSpace\":\"screen\",\"x\":1,\"y\":2,\"width\":\"800\",\"height\":600,\"maximized\":false}",
            "{\"coordinateSpace\":\"screen\",\"x\":1,\"y\":2,\"width\":-800,\"height\":600,\"maximized\":false}",
            "{\"coordinateSpace\":\"screen\",\"x\":1,\"y\":2,\"width\":100001,\"height\":600,\"maximized\":false}",
            "{\"coordinateSpace\":\"screen\",\"x\":1,\"y\":2,\"width\":800,\"height\":600,\"maximized\":\"yes\"}"
        };
        foreach (var window in invalid)
        {
            await File.WriteAllTextAsync(configPath, $"{{\"theme\":\"dark\",\"window\":{window}}}");
            Ensure(new WindowPlacementStore(directory).Read() is null, $"Malformed placement was accepted: {window}");
        }
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckUnreadablePlacementConfigAsync()
{
    var directory = CreateProbeDirectory("placement-unreadable");
    try
    {
        var configPath = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(configPath, "{\"theme\":\"dark\",\"unknown\":{\"kept\":true}}");
        using (var held = new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            new WindowPlacementStore(directory).Write(new SavedWindowPlacement(20, 30, 800, 600, false));
        }
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        Ensure(json.RootElement.GetProperty("theme").GetString() == "dark" &&
            json.RootElement.GetProperty("unknown").GetProperty("kept").GetBoolean(),
            "An unreadable config was replaced by a placement write.");
        Ensure(!json.RootElement.TryGetProperty("window", out _),
            "A placement write unexpectedly succeeded while config.json was exclusively held.");
    }
    finally { TryDeleteDirectory(directory); }
}

static Task CheckPlacementRulesAsync()
{
    var placement = new SavedWindowPlacement(10, 20, 800, 600, false);
    Ensure(WindowPlacementRules.CanRestore(placement, (_, _, _, _) => true),
        "A valid placement intersecting a monitor was rejected.");
    Ensure(!WindowPlacementRules.CanRestore(placement, (_, _, _, _) => false),
        "An off-monitor placement was accepted.");
    Ensure(!WindowPlacementRules.CanRestore(placement with { Width = 299 }, (_, _, _, _) => true),
        "A placement below the minimum size was accepted.");
    Ensure(!WindowPlacementRules.TryApply(() => throw new InvalidOperationException("injected apply failure")),
        "A failing cosmetic placement apply escaped its best-effort boundary.");
    var topTaskbar = WindowPlacementRules.WorkspaceToScreen(100, 80, 0, 0, 0, 40);
    Ensure(topTaskbar == (100, 120), "Top-taskbar workspace coordinates were not converted to screen coordinates.");
    var secondaryLeftTaskbar = WindowPlacementRules.WorkspaceToScreen(-1800, 50, -1920, 0, -1880, 0);
    Ensure(secondaryLeftTaskbar == (-1760, 50),
        "Negative secondary-monitor workspace coordinates were not converted with its left work-area offset.");
    var cascaded = WindowPlacementRules.Cascade(placement, [(10, 20), (42, 52)], 32, 0, 0, 1000, 800);
    Ensure(cascaded.X == 74 && cascaded.Y == 84, "The cascade did not skip occupied top-left positions.");
    var clamped = WindowPlacementRules.Cascade(placement with { X = 900, Y = 700 }, [(900, 700)], 32,
        0, 0, 1000, 800);
    Ensure(clamped.X == 200 && clamped.Y == 200, "The cascade was not kept inside the monitor work area.");
    var edgeOccupied = WindowPlacementRules.Cascade(placement with { X = 200, Y = 200 }, [(200, 200)], 32,
        0, 0, 1000, 800);
    Ensure(edgeOccupied != placement with { X = 200, Y = 200 },
        "A clamped cascade ended on an occupied bottom-right position.");
    return Task.CompletedTask;
}

static async Task<int> RunSecurityChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("security-event-handlers", CheckEventHandlerVectorsAsync);
    failed |= !await RunCheckAsync("security-attribute-allowlist", CheckAttributeAllowlistAsync);
    failed |= !await RunCheckAsync("security-uri-regression", CheckSecurityUrisAsync);
    failed |= !await RunCheckAsync("security-csp-and-functionality", CheckCspAndFunctionalityAsync);
    return failed ? 1 : 0;
}

static async Task<int> RunThemeChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("theme-config-store", CheckThemeConfigStoreAsync);
    failed |= !await RunCheckAsync("theme-valid-values-and-preservation", CheckThemeValuesAsync);
    failed |= !await RunCheckAsync("theme-invalid-values", CheckInvalidThemesAsync);
    failed |= !await RunCheckAsync("theme-token-enforcement", CheckThemeTokensAsync);
    failed |= !await RunCheckAsync("theme-corrupt-config", CheckCorruptThemeConfigAsync);
    failed |= !await RunCheckAsync("theme-cross-window-sse", CheckThemeBroadcastAsync);
    return failed ? 1 : 0;
}

static async Task<int> RunFileChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("files-command-allowlist", CheckFileCommandAllowlistAsync);
    failed |= !await RunCheckAsync("files-command-security", CheckFileCommandSecurityAsync);
    failed |= !await RunCheckAsync("files-save-as-bytes-and-switch", CheckSaveAsBytesAsync);
    failed |= !await RunCheckAsync("files-new-and-cancel", CheckNewAndCancelAsync);
    failed |= !await RunCheckAsync("files-modal-lifecycle-hold", CheckModalLifecycleHoldAsync);
    return failed ? 1 : 0;
}

static async Task<int> RunHandOffChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("handoff-pipe-name-fits-socket-path", () =>
    {
        // macOS caps a domain-socket path at 104 bytes (sun_path), and .NET puts the pipe at
        // $TMPDIR/CoreFxPipe_<name>. A long user name must not push the real name past it.
        var name = HandOffPipeName.Current();
        if (!OperatingSystem.IsWindows())
        {
            Ensure(!name.Contains(Environment.UserName, StringComparison.Ordinal) || Environment.UserName.Length < 4,
                $"The pipe name {name} embeds the user name, so its length is unbounded.");
            var socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);
            Ensure(Encoding.UTF8.GetByteCount(socketPath) <= 103, $"The socket path is {socketPath.Length} bytes: {socketPath}");
        }
        return Task.CompletedTask;
    });
    if (!OperatingSystem.IsWindows())
        failed |= !await RunCheckAsync("handoff-stale-socket-replaced", CheckHandOffStaleSocketAsync);
    failed |= !await RunCheckAsync("handoff-valid-and-rejections", CheckHandOffValidationAsync);
    failed |= !await RunCheckAsync("handoff-oversize-unread", CheckHandOffOversizeAsync);
    failed |= !await RunCheckAsync("handoff-truncated-loop-survives", CheckHandOffTruncatedAsync);
    failed |= !await RunCheckAsync("handoff-slow-client-loop-survives", CheckHandOffSlowAsync);
    failed |= !await RunCheckAsync("handoff-concurrent-clients", CheckHandOffConcurrencyAsync);
    failed |= !await RunCheckAsync("handoff-create-failure-retried", CheckHandOffCreationRetryAsync);
    failed |= !await RunCheckAsync("handoff-persistent-failure-reported-once", CheckHandOffPersistentFailureAsync);
    return failed ? 1 : 0;
}

// On Linux named pipes are implemented with Unix-domain sockets; this exercises concurrency but cannot
// reproduce Windows' named-pipe instance lifetime semantics. The PM also runs concurrent launches on Windows.
static async Task CheckHandOffConcurrencyAsync()
{
    var directory = CreateProbeDirectory("handoff-concurrent");
    try
    {
        var paths = Enumerable.Range(0, 10).Select(index => Path.Combine(directory, $"file-{index}.md")).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, $"# {Path.GetFileName(path)}\n");
        using var host = new DocumentHost(() => DocumentHost.Origin);
        var opened = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        host.DocumentOpened += document => opened.AddOrUpdate(document.Path, 1, (_, count) => count + 1);
        var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
        using var server = new HandOffPipeServer(host, pipeName);
        var results = await Task.WhenAll(paths.Select(path => HandOffPipeClient.TrySendAsync(path, pipeName,
            TimeSpan.FromSeconds(2))));
        Ensure(results.All(result => result), "At least one concurrent hand-off was not accepted.");
        Ensure(paths.All(path => opened.TryGetValue(path, out var count) && count == 1),
            "A distinct concurrent document was not opened exactly once.");
        Ensure(await HandOffPipeClient.TrySendAsync(paths[0], pipeName, TimeSpan.FromSeconds(2)),
            "The accept loop did not serve a client after the concurrent batch.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckHandOffCreationRetryAsync()
{
    var directory = CreateProbeDirectory("handoff-retry");
    try
    {
        var path = Path.Combine(directory, "valid.md");
        await File.WriteAllTextAsync(path, "# valid\n");
        using var host = new DocumentHost(() => DocumentHost.Origin);
        var attempts = 0;
        NamedPipeServerStream Factory(string name, PipeOptions options)
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("injected create failure");
            return new NamedPipeServerStream(name, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
        }
        var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
        var reports = 0;
        using var server = new HandOffPipeServer(host, pipeName, pipeFactory: Factory,
            reportError: _ => Interlocked.Increment(ref reports));
        Ensure(await HandOffPipeClient.TrySendAsync(path, pipeName, TimeSpan.FromSeconds(2)),
            "The server did not recover from an injected creation failure.");
        Ensure(attempts >= 2, "The injected creation failure was not retried.");
        await Task.Delay(300);
        Ensure(reports == 0, "A creation failure that cleared at once put a warning box in front of the user.");
    }
    finally { TryDeleteDirectory(directory); }
}

// A hand-off outage that persists is reported once, and not before it has lasted a few seconds.
static async Task CheckHandOffPersistentFailureAsync()
{
    using var host = new DocumentHost(() => DocumentHost.Origin);
    var reports = 0;
    NamedPipeServerStream AlwaysFails(string name, PipeOptions options) => throw new IOException("injected outage");
    using var server = new HandOffPipeServer(host, $"mdview-p-{Guid.NewGuid():N}"[..21], pipeFactory: AlwaysFails,
        reportError: _ => Interlocked.Increment(ref reports));
    await Task.Delay(TimeSpan.FromSeconds(2));
    Ensure(reports == 0, "An outage was reported after 2 s, before it could be told apart from a routine blip.");
    await Task.Delay(TimeSpan.FromSeconds(5));
    Ensure(reports == 1, $"A persistent outage produced {reports} warnings, not exactly one.");
}

static async Task CheckHandOffStaleSocketAsync()
{
    // A previous instance that exited without disposing its server leaves the socket file behind.
    var directory = CreateProbeDirectory("handoff-stale");
    try
    {
        var path = Path.Combine(directory, "valid.md");
        await File.WriteAllTextAsync(path, "# valid\n");
        var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
        var socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);
        // .NET unlinks a socket's file when the socket is disposed, so bind elsewhere and move the file
        // into place: what remains is exactly what a killed process leaves.
        using (var stale = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                   System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
        {
            stale.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath + "-bind"));
            File.Move(socketPath + "-bind", socketPath);
        }
        Ensure(File.Exists(socketPath), "The probe could not leave a stale socket file.");
        using var host = new DocumentHost(() => DocumentHost.Origin);
        var opened = new TaskCompletionSource<RegisteredDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.DocumentOpened += document => opened.TrySetResult(document);
        using var server = new HandOffPipeServer(host, pipeName, TimeSpan.FromMilliseconds(400));
        Ensure(await HandOffPipeClient.TrySendAsync(path, pipeName), "Hand-off failed with a stale socket file at the pipe path.");
        Ensure((await opened.Task.WaitAsync(TimeSpan.FromSeconds(1))).Path == path, "DocumentOpened had the wrong path.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckHandOffValidationAsync()
{
    var directory = CreateProbeDirectory("handoff");
    try
    {
        var path = Path.Combine(directory, "valid.md");
        var unsupported = Path.Combine(directory, "unsupported.txt");
        await File.WriteAllTextAsync(path, "# valid\n");
        await File.WriteAllTextAsync(unsupported, "text\n");
        using var host = new DocumentHost(() => DocumentHost.Origin);
        var opened = new TaskCompletionSource<RegisteredDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.DocumentOpened += document => opened.TrySetResult(document);
        var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
        using var server = new HandOffPipeServer(host, pipeName, TimeSpan.FromMilliseconds(400));
        Ensure(await HandOffPipeClient.TrySendAsync(path, pipeName), "A valid path was rejected.");
        Ensure((await opened.Task.WaitAsync(TimeSpan.FromSeconds(1))).Path == path, "DocumentOpened had the wrong path.");
        Ensure(!await HandOffPipeClient.TrySendAsync(Path.Combine(directory, "missing.md"), pipeName),
            "A nonexistent path was accepted.");
        Ensure(!await HandOffPipeClient.TrySendAsync(unsupported, pipeName), "An unsupported extension was accepted.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckHandOffOversizeAsync()
{
    using var host = new DocumentHost(() => DocumentHost.Origin);
    var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
    using var server = new HandOffPipeServer(host, pipeName, TimeSpan.FromSeconds(2));
    using var client = await ConnectPipeAsync(pipeName);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, HandOffPipeServer.MaximumPathBytes + 1);
    await client.WriteAsync(header);
    var reply = new byte[1];
    Ensure(await client.ReadAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 1 && reply[0] == 0,
        "An oversize declaration was not rejected without reading its payload.");
}

static async Task CheckHandOffTruncatedAsync()
{
    var directory = CreateProbeDirectory("handoff-truncated");
    try
    {
        var path = Path.Combine(directory, "valid.md");
        await File.WriteAllTextAsync(path, "# valid\n");
        using var host = new DocumentHost(() => DocumentHost.Origin);
        var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
        using var server = new HandOffPipeServer(host, pipeName, TimeSpan.FromMilliseconds(300));
        using (var client = await ConnectPipeAsync(pipeName))
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 10);
            await client.WriteAsync(header);
            await client.WriteAsync("short"u8.ToArray());
        }
        Ensure(await HandOffPipeClient.TrySendAsync(path, pipeName, TimeSpan.FromSeconds(1)),
            "A truncated request killed the accept loop.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckHandOffSlowAsync()
{
    var directory = CreateProbeDirectory("handoff-slow");
    try
    {
        var path = Path.Combine(directory, "valid.md");
        await File.WriteAllTextAsync(path, "# valid\n");
        using var host = new DocumentHost(() => DocumentHost.Origin);
        var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
        using var server = new HandOffPipeServer(host, pipeName, TimeSpan.FromMilliseconds(200));
        using var slow = await ConnectPipeAsync(pipeName);
        await Task.Delay(350);
        Ensure(await HandOffPipeClient.TrySendAsync(path, pipeName, TimeSpan.FromSeconds(1)),
            "A slow client killed or permanently occupied the accept loop.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task<NamedPipeClientStream> ConnectPipeAsync(string pipeName)
{
    var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
    await client.ConnectAsync(1000);
    return client;
}

static async Task<int> RunBrowserChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("browser-order-brave-chrome-edge", CheckBrowserOrderAsync);
    failed |= !await RunCheckAsync("browser-full-search-before-next", CheckBrowserFullSearchAsync);
    failed |= !await RunCheckAsync("browser-stale-registration-skipped", CheckBrowserStaleRegistrationAsync);
    failed |= !await RunCheckAsync("browser-configured-path", CheckConfiguredBrowserAsync);
    return failed ? 1 : 0;
}

static string[] ProbeRoots() => [@"C:\Program Files", @"C:\Program Files (x86)", @"C:\Users\u\AppData\Local"];

static string Installed(string root, string browser) => browser switch
{
    "Brave" => Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
    "Chrome" => Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"),
    _ => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"),
};

static BrowserMatch? FindWith(IEnumerable<string> files, IDictionary<string, string>? registered = null)
{
    var present = new HashSet<string>(files);
    return BrowserDiscovery.Find(name => registered?.TryGetValue(name, out var path) == true ? path : null,
        present.Contains, ProbeRoots());
}

static Task CheckBrowserOrderAsync()
{
    string[] all = [Installed(ProbeRoots()[0], "Brave"), Installed(ProbeRoots()[0], "Chrome"), Installed(ProbeRoots()[1], "Edge")];
    Ensure(FindWith(all)?.Name == "Brave", "Brave was not preferred when all three are installed.");
    Ensure(FindWith(all[1..])?.Name == "Chrome", "Chrome was not chosen without Brave.");
    Ensure(FindWith(all[2..]) is { Name: "Edge" } edge && edge.Path == all[2], "Edge was not chosen without Brave and Chrome.");
    Ensure(FindWith([]) is null, "A browser was reported when none is installed.");
    return Task.CompletedTask;
}

static Task CheckBrowserFullSearchAsync()
{
    // A per-user Brave (Local AppData, searched last) must still beat a system-wide Chrome.
    var userBrave = Installed(ProbeRoots()[2], "Brave");
    Ensure(FindWith([Installed(ProbeRoots()[0], "Chrome"), userBrave])?.Path == userBrave,
        "A system Chrome beat a per-user Brave: browsers are not searched one at a time.");
    // The App Paths registration wins over folder guessing for the same browser.
    Ensure(FindWith([@"D:\Portable\chrome.exe", Installed(ProbeRoots()[0], "Chrome")],
        new Dictionary<string, string> { ["chrome.exe"] = @"D:\Portable\chrome.exe" })?.Path == @"D:\Portable\chrome.exe",
        "A registered browser path was not preferred over the default install folder.");
    return Task.CompletedTask;
}

static Task CheckBrowserStaleRegistrationAsync()
{
    // An uninstalled Brave can leave its registration behind; fall through to Chrome.
    var chrome = Installed(ProbeRoots()[0], "Chrome");
    Ensure(FindWith([chrome], new Dictionary<string, string> { ["brave.exe"] = @"C:\Gone\brave.exe" })?.Path == chrome,
        "A registration pointing at a missing file stopped the search.");
    return Task.CompletedTask;
}

static Task CheckConfiguredBrowserAsync()
{
    Ensure(BrowserDiscovery.ConfiguredPath("""{"browserPath":"C:\\Edge\\msedge.exe"}""") == @"C:\Edge\msedge.exe", "browserPath was not read.");
    Ensure(BrowserDiscovery.ConfiguredPath("""{"bravePath":"C:\\Old\\brave.exe"}""") == @"C:\Old\brave.exe", "The older bravePath setting stopped working.");
    Ensure(BrowserDiscovery.ConfiguredPath("""{"bravePath":"C:\\b.exe","browserPath":"C:\\a.exe"}""") == @"C:\a.exe", "browserPath did not take precedence over bravePath.");
    Ensure(BrowserDiscovery.ConfiguredPath("""{"theme":"dark","browserPath":"  "}""") is null, "A blank or absent setting was treated as a browser choice.");
    Ensure(BrowserDiscovery.ConfiguredPath("[]") is null, "A non-object config was treated as a browser choice.");
    var threw = false;
    try { BrowserDiscovery.ConfiguredPath("{not json"); } catch (JsonException) { threw = true; }
    Ensure(threw, "Malformed config did not raise JsonException, which the launcher relies on to ignore it.");
    return Task.CompletedTask;
}

static async Task<int> RunEditChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("edit-begin-returns-exact-source", CheckEditBeginAsync);
    failed |= !await RunCheckAsync("edit-save-preserves-untouched-bytes", CheckEditSavePreservationAsync);
    failed |= !await RunCheckAsync("edit-conflict-and-force", CheckEditConflictAsync);
    failed |= !await RunCheckAsync("edit-token-and-origin", CheckEditAuthorizationAsync);
    failed |= !await RunCheckAsync("edit-refuses-non-utf8", CheckEditEncodingAsync);
    failed |= !await RunCheckAsync("edit-rejects-unpaired-surrogate", CheckEditSurrogateAsync);
    failed |= !await RunCheckAsync("edit-echo-suppression-and-external-reload", CheckEditEchoAsync);
    failed |= !await RunCheckAsync("edit-render-is-sanitized-and-writes-nothing", CheckEditRenderAsync);
    failed |= !await RunCheckAsync("edit-after-toggle-refreshes-ranges", CheckEditAfterToggleAsync);
    return failed ? 1 : 0;
}

static string Sha256Hex(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

static (int Start, int End)[] Ranges(string html) =>
    Regex.Matches(html, @"data-md-start=""(?<start>\d+)"" data-md-end=""(?<end>\d+)""")
        .Select(match => (int.Parse(match.Groups["start"].Value), int.Parse(match.Groups["end"].Value))).ToArray();

static async Task CheckEditBeginAsync()
{
    var bytes = Encoding.UTF8.GetBytes("# Title\r\n\r\nBody — ünïcödé 🙂 text.\r\n");
    await using var fixture = await ToggleFixture.CreateAsync(bytes);
    Ensure(fixture.Html.Contains($"window.mdviewEdit={{hash:\"{Sha256Hex(bytes)}\"}};", StringComparison.Ordinal),
        "The page did not carry the hash of the bytes it was rendered from.");
    var (status, body) = await fixture.EditAsync("source", new { });
    EnsureStatus(status, HttpStatusCode.OK);
    Ensure(body!.RootElement.GetProperty("text").GetString() == Encoding.UTF8.GetString(bytes), "Edit source was not the file's exact text.");
    Ensure(body.RootElement.GetProperty("hash").GetString() == Sha256Hex(bytes), "Edit source hash did not match the file.");
    Ensure(body.RootElement.GetProperty("newline").GetString() == "\r\n", "A CRLF file was not reported as CRLF.");
    var text = body.RootElement.GetProperty("text").GetString()!;
    var ranges = Ranges(body.RootElement.GetProperty("html").GetString()!);
    Ensure(ranges.Length == 2 && text[ranges[0].Start..ranges[0].End] == "# Title" &&
        text[ranges[1].Start..ranges[1].End] == "Body — ünïcödé 🙂 text.",
        "Edit ranges did not index the returned text (UTF-16 offsets across non-ASCII and a surrogate pair).");
}

static async Task CheckEditSavePreservationAsync()
{
    // BOM, CRLF, a trailing-space hard break and a tab-indented block around the edited paragraph.
    var bom = Encoding.UTF8.GetPreamble();
    var original = "# Keep  \r\n\r\nEdit me.\r\n\r\n\tcode\tkept   \r\nno final newline";
    await using var fixture = await ToggleFixture.CreateAsync([.. bom, .. Encoding.UTF8.GetBytes(original)]);
    var (_, begin) = await fixture.EditAsync("source", new { });
    var text = begin!.RootElement.GetProperty("text").GetString()!;
    var hash = begin.RootElement.GetProperty("hash").GetString();
    var paragraph = Ranges(begin.RootElement.GetProperty("html").GetString()!)
        .Single(range => text[range.Start..range.End] == "Edit me.");
    // What edit.js sends: the textarea's LF text converted to the file's CRLF, spliced in.
    var edited = text[..paragraph.Start] + "Edited,\r\nacross two lines." + text[paragraph.End..];
    var (status, saved) = await fixture.EditAsync("save", new { text = edited, hash, force = false });
    EnsureStatus(status, HttpStatusCode.OK);
    var after = File.ReadAllBytes(fixture.Path);
    var expected = (byte[])[.. bom, .. Encoding.UTF8.GetBytes(original.Replace("Edit me.", "Edited,\r\nacross two lines."))];
    Ensure(after.AsSpan().SequenceEqual(expected), "Saved bytes differed from the original outside the edited block.");
    EnsureLineEndings(after, expectedCrlf: 6, expectedBareLf: 0);
    Ensure(saved!.RootElement.GetProperty("hash").GetString() == Sha256Hex(after), "Save returned a hash that is not the file's.");

    // A second save chains from the hash the first one returned.
    var (again, _) = await fixture.EditAsync("save", new { text = edited + "\r\n", hash = saved.RootElement.GetProperty("hash").GetString(), force = false });
    EnsureStatus(again, HttpStatusCode.OK);
}

static async Task CheckEditConflictAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("original\n");
    var (_, begin) = await fixture.EditAsync("source", new { });
    var hash = begin!.RootElement.GetProperty("hash").GetString();
    await File.WriteAllTextAsync(fixture.Path, "changed by another program\n", new UTF8Encoding(false));
    var (status, _) = await fixture.EditAsync("save", new { text = "mine\n", hash, force = false });
    EnsureStatus(status, HttpStatusCode.Conflict);
    EnsureBytes(fixture.Path, "changed by another program\n");
    (status, _) = await fixture.EditAsync("save", new { text = "mine\n", hash = (string?)null, force = false });
    EnsureStatus(status, HttpStatusCode.BadRequest);
    EnsureBytes(fixture.Path, "changed by another program\n");
    (status, _) = await fixture.EditAsync("save", new { text = "mine\n", hash, force = true });
    EnsureStatus(status, HttpStatusCode.OK);
    EnsureBytes(fixture.Path, "mine\n");

    File.Delete(fixture.Path);
    (status, _) = await fixture.EditAsync("save", new { text = "resurrected\n", hash, force = false });
    EnsureStatus(status, HttpStatusCode.Conflict);
    Ensure(!File.Exists(fixture.Path), "An unforced save recreated a file deleted underneath the editor.");
}

static async Task CheckEditAuthorizationAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("protected\n");
    var (_, begin) = await fixture.EditAsync("source", new { });
    var hash = begin!.RootElement.GetProperty("hash").GetString();
    foreach (var route in new[] { "source", "render", "save" })
    {
        var body = new { text = "pwned\n", hash, force = true };
        EnsureStatus((await fixture.EditAsync(route, body, token: null)).Status, HttpStatusCode.Forbidden);
        EnsureStatus((await fixture.EditAsync(route, body, token: "wrong-token")).Status, HttpStatusCode.Forbidden);
        EnsureStatus((await fixture.EditAsync(route, body, origin: null)).Status, HttpStatusCode.Forbidden);
        EnsureStatus((await fixture.EditAsync(route, body, origin: "https://evil.example")).Status, HttpStatusCode.Forbidden);
        EnsureStatus((await fixture.EditAsync(route, body, documentId: "not-a-registered-id")).Status, HttpStatusCode.NotFound);
    }
    EnsureBytes(fixture.Path, "protected\n");
    EnsureStatus((await fixture.EditAsync("save", new { text = "allowed\n", hash, force = false })).Status, HttpStatusCode.OK);
    EnsureBytes(fixture.Path, "allowed\n");
}

static async Task CheckEditEncodingAsync()
{
    byte[][] unsafeFiles =
    [
        [0x63, 0x61, 0x66, 0xE9, 0x0A],                                   // Windows-1252 "café"
        [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("utf-16 text\n")],      // UTF-16 LE with BOM
    ];
    foreach (var bytes in unsafeFiles)
    {
        await using var fixture = await ToggleFixture.CreateAsync(bytes);
        EnsureStatus((await fixture.EditAsync("source", new { })).Status, HttpStatusCode.UnsupportedMediaType);
        var (status, _) = await fixture.EditAsync("save", new { text = "replaced\n", hash = Sha256Hex(bytes), force = false });
        EnsureStatus(status, HttpStatusCode.UnsupportedMediaType);
        (status, _) = await fixture.EditAsync("save", new { text = "replaced\n", hash = Sha256Hex(bytes), force = true });
        EnsureStatus(status, HttpStatusCode.UnsupportedMediaType);
        Ensure(File.ReadAllBytes(fixture.Path).AsSpan().SequenceEqual(bytes), "A non-UTF-8 file was rewritten.");
    }
}

static async Task CheckEditSurrogateAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("text\n");
    var bytes = File.ReadAllBytes(fixture.Path);
    var (status, _) = await fixture.EditRawAsync("save", $$"""{"token":{{JsonSerializer.Serialize(fixture.Token)}},"text":"bad \ud800 half","hash":"{{Sha256Hex(bytes)}}","force":false}""");
    EnsureStatus(status, HttpStatusCode.BadRequest);
    Ensure(File.ReadAllBytes(fixture.Path).AsSpan().SequenceEqual(bytes), "An unencodable save modified the file.");
}

static async Task CheckEditEchoAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("watched\n");
    await using var events = await fixture.ConnectEventsAsync();
    var (_, begin) = await fixture.EditAsync("source", new { });
    EnsureStatus((await fixture.EditAsync("save", new { text = "saved by the editor\n", hash = begin!.RootElement.GetProperty("hash").GetString(), force = false })).Status, HttpStatusCode.OK);
    Ensure(!await events.HasReloadAsync(TimeSpan.FromSeconds(2)), "Saving produced an echo reload event.");
    await File.AppendAllTextAsync(fixture.Path, "external edit\n", new UTF8Encoding(false));
    Ensure(await events.HasReloadAsync(TimeSpan.FromSeconds(8)), "An external write after a save did not produce a reload event.");
}

static async Task CheckEditRenderAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("safe\n");
    var before = File.ReadAllBytes(fixture.Path);
    const string hostile = "![x](missing.png){onerror=alert(1)}\n\n[a](javascript:alert(1))\n\n<img src=x onerror=alert(1)>\n\n" +
        "<p data-md-start=\"0\" data-md-end=\"9\">forged</p>\n\n:::menu-bar\nx\n:::\n";
    var (status, body) = await fixture.EditAsync("render", new { text = hostile });
    EnsureStatus(status, HttpStatusCode.OK);
    var html = body!.RootElement.GetProperty("html").GetString()!;
    var tags = GetOpeningTags(html);
    Ensure(!tags.Any(tag => Regex.IsMatch(tag, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase)), "Rendered edit text carried an event handler.");
    Ensure(!tags.Any(tag => Regex.IsMatch(tag, @"(?:href|src)\s*=\s*[""']?javascript:", RegexOptions.IgnoreCase)), "Rendered edit text carried a javascript: URI.");
    Ensure(Ranges(html).Length == 5, "Document text forged or suppressed an edit range.");
    Ensure(File.ReadAllBytes(fixture.Path).AsSpan().SequenceEqual(before), "Rendering unsaved text wrote to the file.");
}

static async Task CheckEditAfterToggleAsync()
{
    // A checkbox toggle rewrites the file without reloading the page, so the page's hash is
    // stale. Entering edit mode must hand back the new text and hash, or the first save 409s.
    await using var fixture = await ToggleFixture.CreateAsync("- [ ] task\n\nafter\n");
    EnsureStatus(await fixture.ToggleAsync(1, false), HttpStatusCode.NoContent);
    var (_, begin) = await fixture.EditAsync("source", new { });
    Ensure(begin!.RootElement.GetProperty("text").GetString() == "- [x] task\n\nafter\n", "Edit mode began from pre-toggle text.");
    Ensure(!fixture.Html.Contains(begin.RootElement.GetProperty("hash").GetString()!, StringComparison.Ordinal),
        "The page's hash was not stale after a toggle; this check is vacuous.");
    EnsureStatus((await fixture.EditAsync("save", new { text = "- [x] task\n\nafter, edited\n", hash = begin.RootElement.GetProperty("hash").GetString(), force = false })).Status,
        HttpStatusCode.OK);
    EnsureBytes(fixture.Path, "- [x] task\n\nafter, edited\n");
}

static async Task CheckFileCommandAllowlistAsync()
{
    var directory = CreateProbeDirectory("file-allowlist");
    try
    {
        var openPath = Path.Combine(directory, "opened.md");
        var savePath = Path.Combine(directory, "saved.md");
        var newPath = Path.Combine(directory, "new.md");
        await File.WriteAllTextAsync(openPath, "# Opened\n");
        var dialogs = new StubFileDialogs();
        dialogs.OpenResults.Enqueue(openPath);
        dialogs.SaveResults.Enqueue(savePath);
        dialogs.SaveResults.Enqueue(newPath);
        await using var fixture = await ToggleFixture.CreateAsync("# Current\n", fileDialogs: dialogs);
        var opened = 0;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Host.DocumentOpened += _ => opened++;
        fixture.Server.Host.ExitRequested += () => exited.TrySetResult();

        EnsureStatus((await fixture.CommandAsync("file.open")).StatusCode, HttpStatusCode.NoContent);
        var save = await fixture.CommandAsync("file.save-as");
        EnsureStatus(save.StatusCode, HttpStatusCode.OK);
        Ensure(!string.IsNullOrEmpty(save.DocumentId), "Save As did not return a document ID.");
        EnsureStatus((await fixture.CommandAsync("file.new")).StatusCode, HttpStatusCode.NoContent);
        EnsureStatus((await fixture.CommandAsync("file.exit")).StatusCode, HttpStatusCode.NoContent);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(opened == 2, "Open and New did not use the document-open path.");
        Ensure(dialogs.Calls.All(call => call.Directory == Path.GetDirectoryName(fixture.Path)),
            "A dialog did not default to the current document directory.");
        Ensure(dialogs.Calls.Count(call => call.Name == Path.GetFileName(fixture.Path)) == 1 &&
            dialogs.Calls.Count(call => call.Name == "Untitled.md") == 1,
            "Save As or New did not receive the expected suggested filename.");

        var callsBeforeInvalid = dialogs.TotalCalls;
        EnsureStatus((await fixture.CommandAsync("file.delete")).StatusCode, HttpStatusCode.BadRequest);
        EnsureStatus((await fixture.CommandAsync("")).StatusCode, HttpStatusCode.BadRequest);
        Ensure(dialogs.TotalCalls == callsBeforeInvalid, "Invalid command reached the dialog provider.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckFileCommandSecurityAsync()
{
    var dialogs = new StubFileDialogs();
    await using var fixture = await ToggleFixture.CreateAsync("# Protected\n", fileDialogs: dialogs);
    EnsureStatus((await fixture.CommandAsync("file.open", token: null)).StatusCode, HttpStatusCode.Forbidden);
    EnsureStatus((await fixture.CommandAsync("file.open", token: "wrong")).StatusCode, HttpStatusCode.Forbidden);
    EnsureStatus((await fixture.CommandAsync("file.open", origin: null)).StatusCode, HttpStatusCode.Forbidden);
    EnsureStatus((await fixture.CommandAsync("file.open", origin: "http://127.0.0.1:1")).StatusCode, HttpStatusCode.Forbidden);
    Ensure(dialogs.TotalCalls == 0, "Rejected request reached the dialog provider.");
    dialogs.OpenResults.Enqueue(null);
    EnsureStatus((await fixture.CommandAsync("file.open")).StatusCode, HttpStatusCode.NoContent);
    Ensure(dialogs.TotalCalls == 1, "Valid command did not reach the dialog provider.");

    var injectedPath = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "request-controlled.md");
    dialogs.SaveResults.Enqueue(null);
    var injectedBody = JsonSerializer.Serialize(new
    {
        command = "file.new",
        token = fixture.Token,
        path = injectedPath,
        content = "request-controlled"
    });
    EnsureStatus((await fixture.CommandRawAsync(injectedBody)).StatusCode, HttpStatusCode.NoContent);
    Ensure(!File.Exists(injectedPath), "The command request was able to supply a filesystem path or content.");
}

static async Task CheckSaveAsBytesAsync()
{
    var variants = new[]
    {
        Encoding.UTF8.GetBytes("a\r\n- [ ] CRLF  \t\r\n"),
        Encoding.UTF8.GetBytes("a\n- [ ] LF  \t\n"),
        new byte[] { 0xEF, 0xBB, 0xBF, (byte)'#', (byte)' ', (byte)'B', (byte)'O', (byte)'M', (byte)'\n' }
    };
    foreach (var bytes in variants)
    {
        var destinationDirectory = CreateProbeDirectory("save-bytes");
        try
        {
            var destinationWithoutExtension = Path.Combine(destinationDirectory, "copy");
            var destination = destinationWithoutExtension + ".md";
            var dialogs = new StubFileDialogs();
            dialogs.SaveResults.Enqueue(destinationWithoutExtension);
            await using var fixture = await ToggleFixture.CreateAsync(bytes, fileDialogs: dialogs);
            var response = await fixture.CommandAsync("file.save-as");
            EnsureStatus(response.StatusCode, HttpStatusCode.OK);
            Ensure(File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes), "Save As changed source bytes.");
            Ensure(!string.IsNullOrEmpty(response.DocumentId) && response.DocumentId != fixture.Id,
                "Save As did not switch to a new document ID.");
            Ensure(await fixture.FetchDocumentStatusAsync(response.DocumentId!) == HttpStatusCode.OK,
                "Saved document ID was not registered.");
        }
        finally { TryDeleteDirectory(destinationDirectory); }
    }
}

static async Task CheckNewAndCancelAsync()
{
    var directory = CreateProbeDirectory("new-cancel");
    try
    {
        var newWithoutExtension = Path.Combine(directory, "empty-note");
        var dialogs = new StubFileDialogs();
        dialogs.SaveResults.Enqueue(newWithoutExtension);
        await using var fixture = await ToggleFixture.CreateAsync("# Existing\n", fileDialogs: dialogs);
        RegisteredDocument? opened = null;
        fixture.Server.Host.DocumentOpened += document => opened = document;
        EnsureStatus((await fixture.CommandAsync("file.new")).StatusCode, HttpStatusCode.NoContent);
        Ensure(File.Exists(newWithoutExtension + ".md") && new FileInfo(newWithoutExtension + ".md").Length == 0,
            "New did not create an empty .md file.");
        Ensure(opened?.Path == newWithoutExtension + ".md", "New did not open the created document.");

        var callsBeforeCancel = dialogs.TotalCalls;
        dialogs.OpenResults.Enqueue(null);
        dialogs.SaveResults.Enqueue(null);
        dialogs.SaveResults.Enqueue(null);
        EnsureStatus((await fixture.CommandAsync("file.open")).StatusCode, HttpStatusCode.NoContent);
        EnsureStatus((await fixture.CommandAsync("file.save-as")).StatusCode, HttpStatusCode.NoContent);
        EnsureStatus((await fixture.CommandAsync("file.new")).StatusCode, HttpStatusCode.NoContent);
        Ensure(dialogs.TotalCalls == callsBeforeCancel + 3, "Cancel commands were not dispatched exactly once.");
    }
    finally { TryDeleteDirectory(directory); }
}

static async Task CheckModalLifecycleHoldAsync()
{
    var dialogs = new StubFileDialogs { Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
    dialogs.SaveResults.Enqueue(null);
    await using var fixture = await ToggleFixture.CreateAsync("# Long dialog\n", fileDialogs: dialogs);
    var stateDirectory = CreateProbeDirectory("modal-lifecycle");
    using var coordinator = new InstanceCoordinator(stateDirectory);
    var launcher = new StubBrowserLauncher();
    var signals = new StubLifecycleSignals(fixture.Server.Host, launcher);
    using var lifecycle = new Lifecycle(fixture.Server.Host, new NoOpDisposable(), signals, coordinator, launcher,
        TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3));
    signals.Connect();
    var events = await fixture.ConnectEventsAsync();
    var command = fixture.CommandAsync("file.save-as");
    await dialogs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    _ = await fixture.FetchHtmlAsync();
    await events.DisposeAsync();
    signals.Disconnect();
    await Task.Delay(TimeSpan.FromMilliseconds(800));
    Ensure(!lifecycle.Completion.IsCompleted, "Lifecycle shut down while a simulated long dialog was open.");
    dialogs.Block.TrySetResult();
    EnsureStatus((await command).StatusCode, HttpStatusCode.NoContent);
    await lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    TryDeleteDirectory(stateDirectory);
}

static async Task CheckThemeConfigStoreAsync()
{
    var stateDirectory = CreateProbeDirectory("theme-store");
    try
    {
        var store = new ThemeConfigStore(stateDirectory);
        const string bravePath = @"C:\Tools\Brave\brave.exe";
        await File.WriteAllTextAsync(store.ConfigPath,
            JsonSerializer.Serialize(new { bravePath, unknown = "keep", theme = "light" }));
        Ensure(store.ReadTheme() == ThemePreference.Light, "Config store did not read light.");
        store.WriteTheme(ThemePreference.Dark);
        using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(store.ConfigPath)))
        {
            Ensure(config.RootElement.GetProperty("bravePath").GetString() == bravePath, "Config store changed bravePath.");
            Ensure(config.RootElement.GetProperty("unknown").GetString() == "keep", "Config store removed an unknown key.");
            Ensure(config.RootElement.GetProperty("theme").GetString() == "dark", "Config store did not write dark.");
        }
        await File.WriteAllTextAsync(store.ConfigPath, "{\"theme\":");
        Ensure(store.ReadTheme() == ThemePreference.System, "Corrupt config did not fall back to system.");
        foreach (var wrongType in new[] { "{\"theme\":42}", "{\"theme\":{}}", "[]" })
        {
            await File.WriteAllTextAsync(store.ConfigPath, wrongType);
            Ensure(store.ReadTheme() == ThemePreference.System, "Wrong-typed config did not fall back to system.");
        }
    }
    finally { TryDeleteDirectory(stateDirectory); }
}

static async Task CheckThemeValuesAsync()
{
    var stateDirectory = CreateProbeDirectory("theme-state");
    try
    {
        var store = new ThemeConfigStore(stateDirectory);
        const string bravePath = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
        await File.WriteAllTextAsync(store.ConfigPath,
            JsonSerializer.Serialize(new { bravePath, futureKey = new { retained = true }, theme = "system" }));
        await using var fixture = await ToggleFixture.CreateAsync("# Theme\n", store);
        foreach (var theme in new[] { "system", "light", "dark" })
        {
            EnsureStatus(await fixture.ThemeAsync(theme), HttpStatusCode.NoContent);
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(store.ConfigPath));
            Ensure(config.RootElement.GetProperty("theme").GetString() == theme, $"Config did not store {theme}.");
            Ensure(config.RootElement.GetProperty("bravePath").GetString() == bravePath, "Theme write changed bravePath.");
            Ensure(config.RootElement.GetProperty("futureKey").GetProperty("retained").GetBoolean(),
                "Theme write removed an unknown config key.");
            AssertRenderedTheme(await fixture.FetchHtmlAsync(), theme);
        }
    }
    finally { TryDeleteDirectory(stateDirectory); }
}

static async Task CheckInvalidThemesAsync()
{
    var stateDirectory = CreateProbeDirectory("theme-invalid");
    try
    {
        var store = new ThemeConfigStore(stateDirectory);
        const string original = "{\"bravePath\":\"unchanged\",\"theme\":\"system\"}";
        await File.WriteAllTextAsync(store.ConfigPath, original);
        await using var fixture = await ToggleFixture.CreateAsync("# Invalid themes\n", store);
        foreach (var invalid in new string?[] { "blue", "", null, new string('x', 10_000) })
        {
            EnsureStatus(await fixture.ThemeAsync(invalid), HttpStatusCode.BadRequest);
            Ensure(await File.ReadAllTextAsync(store.ConfigPath) == original, "Invalid theme modified config.json.");
        }
        var objectValue = $"{{\"theme\":{{}},\"token\":{JsonSerializer.Serialize(fixture.Token)}}}";
        EnsureStatus(await fixture.ThemeRawAsync(objectValue), HttpStatusCode.BadRequest);
        Ensure(await File.ReadAllTextAsync(store.ConfigPath) == original, "Object theme modified config.json.");
    }
    finally { TryDeleteDirectory(stateDirectory); }
}

static async Task CheckThemeTokensAsync()
{
    var stateDirectory = CreateProbeDirectory("theme-token");
    try
    {
        var store = new ThemeConfigStore(stateDirectory);
        const string original = "{\"theme\":\"system\"}";
        await File.WriteAllTextAsync(store.ConfigPath, original);
        await using var fixture = await ToggleFixture.CreateAsync("# Tokens\n", store);
        EnsureStatus(await fixture.ThemeAsync("dark", token: null), HttpStatusCode.Forbidden);
        EnsureStatus(await fixture.ThemeAsync("dark", token: "wrong-token"), HttpStatusCode.Forbidden);
        EnsureStatus(await fixture.ThemeAsync("dark", origin: null), HttpStatusCode.Forbidden);
        EnsureStatus(await fixture.ThemeAsync("dark", origin: "http://127.0.0.1:1"), HttpStatusCode.Forbidden);
        Ensure(await File.ReadAllTextAsync(store.ConfigPath) == original, "Rejected token modified config.json.");
        EnsureStatus(await fixture.ThemeAsync("dark"), HttpStatusCode.NoContent);
        Ensure(JsonDocument.Parse(await File.ReadAllTextAsync(store.ConfigPath)).RootElement
            .GetProperty("theme").GetString() == "dark", "Valid token did not update theme.");
    }
    finally { TryDeleteDirectory(stateDirectory); }
}

static async Task CheckCorruptThemeConfigAsync()
{
    var stateDirectory = CreateProbeDirectory("theme-corrupt");
    try
    {
        var store = new ThemeConfigStore(stateDirectory);
        await File.WriteAllTextAsync(store.ConfigPath, "{");
        await using var fixture = await ToggleFixture.CreateAsync("# Corrupt\n", store);
        AssertRenderedTheme(fixture.Html, "system");
        foreach (var corrupt in new[] { "{\"theme\":42}", "{\"theme\":{}}", "[]" })
        {
            await File.WriteAllTextAsync(store.ConfigPath, corrupt);
            AssertRenderedTheme(await fixture.FetchHtmlAsync(), "system");
        }
    }
    finally { TryDeleteDirectory(stateDirectory); }
}

static async Task CheckThemeBroadcastAsync()
{
    var stateDirectory = CreateProbeDirectory("theme-sse");
    try
    {
        var store = new ThemeConfigStore(stateDirectory);
        await using var fixture = await ToggleFixture.CreateAsync("# Windows\n", store);
        await using var firstWindow = await fixture.ConnectEventsAsync();
        await using var secondWindow = await fixture.ConnectEventsAsync();
        EnsureStatus(await fixture.ThemeAsync("light"), HttpStatusCode.NoContent);
        Ensure(await firstWindow.WaitForEventAsync("theme", TimeSpan.FromSeconds(5)) == "light",
            "First window did not receive the theme event.");
        Ensure(await secondWindow.WaitForEventAsync("theme", TimeSpan.FromSeconds(5)) == "light",
            "Second window did not receive the theme event.");
    }
    finally { TryDeleteDirectory(stateDirectory); }
}

static void AssertRenderedTheme(string html, string theme)
{
    var root = GetOpeningTags(html).Single(tag => tag.StartsWith("<html", StringComparison.OrdinalIgnoreCase));
    var hasTheme = Regex.Match(root, "\\sdata-theme=\\\"(?<theme>[^\\\"]+)\\\"", RegexOptions.CultureInvariant);
    if (theme == "system") Ensure(!hasTheme.Success, "System theme rendered a data-theme attribute.");
    else Ensure(hasTheme.Success && hasTheme.Groups["theme"].Value == theme, $"Rendered theme was not {theme}.");
    var checkedCommands = GetOpeningTags(html)
        .Where(tag => tag.Contains("data-command=\"theme.", StringComparison.Ordinal) &&
            tag.Contains("aria-checked=\"true\"", StringComparison.Ordinal)).ToArray();
    Ensure(checkedCommands.Length == 1 && checkedCommands[0].Contains($"data-command=\"theme.{theme}\"", StringComparison.Ordinal),
        "Rendered theme menu did not have exactly one matching checked item.");
}

static string CreateProbeDirectory(string prefix)
{
    var path = Path.Combine(Path.GetTempPath(), $"mdview-{prefix}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    return path;
}

static void TryDeleteDirectory(string path)
{
    try { Directory.Delete(path, true); } catch (IOException) { }
}

static Task CheckEventHandlerVectorsAsync()
{
    const string markdown = """
        ![x](missing.png){onerror="alert(1)"}
        [link](https://example.com){onclick="alert(2)"}
        ## Heading {onmouseover="alert(3)"}
        ```csharp {onmouseover="alert(9)"}
        Console.WriteLine("safe");
        ```
        """;
    var html = Renderer.RenderDocument(markdown, "vectors");
    var openingTags = string.Join('\n', GetOpeningTags(html));
    Ensure(!Regex.IsMatch(openingTags, @"\s+on[a-z0-9_-]*\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        "Rendered HTML retained an event-handler attribute.");
    return Task.CompletedTask;
}

static Task CheckAttributeAllowlistAsync()
{
    const string hostile = "<img\n src=\"safe.png\" alt=\"x\" STYLE=\"color:red\" srcset=\"evil 2x\" formaction=\"https://evil\" onerror=\"alert(1)\" data-unknown=\"x\">";
    var sanitized = UriSanitizer.SanitizeHtml(hostile);
    Ensure(sanitized.Contains("src=\"safe.png\"", StringComparison.Ordinal) &&
        sanitized.Contains("alt=\"x\"", StringComparison.Ordinal), "Allowlisted attributes were removed.");
    foreach (var forbidden in new[] { "style=", "srcset=", "formaction=", "onerror=", "data-unknown=" })
        Ensure(!sanitized.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Sanitizer retained {forbidden}");
    return Task.CompletedTask;
}

static Task CheckSecurityUrisAsync()
{
    const string markdown = """
        [http](http://example.com) [https](https://example.com) [mail](mailto:test@example.com) [file](file:///tmp/a) [relative](docs/readme.md)
        ![data](data:image/png;base64,AA==)
        [js](javascript:alert(1)) [vb](vbscript:msgbox(1)) [encoded](&#x6a;avascript:alert(1)) ![html](data:text/html;base64,PHNjcmlwdD4=)
        """;
    var html = Renderer.RenderDocument(markdown, "uris");
    foreach (var safe in new[] { "http://example.com", "https://example.com", "mailto:test@example.com", "file:///tmp/a", "docs/readme.md", "data:image/png;base64,AA==" })
        Ensure(html.Contains(safe, StringComparison.Ordinal), $"Safe URI was removed: {safe}");
    var openingTags = string.Join('\n', GetOpeningTags(html));
    Ensure(!Regex.IsMatch(openingTags, "(?:href|src)\\s*=\\s*[\\\"']?(?:javascript|vbscript|data:text/html):",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "Dangerous URI survived in an active attribute.");
    return Task.CompletedTask;
}

static async Task CheckCspAndFunctionalityAsync()
{
    const string probeNonce = "probe-nonce";
    EnsureExecutableTagsHaveNonce(
        Renderer.RenderDocument(DocumentKind.Markdown, "# CSP probe", "probe", cspNonce: probeNonce), probeNonce);
    await using var fixture = await ToggleFixture.CreateAsync("- [ ] secured\n");
    var policy = fixture.ContentSecurityPolicy ?? throw new InvalidOperationException("CSP response header was missing.");
    Ensure(!policy.Contains("'unsafe-inline'", StringComparison.OrdinalIgnoreCase), "CSP permits unsafe inline script.");
    Ensure(Regex.IsMatch(policy, @"(?:^|;\s*)img-src\s+'self'\s+data:\s+https:(?:;|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "CSP img-src did not allow self, data, and HTTPS images.");
    var nonceMatch = Regex.Match(policy, @"(?:^|;\s*)script-src\s+'nonce-(?<nonce>[^']+)'(?:;|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    Ensure(nonceMatch.Success, "script-src did not contain exactly a nonce source.");
    var nonce = nonceMatch.Groups["nonce"].Value;
    var metaMatch = Regex.Match(fixture.Html, "<meta\\s+http-equiv=\\\"Content-Security-Policy\\\"\\s+content=\\\"(?<policy>[^\\\"]+)\\\">",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    Ensure(metaMatch.Success && metaMatch.Groups["policy"].Value == policy, "Meta CSP did not exactly match the response header.");
    EnsureExecutableTagsHaveNonce(fixture.Html, nonce);
    Ensure(fixture.Html.Contains("hljs.highlightAll()", StringComparison.Ordinal), "Highlight initialization was missing.");
    var secondPolicy = await fixture.FetchContentSecurityPolicyAsync();
    Ensure(secondPolicy is not null, "CSP response header was missing on a subsequent response.");
    Ensure(!string.Equals(secondPolicy, policy, StringComparison.Ordinal), "CSP nonce was reused across responses.");

    await using var events = await fixture.ConnectEventsAsync();
    EnsureStatus(await fixture.ToggleAsync(1, false), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "- [x] secured\n");
    Ensure(!await events.HasReloadAsync(TimeSpan.FromSeconds(2)), "Own toggle produced a reload under CSP.");
    await File.AppendAllTextAsync(fixture.Path, "external\n", new UTF8Encoding(false));
    Ensure(await events.HasReloadAsync(TimeSpan.FromSeconds(8)), "SSE did not deliver an external reload under CSP.");
}

static async Task<bool> RunCheckAsync(string name, Func<Task> check)
{
    try
    {
        await check();
        Console.WriteLine($"{name}=passed");
        return true;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"{name}=failed detail={SingleLine(exception.Message)}");
        return false;
    }
}

static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

static void EnsureExecutableTagsHaveNonce(string html, string nonce)
{
    var executableTags = GetOpeningTags(html)
        .Where(tag => Regex.IsMatch(tag, @"^<(?:script|style)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .ToArray();
    Ensure(executableTags.Length > 0, "Rendered document contained no script or style elements.");
    Ensure(executableTags.All(tag => Regex.IsMatch(tag, $"\\snonce=\\\"{Regex.Escape(nonce)}\\\"(?:\\s|>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)), "An inline script or style did not carry the CSP nonce.");
}

static IReadOnlyList<string> GetOpeningTags(string html)
{
    var tags = new List<string>();
    var position = 0;
    while (position < html.Length)
    {
        var start = html.IndexOf('<', position);
        if (start < 0 || start + 1 >= html.Length) break;
        if (!char.IsAsciiLetter(html[start + 1]))
        {
            position = start + 1;
            continue;
        }

        var nameEnd = start + 2;
        while (nameEnd < html.Length && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] is '-' or ':')) nameEnd++;
        var name = html[(start + 1)..nameEnd];
        var quote = '\0';
        var end = nameEnd;
        for (; end < html.Length; end++)
        {
            var character = html[end];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"') quote = character;
            else if (character == '>') break;
        }
        if (end >= html.Length) throw new InvalidOperationException($"Unterminated <{name}> element in rendered HTML.");

        tags.Add(html[start..(end + 1)]);
        position = end + 1;
        if (!name.Equals("script", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("style", StringComparison.OrdinalIgnoreCase)) continue;

        var closingStart = html.IndexOf($"</{name}", position, StringComparison.OrdinalIgnoreCase);
        if (closingStart < 0) throw new InvalidOperationException($"Rendered <{name}> element had no closing tag.");
        var closingEnd = html.IndexOf('>', closingStart);
        if (closingEnd < 0) throw new InvalidOperationException($"Unterminated </{name}> element in rendered HTML.");
        position = closingEnd + 1;
    }
    return tags;
}

static async Task CheckBasicToggleAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("- [ ] basic\n");
    EnsureStatus(await fixture.ToggleAsync(1, false), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "- [x] basic\n");
    EnsureStatus(await fixture.ToggleAsync(1, true), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "- [ ] basic\n");
}

static async Task CheckPreservationAsync()
{
    var bom = Encoding.UTF8.GetPreamble();
    var crlfBody = Encoding.UTF8.GetBytes("heading\r\n- [ ] preserve me  \t\r\nlast\r\n");
    await using (var fixture = await ToggleFixture.CreateAsync([.. bom, .. crlfBody]))
    {
        var before = File.ReadAllBytes(fixture.Path);
        EnsureLineEndings(before, expectedCrlf: 3, expectedBareLf: 0);
        EnsureStatus(await fixture.ToggleAsync(2, false), HttpStatusCode.NoContent);
        var after = File.ReadAllBytes(fixture.Path);
        Ensure(after.AsSpan(0, bom.Length).SequenceEqual(bom), "UTF-8 BOM was not preserved.");
        EnsureLineEndings(after, expectedCrlf: 3, expectedBareLf: 0);
        Ensure(after.AsSpan().SequenceEqual(ReplaceAscii(before, "[ ]", "[x]")),
            "CRLF/BOM fixture changed bytes other than the task marker.");
    }

    await using (var fixture = await ToggleFixture.CreateAsync("heading\n- [ ] preserve me  \t\nlast\n"))
    {
        var before = File.ReadAllBytes(fixture.Path);
        EnsureLineEndings(before, expectedCrlf: 0, expectedBareLf: 3);
        EnsureStatus(await fixture.ToggleAsync(2, false), HttpStatusCode.NoContent);
        var after = File.ReadAllBytes(fixture.Path);
        EnsureLineEndings(after, expectedCrlf: 0, expectedBareLf: 3);
        Ensure(after.AsSpan().SequenceEqual(ReplaceAscii(before, "[ ]", "[x]")),
            "LF fixture changed bytes other than the task marker.");
    }
}

static async Task CheckListFormsAsync()
{
    const string source = "- parent\n  - [ ] nested  \n1. [ ] ordered \t\n";
    await using var fixture = await ToggleFixture.CreateAsync(source);
    EnsureStatus(await fixture.ToggleAsync(2, false), HttpStatusCode.NoContent);
    EnsureStatus(await fixture.ToggleAsync(3, false), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "- parent\n  - [x] nested  \n1. [x] ordered \t\n");
}

static async Task CheckStaleStateAsync()
{
    const string source = "- [x] already checked\n";
    await using var fixture = await ToggleFixture.CreateAsync(source);
    var before = File.ReadAllBytes(fixture.Path);
    EnsureStatus(await fixture.ToggleAsync(1, false), HttpStatusCode.Conflict);
    Ensure(File.ReadAllBytes(fixture.Path).AsSpan().SequenceEqual(before), "Stale request modified the file.");
    EnsureStatus(await fixture.ToggleAsync(1, true), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "- [ ] already checked\n");
}

static async Task CheckImmunityAsync()
{
    const string source = "```text\n- [ ] fenced\n```\nordinary [ ] prose\n- [ ] legitimate\n";
    await using var fixture = await ToggleFixture.CreateAsync(source);
    Ensure(!fixture.Html.Contains("data-line=\"2\"", StringComparison.Ordinal) &&
        !fixture.Html.Contains("data-line=\"4\"", StringComparison.Ordinal),
        "Fenced code or prose rendered as a clickable input.");
    Ensure(fixture.Html.Contains("data-line=\"5\"", StringComparison.Ordinal),
        "Legitimate task item did not render as a clickable input.");
    var before = File.ReadAllBytes(fixture.Path);
    EnsureStatus(await fixture.ToggleAsync(2, false), HttpStatusCode.Conflict);
    EnsureStatus(await fixture.ToggleAsync(4, false), HttpStatusCode.Conflict);
    Ensure(File.ReadAllBytes(fixture.Path).AsSpan().SequenceEqual(before), "Hostile request modified a non-task line.");
    EnsureStatus(await fixture.ToggleAsync(5, false), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "```text\n- [ ] fenced\n```\nordinary [ ] prose\n- [x] legitimate\n");
}

static async Task CheckTokenEnforcementAsync()
{
    const string source = "- [ ] protected\n";
    await using var fixture = await ToggleFixture.CreateAsync(source);
    var before = File.ReadAllBytes(fixture.Path);
    EnsureStatus(await fixture.ToggleAsync(1, false, token: null), HttpStatusCode.Forbidden);
    EnsureStatus(await fixture.ToggleAsync(1, false, token: "wrong-token"), HttpStatusCode.Forbidden);
    Ensure(File.ReadAllBytes(fixture.Path).AsSpan().SequenceEqual(before), "Rejected token request modified the file.");
    EnsureStatus(await fixture.ToggleAsync(1, false), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "- [x] protected\n");
}

static async Task CheckEchoSuppressionAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("- [ ] watched\n");
    await using var events = await fixture.ConnectEventsAsync();
    EnsureStatus(await fixture.ToggleAsync(1, false), HttpStatusCode.NoContent);
    Ensure(!await events.HasReloadAsync(TimeSpan.FromSeconds(2)), "Toggle produced an echo reload event.");

    await File.AppendAllTextAsync(fixture.Path, "external edit\n", new UTF8Encoding(false));
    Ensure(await events.HasReloadAsync(TimeSpan.FromSeconds(8)), "External write did not produce a reload event.");
}

static Task CheckLineNumbersAsync()
{
    const string source = "heading\n\n- [ ] third line\n- [ ] fourth line\n";
    var html = Renderer.RenderDocument(DocumentKind.Markdown, source, "lines", "document", "token");
    Ensure(html.Contains("data-line=\"3\"", StringComparison.Ordinal), "Renderer did not emit 1-based line 3.");
    Ensure(html.Contains("data-line=\"4\"", StringComparison.Ordinal), "Renderer did not emit 1-based line 4.");
    return CheckLineNumberEndpointAsync(source);
}

static async Task CheckLineNumberEndpointAsync(string source)
{
    await using var fixture = await ToggleFixture.CreateAsync(source);
    EnsureStatus(await fixture.ToggleAsync(3, false), HttpStatusCode.NoContent);
    EnsureBytes(fixture.Path, "heading\n\n- [x] third line\n- [ ] fourth line\n");
}

static Task CheckUriSanitizerAsync()
{
    var html = Renderer.RenderDocument("[safe](https://example.com) [bad](javascript:alert(1))", "uris");
    Ensure(html.Contains("href=\"https://example.com\"", StringComparison.Ordinal), "Safe URI was removed.");
    Ensure(!Regex.IsMatch(string.Join('\n', GetOpeningTags(html)), "(?:href|src)\\s*=\\s*[\\\"']?javascript:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        "Unsafe javascript URI survived in an HTML attribute.");
    return Task.CompletedTask;
}

static void EnsureStatus(HttpStatusCode actual, HttpStatusCode expected) =>
    Ensure(actual == expected, $"Expected HTTP {(int)expected}, received {(int)actual}.");

static void EnsureBytes(string path, string expected) =>
    Ensure(File.ReadAllBytes(path).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)), "File bytes did not match expected content.");

static void EnsureLineEndings(byte[] bytes, int expectedCrlf, int expectedBareLf)
{
    var crlf = 0;
    var bareLf = 0;
    for (var i = 0; i < bytes.Length; i++)
    {
        if (bytes[i] != (byte)'\n') continue;
        if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++; else bareLf++;
    }
    Ensure(crlf == expectedCrlf && bareLf == expectedBareLf,
        $"Expected CRLF={expectedCrlf}/bare-LF={expectedBareLf}, got CRLF={crlf}/bare-LF={bareLf}.");
}

static byte[] ReplaceAscii(byte[] bytes, string oldValue, string newValue)
{
    var result = bytes.ToArray();
    var oldBytes = Encoding.ASCII.GetBytes(oldValue);
    var index = result.AsSpan().IndexOf(oldBytes);
    Ensure(index >= 0, $"Fixture did not contain {oldValue}.");
    Encoding.ASCII.GetBytes(newValue).CopyTo(result, index);
    return result;
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<int> RunLifecycleChecksAsync(string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }

    await CheckLastDisconnectAsync(path);
    await CheckImmediateLastDisconnectAsync(path);
    await CheckInFlightHandOffAtShutdownAsync(path);
    await CheckFaultedHandOffShutdownAsync(path);
    await CheckGraceCancellationAsync(path);
    await CheckDocumentOpenDuringGraceAsync(path);
    await CheckAbandonedExpectedWindowAsync(path);
    await CheckStartupGuardAsync(path);
    await CheckSatisfiedByExistingThenModalAsync(path);
    await CheckDoubleResolutionAsync(path);
    await CheckLaunchTimeoutAsync(path);
    CheckOpeningDocumentGuard();
    CheckLaunchFailureClassification();
    CheckPendingLaunchSet();
    CheckHandOffRaceBecomesPrimary();
    await CheckExitWithPendingLaunchAsync(path);
    return 0;
}

static async Task CheckInFlightHandOffAtShutdownAsync(string path)
{
    var stateDirectory = CreateProbeDirectory("lifecycle-inflight");
    using var coordinator = new InstanceCoordinator(stateDirectory);
    var host = new DocumentHost(() => DocumentHost.Origin);
    var launcher = new StubBrowserLauncher();
    var signals = new StubLifecycleSignals(host, launcher);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pipeFailure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    Lifecycle? lifecycle = null;
    var pipeName = $"mdview-p-{Guid.NewGuid():N}"[..21];
    var server = new HandOffPipeServer(host, pipeName, accept: document =>
    {
        entered.TrySetResult();
        release.Task.GetAwaiter().GetResult();
        return lifecycle?.TryAcceptHandOff(document) == true;
    }, reportError: message => pipeFailure.TrySetResult(message));
    lifecycle = new Lifecycle(host, server, signals, coordinator, launcher, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    var document = host.RegisterDocument(path);
    Ensure(lifecycle.TryLaunch(document), "The in-flight fixture could not launch its initial document.");
    signals.Connect();
    var client = HandOffPipeClient.TrySendDetailedAsync(path, pipeName, TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2));
    try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    catch (TimeoutException) when (pipeFailure.Task.IsCompleted)
    {
        signals.Disconnect();
        release.TrySetResult();
        await lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine($"inflight-handoff-shutdown=skipped detail={await pipeFailure.Task}");
        lifecycle.Dispose();
        TryDeleteDirectory(stateDirectory);
        return;
    }
    signals.Disconnect();
    await WaitUntilAsync(() => lifecycle.IsShuttingDown, TimeSpan.FromSeconds(1));
    release.TrySetResult();
    Ensure(await client == HandOffResult.Rejected, "A hand-off admitted during shutdown was not rejected.");
    await lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine("inflight-handoff-shutdown=passed reply=rejected deadlock=false");
    lifecycle.Dispose();
    TryDeleteDirectory(stateDirectory);
}

static async Task CheckFaultedHandOffShutdownAsync(string path)
{
    var stateDirectory = CreateProbeDirectory("lifecycle-faulted-pipe");
    using var coordinator = new InstanceCoordinator(stateDirectory);
    var host = new DocumentHost(() => DocumentHost.Origin);
    var server = new HandOffPipeServer(host, acceptLoopOverride: _ =>
        Task.FromException(new InvalidOperationException("injected accept-loop fault")));
    var launcher = new StubBrowserLauncher();
    var signals = new StubLifecycleSignals(host, launcher);
    using var lifecycle = new Lifecycle(host, server, signals, coordinator, launcher,
        TimeSpan.Zero, TimeSpan.FromSeconds(3));
    Ensure(lifecycle.TryLaunch(host.RegisterDocument(path)), "The faulted-pipe fixture could not launch.");
    signals.Connect();
    signals.Disconnect();
    await lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    // Completion alone proves little: Shutdown sets it in a finally. The orphan this guards against is a
    // shutdown that never released the single-instance mutex, so a later launch must be able to take it.
    var successorDirectory = CreateProbeDirectory("lifecycle-faulted-pipe-successor");
    using (var successor = new InstanceCoordinator(successorDirectory))
        Ensure(successor.IsPrimary || successor.TryBecomePrimary(),
            "Shutdown after a faulted hand-off loop left the single-instance mutex held.");
    TryDeleteDirectory(successorDirectory);
    Console.WriteLine("faulted-handoff-shutdown=passed completion=true mutex-released=true");
    TryDeleteDirectory(stateDirectory);
}

static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!predicate())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not reached before timeout.");
        await Task.Delay(10);
    }
}

static void CheckHandOffRaceBecomesPrimary()
{
    var firstDirectory = CreateProbeDirectory("race-primary");
    var secondDirectory = CreateProbeDirectory("race-secondary");
    try
    {
        using var primary = new InstanceCoordinator(firstDirectory);
        using var secondary = new InstanceCoordinator(secondDirectory);
        Ensure(primary.IsPrimary && !secondary.IsPrimary, "The race fixture did not establish primary/secondary roles.");
        primary.Dispose();
        Ensure(secondary.TryBecomePrimary(), "The secondary did not become primary after the mutex was released.");
        Console.WriteLine("mutex-handover=passed secondary-became-primary=true");
    }
    finally
    {
        TryDeleteDirectory(firstDirectory);
        TryDeleteDirectory(secondDirectory);
    }
}

static async Task CheckImmediateLastDisconnectAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    fixture.Signals.Connect();
    var started = System.Diagnostics.Stopwatch.StartNew();
    fixture.Signals.Disconnect();
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromMilliseconds(200));
    Ensure(started.Elapsed < TimeSpan.FromMilliseconds(200), "Grace-zero shutdown was not immediate.");
    Console.WriteLine($"last-disconnect-immediate=passed elapsed-ms={started.ElapsedMilliseconds}");
    fixture.Dispose();
}

// N1: re-opening an open file resolves as SatisfiedByExisting; a later modal command must not leave the
// process waiting for a window that was never going to connect.
static async Task CheckSatisfiedByExistingThenModalAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30));
    fixture.Signals.Connect();
    fixture.Signals.ExpectAdditionalWindow(fixture.Server.RegisterDocument(path));
    fixture.Launcher.Resolve(1, LaunchOutcome.SatisfiedByExisting);
    fixture.Signals.StartModal();
    fixture.Signals.CompleteModal();
    await Task.Delay(TimeSpan.FromMilliseconds(600));
    Ensure(!fixture.Lifecycle.Completion.IsCompleted, "Lifecycle exited while a window was still connected.");
    fixture.Signals.Disconnect();
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine("satisfied-by-existing-then-modal=passed exited-after-last-close=true");
    fixture.Dispose();
}

// A launch resolved twice (abandon, then a late failure) must release exactly one expectation.
static async Task CheckDoubleResolutionAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30));
    fixture.Signals.Connect();
    fixture.Signals.ExpectAdditionalWindow(fixture.Server.RegisterDocument(path));
    fixture.Signals.ExpectAdditionalWindow(fixture.Server.RegisterDocument(path));
    Ensure(fixture.Launcher.Resolve(1, LaunchOutcome.Abandoned), "First resolution was refused.");
    Ensure(!fixture.Launcher.Resolve(1, LaunchOutcome.Abandoned), "A launch resolved twice.");
    fixture.Signals.Disconnect();
    await Task.Delay(TimeSpan.FromMilliseconds(800));
    Ensure(!fixture.Lifecycle.Completion.IsCompleted,
        "A double-resolved launch released another in-flight launch's expectation.");
    fixture.Launcher.Resolve(2, LaunchOutcome.Abandoned);
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine("double-resolution=passed other-launch-held-shutdown=true");
    fixture.Dispose();
}

// A launch that never resolves on its own is abandoned by its timeout, after which the process exits.
static async Task CheckLaunchTimeoutAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1));
    fixture.Signals.Connect();
    fixture.Signals.ExpectAdditionalWindow(fixture.Server.RegisterDocument(path));
    fixture.Signals.Disconnect();
    await Task.Delay(TimeSpan.FromMilliseconds(500));
    Ensure(!fixture.Lifecycle.Completion.IsCompleted, "Exited before the pending launch timed out.");
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Ensure(fixture.Launcher.Tickets[1].Outcome == LaunchOutcome.Abandoned, "The timed-out launch was not abandoned.");
    Ensure(fixture.Launcher.Tickets[1].WhenAbandoned.IsCancellationRequested, "The launcher was not told to give up.");
    Console.WriteLine("launch-timeout=passed abandoned-then-exited=true");
    fixture.Dispose();
}

// N3: a request that finds the document already opening must not release the first request's claim.
static void CheckOpeningDocumentGuard()
{
    var guard = new OpeningDocumentGuard();
    using var first = guard.TryEnter("doc") ?? throw new InvalidOperationException("First claim refused.");
    Ensure(guard.TryEnter("doc") is null, "A second claim on an opening document was granted.");
    Ensure(guard.TryEnter("doc") is null, "A third claim was granted after a refused second claim.");
    Ensure(guard.IsOpening("doc"), "The first claim was released by a refused request.");
    first.Dispose();
    Ensure(!guard.IsOpening("doc"), "The owner's release did not free the document.");
    using var again = guard.TryEnter("doc");
    Ensure(again is not null, "The document could not be claimed after release.");
    Console.WriteLine("opening-document-guard=passed");
}

// R1: only runtime/environment/controller failures (and the first launch's timeout) offer the download prompt.
static void CheckLaunchFailureClassification()
{
    Ensure(LaunchMessages.Classify(LaunchMessages.RuntimeUnavailable) == LaunchFailurePrompt.RuntimeDownload,
        "A runtime failure did not offer the download prompt.");
    Ensure(LaunchMessages.RuntimePrompt.StartsWith(LaunchMessages.RuntimeUnavailable, StringComparison.Ordinal) &&
        LaunchMessages.RuntimePrompt.EndsWith("Open the download page?", StringComparison.Ordinal),
        "The runtime prompt text drifted from the required wording.");
    Ensure(LaunchMessages.Classify(LaunchMessages.QueuedTimeout) == LaunchFailurePrompt.Warning,
        "A queued timeout claimed the runtime was missing.");
    Ensure(LaunchMessages.Classify("mdview could not open the window: x") == LaunchFailurePrompt.Warning,
        "A non-runtime failure offered the download prompt.");
    Console.WriteLine("launch-failure-classification=passed");
}

// R2: File → Exit aborts launches still being set up.
static void CheckPendingLaunchSet()
{
    var set = new PendingLaunchSet();
    var aborted = new List<int>();
    var first = set.Register(() => aborted.Add(1));
    set.Register(() => throw new InvalidOperationException("abort failed"));
    set.Register(() => aborted.Add(3));
    var finished = set.Register(() => aborted.Add(4));
    finished.Dispose();
    first.Dispose();
    Ensure(set.Count == 2, "Disposing a registration did not remove it.");
    set.AbortAll();
    Ensure(aborted.SequenceEqual([3]), "AbortAll did not abort exactly the live registrations past a failing one.");
    var live = new PendingLaunchSet();
    var ran = false;
    live.Register(() => ran = true);
    live.AbortAll();
    Ensure(ran, "AbortAll did not run a registered abort.");
    Console.WriteLine("pending-launch-set=passed");
}

static async Task CheckExitWithPendingLaunchAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    fixture.Signals.Connect();
    fixture.Signals.ExpectAdditionalWindow(fixture.Server.RegisterDocument(path));
    Ensure(!fixture.Launcher.Tickets[1].IsResolved, "The second launch resolved before exit.");
    fixture.Signals.RequestExit();
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Ensure(fixture.Launcher.Tickets[1].Outcome == LaunchOutcome.Abandoned, "Exit left a pending launch unresolved.");
    Console.WriteLine("exit-with-pending-launch=passed pending-launch-aborted=true");
    fixture.Dispose();
}

static async Task CheckAbandonedExpectedWindowAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
    fixture.Signals.Connect();
    fixture.Signals.ExpectAdditionalWindow(fixture.Server.RegisterDocument(path));
    fixture.Signals.Abandon();
    await Task.Delay(TimeSpan.FromMilliseconds(700));
    Ensure(!fixture.Lifecycle.Completion.IsCompleted,
        "Abandoning an unconnected second window shut down while the first window was connected.");
    fixture.Signals.Disconnect();
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine("abandoned-window=passed last-connected-close-exited=true");
    fixture.Dispose();
}

static async Task CheckLastDisconnectAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
    fixture.Signals.Connect();
    fixture.Signals.Disconnect();
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine("last-disconnect=passed");
    fixture.Dispose();
}

static async Task CheckGraceCancellationAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Signals.ClientDisconnected += () => disconnected.TrySetResult();
    fixture.Signals.Connect();
    fixture.Signals.Disconnect();

    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(7));
    fixture.Signals.Connect();
    await Task.Delay(TimeSpan.FromMilliseconds(2300));
    if (fixture.Lifecycle.Completion.IsCompleted)
    {
        throw new InvalidOperationException("Lifecycle exited even though grace was cancelled by a reconnect.");
    }

    Console.WriteLine("grace-cancellation=passed process-still-running=true");
    fixture.Lifecycle.Shutdown();
    await fixture.Lifecycle.Completion;
    Console.WriteLine("grace-cancellation-cleanup=passed");
    fixture.Dispose();
}

static async Task CheckStartupGuardAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine("startup-guard=passed");
    fixture.Dispose();
}

static async Task CheckDocumentOpenDuringGraceAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Signals.ClientDisconnected += () => disconnected.TrySetResult();
    fixture.Signals.Connect();
    fixture.Signals.Disconnect();

    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(7));
    await Task.Delay(TimeSpan.FromMilliseconds(750));
    var secondPath = Path.Combine(Path.GetDirectoryName(path)!, "mdview-lifecycle-second.md");
    await File.WriteAllTextAsync(secondPath, "# Second document\n");
    RegisteredDocument? opened = null;
    fixture.Server.Host.DocumentOpened += document => opened = document;
    Ensure(fixture.Server.Host.OpenFromHandOff(secondPath), "The hand-off path was rejected.");
    var secondId = opened?.Id ?? throw new InvalidOperationException("The hand-off did not raise DocumentOpened.");

    await Task.Delay(TimeSpan.FromMilliseconds(600));
    var page = await fixture.Server.Host.HandleAsync(new HostRequest("GET", $"/d/{secondId}", null, []));
    if (fixture.Lifecycle.Completion.IsCompleted || page.StatusCode != (int)HttpStatusCode.OK)
    {
        throw new InvalidOperationException("Opening a document during grace did not keep the server alive.");
    }

    fixture.Signals.Connect();
    fixture.Signals.Disconnect();

    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine("document-open-during-grace=passed");
    fixture.Dispose();
    try { File.Delete(secondPath); } catch (IOException) { }
}

static Fixture StartFixture(string path, TimeSpan grace, TimeSpan startup)
{
    var stateDirectory = Path.Combine(Path.GetTempPath(), $"mdview-p-{Guid.NewGuid():N}"[..21]);
    var coordinator = new InstanceCoordinator(stateDirectory);
    if (!coordinator.IsPrimary)
    {
        coordinator.Dispose();
        throw new InvalidOperationException("Lifecycle probe could not acquire its single-instance mutex.");
    }

    var server = new ProbeHost();
    var document = server.RegisterDocument(path);
    var launcher = new StubBrowserLauncher();
    var signals = new StubLifecycleSignals(server.Host, launcher);
    var lifecycle = new Lifecycle(server.Host, new NoOpDisposable(), signals, coordinator, launcher, grace, startup);
    if (!lifecycle.TryLaunch(document) || launcher.Url is null ||
        !Uri.TryCreate(launcher.Url, UriKind.Absolute, out var uri) ||
        uri.GetLeftPart(UriPartial.Authority) != DocumentHost.Origin ||
        !uri.AbsolutePath.StartsWith("/d/", StringComparison.Ordinal))
    {
        lifecycle.Dispose();
        throw new InvalidOperationException("Stub launcher did not receive a valid loopback document URL.");
    }

    Console.WriteLine($"stub-url={launcher.Url}");
    return new Fixture(server, signals, lifecycle, stateDirectory, launcher.Url, launcher);
}

sealed class StubBrowserLauncher : IBrowserLauncher, IQueuedWindowLauncher
{
    internal List<LaunchTicket> Tickets { get; } = [];
    public string? Url { get; private set; }

    public bool TryLaunch(string url, out string? error)
    {
        Url = url;
        error = null;
        return true;
    }

    public bool TryLaunch(string url, LaunchTicket ticket, out string? error)
    {
        Tickets.Add(ticket);
        return TryLaunch(url, out error);
    }

    public void QueueLaunch(string url, LaunchTicket ticket, Action<string?> completed)
    {
        Tickets.Add(ticket);
        pending.Register(() => ticket.Resolve(LaunchOutcome.Abandoned));
    }

    private readonly PendingLaunchSet pending = new();

    public void RequestExit(Action shutdown)
    {
        pending.AbortAll();
        shutdown();
    }

    internal bool Resolve(int index, LaunchOutcome outcome) => Tickets[index].Resolve(outcome);

    // Mimics the real launcher: the oldest launch still waiting is the one whose window connects.
    internal void ResolveOldest(LaunchOutcome outcome) => Tickets.FirstOrDefault(ticket => !ticket.IsResolved)?.Resolve(outcome);
}

sealed class StubLifecycleSignals : ILifecycleSignals
{
    private readonly StubBrowserLauncher launcher;

    internal StubLifecycleSignals(DocumentHost host, StubBrowserLauncher launcher)
    {
        this.launcher = launcher;
        host.DocumentOpened += value => DocumentOpened?.Invoke(value);
        host.ModalCommandStarted += () => ModalCommandStarted?.Invoke();
        host.ModalCommandCompleted += () => ModalCommandCompleted?.Invoke();
        host.ExitRequested += () => ExitRequested?.Invoke();
    }

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<RegisteredDocument>? DocumentOpened;
    public event Action? ModalCommandStarted;
    public event Action? ModalCommandCompleted;
    public event Action? ExitRequested;
    internal void Connect()
    {
        ClientConnected?.Invoke();
        launcher.ResolveOldest(LaunchOutcome.Connected);
    }
    internal void Disconnect() => ClientDisconnected?.Invoke();
    internal void Abandon() => launcher.ResolveOldest(LaunchOutcome.Abandoned);
    internal void RequestExit() => ExitRequested?.Invoke();
    internal void StartModal() => ModalCommandStarted?.Invoke();
    internal void CompleteModal() => ModalCommandCompleted?.Invoke();
    internal void ExpectAdditionalWindow(RegisteredDocument document) => DocumentOpened?.Invoke(document);
}

sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> callbacks = [];

    internal async Task RunAsync(Func<Task> action)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            // Poll rather than block on Take(): when a continuation wrongly resumes off this context, no
            // callback ever arrives, and a blocking Take() would hang the suite instead of failing it.
            var task = action();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("The flow never returned to the calling synchronization context.");
                if (callbacks.TryTake(out var work, TimeSpan.FromMilliseconds(50)))
                    work.Callback(work.State);
            }
            await task;
        }
        finally { SetSynchronizationContext(previous); }
    }

    public override void Post(SendOrPostCallback callback, object? state) => callbacks.Add((callback, state));
    public void Dispose() => callbacks.Dispose();
}

sealed record Fixture(ProbeHost Server, StubLifecycleSignals Signals, Lifecycle Lifecycle,
    string StateDirectory, string Url, StubBrowserLauncher Launcher) : IDisposable
{
    public void Dispose()
    {
        Lifecycle.Dispose();
        try { Directory.Delete(StateDirectory, true); } catch (IOException) { }
    }
}

sealed class ToggleFixture : IAsyncDisposable
{
    private static readonly Regex ToggleConfiguration = new(
        "window\\.mdviewToggle=\\{id:(?<id>\\\"(?:[^\\\"\\\\]|\\\\.)*\\\"),token:(?<token>\\\"(?:[^\\\"\\\\]|\\\\.)*\\\")\\};",
        RegexOptions.CultureInvariant);

    private readonly string directory;
    private readonly ProbeHost server;

    private ToggleFixture(string directory, string path, ProbeHost server, string id, string token, string html,
        string? contentSecurityPolicy)
    {
        this.directory = directory;
        this.server = server;
        Path = path;
        Id = id;
        Token = token;
        Html = html;
        ContentSecurityPolicy = contentSecurityPolicy;
    }

    internal string Path { get; }
    internal string Id { get; }
    internal string Token { get; }
    internal string Html { get; }
    internal string? ContentSecurityPolicy { get; }
    internal ProbeHost Server => server;

    internal static Task<ToggleFixture> CreateAsync(string source, ThemeConfigStore? themeConfig = null,
        IFileDialogProvider? fileDialogs = null) =>
        CreateAsync(Encoding.UTF8.GetBytes(source), themeConfig, fileDialogs);

    internal static async Task<ToggleFixture> CreateAsync(byte[] source, ThemeConfigStore? themeConfig = null,
        IFileDialogProvider? fileDialogs = null)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mdview-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "fixture.md");
        await File.WriteAllBytesAsync(path, source);
        var server = new ProbeHost(themeConfig, fileDialogs);
        try
        {
            var document = server.RegisterDocument(path);
            var response = await server.Host.HandleAsync(new HostRequest("GET", $"/d/{document.Id}", null, []));
            var html = Encoding.UTF8.GetString(response.Body);
            var contentSecurityPolicy = response.Headers.GetValueOrDefault("Content-Security-Policy");
            var match = ToggleConfiguration.Match(html);
            if (!match.Success) throw new InvalidOperationException("Rendered document did not expose toggle configuration.");
            var renderedId = JsonSerializer.Deserialize<string>(match.Groups["id"].Value);
            var token = JsonSerializer.Deserialize<string>(match.Groups["token"].Value);
            if (renderedId != document.Id || string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Rendered toggle configuration was invalid.");
            return new ToggleFixture(directory, path, server, document.Id, token, html, contentSecurityPolicy);
        }
        catch
        {
            server.Dispose();
            try { Directory.Delete(directory, true); } catch (IOException) { }
            throw;
        }
    }

    internal async Task<HttpStatusCode> ToggleAsync(int line, bool expectedChecked, string? token = "__fixture_token__")
    {
        var effectiveToken = token == "__fixture_token__" ? Token : token;
        var body = JsonSerializer.SerializeToUtf8Bytes(new { id = Id, line, @checked = expectedChecked, token = effectiveToken });
        var response = await SendAsync("POST", "/toggle", body, DocumentHost.Origin);
        return (HttpStatusCode)response.StatusCode;
    }

    internal Task<HttpStatusCode> ThemeAsync(string? theme, string? token = "__fixture_token__",
        string? origin = "__fixture_origin__")
    {
        var effectiveToken = token == "__fixture_token__" ? Token : token;
        return ThemeRawAsync(JsonSerializer.Serialize(new { theme, token = effectiveToken }), origin);
    }

    internal async Task<HttpStatusCode> ThemeRawAsync(string json, string? origin = "__fixture_origin__")
    {
        var response = await SendAsync("POST", "/theme", Encoding.UTF8.GetBytes(json), EffectiveOrigin(origin));
        return (HttpStatusCode)response.StatusCode;
    }

    internal Task<CommandResponse> CommandAsync(string command, string? token = "__fixture_token__",
        string? origin = "__fixture_origin__")
    {
        var effectiveToken = token == "__fixture_token__" ? Token : token;
        return CommandRawAsync(JsonSerializer.Serialize(new { command, token = effectiveToken }), origin);
    }

    internal async Task<CommandResponse> CommandRawAsync(string json, string? origin = "__fixture_origin__")
    {
        var response = await SendAsync("POST", $"/command/{Id}", Encoding.UTF8.GetBytes(json), EffectiveOrigin(origin));
        string? documentId = null;
        if (response.Body.Length > 0 && response.ContentType.StartsWith("application/json", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(response.Body);
            documentId = body.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        return new CommandResponse((HttpStatusCode)response.StatusCode, documentId);
    }

    internal Task<(HttpStatusCode Status, JsonDocument? Body)> EditAsync(string route, object body,
        string? token = "__fixture_token__", string? origin = "__fixture_origin__", string? documentId = null)
    {
        var fields = JsonSerializer.SerializeToNode(body)!.AsObject();
        fields["token"] = token == "__fixture_token__" ? Token : token;
        return EditRawAsync(route, fields.ToJsonString(), origin, documentId);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument? Body)> EditRawAsync(string route, string json,
        string? origin = "__fixture_origin__", string? documentId = null)
    {
        var response = await SendAsync("POST", $"/{route}/{documentId ?? Id}", Encoding.UTF8.GetBytes(json),
            EffectiveOrigin(origin));
        var body = response.ContentType.StartsWith("application/json", StringComparison.Ordinal)
            ? JsonDocument.Parse(response.Body)
            : null;
        return ((HttpStatusCode)response.StatusCode, body);
    }

    internal Task<EventStream> ConnectEventsAsync() => Task.FromResult(new EventStream(server.Host, Id));

    internal async Task<string?> FetchContentSecurityPolicyAsync()
    {
        var response = await SendAsync("GET", $"/d/{Id}", [], null);
        return response.Headers.GetValueOrDefault("Content-Security-Policy");
    }

    internal async Task<string> FetchHtmlAsync()
    {
        var response = await SendAsync("GET", $"/d/{Id}", [], null);
        return Encoding.UTF8.GetString(response.Body);
    }

    internal async Task<HttpStatusCode> FetchDocumentStatusAsync(string id)
    {
        var response = await SendAsync("GET", $"/d/{id}", [], null);
        return (HttpStatusCode)response.StatusCode;
    }

    private string? EffectiveOrigin(string? origin) =>
        origin == "__fixture_origin__" ? DocumentHost.Origin : origin;

    private async Task<HostResponse> SendAsync(string method, string path, byte[] body, string? origin)
    {
        var response = await server.Host.HandleAsync(new HostRequest(method, path, origin, body));
        server.Host.CompleteResponse(response);
        return response;
    }

    public ValueTask DisposeAsync()
    {
        server.Dispose();
        try { Directory.Delete(directory, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

sealed class ProbeHost : IDisposable
{
    internal ProbeHost(ThemeConfigStore? themeConfig = null, IFileDialogProvider? fileDialogs = null) =>
        Host = new DocumentHost(() => DocumentHost.Origin, themeConfig: themeConfig, fileDialogs: fileDialogs);

    internal DocumentHost Host { get; }
    internal RegisteredDocument RegisterDocument(string path) => Host.RegisterDocument(path);
    public void Dispose() => Host.Dispose();
}

sealed class NoOpDisposable : IDisposable
{
    public void Dispose() { }
}

sealed record CommandResponse(HttpStatusCode StatusCode, string? DocumentId);

sealed class StubFileDialogs : IFileDialogProvider
{
    internal Queue<string?> OpenResults { get; } = new();
    internal Queue<string?> SaveResults { get; } = new();
    internal List<(string Kind, string Directory, string? Name)> Calls { get; } = new();
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource? Block { get; init; }
    internal int TotalCalls => Calls.Count;

    public async Task<string?> ShowOpenAsync(string initialDirectory, CancellationToken cancellationToken)
    {
        Calls.Add(("open", initialDirectory, null));
        Started.TrySetResult();
        if (Block is not null) await Block.Task.WaitAsync(cancellationToken);
        return OpenResults.Dequeue();
    }

    public async Task<string?> ShowSaveAsAsync(string initialDirectory, string suggestedFileName,
        CancellationToken cancellationToken)
    {
        Calls.Add(("save", initialDirectory, suggestedFileName));
        Started.TrySetResult();
        if (Block is not null) await Block.Task.WaitAsync(cancellationToken);
        return SaveResults.Dequeue();
    }
}

sealed class EventStream : IAsyncDisposable
{
    private readonly DocumentHost host;
    private readonly string documentId;
    private readonly Channel<ProbeEvent> events = Channel.CreateUnbounded<ProbeEvent>();

    internal EventStream(DocumentHost host, string documentId)
    {
        this.host = host;
        this.documentId = documentId;
        host.DocumentChanged += OnDocumentChanged;
        host.ThemeChanged += OnThemeChanged;
    }

    internal async Task<bool> HasReloadAsync(TimeSpan timeout) =>
        await WaitForEventAsync("reload", timeout) is not null;

    internal async Task<string?> WaitForEventAsync(string eventName, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (await events.Reader.WaitToReadAsync(cancellation.Token))
                while (events.Reader.TryRead(out var message))
                    if (message.Name == eventName) return message.Data;
            throw new IOException("Event source ended before the assertion completed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    private void OnDocumentChanged(string id)
    { if (id == documentId) events.Writer.TryWrite(new ProbeEvent("reload", "reload")); }

    private void OnThemeChanged(string theme) => events.Writer.TryWrite(new ProbeEvent("theme", theme));

    public ValueTask DisposeAsync()
    {
        host.DocumentChanged -= OnDocumentChanged;
        host.ThemeChanged -= OnThemeChanged;
        events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private sealed record ProbeEvent(string Name, string Data);
}
