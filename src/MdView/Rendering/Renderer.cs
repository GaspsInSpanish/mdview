using System.Text;
using System.Text.Encodings.Web;
using System.Security.Cryptography;
using MdView.Serving;

namespace MdView.Rendering;

/// <summary>Converts untrusted Markdown into a standalone reader document.</summary>
public static class Renderer
{
    private static readonly string[] HighlightScripts =
    [
        "highlight.min.js",
        "lang-bash.min.js",
        "lang-csharp.min.js",
        "lang-diff.min.js",
        "lang-dockerfile.min.js",
        "lang-go.min.js",
        "lang-ini.min.js",
        "lang-javascript.min.js",
        "lang-json.min.js",
        "lang-markdown.min.js",
        "lang-powershell.min.js",
        "lang-python.min.js",
        "lang-rust.min.js",
        "lang-sql.min.js",
        "lang-typescript.min.js",
        "lang-xml.min.js",
        "lang-yaml.min.js"
    ];

    /// <summary>Renders Markdown as a complete, self-contained HTML5 document.</summary>
    public static string RenderDocument(string markdown, string title)
        => RenderDocument(DocumentKind.Markdown, markdown, title);

    /// <summary>Renders a supported document format as a complete, self-contained HTML5 document.</summary>
    public static string RenderDocument(DocumentKind kind, string document, string title, string documentId = "", string writeToken = "",
        string? cspNonce = null, ThemePreference theme = ThemePreference.System, string sourceHash = "")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(title);
        cspNonce ??= CreateNonce();
        var contentSecurityPolicy = CreateContentSecurityPolicy(cspNonce);

        var body = RenderBody(kind, document, documentId);
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.Append("<html lang=\"en\"");
        if (theme is not ThemePreference.System)
            html.Append(" data-theme=\"").Append(ThemeConfigStore.ToWireValue(theme)).Append('"');
        html.AppendLine(">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(contentSecurityPolicy).AppendLine("\">");
        html.Append("<title>").Append(HtmlEncoder.Default.Encode(title)).AppendLine("</title>");
        html.Append("<style nonce=\"").Append(cspNonce).AppendLine("\">");
        html.AppendLine(Assets.LoadTheme());
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body tabindex=\"-1\">");
        AppendMenuBar(html, theme);
        AppendFindBar(html);
        html.AppendLine("<main id=\"reader-content\" tabindex=\"-1\">");
        html.Append(body);
        html.AppendLine("</main>");
        html.Append("<script nonce=\"").Append(cspNonce).AppendLine("\">");
        html.Append("window.mdviewToggle={id:").Append(System.Text.Json.JsonSerializer.Serialize(documentId)).Append(",token:").Append(System.Text.Json.JsonSerializer.Serialize(writeToken)).AppendLine("};");
        html.Append("window.mdviewEdit={hash:").Append(System.Text.Json.JsonSerializer.Serialize(sourceHash)).AppendLine("};");
        html.AppendLine("</script>");
        AppendScript(html, cspNonce, Assets.LoadScript("page.js"));
        AppendScript(html, cspNonce, Assets.LoadScript("fold.js"));
        AppendScript(html, cspNonce, Assets.LoadScript("menu.js"));
        AppendScript(html, cspNonce, Assets.LoadScript("edit.js"));

        foreach (var script in HighlightScripts)
        {
            AppendScript(html, cspNonce, Assets.LoadScript(script));
        }

        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>Renders only the reader body, as edit mode swaps it in after a change.</summary>
    internal static string RenderBody(DocumentKind kind, string document, string documentId) => kind switch
    {
        DocumentKind.Markdown => MarkdownRenderer.RenderBody(document, documentId),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported document kind.")
    };

    private static void AppendMenuBar(StringBuilder html, ThemePreference theme)
    {
        html.AppendLine("<nav class=\"menu-bar\" role=\"menubar\" aria-label=\"Application menu\">");
        AppendMenu(html, "file", "File",
            [("New", "Ctrl+N", "file.new"), ("Open…", "Ctrl+O", "file.open"), ("Save", "Ctrl+S", "file.save"),
             ("Save As…", "Ctrl+Shift+S", "file.save-as"), ("Exit", "Alt+F4", "file.exit")], 4);
        AppendMenu(html, "edit", "Edit",
            [("Copy", "Ctrl+C", "edit.copy"), ("Select All", "Ctrl+A", "edit.select-all"),
             ("Find…", "Ctrl+F", "edit.find")]);
        AppendMenu(html, "view", "View",
            [("Collapse All", "", "view.collapse-all"), ("Expand All", "", "view.expand-all")]);
        AppendThemeMenu(html, theme);
        // The edit lock. Locked is the default on every page load; edit.js flips it.
        html.AppendLine("<button class=\"edit-lock\" type=\"button\" role=\"menuitemcheckbox\" aria-checked=\"false\" title=\"Unlock to edit this file (Ctrl+E)\">Locked</button>");
        html.AppendLine("</nav>");
    }

    private static void AppendMenu(StringBuilder html, string id, string label,
        (string Label, string Accelerator, string Command)[] items, int separatorBefore = -1)
    {
        html.Append("<div class=\"menu\" data-menu=\"").Append(id).AppendLine("\">");
        html.Append("<button class=\"menu-button\" type=\"button\" role=\"menuitem\" aria-haspopup=\"true\" aria-expanded=\"false\" aria-controls=\"menu-")
            .Append(id).Append("\">").Append(label).AppendLine("</button>");
        html.Append("<div class=\"menu-panel\" id=\"menu-").Append(id).AppendLine("\" role=\"menu\" hidden>");
        for (var index = 0; index < items.Length; index++)
        {
            if (index == separatorBefore) html.AppendLine("<div class=\"menu-separator\" role=\"separator\"></div>");
            var item = items[index];
            html.Append("<button class=\"menu-item\" type=\"button\" role=\"menuitem\" tabindex=\"-1\" data-command=\"")
                .Append(item.Command).Append('"');
            if (item.Command is "edit.copy" or "file.save") html.Append(" aria-disabled=\"true\"");
            html.Append("><span>").Append(item.Label).Append("</span><span class=\"menu-accelerator\">")
                .Append(item.Accelerator).AppendLine("</span></button>");
        }
        html.AppendLine("</div></div>");
    }

    private static void AppendFindBar(StringBuilder html)
    {
        html.AppendLine("<section class=\"find-bar\" role=\"search\" aria-label=\"Find in document\" hidden>");
        html.AppendLine("<label class=\"find-label\" for=\"find-query\">Find:</label>");
        html.AppendLine("<input id=\"find-query\" class=\"find-query\" type=\"search\" autocomplete=\"off\" spellcheck=\"false\" aria-describedby=\"find-status\">");
        html.AppendLine("<span id=\"find-status\" class=\"find-status\" role=\"status\" aria-live=\"polite\">0 / 0</span>");
        html.AppendLine("<button class=\"find-button\" type=\"button\" data-find-action=\"previous\" aria-label=\"Previous match\">Previous</button>");
        html.AppendLine("<button class=\"find-button\" type=\"button\" data-find-action=\"next\" aria-label=\"Next match\">Next</button>");
        html.AppendLine("<button class=\"find-button find-close\" type=\"button\" data-find-action=\"close\" aria-label=\"Close find\">×</button>");
        html.AppendLine("</section>");
    }

    private static void AppendThemeMenu(StringBuilder html, ThemePreference theme)
    {
        html.AppendLine("<div class=\"menu menu-theme\" data-menu=\"theme\">");
        html.AppendLine("<button class=\"menu-button\" type=\"button\" role=\"menuitem\" aria-haspopup=\"true\" aria-expanded=\"false\" aria-controls=\"menu-theme\">Theme</button>");
        html.AppendLine("<div class=\"menu-panel\" id=\"menu-theme\" role=\"menu\" hidden>");
        AppendThemeItem(html, "System", "system", theme is ThemePreference.System);
        AppendThemeItem(html, "Light", "light", theme is ThemePreference.Light);
        AppendThemeItem(html, "Dark", "dark", theme is ThemePreference.Dark);
        html.AppendLine("</div></div>");
    }

    private static void AppendThemeItem(StringBuilder html, string label, string value, bool isCurrent)
    {
        html.Append("<button class=\"menu-item menu-radio\" type=\"button\" role=\"menuitemradio\" aria-checked=\"")
            .Append(isCurrent ? "true" : "false").Append("\" tabindex=\"-1\" data-command=\"theme.")
            .Append(value).Append("\"><span>").Append(label)
            .AppendLine("</span><span class=\"menu-accelerator\"></span></button>");
    }

    private static void AppendScript(StringBuilder html, string nonce, string script)
    {
        html.Append("<script nonce=\"").Append(nonce).AppendLine("\">");
        html.AppendLine(script);
        html.AppendLine("</script>");
    }

    internal static string CreateNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    internal static string CreateContentSecurityPolicy(string nonce) =>
        $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; " +
        "img-src 'self' data: https:; base-uri 'none'; form-action 'none'; object-src 'none'; frame-ancestors 'none'";
}
