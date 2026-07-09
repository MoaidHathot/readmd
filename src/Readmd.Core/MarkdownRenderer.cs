using System.Text;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Readmd.Core.Toc;

namespace Readmd.Core;

/// <summary>
/// Parses markdown into a <see cref="MarkdownDocument"/>: HTML body for the browser,
/// a table of contents, and the list of mermaid/D2 diagrams referenced in the document.
/// This is the single source of truth shared by both the terminal and browser front-ends.
/// </summary>
public sealed class MarkdownRenderer
{
    // The browser pipeline is process-wide (building UseAdvancedExtensions et al. is not free);
    // share a single instance instead of rebuilding it per MarkdownRenderer.
    private static readonly MarkdownPipeline BrowserPipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()          // strip leading ---\n...\n--- so it doesn't leak as content
        // GitHub-style heading ids (keep leading numbers, preserve consecutive dashes, keep unicode)
        // so author-written self-links like "#1-intro" / "#appendix-a--b" resolve. This MUST precede
        // UseAdvancedExtensions(): that helper registers UseAutoIdentifiers() with Default options, and
        // UseAutoIdentifiers is a guarded add (it no-ops if already present) — so registering GitHub
        // first is what makes the options actually take effect.
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseAdvancedExtensions()       // tables, footnotes, task lists, alerts, etc.
        .UseEmojiAndSmiley()
        .UseMathematics()              // $...$ / $$...$$ -> KaTeX in browser
        .UseGenericAttributes()
        .UseTocMarker()                // [[_TOC_]]
        .Build();

    private readonly MarkdownPipeline _pipeline = BrowserPipeline;

    public MarkdownRenderer() { }

    public MarkdownDocument Parse(string sourcePath, string markdown)
    {
        var document = Markdown.Parse(markdown, _pipeline);

        var frontMatter = ExtractFrontMatter(document);
        var toc = TocExtractor.Extract(document);
        var diagrams = DiagramExtractor.Extract(document);
        var title = DeriveTitle(frontMatter, toc, sourcePath);
        var html = RenderHtml(document);
        html = FrontMatterHeader.Prepend(html, frontMatter);

        return new MarkdownDocument(sourcePath, title, html, toc, diagrams, markdown)
        {
            FrontMatter = frontMatter,
        };
    }

    /// <summary>
    /// Extracts the document metadata (front matter, table of contents, diagram list, title) from an
    /// already-parsed Markdig AST, <em>without</em> rendering HTML. Terminal/print callers use this so
    /// they parse the document once and never pay for the HTML render they don't display.
    /// </summary>
    public static DocumentMetadata ExtractMetadata(Markdig.Syntax.MarkdownDocument document, string sourcePath)
    {
        var frontMatter = ExtractFrontMatter(document);
        var toc = TocExtractor.Extract(document);
        var diagrams = DiagramExtractor.Extract(document);
        var title = DeriveTitle(frontMatter, toc, sourcePath);
        return new DocumentMetadata(title, toc, diagrams, frontMatter);
    }

    private static FrontMatter ExtractFrontMatter(Markdig.Syntax.MarkdownDocument document)
    {
        if (document.Count > 0 && document[0] is Markdig.Extensions.Yaml.YamlFrontMatterBlock yaml)
            return FrontMatter.FromBlock(yaml);
        return FrontMatter.Empty;
    }

    private string RenderHtml(MarkdownObject document)
    {
        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);

        // Swap in a custom code-block renderer that emits diagram containers for mermaid/d2.
        var existing = renderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
        if (existing is not null)
        {
            renderer.ObjectRenderers.Remove(existing);
        }
        renderer.ObjectRenderers.AddIfNotAlready(new DiagramAwareCodeBlockRenderer(existing));

        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static string DeriveTitle(FrontMatter frontMatter, IReadOnlyList<TocEntry> toc, string sourcePath)
    {
        // Front-matter `title:` wins, then the first H1, then any heading, then the file name.
        if (frontMatter.TryGet("title", out var fmTitle) && !string.IsNullOrWhiteSpace(fmTitle))
            return fmTitle;
        var firstH1 = toc.FirstOrDefault(t => t.Level == 1);
        if (firstH1 is not null) return firstH1.Title;
        var firstAny = toc.FirstOrDefault();
        if (firstAny is not null) return firstAny.Title;
        return Path.GetFileName(sourcePath);
    }
}
