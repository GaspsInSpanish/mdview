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
    public static string RenderDocument(DocumentKind kind, string document, string title, string documentId = "", string writeToken = "", string? cspNonce = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(title);
        cspNonce ??= CreateNonce();
        var contentSecurityPolicy = CreateContentSecurityPolicy(cspNonce);

        var body = kind switch
        {
            DocumentKind.Markdown => MarkdownRenderer.RenderBody(document, documentId),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported document kind.")
        };
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(contentSecurityPolicy).AppendLine("\">");
        html.Append("<title>").Append(HtmlEncoder.Default.Encode(title)).AppendLine("</title>");
        html.Append("<style nonce=\"").Append(cspNonce).AppendLine("\">");
        html.AppendLine(Assets.LoadTheme());
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main>");
        html.Append(body);
        html.AppendLine("</main>");
        html.Append("<script nonce=\"").Append(cspNonce).AppendLine("\">");
        html.Append("window.mdviewToggle={id:").Append(System.Text.Json.JsonSerializer.Serialize(documentId)).Append(",token:").Append(System.Text.Json.JsonSerializer.Serialize(writeToken)).AppendLine("};");
        html.AppendLine("document.addEventListener('change',async e=>{const b=e.target;if(!b.matches('input[data-line]'))return;const old=!b.checked;b.disabled=true;try{const r=await fetch('/toggle',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:window.mdviewToggle.id,line:+b.dataset.line,checked:old,token:window.mdviewToggle.token})});if(r.status===409){location.reload();return;}if(!r.ok)throw 0;}catch{b.checked=old;alert('Checkbox change was not saved.');}finally{b.disabled=false;}});");
        html.AppendLine("</script>");

        foreach (var script in HighlightScripts)
        {
            html.Append("<script nonce=\"").Append(cspNonce).AppendLine("\">");
            html.AppendLine(Assets.LoadScript(script));
            html.AppendLine("</script>");
        }

        html.Append("<script nonce=\"").Append(cspNonce).AppendLine("\">");
        html.AppendLine("document.addEventListener('DOMContentLoaded', () => hljs.highlightAll());");
        html.AppendLine("</script>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    internal static string CreateNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    internal static string CreateContentSecurityPolicy(string nonce) =>
        $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; " +
        "img-src 'self' data: https:; base-uri 'none'; form-action 'none'; object-src 'none'; frame-ancestors 'none'";
}
