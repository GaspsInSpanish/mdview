using System.Collections.Concurrent;
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

if (args.Length == 1 && args[0] == "--limits")
{
    return await RunLimitChecksAsync();
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

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ServeProbe <input.md> | ServeProbe --lifecycle <input.md> | ServeProbe --toggle | ServeProbe --security | ServeProbe --theme | ServeProbe --files | ServeProbe --edit | ServeProbe --transport | ServeProbe --browser | ServeProbe --limits");
    return 1;
}

using (var server = new ReaderServer())
{
    server.Start();
    var document = server.RegisterDocument(args[0]);
    Console.WriteLine($"port={server.Port}");
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
    return failed ? 1 : 0;
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

static async Task<int> RunLimitChecksAsync()
{
    var failed = false;
    failed |= !await RunCheckAsync("limits-unknown-route-answers-without-reading", CheckUnknownRouteUnreadAsync);
    failed |= !await RunCheckAsync("limits-wrong-origin-host-boundary", CheckWrongOriginUnreadAsync);
    failed |= !await RunCheckAsync("limits-declared-oversize-refused-unread", CheckDeclaredOversizeAsync);
    failed |= !await RunCheckAsync("limits-small-routes-capped", CheckSmallRouteCapAsync);
    return failed ? 1 : 0;
}

// Sends request headers that promise `declaredLength` body bytes and then sends none. A server that
// answers has decided without reading the body; one that tries to read it stalls until the timeout.
static async Task<int?> StatusWithoutBodyAsync(int port, string method, string path, string? origin, long declaredLength)
{
    using var tcp = new System.Net.Sockets.TcpClient();
    await tcp.ConnectAsync("127.0.0.1", port);
    var stream = tcp.GetStream();
    var headers = $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\n" +
        (origin is null ? "" : $"Origin: {origin}\r\n") + $"Content-Length: {declaredLength}\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
    using var reader = new StreamReader(stream, Encoding.ASCII);
    try
    {
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        return line is null ? null : int.Parse(line.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (TimeoutException) { return null; }
}

static async Task CheckUnknownRouteUnreadAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("text\n");
    fixture.StartListener();
    var status = await StatusWithoutBodyAsync(fixture.Server.Port, "POST", "/anything", null, 500_000_000);
    Ensure(status == 404, $"An unknown route with a 500 MB declared body answered {status?.ToString() ?? "nothing within 3 s"}, not 404 without reading it.");
}

static async Task CheckWrongOriginUnreadAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("text\n");
    foreach (var path in new[] { "/toggle", "/theme", $"/command/{fixture.Id}", $"/save/{fixture.Id}" })
    {
        var response = await fixture.Server.Host.HandleAsync(new HostRequest("POST", path,
            "https://evil.example", "{}"u8.ToArray()));
        Ensure(response.StatusCode == 403, $"{path} from a foreign origin answered {response.StatusCode}, not 403 at the host boundary.");
    }
    fixture.StartListener();
    var routes = new[] { ("GET", $"/d/{fixture.Id}"), ("GET", $"/events/{fixture.Id}"),
        ("POST", "/toggle"), ("POST", "/theme"), ("POST", $"/command/{fixture.Id}"),
        ("POST", $"/save/{fixture.Id}") };
    foreach (var (method, path) in routes)
        Ensure(await StatusWithoutBodyAsync(fixture.Server.Port, method, path, null, method == "POST" ? 1_000 : 0) == 404,
            $"{method} {path} was not 404 over the hand-off listener.");
    Ensure(await StatusWithoutBodyAsync(fixture.Server.Port, "POST", "/open", "https://evil.example", 1_000) == 403,
        "POST /open with an Origin header was not rejected before reading its body.");
}

static async Task CheckDeclaredOversizeAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("text\n");
    var save = await fixture.Server.Host.HandleAsync(new HostRequest("POST", $"/save/{fixture.Id}",
        DocumentHost.Origin, new byte[DocumentHost.MaximumEditBodyBytes + 1]));
    Ensure(save.StatusCode == 413, $"A save of 64 MiB + 1 answered {save.StatusCode}, not 413 at the host boundary.");
    fixture.StartListener();
    var status = await StatusWithoutBodyAsync(fixture.Server.Port, "POST", "/open", null, 500_000_000);
    Ensure(status == 413, $"/open declaring 500 MB answered {status?.ToString() ?? "nothing within 3 s"}, not 413 without reading.");
}

static async Task CheckSmallRouteCapAsync()
{
    await using var fixture = await ToggleFixture.CreateAsync("- [ ] task\n");
    var padded = JsonSerializer.Serialize(new { id = fixture.Id, line = 1, @checked = false, token = fixture.Token, pad = new string('x', 70 * 1024) });
    var response = await fixture.Server.Host.HandleAsync(new HostRequest("POST", "/toggle", DocumentHost.Origin,
        Encoding.UTF8.GetBytes(padded)));
    Ensure(response.StatusCode == (int)HttpStatusCode.RequestEntityTooLarge, $"A 70 KiB toggle body answered {response.StatusCode}, not 413.");
    EnsureBytes(fixture.Path, "- [ ] task\n");
    EnsureStatus(await fixture.ToggleAsync(1, false), HttpStatusCode.NoContent);
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
    using var lifecycle = new Lifecycle(fixture.Server, signals, coordinator, launcher,
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
    await CheckExitWithPendingLaunchAsync(path);
    return 0;
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
    Console.WriteLine($"last-disconnect=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
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
    Console.WriteLine($"grace-cancellation-cleanup handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
}

static async Task CheckStartupGuardAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine($"startup-guard=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
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
    var openResponse = await fixture.Server.Host.HandleAsync(new HostRequest("POST", "/open", null,
        Encoding.UTF8.GetBytes(secondPath)));
    using var openJson = JsonDocument.Parse(openResponse.Body);
    var secondId = openJson.RootElement.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("The open response did not contain an ID.");
    var secondUrl = $"{DocumentHost.Origin}/d/{secondId}";

    await Task.Delay(TimeSpan.FromMilliseconds(600));
    var page = await fixture.Server.Host.HandleAsync(new HostRequest("GET", $"/d/{secondId}", null, []));
    if (fixture.Lifecycle.Completion.IsCompleted || page.StatusCode != (int)HttpStatusCode.OK)
    {
        throw new InvalidOperationException("Opening a document during grace did not keep the server alive.");
    }

    fixture.Signals.Connect();
    fixture.Signals.Disconnect();

    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine($"document-open-during-grace=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
    try { File.Delete(secondPath); } catch (IOException) { }
}

static Fixture StartFixture(string path, TimeSpan grace, TimeSpan startup)
{
    var stateDirectory = Path.Combine(Path.GetTempPath(), $"mdview-probe-{Guid.NewGuid():N}");
    var coordinator = new InstanceCoordinator(stateDirectory);
    if (!coordinator.IsPrimary)
    {
        coordinator.Dispose();
        throw new InvalidOperationException("Lifecycle probe could not acquire its single-instance mutex.");
    }

    var server = new ReaderServer();
    var document = server.RegisterDocument(path);
    coordinator.WriteHandshake(7717);
    var launcher = new StubBrowserLauncher();
    var signals = new StubLifecycleSignals(server.Host, launcher);
    var lifecycle = new Lifecycle(server, signals, coordinator, launcher, grace, startup);
    if (!lifecycle.TryLaunch(document) || launcher.Url is null ||
        !Uri.TryCreate(launcher.Url, UriKind.Absolute, out var uri) ||
        uri.GetLeftPart(UriPartial.Authority) != DocumentHost.Origin ||
        !uri.AbsolutePath.StartsWith("/d/", StringComparison.Ordinal))
    {
        lifecycle.Dispose();
        throw new InvalidOperationException("Stub launcher did not receive a valid loopback document URL.");
    }

    Console.WriteLine($"stub-url={launcher.Url}");
    return new Fixture(server, signals, lifecycle, coordinator.HandshakePath, stateDirectory, launcher.Url, launcher);
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

sealed record Fixture(ReaderServer Server, StubLifecycleSignals Signals, Lifecycle Lifecycle, string HandshakePath,
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
    private readonly ReaderServer server;
    private readonly HttpClient client = new();

    private ToggleFixture(string directory, string path, ReaderServer server, string id, string token, string html,
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
    internal ReaderServer Server => server;
    internal void StartListener() => server.Start();

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
        var server = new ReaderServer(themeConfig: themeConfig, fileDialogs: fileDialogs);
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
        client.Dispose();
        server.Dispose();
        try { Directory.Delete(directory, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
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
