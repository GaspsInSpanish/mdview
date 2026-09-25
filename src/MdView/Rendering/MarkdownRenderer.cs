using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MdView.Rendering;

/// <summary>Renders Markdown source into safe body HTML.</summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private static MarkdownPipeline BuildPipeline()
    {
        // Every extension here is an HTML-emission surface for untrusted input; the
        // comment on each names what it lets document content emit. UseMathematics() is
        // deliberately absent: without a typesetting engine it turns `$a^2$` into a
        // literal `\(a^2\)`, which reads worse than the source it replaced.
        var builder = new MarkdownPipelineBuilder()
            .UseAbbreviations()      // <abbr title="…">, value escaped
            .UseAlertBlocks()
            .UseCustomContainers()   // <div class="…"> — class is guarded, see SafeContainerRenderer
            .UseFigures()            // <figure>, <figcaption> — no attributes
            .UseGridTables()         // <table>, <col style="…"> — style is stripped by the sanitizer
            .UseListExtras()         // <ol type="a">
            .UseYamlFrontMatter()
            .UseAutoLinks()
            .UseDefinitionLists()
            .UseEmphasisExtras()
            .UseFootnotes()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UsePreciseSourceLocation()
            .DisableHtml();
        builder.Extensions.Add(new TaskListHtmlExtension());
        builder.Extensions.Add(new SafeAlertHtmlExtension());
        builder.Extensions.Add(new SafeContainerHtmlExtension());
        return builder.Build();
    }

    internal static string RenderBody(string markdown, string documentId)
    {
        using var scope = TaskRenderContext.Push(documentId);
        var document = Markdown.Parse(markdown, Pipeline);
        return UriSanitizer.SanitizeHtml(Markdown.ToHtml(document, Pipeline)
            .Replace("<pre><code>", "<pre><code class=\"language-plaintext\">"));
    }

    internal static bool IsExpectedTask(string markdown, int oneBasedLine, bool expectedChecked)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        return document.Descendants<TaskList>().Any(task => task.Line + 1 == oneBasedLine && task.Checked == expectedChecked);
    }

    private sealed class TaskListHtmlExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) { }
        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer htmlRenderer) return;
            for (var i = htmlRenderer.ObjectRenderers.Count - 1; i >= 0; i--)
                if (htmlRenderer.ObjectRenderers[i] is HtmlTaskListRenderer) htmlRenderer.ObjectRenderers.RemoveAt(i);
            htmlRenderer.ObjectRenderers.Insert(0, new InteractiveTaskListRenderer());
        }
    }

    private sealed class InteractiveTaskListRenderer : HtmlObjectRenderer<TaskList>
    {
        protected override void Write(HtmlRenderer renderer, TaskList task)
        {
            renderer.Write("<input type=\"checkbox\" data-line=\"").Write((task.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Write("\" data-document=\"").Write(TaskRenderContext.DocumentId).Write("\"");
            if (task.Checked) renderer.Write(" checked");
            renderer.Write(" />");
        }
    }

    /// <summary>
    /// Replaces Markdig's stock alert renderer. Two reasons, both security-relevant.
    /// The stock renderer interpolates the document's own alert kind straight into a
    /// class name, so <c>&gt; [!ANYTHING]</c> yields <c>markdown-alert-anything</c> and a
    /// 200-character kind yields a 200-character class. It also emits an inline SVG icon
    /// whose <c>viewBox</c>, <c>width</c>, <c>height</c> and <c>d</c> attributes are not on the
    /// sanitizer allowlist, so the icon would arrive stripped and empty. Here the class is
    /// looked up in a fixed table, so no document text reaches the attribute at all, and the
    /// icon comes from CSS instead of markup — no new attributes need allowlisting.
    /// </summary>
    private sealed class SafeAlertHtmlExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) { }
        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer htmlRenderer) return;
            for (var i = htmlRenderer.ObjectRenderers.Count - 1; i >= 0; i--)
                if (htmlRenderer.ObjectRenderers[i] is AlertBlockRenderer) htmlRenderer.ObjectRenderers.RemoveAt(i);
            htmlRenderer.ObjectRenderers.Insert(0, new SafeAlertRenderer());
        }
    }

    private sealed class SafeAlertRenderer : HtmlObjectRenderer<AlertBlock>
    {
        private static readonly Dictionary<string, (string Slug, string Title)> Kinds =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["note"] = ("note", "Note"),
                ["tip"] = ("tip", "Tip"),
                ["important"] = ("important", "Important"),
                ["warning"] = ("warning", "Warning"),
                ["caution"] = ("caution", "Caution")
            };

        protected override void Write(HtmlRenderer renderer, AlertBlock alert)
        {
            renderer.EnsureLine();
            if (!Kinds.TryGetValue(alert.Kind.ToString(), out var kind))
            {
                // Unrecognised kind renders as an ordinary quote. The parser has already
                // consumed the `[!KIND]` marker, so write it back: dropping it would delete
                // a line of the user's document from the display over a typo. WriteEscape
                // because the kind is document text, even though the parser admits letters only.
                renderer.WriteLine("<blockquote>");
                renderer.Write("<p>[!").WriteEscape(alert.Kind).WriteLine("]</p>");
                renderer.WriteChildren(alert);
                renderer.WriteLine("</blockquote>");
                return;
            }

            // kind.Slug and kind.Title are table constants, never the document's text.
            renderer.Write("<div class=\"markdown-alert markdown-alert-").Write(kind.Slug).WriteLine("\">");
            renderer.Write("<p class=\"markdown-alert-title\">").Write(kind.Title).WriteLine("</p>");
            renderer.WriteChildren(alert);
            renderer.WriteLine("</div>");
        }
    }

    /// <summary>
    /// Replaces Markdig's custom-container renderer. The stock one writes the container's
    /// own info string straight into a class, so <c>:::menu-panel</c> yields
    /// <c>&lt;div class="menu-panel"&gt;</c> — a document can borrow this application's chrome
    /// classes and dress its own content up as mdview's UI, or fake a callout with
    /// <c>:::markdown-alert</c>. Prefixing puts every container in its own namespace, and the
    /// slug is reduced to <c>[a-z0-9-]</c> and length-capped so the class stays well-formed.
    /// </summary>
    private sealed class SafeContainerHtmlExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) { }
        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer htmlRenderer) return;
            for (var i = htmlRenderer.ObjectRenderers.Count - 1; i >= 0; i--)
                if (htmlRenderer.ObjectRenderers[i] is HtmlCustomContainerRenderer) htmlRenderer.ObjectRenderers.RemoveAt(i);
            htmlRenderer.ObjectRenderers.Insert(0, new SafeContainerRenderer());
        }
    }

    private sealed class SafeContainerRenderer : HtmlObjectRenderer<CustomContainer>
    {
        private const int MaximumSlugLength = 32;

        protected override void Write(HtmlRenderer renderer, CustomContainer container)
        {
            renderer.EnsureLine();
            renderer.Write("<div class=\"md-container");
            var slug = Slug(container.Info);
            if (slug.Length > 0) renderer.Write(" md-container-").Write(slug);
            renderer.WriteLine("\">");
            renderer.WriteChildren(container);
            renderer.WriteLine("</div>");
        }

        private static string Slug(string? info)
        {
            if (string.IsNullOrEmpty(info)) return string.Empty;
            var slug = new System.Text.StringBuilder();
            foreach (var character in info)
            {
                if (slug.Length == MaximumSlugLength) break;
                if (char.IsAsciiLetterOrDigit(character)) slug.Append(char.ToLowerInvariant(character));
                else if (character is '-' or '_' && slug.Length > 0) slug.Append('-');
            }
            return slug.ToString().Trim('-');
        }
    }

    private static class TaskRenderContext
    {
        private static readonly AsyncLocal<string?> Current = new();
        internal static string DocumentId => Current.Value ?? string.Empty;
        internal static IDisposable Push(string documentId) { var old = Current.Value; Current.Value = documentId; return new Scope(old); }
        private sealed class Scope(string? old) : IDisposable { public void Dispose() => Current.Value = old; }
    }
}
