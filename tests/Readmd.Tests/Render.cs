using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Readmd.Core;
using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>Helpers to render Markdown to terminal display lines for assertions.</summary>
internal static class Render
{
    // Mirrors Readmd.Terminal.TerminalPipeline.Instance (GitHub auto-identifiers first) so tests see
    // the same heading ids the terminal produces in production.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseMathematics()
        .UseGenericAttributes()
        .Build();

    /// <summary>Renders markdown and returns each display line's plain text.</summary>
    public static List<string> Lines(string markdown, int width = 100, bool dark = true)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        var renderer = new MarkdownTerminalRenderer(TerminalTheme.For(dark), width);
        var result = renderer.Render(doc, null);
        return result.Lines.Select(l => l.PlainText).ToList();
    }

    /// <summary>Renders markdown and returns the anchor map (id -> display line index) used for
    /// in-document "#fragment" navigation.</summary>
    public static IReadOnlyDictionary<string, int> Anchors(string markdown, int width = 100, bool dark = true)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        var renderer = new MarkdownTerminalRenderer(TerminalTheme.For(dark), width);
        return renderer.Render(doc, null).Anchors;
    }

    /// <summary>Renders markdown and returns the whole document as a single newline-joined string.</summary>
    public static string Text(string markdown, int width = 100, bool dark = true) =>
        string.Join("\n", Lines(markdown, width, dark));
}
