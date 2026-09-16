using MdView.Rendering;
using MdView.Serving;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: RenderProbe <input.md> <output.html>");
    return 1;
}

var markdown = await File.ReadAllTextAsync(args[0]);
var title = Path.GetFileNameWithoutExtension(args[0]);
var document = Renderer.RenderDocument(markdown, title);
ValidateMenuShell(document);
ValidateEditFeatures(document);
ValidateThemeRendering(markdown, title);
await File.WriteAllTextAsync(args[1], document);
Console.WriteLine("render-menu=passed");
Console.WriteLine("render-csp-nonces=passed");
Console.WriteLine("render-menu-css=passed");
Console.WriteLine("render-edit=passed");
return 0;

static void ValidateMenuShell(string html)
{
    var tags = GetOpeningTags(html);
    var menuTags = tags.Where(tag => HasAttribute(tag, "data-menu")).ToArray();
    Ensure(menuTags.Length == 3, $"Expected exactly three menus, found {menuTags.Length}.");
    foreach (var menu in new[] { "file", "edit", "theme" })
        Ensure(menuTags.Count(tag => HasAttribute(tag, "data-menu", menu)) == 1, $"Menu '{menu}' was missing or duplicated.");

    var expectedCommands = new[]
    {
        "file.new", "file.open", "file.save-as", "file.exit",
        "edit.copy", "edit.select-all", "edit.find",
        "theme.system", "theme.light", "theme.dark"
    };
    var commandTags = tags.Where(tag => HasAttribute(tag, "data-command")).ToArray();
    Ensure(commandTags.Length == expectedCommands.Length, "Unexpected number of menu commands.");
    foreach (var command in expectedCommands)
        Ensure(commandTags.Count(tag => HasAttribute(tag, "data-command", command)) == 1,
            $"Command '{command}' was missing or duplicated.");

    var shellEnd = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
    Ensure(shellEnd >= 0, "Rendered document had no scripts.");
    var shell = html[..shellEnd];
    foreach (var label in new[] { ">File<", ">New<", ">Open…<", ">Save As…<", ">Exit<", ">Edit<", ">Copy<", ">Select All<", ">Find…<", ">Theme<", ">System<", ">Light<", ">Dark<" })
        Ensure(shell.Contains(label, StringComparison.Ordinal), $"Menu label '{label[1..^1]}' was missing.");
    foreach (var accelerator in new[] { "Ctrl+N", "Ctrl+O", "Ctrl+Shift+S", "Alt+F4", "Ctrl+C", "Ctrl+A", "Ctrl+F" })
        Ensure(shell.Contains($">{accelerator}<", StringComparison.Ordinal), $"Accelerator '{accelerator}' was missing.");

    Ensure(tags.Count(tag => HasAttribute(tag, "role", "menubar")) == 1, "Menubar role was missing or duplicated.");
    Ensure(tags.Count(tag => HasAttribute(tag, "role", "menu")) == 3, "Menu roles were missing.");
    Ensure(tags.Count(tag => HasAttribute(tag, "aria-expanded", "false")) == 3, "Menu expansion state was missing.");
    Ensure(tags.Count(tag => HasAttribute(tag, "aria-haspopup", "true")) == 3, "Menu popup semantics were missing.");
    Ensure(tags.Count(tag => HasAttribute(tag, "role", "menuitemradio") && HasAttribute(tag, "aria-checked", "true")) == 1,
        "Exactly one theme item must be marked current.");
    Ensure(commandTags.Single(tag => HasAttribute(tag, "data-command", "theme.system"))
        .Contains("aria-checked=\"true\"", StringComparison.Ordinal), "System theme was not current.");

    var menuScript = Assets.LoadScript("menu.js");
    foreach (var behavior in new[] { "event.key === 'Alt'", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "event.key === 'Enter'", "event.key === 'Escape'", "aria-expanded" })
        Ensure(menuScript.Contains(behavior, StringComparison.Ordinal), $"Menu behavior '{behavior}' was missing.");
    Ensure(!menuScript.Contains("localStorage", StringComparison.Ordinal) &&
        menuScript.Contains("fetch('/theme'", StringComparison.Ordinal) &&
        menuScript.Contains("fetch(`/command/", StringComparison.Ordinal) &&
        !menuScript.Contains("fetch('/edit", StringComparison.Ordinal),
        "Menu commands were not limited to the assigned Theme and File endpoints.");

    var meta = tags.Single(tag => HasAttribute(tag, "http-equiv", "Content-Security-Policy"));
    var policy = GetAttribute(meta, "content") ?? throw new InvalidOperationException("CSP meta had no content.");
    var nonceMatch = Regex.Match(policy, @"(?:^|;\s*)script-src\s+'nonce-(?<nonce>[^']+)'(?:;|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    Ensure(nonceMatch.Success, "CSP did not contain a nonce-only script source.");
    var nonce = nonceMatch.Groups["nonce"].Value;
    var executableTags = tags.Where(tag => Regex.IsMatch(tag, @"^<(?:script|style)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
    Ensure(executableTags.Length > 0, "No inline script or style elements were found.");
    Ensure(executableTags.All(tag => HasAttribute(tag, "nonce", nonce)),
        "An inline script or style was missing the CSP nonce.");

    var css = Assets.LoadTheme();
    Ensure(Regex.IsMatch(css, @"main\s*\{[^}]*max-width:\s*46rem", RegexOptions.CultureInvariant),
        "The reading column no longer has a 46rem measure.");
    ValidateBevelTheme(css, dark: false);
    ValidateBevelTheme(css, dark: true);
    ValidatePressedBevel(css);
    Ensure(Regex.IsMatch(css, """@media\s*\(prefers-color-scheme:\s*dark\)\s*\{\s*:root:not\(\[data-theme="light"\]\)""",
        RegexOptions.CultureInvariant), "OS-dark rules were not guarded against forced light.");
    Ensure(css.Contains(":root[data-theme=\"dark\"]", StringComparison.Ordinal), "Forced-dark selector was missing.");
    foreach (Match rule in Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<declarations>[^{}]*)\}", RegexOptions.CultureInvariant))
    {
        if (!rule.Groups["selectors"].Value.Contains(".menu", StringComparison.Ordinal)) continue;
        var declarations = rule.Groups["declarations"].Value;
        Ensure(!declarations.Contains("border-radius", StringComparison.OrdinalIgnoreCase),
            "A menu rule uses border-radius.");
        Ensure(!declarations.Contains("transition", StringComparison.OrdinalIgnoreCase),
            "A menu rule uses a transition.");
        foreach (Match shadow in Regex.Matches(declarations, @"box-shadow\s*:\s*(?<value>[^;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            ValidateHardInsetShadows(shadow.Groups["value"].Value);
    }
}

static void ValidateEditFeatures(string html)
{
    var tags = GetOpeningTags(html);
    var findBar = tags.SingleOrDefault(tag => tag.StartsWith("<section", StringComparison.OrdinalIgnoreCase) &&
        tag.Contains("class=\"find-bar\"", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("Find bar markup was missing.");
    Ensure(findBar.Contains(" hidden", StringComparison.Ordinal) && HasAttribute(findBar, "role", "search") &&
        HasAttribute(findBar, "aria-label", "Find in document"),
        "Find bar was not hidden by default or lacked search semantics.");
    Ensure(tags.Count(tag => HasAttribute(tag, "data-find-action")) == 3,
        "Find bar did not contain Previous, Next, and Close controls.");
    Ensure(tags.Any(tag => HasAttribute(tag, "id", "find-query") && HasAttribute(tag, "aria-describedby", "find-status")),
        "Find input accessibility wiring was missing.");
    Ensure(tags.Any(tag => HasAttribute(tag, "id", "find-status") && HasAttribute(tag, "role", "status") &&
        HasAttribute(tag, "aria-live", "polite")), "Find status was not an accessible live region.");
    Ensure(tags.Single(tag => HasAttribute(tag, "data-command", "edit.copy"))
        .Contains("aria-disabled=\"true\"", StringComparison.Ordinal), "Copy was not initially disabled.");

    var script = Assets.LoadScript("menu.js");
    Ensure(script.Contains("CSS.highlights", StringComparison.Ordinal) &&
        script.Contains("new Highlight", StringComparison.Ordinal) &&
        script.Contains("document.createRange()", StringComparison.Ordinal),
        "Find does not use the CSS Custom Highlight API with Range objects.");
    foreach (var forbidden in new[] { "<mark", "insertNode", "surroundContents" })
        Ensure(!script.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"Find uses forbidden DOM mutation '{forbidden}'.");
    Ensure(script.Contains("document.createTreeWalker(reader", StringComparison.Ordinal) &&
        script.Contains("range.selectNodeContents(reader)", StringComparison.Ordinal),
        "Search or Select All was not scoped to the reader <main>.");
    Ensure(script.Contains("if (!text) return false", StringComparison.Ordinal) &&
        script.Contains("aria-disabled", StringComparison.Ordinal) &&
        script.Contains("navigator.clipboard.writeText", StringComparison.Ordinal) &&
        script.Contains("document.execCommand('copy')", StringComparison.Ordinal),
        "Copy did not enforce selection-only behavior with the required fallback.");
    Ensure(script.Contains("setTimeout(runSearch, 150)", StringComparison.Ordinal),
        "Incremental search was not debounced.");
    Ensure(script.Contains("event.shiftKey ? -1 : 1", StringComparison.Ordinal) &&
        script.Contains("% findRanges.length", StringComparison.Ordinal),
        "Find navigation did not implement Shift+Enter and wrap-around.");
    foreach (var shortcut in new[] { "=== 'f'", "=== 'a'", "=== 'c'" })
        Ensure(script.Contains(shortcut, StringComparison.Ordinal), $"Edit shortcut {shortcut} was missing.");

    var css = Assets.LoadTheme();
    Ensure(css.Contains("::highlight(mdview-find-results)", StringComparison.Ordinal) &&
        css.Contains("::highlight(mdview-find-current)", StringComparison.Ordinal),
        "Find result/current highlight styles were missing.");
    foreach (Match rule in Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<declarations>[^{}]*)\}", RegexOptions.CultureInvariant))
    {
        if (!rule.Groups["selectors"].Value.Contains(".find-", StringComparison.Ordinal)) continue;
        var declarations = rule.Groups["declarations"].Value;
        Ensure(!declarations.Contains("border-radius", StringComparison.OrdinalIgnoreCase),
            "A find-bar rule uses border-radius.");
        Ensure(!declarations.Contains("transition", StringComparison.OrdinalIgnoreCase),
            "A find-bar rule uses a transition.");
        foreach (Match shadow in Regex.Matches(declarations, @"box-shadow\s*:\s*(?<value>[^;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            ValidateHardInsetShadows(shadow.Groups["value"].Value);
    }
    var findRules = string.Join('\n', Regex.Matches(css, @"(?<selectors>[^{}]*\.find-[^{}]*)\{(?<declarations>[^{}]*)\}",
        RegexOptions.CultureInvariant).Cast<Match>().Select(match => match.Value));
    foreach (var token in new[] { "--bevel-face", "--bevel-hi-outer", "--bevel-hi-inner", "--bevel-lo-inner", "--bevel-lo-outer" })
        Ensure(findRules.Contains($"var({token})", StringComparison.Ordinal), $"Find bar does not reuse {token}.");
}

static void ValidateThemeRendering(string markdown, string title)
{
    var systemHtml = Renderer.RenderDocument(DocumentKind.Markdown, markdown, title, theme: ThemePreference.System);
    var lightHtml = Renderer.RenderDocument(DocumentKind.Markdown, markdown, title, theme: ThemePreference.Light);
    var darkHtml = Renderer.RenderDocument(DocumentKind.Markdown, markdown, title, theme: ThemePreference.Dark);
    var systemRoot = GetOpeningTags(systemHtml).Single(tag => tag.StartsWith("<html", StringComparison.OrdinalIgnoreCase));
    var lightRoot = GetOpeningTags(lightHtml).Single(tag => tag.StartsWith("<html", StringComparison.OrdinalIgnoreCase));
    var darkRoot = GetOpeningTags(darkHtml).Single(tag => tag.StartsWith("<html", StringComparison.OrdinalIgnoreCase));
    Ensure(!HasAttribute(systemRoot, "data-theme"), "System rendering must omit data-theme.");
    Ensure(HasAttribute(lightRoot, "data-theme", "light"), "Light rendering did not force light.");
    Ensure(HasAttribute(darkRoot, "data-theme", "dark"), "Dark rendering did not force dark.");
    EnsureCheckedTheme(systemHtml, "system");
    EnsureCheckedTheme(lightHtml, "light");
    EnsureCheckedTheme(darkHtml, "dark");
}

static void EnsureCheckedTheme(string html, string theme)
{
    var checkedItems = GetOpeningTags(html).Where(tag => HasAttribute(tag, "aria-checked", "true") &&
        GetAttribute(tag, "data-command")?.StartsWith("theme.", StringComparison.Ordinal) == true).ToArray();
    Ensure(checkedItems.Length == 1 && HasAttribute(checkedItems[0], "data-command", $"theme.{theme}"),
        $"Theme menu did not mark only {theme} as current.");
}

static void ValidateBevelTheme(string css, bool dark)
{
    var block = Regex.Match(css, @"^\s*:root\s*\{(?<tokens>[^}]*)\}",
        RegexOptions.CultureInvariant).Groups["tokens"].Value;
    Ensure(block.Length > 0, $"{(dark ? "Dark" : "Light")} theme token block was missing.");
    var prefix = dark ? "--dark-bevel-" : "--bevel-";
    var names = new[] { $"{prefix}hi-outer", $"{prefix}hi-inner", $"{prefix}face", $"{prefix}lo-inner", $"{prefix}lo-outer" };
    var colors = names.Select(name => ReadHexToken(block, name)).ToArray();
    var luminances = colors.Select(RelativeLuminance).ToArray();
    for (var index = 1; index < luminances.Length; index++)
        Ensure(luminances[index - 1] > luminances[index],
            $"{(dark ? "Dark" : "Light")} bevel tones are not ordered lightest to darkest.");

    var highlightContrast = ContrastRatio(luminances[2], luminances[0]);
    var shadowContrast = ContrastRatio(luminances[2], luminances[4]);
    Ensure(highlightContrast > 1.7, $"Face-to-outer-highlight contrast was only {highlightContrast:F2}:1.");
    Ensure(shadowContrast > 1.7, $"Face-to-outer-shadow contrast was only {shadowContrast:F2}:1.");
    Console.WriteLine($"bevel-contrast-{(dark ? "dark" : "light")}=highlight:{highlightContrast:F2} shadow:{shadowContrast:F2}");
}

static void ValidatePressedBevel(string css)
{
    var raised = Regex.Match(css, @"\.menu-button\s*\{(?<declarations>[^}]*)\}",
        RegexOptions.CultureInvariant).Groups["declarations"].Value;
    Ensure(raised.Contains("var(--bevel-hi-outer) var(--bevel-lo-outer) var(--bevel-lo-outer) var(--bevel-hi-outer)",
        StringComparison.Ordinal) && raised.Contains("inset 1px 1px 0 var(--bevel-hi-inner)", StringComparison.Ordinal) &&
        raised.Contains("inset -1px -1px 0 var(--bevel-lo-inner)", StringComparison.Ordinal),
        "Resting button did not use the raised double bevel.");

    var pressedRules = Regex.Matches(css, @"\.menu\.is-open\s*>\s*\.menu-button\s*\{(?<declarations>[^}]*)\}",
        RegexOptions.CultureInvariant).Cast<Match>().Select(match => match.Groups["declarations"].Value).ToArray();
    var pressed = pressedRules.FirstOrDefault() ?? string.Empty;
    Ensure(pressed.Contains("var(--bevel-lo-outer) var(--bevel-hi-outer) var(--bevel-hi-outer) var(--bevel-lo-outer)",
        StringComparison.Ordinal), "Pressed state did not invert the outer bevel.");
    Ensure(pressed.Contains("inset 1px 1px 0 var(--bevel-lo-inner)", StringComparison.Ordinal) &&
        pressed.Contains("inset -1px -1px 0 var(--bevel-hi-inner)", StringComparison.Ordinal),
        "Pressed state did not invert the inner bevel.");
    Ensure(pressedRules.Any(rule => rule.Contains("padding-top: calc(0.22rem + 2px)", StringComparison.Ordinal) &&
        rule.Contains("padding-left: calc(0.7rem + 2px)", StringComparison.Ordinal)),
        "Pressed state did not nudge its label down and right.");
}

static void ValidateHardInsetShadows(string value)
{
    foreach (var shadow in value.Split(','))
    {
        Ensure(Regex.IsMatch(shadow.Trim(), @"^inset\s+-?\d+(?:\.\d+)?px\s+-?\d+(?:\.\d+)?px\s+0(?:px)?\s+var\(--bevel-(?:hi|lo)-inner\)$",
            RegexOptions.CultureInvariant), $"Menu shadow is not a zero-blur inset bevel: {shadow.Trim()}");
    }
}

static string ReadHexToken(string block, string name)
{
    var match = Regex.Match(block, $@"{Regex.Escape(name)}\s*:\s*(?<color>#[0-9A-Fa-f]{{6}})\s*;",
        RegexOptions.CultureInvariant);
    Ensure(match.Success, $"Bevel token '{name}' was missing or was not a six-digit color.");
    return match.Groups["color"].Value;
}

static double RelativeLuminance(string color)
{
    var channels = new[]
    {
        Convert.ToInt32(color[1..3], 16) / 255d,
        Convert.ToInt32(color[3..5], 16) / 255d,
        Convert.ToInt32(color[5..7], 16) / 255d
    };
    var linear = channels.Select(channel => channel <= 0.04045
        ? channel / 12.92
        : Math.Pow((channel + 0.055) / 1.055, 2.4)).ToArray();
    return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2];
}

static double ContrastRatio(double first, double second) =>
    (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);

static bool HasAttribute(string tag, string name, string? value = null) =>
    value is null ? GetAttribute(tag, name) is not null :
    string.Equals(GetAttribute(tag, name), value, StringComparison.Ordinal);

static string? GetAttribute(string tag, string name)
{
    var match = Regex.Match(tag, $"\\s{Regex.Escape(name)}=\\\"(?<value>[^\\\"]*)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    return match.Success ? match.Groups["value"].Value : null;
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
        if (end >= html.Length) throw new InvalidOperationException($"Unterminated <{name}> element.");

        tags.Add(html[start..(end + 1)]);
        position = end + 1;
        if (!name.Equals("script", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("style", StringComparison.OrdinalIgnoreCase)) continue;
        var closingStart = html.IndexOf($"</{name}", position, StringComparison.OrdinalIgnoreCase);
        if (closingStart < 0) throw new InvalidOperationException($"<{name}> had no closing tag.");
        var closingEnd = html.IndexOf('>', closingStart);
        if (closingEnd < 0) throw new InvalidOperationException($"</{name}> was unterminated.");
        position = closingEnd + 1;
    }
    return tags;
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
