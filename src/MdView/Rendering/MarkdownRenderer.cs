using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MdView.Rendering;

/// <summary>Renders Markdown source into safe body HTML.</summary>
internal static class MarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline = BuildPipeline();

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

    /// <summary>
    /// Renders one top-level block at a time so each can be stamped with the source range
    /// it came from; edit mode reveals exactly that slice of the file for editing. The
    /// stamp is added <em>after</em> sanitizing, and <c>data-md-*</c> is not on the
    /// sanitizer allowlist, so document content can never forge or move a range.
    /// </summary>
    internal static string RenderBody(string markdown, string documentId)
    {
        using var scope = TaskRenderContext.Push(documentId);
        var document = Markdown.Parse(markdown, Pipeline);
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        var fragments = new List<(int Order, string Html)>();
        var blockRanges = new List<(int Start, int End)>();
        var placeholders = new List<(string Kind, int Start, int End)>();
        foreach (var block in document)
        {
            if (block is LinkReferenceDefinitionGroup definitions)
            {
                // The parser gathers every `[label]: url` line into one group at the first
                // definition's position, with a span that is not the definitions' own. Each
                // definition gets its own placeholder, ordered by where it really sits.
                // A footnote's definition span covers only its `[^label]`, and its body is a
                // separate block rendered at the end of the page, so the two are joined.
                foreach (var definition in definitions.OfType<LinkReferenceDefinition>())
                    placeholders.Add(definition is FootnoteLinkReferenceDefinition footnote
                        ? ("footnote", definition.Span.Start, TrimmedRange(markdown, SourceRange(footnote.Footnote)).End)
                        : ("definition", definition.Span.Start, definition.Span.End + 1));
                continue;
            }
            if (block is YamlFrontMatterBlock)
            {
                // Renders nothing, but is part of the file and must stay editable.
                var (start, end) = TrimmedRange(markdown, SourceRange(block));
                placeholders.Add(("frontmatter", start, end));
                continue;
            }
            writer.GetStringBuilder().Clear();
            renderer.Render(block);
            writer.Flush();
            var fragment = UriSanitizer.SanitizeHtml(writer.ToString()
                .Replace("<pre><code>", "<pre><code class=\"language-plaintext\">"));
            if (block is FootnoteGroup || block.Span.IsEmpty)
            {
                // Footnotes render after everything else, whatever their position in the source.
                fragments.Add((int.MaxValue, fragment));
                continue;
            }
            var range = TrimmedRange(markdown, SourceRange(block));
            blockRanges.Add(range);
            fragments.Add((range.Start, StampSourceRange(fragment, range)));
        }
        // Abbreviation definitions are lifted out of the block tree entirely.
        foreach (var abbreviation in document.GetAbbreviations()?.Values ?? Enumerable.Empty<Abbreviation>())
            placeholders.Add(("abbreviation", abbreviation.Span.Start, abbreviation.Span.End + 1));
        // A definition nested in a quote or list is already inside that block's range.
        foreach (var (kind, start, end) in placeholders)
            if (!blockRanges.Any(range => start >= range.Start && start < range.End))
                fragments.Add((start, SourceOnly(kind, start, end)));
        return string.Concat(fragments.OrderBy(fragment => fragment.Order).Select(fragment => fragment.Html));
    }

    private static string SourceOnly(string kind, int start, int end) =>
        FormattableString.Invariant($"<div class=\"md-source-only md-source-{kind}\" data-md-start=\"{start}\" data-md-end=\"{end}\"></div>\n");

    private static string StampSourceRange(string fragment, (int Start, int End) range)
    {
        var offset = 0;
        while (offset < fragment.Length && char.IsWhiteSpace(fragment[offset])) offset++;
        if (offset + 1 >= fragment.Length || fragment[offset] != '<' || !char.IsAsciiLetter(fragment[offset + 1]))
            return fragment;
        // Appended after the tag's own attributes, which stay byte-identical. Attribute values
        // are escaped by the renderer, so the first '>' closes the tag.
        var close = fragment.IndexOf('>', offset);
        if (close < 0) return fragment;
        if (fragment[close - 1] == '/') close -= fragment[close - 2] == ' ' ? 2 : 1;
        return fragment.Insert(close, FormattableString.Invariant(
            $" data-md-start=\"{range.Start}\" data-md-end=\"{range.End}\""));
    }

    /// <summary>Start and exclusive end of a block's source. Container spans already cover
    /// their children, lazy continuation lines included (asserted by RenderProbe).</summary>
    internal static (int Start, int End) SourceRange(Block block) => (block.Span.Start, block.Span.End + 1);

    /// <summary>Like <see cref="SourceRange(Block)"/>, minus trailing line breaks: some spans
    /// (lists) swallow one, and handing it to the editor invites deleting the blank line
    /// that separates this block from the next.</summary>
    internal static (int Start, int End) TrimmedRange(string markdown, (int Start, int End) range)
    {
        var end = Math.Min(range.End, markdown.Length);
        while (end > range.Start && markdown[end - 1] is '\n' or '\r') end--;
        return (range.Start, end);
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
