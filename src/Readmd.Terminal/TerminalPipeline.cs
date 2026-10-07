using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace Readmd.Terminal;

/// <summary>
/// The process-wide Markdig pipelines used to parse documents for terminal rendering. Built once
/// per option set (constructing the advanced-extensions pipeline is not free) and shared by the
/// interactive viewer and the non-interactive text renderer. It matches the browser's heading-id
/// scheme (GitHub auto-identifiers, registered before <c>UseAdvancedExtensions</c> so the options
/// actually apply) so in-document "#fragment" links resolve to the same targets in both front-ends.
/// It deliberately omits the browser's <c>UseTocMarker</c>: the terminal detects <c>[[_TOC_]]</c> as
/// a plain paragraph, so adding it would change output.
/// </summary>
internal static class TerminalPipeline
{
    /// <summary>The default pipeline (no typographic replacements).</summary>
    public static readonly MarkdownPipeline Instance = Build(smartyPants: false);

    private static MarkdownPipeline? _smarty;

    /// <summary>Returns the pipeline for the given options (cached).</summary>
    public static MarkdownPipeline For(bool smartyPants)
    {
        if (!smartyPants) return Instance;
        return _smarty ??= Build(smartyPants: true);
    }

    private static MarkdownPipeline Build(bool smartyPants)
    {
        var b = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UseAdvancedExtensions()
            .UseEmojiAndSmiley()
            .UseMathematics()
            .UseGenericAttributes();
        // Typographic quotes/dashes/ellipses ("smart punctuation"); opt-in because it changes the
        // characters shown for source that may be quoting code or exact strings.
        if (smartyPants) b = b.UseSmartyPants();
        return b.Build();
    }
}
