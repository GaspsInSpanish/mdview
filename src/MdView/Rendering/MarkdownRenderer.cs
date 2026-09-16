using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace MdView.Rendering;

/// <summary>Renders Markdown source into safe body HTML.</summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .DisableHtml()
        .Build();

    internal static string RenderBody(string markdown) => UriSanitizer.SanitizeHtml(
        Markdown.ToHtml(markdown, Pipeline)
            .Replace("<pre><code>", "<pre><code class=\"language-plaintext\">"));
}
