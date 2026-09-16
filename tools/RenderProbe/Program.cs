using MdView.Rendering;
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
await File.WriteAllTextAsync(args[1], document);
Console.WriteLine("render-menu=passed");
Console.WriteLine("render-csp-nonces=passed");
Console.WriteLine("render-menu-css=passed");
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
        !menuScript.Contains("fetch(", StringComparison.Ordinal), "Menu shell must not persist state or call an endpoint.");

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
    foreach (Match rule in Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<declarations>[^{}]*)\}", RegexOptions.CultureInvariant))
    {
        if (!rule.Groups["selectors"].Value.Contains(".menu", StringComparison.Ordinal)) continue;
        var declarations = rule.Groups["declarations"].Value;
        Ensure(!declarations.Contains("border-radius", StringComparison.OrdinalIgnoreCase),
            "A menu rule uses border-radius.");
        Ensure(!declarations.Contains("box-shadow", StringComparison.OrdinalIgnoreCase),
            "A menu rule uses box-shadow.");
    }
}

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
