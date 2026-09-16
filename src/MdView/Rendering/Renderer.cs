using System.Text;
using System.Text.Encodings.Web;
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
    public static string RenderDocument(DocumentKind kind, string document, string title)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(title);

        var body = kind switch
        {
            DocumentKind.Markdown => MarkdownRenderer.RenderBody(document),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported document kind.")
        };
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(HtmlEncoder.Default.Encode(title)).AppendLine("</title>");
        html.AppendLine("<style>");
        html.AppendLine(Assets.LoadTheme());
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main>");
        html.Append(body);
        html.AppendLine("</main>");

        foreach (var script in HighlightScripts)
        {
            html.AppendLine("<script>");
            html.AppendLine(Assets.LoadScript(script));
            html.AppendLine("</script>");
        }

        html.AppendLine("<script>");
        html.AppendLine("document.addEventListener('DOMContentLoaded', () => hljs.highlightAll());");
        html.AppendLine("</script>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }
}
