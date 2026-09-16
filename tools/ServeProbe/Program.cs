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

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ServeProbe <input.md> | ServeProbe --lifecycle <input.md> | ServeProbe --toggle | ServeProbe --security | ServeProbe --theme");
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
    await CheckStartupGuardAsync(path);
    return 0;
}

static async Task CheckLastDisconnectAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
    using var client = await ConnectAsync(fixture.Url);
    client.Dispose();
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine($"last-disconnect=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
}

static async Task CheckGraceCancellationAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Server.ClientDisconnected += () => disconnected.TrySetResult();
    using (var first = await ConnectAsync(fixture.Url))
    {
        first.Dispose();
    }

    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(7));
    using var second = await ConnectAsync(fixture.Url);
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
    fixture.Server.ClientDisconnected += () => disconnected.TrySetResult();
    using (var first = await ConnectAsync(fixture.Url))
    {
        first.Dispose();
    }

    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(7));
    await Task.Delay(TimeSpan.FromMilliseconds(750));
    var secondPath = Path.Combine(Path.GetDirectoryName(path)!, "mdview-lifecycle-second.md");
    await File.WriteAllTextAsync(secondPath, "# Second document\n");
    var secondId = await OpenDocumentAsync(fixture.Server.Port, secondPath);
    var secondUrl = $"http://127.0.0.1:{fixture.Server.Port}/d/{secondId}";

    await Task.Delay(TimeSpan.FromMilliseconds(600));
    using var documentClient = new HttpClient();
    if (fixture.Lifecycle.Completion.IsCompleted || (await documentClient.GetAsync(secondUrl)).StatusCode != HttpStatusCode.OK)
    {
        throw new InvalidOperationException("Opening a document during grace did not keep the server alive.");
    }

    using (var second = await ConnectAsync(secondUrl))
    {
        second.Dispose();
    }

    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine($"document-open-during-grace=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
}

static async Task<string> OpenDocumentAsync(int port, string path)
{
    using var client = new HttpClient();
    using var response = await client.PostAsync($"http://127.0.0.1:{port}/open", new StringContent(path));
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("The open response did not contain an ID.");
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
    server.Start();
    var document = server.RegisterDocument(path);
    coordinator.WriteHandshake(server.Port);
    var launcher = new StubBrowserLauncher();
    var lifecycle = new Lifecycle(server, coordinator, launcher, grace, startup);
    if (!lifecycle.TryLaunch(document) || launcher.Url is null ||
        !Uri.TryCreate(launcher.Url, UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttp || uri.Host != IPAddress.Loopback.ToString() ||
        uri.Port != server.Port || !uri.AbsolutePath.StartsWith("/d/", StringComparison.Ordinal))
    {
        lifecycle.Dispose();
        throw new InvalidOperationException("Stub launcher did not receive a valid loopback document URL.");
    }

    Console.WriteLine($"stub-url={launcher.Url}");
    return new Fixture(server, lifecycle, coordinator.HandshakePath, stateDirectory, launcher.Url);
}

static async Task<HttpResponseMessage> ConnectAsync(string documentUrl)
{
    var document = new Uri(documentUrl);
    var id = document.AbsolutePath[3..];
    var client = new HttpClient();
    try
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{document.Port}/events/{id}");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new OwnedResponse(response, client);
    }
    catch
    {
        client.Dispose();
        throw;
    }
}

sealed class StubBrowserLauncher : IBrowserLauncher
{
    public string? Url { get; private set; }
    public bool TryLaunch(string url, out string? error)
    {
        Url = url;
        error = null;
        return true;
    }
}

sealed record Fixture(ReaderServer Server, Lifecycle Lifecycle, string HandshakePath,
    string StateDirectory, string Url) : IDisposable
{
    public void Dispose()
    {
        Lifecycle.Dispose();
        try { Directory.Delete(StateDirectory, true); } catch (IOException) { }
    }
}

sealed class OwnedResponse(HttpResponseMessage response, HttpClient client) : HttpResponseMessage(response.StatusCode)
{
    private readonly HttpResponseMessage inner = response;
    private readonly HttpClient client = client;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            client.Dispose();
        }
        base.Dispose(disposing);
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

    internal static Task<ToggleFixture> CreateAsync(string source, ThemeConfigStore? themeConfig = null) =>
        CreateAsync(Encoding.UTF8.GetBytes(source), themeConfig);

    internal static async Task<ToggleFixture> CreateAsync(byte[] source, ThemeConfigStore? themeConfig = null)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mdview-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "fixture.md");
        await File.WriteAllBytesAsync(path, source);
        var server = new ReaderServer(themeConfig: themeConfig);
        try
        {
            server.Start();
            var document = server.RegisterDocument(path);
            using var client = new HttpClient();
            using var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/d/{document.Id}");
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var contentSecurityPolicy = response.Headers.TryGetValues("Content-Security-Policy", out var policies)
                ? policies.Single()
                : null;
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/toggle");
        request.Headers.Add("Origin", $"http://127.0.0.1:{server.Port}");
        request.Content = JsonContent.Create(new { id = Id, line, @checked = expectedChecked, token = effectiveToken });
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    internal Task<HttpStatusCode> ThemeAsync(string? theme, string? token = "__fixture_token__",
        string? origin = "__fixture_origin__")
    {
        var effectiveToken = token == "__fixture_token__" ? Token : token;
        return ThemeRawAsync(JsonSerializer.Serialize(new { theme, token = effectiveToken }), origin);
    }

    internal async Task<HttpStatusCode> ThemeRawAsync(string json, string? origin = "__fixture_origin__")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/theme");
        if (origin is not null)
            request.Headers.Add("Origin", origin == "__fixture_origin__" ? $"http://127.0.0.1:{server.Port}" : origin);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    internal async Task<EventStream> ConnectEventsAsync()
    {
        var eventClient = new HttpClient();
        try
        {
            var response = await eventClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/events/{Id}"),
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var firstLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (firstLine != ": connected")
            {
                response.Dispose();
                eventClient.Dispose();
                throw new InvalidOperationException("SSE connection did not send its connected comment.");
            }
            _ = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            return new EventStream(eventClient, response, reader);
        }
        catch
        {
            eventClient.Dispose();
            throw;
        }
    }

    internal async Task<string?> FetchContentSecurityPolicyAsync()
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/d/{Id}");
        response.EnsureSuccessStatusCode();
        return response.Headers.TryGetValues("Content-Security-Policy", out var policies) ? policies.Single() : null;
    }

    internal async Task<string> FetchHtmlAsync()
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/d/{Id}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        server.Dispose();
        try { Directory.Delete(directory, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

sealed class EventStream : IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly HttpResponseMessage response;
    private readonly StreamReader reader;
    private readonly Channel<ProbeEvent> events = Channel.CreateUnbounded<ProbeEvent>();
    private readonly Task readPump;
    private volatile bool stopping;

    internal EventStream(HttpClient client, HttpResponseMessage response, StreamReader reader)
    {
        this.client = client;
        this.response = response;
        this.reader = reader;
        readPump = ReadEventsAsync();
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
            throw new IOException("SSE stream ended before the assertion completed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (readPump.IsCompleted) await events.Reader.Completion;
            return null;
        }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            string? eventName = null;
            string? data = null;
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    throw new IOException("SSE stream closed unexpectedly.");
                if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                else if (line.Length == 0 && eventName is not null)
                {
                    events.Writer.TryWrite(new ProbeEvent(eventName, data ?? string.Empty));
                    eventName = null;
                    data = null;
                }
            }
        }
        catch (Exception) when (stopping)
        {
            events.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            events.Writer.TryComplete(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        response.Dispose();
        reader.Dispose();
        client.Dispose();
        await readPump.ConfigureAwait(false);
    }

    private sealed record ProbeEvent(string Name, string Data);
}
