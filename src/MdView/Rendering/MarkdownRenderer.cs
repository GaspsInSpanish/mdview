using Markdig;
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
        var builder = new MarkdownPipelineBuilder()
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

    private static class TaskRenderContext
    {
        private static readonly AsyncLocal<string?> Current = new();
        internal static string DocumentId => Current.Value ?? string.Empty;
        internal static IDisposable Push(string documentId) { var old = Current.Value; Current.Value = documentId; return new Scope(old); }
        private sealed class Scope(string? old) : IDisposable { public void Dispose() => Current.Value = old; }
    }
}
