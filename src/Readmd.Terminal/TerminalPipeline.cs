using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace Readmd.Terminal;

/// <summary>
/// The single, process-wide Markdig pipeline used to parse documents for terminal rendering.
/// Built once (constructing the advanced-extensions pipeline is not free) and shared by the
/// interactive viewer and the non-interactive text renderer. It matches the browser's heading-id
/// scheme (GitHub auto-identifiers, registered before <c>UseAdvancedExtensions</c> so the options
/// actually apply) so in-document "#fragment" links resolve to the same targets in both front-ends.
/// It deliberately omits the browser's <c>UseTocMarker</c>: the terminal detects <c>[[_TOC_]]</c> as
/// a plain paragraph, so adding it would change output.
/// </summary>
internal static class TerminalPipeline
{
    public static readonly MarkdownPipeline Instance =
        new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UseAdvancedExtensions()
            .UseEmojiAndSmiley()
            .UseMathematics()
            .UseGenericAttributes()
            .Build();
}
