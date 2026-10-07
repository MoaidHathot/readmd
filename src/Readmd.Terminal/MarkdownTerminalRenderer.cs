using System.Text;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Readmd.Core;

namespace Readmd.Terminal;

/// <summary>
/// Converts a parsed Markdig document into a flat list of <see cref="DisplayLine"/>s, word-wrapped
/// to a target width and styled with a <see cref="TerminalTheme"/>. Links are registered for
/// navigation, and mermaid/D2 code blocks become diagram anchors (image placement happens later).
/// </summary>
public sealed partial class MarkdownTerminalRenderer(TerminalTheme theme, int width)
{
    private readonly List<DisplayLine> _lines = [];
    private readonly List<TerminalLink> _links = [];
    // Anchor id -> index into _lines. Populated from heading ids AND explicit HTML anchors
    // (<a id>/<a name>/id=) so in-document "#fragment" links resolve the same targets the browser
    // sees in the DOM. Case-insensitive; first occurrence wins (mirrors getElementById).
    private readonly Dictionary<string, int> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private int _width = Math.Max(20, width);

    public IReadOnlyList<TerminalLink> Links => _links;

    private IReadOnlyList<TocEntry> _toc = [];

    public sealed record RenderResult(
        IReadOnlyList<DisplayLine> Lines,
        IReadOnlyList<TerminalLink> Links,
        IReadOnlyDictionary<string, int> Anchors,
        IReadOnlyDictionary<int, BlockInfo> Blocks,
        IReadOnlyList<FoldRegion> Folds);

    public RenderResult Render(MarkdownObject document, IReadOnlyList<TocEntry>? toc = null, FrontMatter? frontMatter = null, string? source = null)
    {
        _lines.Clear();
        _links.Clear();
        _anchors.Clear();
        _blocks.Clear();
        _folds.Clear();
        _openDetails.Clear();
        _abbreviationsShown.Clear();
        _source = source;
        _detailsIndent = 0;
        _detailsDepth = 0;
        _toc = toc ?? [];
        RenderFrontMatterHeader(frontMatter, FirstHeadingText(document));
        if (document is ContainerBlock container)
        {
            foreach (var block in container)
                RenderBlock(block, TopLevelIndent);
        }
        while (_openDetails.Count > 0) CloseDetails();
        TrimTrailingBlank();
        ComputeHeadingFolds();
        return new RenderResult(_lines, _links, _anchors, _blocks, _folds);
    }

    // ---------------- block identity & folds ----------------
    private readonly Dictionary<int, BlockInfo> _blocks = [];
    private readonly List<FoldRegion> _folds = [];
    private int _nextFoldId;

    /// <summary>
    /// Assigns a fresh block id (and kind) to every line emitted since <paramref name="start"/> that
    /// isn't already owned by a nested block, and records the block's metadata. Blank separator lines
    /// stay unowned. Returns the id, or 0 when nothing was tagged.
    /// </summary>
    private int TagBlock(int start, BlockKind kind, int sourceLine, string? text = null, string? diagramKey = null, int headingLevel = 0, string? language = null)
    {
        int id = 0;
        for (int i = start; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.BlockId != 0 || line.Spans.Count == 0) continue;
            if (id == 0) id = ++_blockCounter;
            line.BlockId = id;
            line.Kind = kind;
        }
        if (id != 0) _blocks[id] = new BlockInfo(id, kind, sourceLine, text, diagramKey, headingLevel, language);
        return id;
    }

    private void RegisterBlock(int id, BlockKind kind, int sourceLine, string? text = null, string? diagramKey = null, int headingLevel = 0, string? language = null)
    {
        if (id != 0) _blocks[id] = new BlockInfo(id, kind, sourceLine, text, diagramKey, headingLevel, language);
    }

    /// <summary>Heading sections are foldable: a heading hides everything up to the next heading of the same or a higher level.</summary>
    private void ComputeHeadingFolds()
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            int level = _lines[i].HeadingLevel;
            if (level == 0) continue;
            // The heading block itself may span several lines (wrapped title, H1/H2 rule).
            int headerBlock = _lines[i].BlockId;
            int bodyStart = i + 1;
            while (bodyStart < _lines.Count && _lines[bodyStart].BlockId == headerBlock) bodyStart++;
            int end = bodyStart;
            while (end < _lines.Count && !(_lines[end].HeadingLevel > 0 && _lines[end].HeadingLevel <= level)) end++;
            end--;   // inclusive
            while (end >= bodyStart && _lines[end].Spans.Count == 0) end--;
            if (end < bodyStart) continue;   // nothing to fold
            int id = ++_nextFoldId;
            _lines[i].FoldHeader = id;
            _folds.Add(new FoldRegion(id, i, bodyStart, end, DefaultOpen: true, IsHeading: true));
        }
    }

    // <details> blocks span several Markdig blocks (the opening HTML, Markdown body, the closing HTML);
    // track the open ones so the body can be indented and folded as a unit.
    private readonly Stack<(int FoldId, int HeaderLine, bool Open)> _openDetails = new();

    private void OpenDetails(bool open) => _openDetails.Push((++_nextFoldId, -1, open));

    private void SetDetailsHeader(int headerLine)
    {
        if (_openDetails.Count == 0) return;
        var top = _openDetails.Pop();
        _openDetails.Push((top.FoldId, headerLine, top.Open));
        _lines[headerLine].FoldHeader = top.FoldId;
    }

    private void CloseDetails()
    {
        if (_openDetails.Count == 0) return;
        var (id, header, open) = _openDetails.Pop();
        _detailsDepth = Math.Max(0, _detailsDepth - 1);
        _detailsIndent = Math.Max(0, _detailsIndent - 2);
        if (header < 0) return;
        int start = header + 1;
        int end = _lines.Count - 1;
        while (end >= start && _lines[end].Spans.Count == 0) end--;
        if (end < start) return;
        _folds.Add(new FoldRegion(id, header, start, end, DefaultOpen: open, IsHeading: false));
    }

    /// <summary>Maps an anchor id to the display line it should scroll to. Empty ids are ignored and
    /// the first registration of an id wins, matching how a browser resolves duplicate element ids.</summary>
    private void RegisterAnchor(string? id, int lineIndex)
    {
        if (!string.IsNullOrWhiteSpace(id))
            _anchors.TryAdd(id.Trim(), lineIndex);
    }

    // Captures the value of an id="" / name="" attribute (double, single, or unquoted) on an HTML
    // tag, while a negative look-behind for a word char or '-' avoids matching data-id, aria-*, etc.
    private static readonly System.Text.RegularExpressions.Regex HtmlAnchorIdRegex = new(
        "(?<![\\w-])(?:id|name)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'>]+))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Extracts every id/name anchor value from a raw HTML fragment.</summary>
    private static IEnumerable<string> ExtractHtmlAnchorIds(string html)
    {
        if (string.IsNullOrEmpty(html)) yield break;
        foreach (System.Text.RegularExpressions.Match m in HtmlAnchorIdRegex.Matches(html))
        {
            var value = m.Groups[1].Success ? m.Groups[1].Value
                : m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Value;
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
    }

    /// <summary>Plain text of the document's first H1, if it is the first heading (for de-duplicating the front-matter title).</summary>
    private static string? FirstHeadingText(MarkdownObject document)
    {
        if (document is not ContainerBlock container) return null;
        foreach (var block in container)
        {
            if (block is HeadingBlock h)
                return h.Level == 1 && h.Inline is not null ? GetInlineText(h.Inline).Trim() : null;
        }
        return null;
    }

    // Renders a compact metadata header (title/subtitle/author·date/tags) from YAML front matter.
    // Emits nothing when there's no recognized metadata, so plain documents look unchanged. A title
    // that merely repeats the document's first H1 is left out so it doesn't read twice.
    private void RenderFrontMatterHeader(FrontMatter? fm, string? firstHeading = null)
    {
        if (fm is null || fm.IsEmpty) return;
        var title = fm.Get("title");
        if (title is not null && firstHeading is not null && string.Equals(title.Trim(), firstHeading, StringComparison.OrdinalIgnoreCase))
            title = null;
        var subtitle = fm.Get("subtitle") ?? fm.Get("description");
        var author = fm.Get("author") ?? fm.Get("authors");
        var date = fm.Get("date");
        var tags = fm.GetList("tags") ?? fm.GetList("keywords");

        var hasAny = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(subtitle)
                     || !string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(date)
                     || tags is { Count: > 0 };
        if (!hasAny) return;
        int start = _lines.Count;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var line = new DisplayLine { LineBackground = theme.BackgroundElevated };
            line.Spans.Add(new StyledSpan("  ", null, CellStyle.None, null, theme.BackgroundElevated));
            line.Spans.Add(new StyledSpan(title, theme.H1, CellStyle.Bold, null, theme.BackgroundElevated));
            _lines.Add(line);
        }
        if (!string.IsNullOrWhiteSpace(subtitle))
            EmitWrapped([new StyledSpan(subtitle, theme.Muted, CellStyle.Italic)], 0);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(author)) meta.Add(author);
        if (!string.IsNullOrWhiteSpace(date)) meta.Add(date);
        if (meta.Count > 0)
            EmitWrapped([new StyledSpan(string.Join("  ·  ", meta), theme.Muted)], 0);

        if (tags is { Count: > 0 })
            EmitWrapped([new StyledSpan(string.Join("  ", tags.Select(t => $"#{t}")), theme.Accent)], 0);

        var rule = new DisplayLine();
        rule.Spans.Add(new StyledSpan(new string(Glyphs.Rule, Math.Max(8, _width - 1)), theme.Rule));
        _lines.Add(rule);
        TagBlock(start, BlockKind.FrontMatter, 1, text: title);
        AddBlank();
    }

    private void RenderBlock(Block block, int indent)
    {
        int start = _lines.Count;
        switch (block)
        {
            case HeadingBlock heading: RenderHeading(heading); break;
            case ParagraphBlock para: RenderParagraph(para, indent); break;
            case ListBlock list: RenderList(list, indent); break;
            case AlertBlock alert: RenderAlert(alert, indent); break;
            case QuoteBlock quote: RenderQuote(quote, indent); break;
            case MathBlock math: RenderMathBlock(math, indent); break;
            case Markdig.Extensions.Yaml.YamlFrontMatterBlock: break; // stripped: never render front matter
            case FencedCodeBlock fenced: RenderFenced(fenced, indent); break;
            case CodeBlock code: RenderIndentedCode(code, indent); break;
            case Table table: RenderTable(table, indent); break;
            case ThematicBreakBlock: RenderRule(); break;
            case Markdig.Syntax.HtmlBlock html: RenderHtmlBlock(html, indent); break;
            case Markdig.Extensions.Footnotes.FootnoteGroup footnotes: RenderFootnotes(footnotes); break;
            case Markdig.Extensions.DefinitionLists.DefinitionList defList: RenderDefinitionList(defList, indent); break;
            case Markdig.Extensions.CustomContainers.CustomContainer container: RenderCustomContainer(container, indent); break;
            case Markdig.Extensions.Figures.FigureCaption caption: RenderFigureCaption(caption, indent); break;
            case Markdig.Extensions.Footers.FooterBlock footer: RenderFooter(footer, indent); break;
            case ContainerBlock generic:
                foreach (var child in generic) RenderBlock(child, indent);
                break;
            default:
                // Unknown leaf: render its text if any.
                if (block is LeafBlock leaf && leaf.Inline is not null)
                    EmitWrapped(InlineToSpans(leaf.Inline, theme.Text), indent);
                break;
        }
        // Anything the specific emitter didn't claim belongs to this block (e.g. the paragraph text
        // of a quote, a table, a rule). Nested blocks keep their own ids.
        TagBlock(start, KindOf(block), block.Line + 1, text: block is MathBlock m ? m.Lines.ToString() : null);
    }

    private static BlockKind KindOf(Block block) => block switch
    {
        HeadingBlock => BlockKind.Heading,
        ParagraphBlock => BlockKind.Paragraph,
        ListBlock or ListItemBlock => BlockKind.ListItem,
        AlertBlock => BlockKind.Alert,
        QuoteBlock => BlockKind.Quote,
        MathBlock => BlockKind.Math,
        FencedCodeBlock or CodeBlock => BlockKind.Code,
        Table => BlockKind.Table,
        ThematicBreakBlock => BlockKind.Rule,
        Markdig.Syntax.HtmlBlock => BlockKind.Html,
        Markdig.Extensions.Footnotes.FootnoteGroup => BlockKind.Footnote,
        Markdig.Extensions.CustomContainers.CustomContainer => BlockKind.Quote,
        _ => BlockKind.Paragraph,
    };

    // ---------------- headings ----------------
    private void RenderHeading(HeadingBlock heading)
    {
        var id = heading.GetAttributes().Id;
        var titleText = TocExtractor.GetPlainText(heading);
        EmitHeading(heading.Level, (color, style) => InlineToSpans(heading.Inline, color, style), titleText, id, heading.Line + 1);
    }

    /// <summary>
    /// Emits a heading of the given level. <paramref name="spansFor"/> produces the inline spans
    /// given the level's colour and base style (so Markdown and HTML headings share one look):
    /// H1 is a banner row with an elevated background and a heavy underline, H2 a marker plus a
    /// full-width rule, H3–H6 a marker with distinct glyphs per level.
    /// </summary>
    internal void EmitHeading(int level, Func<Rgb, CellStyle, List<StyledSpan>> spansFor, string titleText, string? id, int sourceLine)
    {
        EnsureBlankBefore();
        // The heading line is emitted next (at _lines.Count), for both the H1 and H2+ paths below.
        RegisterAnchor(id, _lines.Count);
        int start = _lines.Count;

        if (level <= 1)
        {
            // H1: a bold banner row with a colored background bar. Inline formatting (code, emphasis,
            // links) is preserved and long titles wrap instead of being clipped.
            var h1 = spansFor(theme.H1, CellStyle.Bold);
            EmitWrapped(h1, 2, headingId: id, sourceLine: sourceLine, lineBackground: theme.BackgroundElevated);
            // underline rule
            var rule = new DisplayLine();
            rule.Spans.Add(new StyledSpan(new string(Glyphs.H1Rule, Math.Max(8, Math.Min(_width - 1, TextWidth.Of(titleText) + 4))), theme.H1));
            _lines.Add(rule);
        }
        else
        {
            // H2/H3+: a colored marker + bold title; H2 also gets a thin underline. Each level has its
            // own glyph so H4/H5/H6 remain distinguishable.
            var (marker, color, extraStyle) = level switch
            {
                2 => (Glyphs.H2Marker, theme.H2, CellStyle.None),
                3 => (Glyphs.H3Marker, theme.H3, CellStyle.None),
                4 => (Glyphs.H4Marker, theme.Heading, CellStyle.None),
                5 => (Glyphs.H5Marker, theme.Heading, CellStyle.None),
                _ => (Glyphs.H6Marker, theme.Muted, CellStyle.Italic),
            };
            var spans = new List<StyledSpan> { new(marker, color, CellStyle.Bold) };
            spans.AddRange(spansFor(color, CellStyle.Bold | extraStyle));
            EmitWrapped(spans, 0, headingId: id, sourceLine: sourceLine);
            if (level == 2)
            {
                var rule = new DisplayLine();
                rule.Spans.Add(new StyledSpan(new string(Glyphs.Rule, Math.Max(8, _width - 1)), theme.Rule));
                _lines.Add(rule);
            }
        }
        _lines[start].HeadingLevel = Math.Max(1, level);
        TagBlock(start, BlockKind.Heading, sourceLine, text: titleText, headingLevel: Math.Max(1, level));
        AddBlank();
    }

    // ---------------- paragraphs ----------------
    private void RenderParagraph(ParagraphBlock para, int indent)
    {
        if (para.Inline is null) return;

        if (IsTocMarker(para))
        {
            EmitToc(indent);
            return;
        }

        EmitInlineBlock(para.Inline, indent, sourceLine: para.Line + 1);
        AddBlank();
    }

    /// <summary>
    /// Emits a paragraph-like inline run in any context (top level, list item, quote, alert,
    /// footnote, definition, container). Images inside it — Markdown <c>![..](..)</c> or HTML
    /// <c>&lt;img&gt;</c> — are rendered inline as image anchors below the text (several images
    /// with no text, e.g. a badge row, become one side-by-side strip, like a browser), so images
    /// show up wherever they appear instead of degrading to a placeholder.
    /// </summary>
    private void EmitInlineBlock(ContainerInline inline, int indent, int sourceLine = 0,
        Rgb? baseColor = null, CellStyle baseStyle = CellStyle.None,
        Rgb? gutterColor = null, string? gutter = null, bool repeatGutter = true, string? headingId = null)
    {
        var color = baseColor ?? theme.Text;
        var images = CollectImages(inline);
        if (images.Count == 0)
        {
            EmitWrapped(InlineToSpans(inline, color, baseStyle), indent, headingId: headingId, sourceLine: sourceLine,
                gutterColor: gutterColor, gutter: gutter, repeatGutter: repeatGutter);
            return;
        }

        bool hasText = HasNonImageText(inline);
        // The gutter (bullet, quote bar) goes on whichever line comes first: the text, or the image.
        string? imageGutter = gutter;
        if (hasText)
        {
            _suppressImagePlaceholders++;
            var textSpans = InlineToSpans(inline, color, baseStyle);
            _suppressImagePlaceholders--;
            if (textSpans.Any(s => !string.IsNullOrWhiteSpace(s.Text) && s.Text != "\n"))
            {
                EmitWrapped(textSpans, indent, headingId: headingId, sourceLine: sourceLine,
                    gutterColor: gutterColor, gutter: gutter, repeatGutter: repeatGutter);
                imageGutter = repeatGutter ? gutter : null;
            }
            foreach (var img in images)
            {
                EmitImageAnchor(img.Url, img.Alt, img.LinkUrl, indent, gutterColor, imageGutter);
                if (!repeatGutter) imageGutter = null;
            }
        }
        else if (images.Count == 1)
        {
            EmitImageAnchor(images[0].Url, images[0].Alt, images[0].LinkUrl, indent, gutterColor, imageGutter);
        }
        else
        {
            EmitImageGroupAnchor(images, indent, gutterColor, imageGutter);
        }
    }

    // >0 while a block-level emitter is collecting a paragraph's images itself, so inline image
    // placeholders ("🖼 alt") are not also written into the text.
    private int _suppressImagePlaceholders;

    /// <summary>True if the inline tree has any literal/code text that is NOT inside an image.</summary>
    private static bool HasNonImageText(Markdig.Syntax.Inlines.ContainerInline container)
    {
        bool found = false;
        void Walk(Markdig.Syntax.Inlines.Inline inline)
        {
            if (found) return;
            switch (inline)
            {
                case Markdig.Syntax.Inlines.LinkInline { IsImage: true } img:
                    // Media embeds render as text links, so they count as text; real images don't.
                    if (IsMediaUrl(img.Url)) found = true;
                    return;
                case Markdig.Syntax.Inlines.LiteralInline lit:
                    if (!string.IsNullOrWhiteSpace(lit.Content.ToString())) found = true;
                    return;
                case Markdig.Syntax.Inlines.HtmlEntityInline:
                case Markdig.Syntax.Inlines.CodeInline:
                case Markdig.Syntax.Inlines.AutolinkInline:
                    found = true;
                    return;
                case Markdig.Syntax.Inlines.ContainerInline c:
                    foreach (var child in c) Walk(child);
                    return;
            }
        }
        foreach (var child in container) Walk(child);
        return found;
    }

    private static List<(string Url, string Alt, string? LinkUrl)> CollectImages(Markdig.Syntax.Inlines.ContainerInline container)
    {
        var result = new List<(string, string, string?)>();
        // Track the nearest enclosing (non-image) link so an image wrapped in a link becomes
        // clickable: [![alt](img)](href). Raw HTML <a href> … <img> … </a> is tracked the same way.
        string? htmlLink = null;
        void Walk(Markdig.Syntax.Inlines.Inline inline, string? enclosingLink)
        {
            if (inline is Markdig.Syntax.Inlines.LinkInline link)
            {
                if (link.IsImage)
                {
                    if (IsMediaUrl(link.Url)) return;   // audio/video: rendered as a text link instead
                    var alt = GetInlineText(link);
                    result.Add((link.Url ?? "", alt, enclosingLink ?? htmlLink));
                    return;
                }
                // A normal link: its children (which may include an image) get this as the target.
                foreach (var child in link) Walk(child, link.Url ?? enclosingLink);
                return;
            }
            if (inline is Markdig.Syntax.Inlines.HtmlInline html)
            {
                foreach (var tok in HtmlFragments.Tokenize(html.Tag))
                {
                    if (tok.Kind == HtmlFragments.TokenKind.Open && tok.Name == "a" && tok.Attr("href") is { Length: > 0 } href)
                        htmlLink = href;
                    else if (tok.Kind == HtmlFragments.TokenKind.Close && tok.Name == "a")
                        htmlLink = null;
                    else if (tok.Kind == HtmlFragments.TokenKind.Open && tok.Name == "img" && tok.Attr("src") is { Length: > 0 } src)
                        result.Add((src, tok.Attr("alt") ?? "", enclosingLink ?? htmlLink));
                }
                return;
            }
            if (inline is Markdig.Syntax.Inlines.ContainerInline c)
            {
                foreach (var child in c) Walk(child, enclosingLink);
            }
        }
        foreach (var child in container) Walk(child, null);
        return result;
    }

    private static bool IsTocMarker(ParagraphBlock para)
    {
        // The marker is "[[_TOC_]]" on its own line; emphasis parsing may turn "_TOC_" into
        // an <em>, so compare against the de-emphasized text too.
        var text = TocExtractor.GetPlainText(para).Trim();
        return text is "[[_TOC_]]" or "[[TOC]]";
    }

    private void EmitToc(int indent)
    {
        if (_toc.Count == 0) return;
        EnsureBlankBefore();
        int start = _lines.Count;
        foreach (var entry in _toc)
        {
            var line = new DisplayLine();
            var pad = new string(' ', indent + (entry.Level - 1) * 2);
            var id = RegisterLink("#" + entry.Id);
            line.Spans.Add(new StyledSpan(pad + Glyphs.Bullet1, theme.Muted));
            line.Spans.Add(new StyledSpan(entry.Title, theme.Link, CellStyle.Underline, id));
            _lines.Add(line);
        }
        TagBlock(start, BlockKind.Toc, 0);
        AddBlank();
    }

    // ---------------- inline conversion ----------------
    private List<StyledSpan> InlineToSpans(ContainerInline? container, Rgb baseColor, CellStyle baseStyle = CellStyle.None)
    {
        var spans = new List<StyledSpan>();
        if (container is null) return spans;
        // Inline HTML styling (<b>, <kbd>, <a href>, …) is scoped to one inline run: an unclosed
        // tag never leaks into the next block.
        _html.ResetInline();
        foreach (var inline in container)
            AppendInline(inline, spans, baseColor, baseStyle, null);
        _html.ResetInline();
        return spans;
    }

    /// <summary>
    /// Adds a text leaf, applying any active inline-HTML overlay (style flags, colour, link, sub/
    /// superscript transform, keycap chrome) on top of the Markdown-derived style.
    /// </summary>
    private void Leaf(List<StyledSpan> spans, string text, Rgb? color, CellStyle style, int? linkId, Rgb? background = null)
    {
        if (text.Length == 0) return;
        var o = _html.Overlay;
        if (o.IsEmpty)
        {
            spans.Add(new StyledSpan(text, color, style, linkId, background));
            return;
        }
        if (o.Superscript) text = MathRenderer.ToScript(text, superscript: true);
        else if (o.Subscript) text = MathRenderer.ToScript(text, superscript: false);
        spans.Add(new StyledSpan(text, o.Color ?? (o.LinkId is not null ? theme.Link : color), style | o.Style, o.LinkId ?? linkId, o.Background ?? background));
    }

    private void AppendInline(Inline inline, List<StyledSpan> spans, Rgb color, CellStyle style, int? linkId)
    {
        switch (inline)
        {
            case LiteralInline literal:
                Leaf(spans, literal.Content.ToString(), color, style, linkId);
                break;
            case EmphasisInline emphasis:
            {
                // GitHub/Markdig EmphasisExtras map several delimiters onto EmphasisInline:
                //   *x* / _x_      → italic        **x** / __x__ → bold
                //   ~~x~~          → strikethrough  ~x~          → subscript
                //   ^x^            → superscript    ++x++        → inserted (underline)
                //   ==x==          → marked / highlight (reverse video)
                switch (emphasis.DelimiterChar, emphasis.DelimiterCount)
                {
                    case ('~', 1):  // subscript — convert inner text to Unicode subscript glyphs
                        AppendScripted(emphasis, spans, color, style, linkId, subscript: true);
                        break;
                    case ('^', 1):  // superscript
                        AppendScripted(emphasis, spans, color, style, linkId, subscript: false);
                        break;
                    case ('~', _): // ~~ (and longer) → strikethrough
                    {
                        var s = style | CellStyle.Strikethrough;
                        foreach (var child in emphasis) AppendInline(child, spans, color, s, linkId);
                        break;
                    }
                    case ('+', _): // ++inserted++ → underline
                    {
                        var s = style | CellStyle.Underline;
                        foreach (var child in emphasis) AppendInline(child, spans, color, s, linkId);
                        break;
                    }
                    case ('=', _): // ==marked== → highlight (reverse video so it reads on any theme)
                    {
                        var s = style | CellStyle.Reverse;
                        foreach (var child in emphasis) AppendInline(child, spans, color, s, linkId);
                        break;
                    }
                    case ('"', _): // ""citation"" → a cited reference, shown italic in muted color
                    {
                        Leaf(spans, "“", theme.Muted, style, linkId);
                        foreach (var child in emphasis) AppendInline(child, spans, theme.Muted, style | CellStyle.Italic, linkId);
                        Leaf(spans, "”", theme.Muted, style, linkId);
                        break;
                    }
                    default:
                    {
                        var s = style | (emphasis.DelimiterCount >= 2 ? CellStyle.Bold : CellStyle.Italic);
                        foreach (var child in emphasis) AppendInline(child, spans, color, s, linkId);
                        break;
                    }
                }
                break;
            }
            case CodeInline code:
                if (TryColorChip(code.Content, out var chip))
                {
                    // GitHub shows a colour swatch after `#rrggbb` / rgb() / hsl() inline code.
                    Leaf(spans, code.Content, theme.Code, style, linkId);
                    spans.Add(new StyledSpan(" " + Glyphs.ColorChip, chip, style, linkId));
                    break;
                }
                Leaf(spans, code.Content, theme.Code, style, linkId);
                break;
            case MathInline math:
                Leaf(spans, MathRenderer.ToUnicode(math.Content.ToString()), theme.Code, style, linkId);
                break;
            case LinkInline link:
            {
                if (link.IsImage)
                {
                    var alt = GetInlineText(link);
                    if (IsMediaUrl(link.Url))
                    {
                        // Audio/video embeds (YouTube, Vimeo, .mp4, .mp3, …) can't play in a terminal:
                        // show a play marker + alt as a followable link instead of fetching the page
                        // as if it were an image.
                        var mediaId = RegisterLink(link.Url ?? "");
                        spans.Add(new StyledSpan(Glyphs.Media, theme.Accent, style, mediaId));
                        spans.Add(new StyledSpan(string.IsNullOrWhiteSpace(alt) ? link.Url ?? "media" : alt, theme.Link, style | CellStyle.Underline, mediaId));
                        AppendLinkMarker(spans, mediaId);
                        break;
                    }
                    if (_suppressImagePlaceholders == 0)
                        spans.Add(new StyledSpan($"{Glyphs.ImagePlaceholder} {alt} ({link.Url})", theme.Muted, style | CellStyle.Italic, linkId));
                    break;
                }
                var id = RegisterLink(link.Url ?? "");
                var linkColor = theme.Link;
                var linkStyle = style | CellStyle.Underline;
                foreach (var child in link) AppendInline(child, spans, linkColor, linkStyle, id);
                AppendLinkMarker(spans, id);
                break;
            }
            case AutolinkInline auto:
            {
                var id = RegisterLink(auto.Url);
                Leaf(spans, auto.Url, theme.Link, style | CellStyle.Underline, id);
                AppendLinkMarker(spans, id);
                break;
            }
            case LineBreakInline lb:
                spans.Add(new StyledSpan(lb.IsHard ? "\n" : " ", color, style, linkId));
                break;
            case HtmlEntityInline entity:
                // &amp; &copy; &#169; &nbsp; … — emit the decoded character(s), never drop them.
                Leaf(spans, entity.Transcoded.ToString(), color, style, linkId);
                break;
            case Markdig.Extensions.SmartyPants.SmartyPant pant:
                Leaf(spans, TocExtractor.SmartyPantText(pant), color, style, linkId);
                break;
            case Markdig.Extensions.TaskLists.TaskList task:
                // GitHub task list checkbox. ☑ when checked, ☐ when unchecked. The literal that follows
                // already starts with the source's space, so the glyph itself carries none.
                spans.Add(new StyledSpan((task.Checked ? Glyphs.Checked : Glyphs.Unchecked).TrimEnd(),
                    task.Checked ? (theme.IsDark ? Rgb.FromHex("#3fb950") : Rgb.FromHex("#1a7f37")) : theme.Muted,
                    style, linkId));
                break;
            case Markdig.Extensions.Footnotes.FootnoteLink footnote:
                AppendFootnoteRef(footnote, spans, style, linkId);
                break;
            case Markdig.Extensions.Abbreviations.AbbreviationInline abbr:
            {
                // The abbreviated term itself (otherwise it would vanish), underlined. The first time
                // each abbreviation appears, its definition follows once, dimmed in parentheses, so
                // the expansion is visible in a terminal (there's no hover tooltip).
                var label = abbr.Abbreviation.Label ?? "";
                Leaf(spans, label, color, style | CellStyle.Underline, linkId);
                var expansion = abbr.Abbreviation.Text.ToString().Trim();
                if (expansion.Length > 0 && _abbreviationsShown.Add(label))
                    spans.Add(new StyledSpan($" ({expansion})", theme.Muted, style | CellStyle.Dim, linkId));
                break;
            }
            case Markdig.Syntax.Inlines.HtmlInline html:
                AppendInlineHtml(html.Tag, spans, color, style, linkId);
                break;
            case ContainerInline c:
                foreach (var child in c) AppendInline(child, spans, color, style, linkId);
                break;
            default:
                break;
        }
    }

    // Abbreviations whose expansion has already been shown once in this document.
    private readonly HashSet<string> _abbreviationsShown = new(StringComparer.Ordinal);

    /// <summary>
    /// Handles a raw inline HTML tag inside a Markdown paragraph: line breaks, anchors, images
    /// (collected by the enclosing block), and the inline formatting subset (<c>&lt;b&gt;</c>,
    /// <c>&lt;kbd&gt;</c>, <c>&lt;sup&gt;</c>, <c>&lt;a href&gt;</c>, <c>&lt;span style=color&gt;</c>, …)
    /// which is applied as an overlay to the following text until the matching close tag.
    /// </summary>
    private void AppendInlineHtml(string tag, List<StyledSpan> spans, Rgb color, CellStyle style, int? linkId)
    {
        foreach (var anchorId in ExtractHtmlAnchorIds(tag))
            RegisterAnchor(anchorId, _lines.Count);
        foreach (var tok in HtmlFragments.Tokenize(tag))
        {
            switch (tok.Kind)
            {
                case HtmlFragments.TokenKind.Open when tok.Name == "br":
                    spans.Add(new StyledSpan("\n", color, style, linkId));
                    break;
                case HtmlFragments.TokenKind.Open when tok.Name == "img":
                    if (_suppressImagePlaceholders == 0 && tok.Attr("src") is { Length: > 0 } src)
                        spans.Add(new StyledSpan($"{Glyphs.ImagePlaceholder} {tok.Attr("alt") ?? ""} ({src})".Replace("  ", " "), theme.Muted, style | CellStyle.Italic, linkId));
                    break;
                case HtmlFragments.TokenKind.Open when tok.Name == "abbr":
                    _html.Push(tok, this);
                    break;
                case HtmlFragments.TokenKind.Open:
                    _html.Push(tok, this);
                    break;
                case HtmlFragments.TokenKind.Close:
                    _html.Pop(tok.Name);
                    break;
                case HtmlFragments.TokenKind.Text:
                    // A stray text fragment inside what Markdig classed as a tag — keep it visible.
                    Leaf(spans, tok.Text, color, style, linkId);
                    break;
            }
        }
    }

    /// <summary>GitHub-style colour chips: inline code that is exactly a CSS colour literal.</summary>
    private static bool TryColorChip(string code, out Rgb color)
    {
        color = default;
        var s = code.Trim();
        if (s.Length is 4 or 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit))
        {
            try { color = Rgb.FromHex(s); return true; } catch { return false; }
        }
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            color = new Rgb((byte)Math.Min(255, int.Parse(m.Groups[1].Value)), (byte)Math.Min(255, int.Parse(m.Groups[2].Value)), (byte)Math.Min(255, int.Parse(m.Groups[3].Value)));
            return true;
        }
        m = System.Text.RegularExpressions.Regex.Match(s, @"^hsl\(\s*(\d{1,3})\s*,\s*(\d{1,3})%\s*,\s*(\d{1,3})%\s*\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            color = HslToRgb(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value) / 100.0, int.Parse(m.Groups[3].Value) / 100.0);
            return true;
        }
        return false;
    }

    private static Rgb HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double Channel(double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return new Rgb((byte)Math.Round(Channel(h + 1.0 / 3) * 255), (byte)Math.Round(Channel(h) * 255), (byte)Math.Round(Channel(h - 1.0 / 3) * 255));
    }

    private static readonly string[] MediaExtensions = [".mp4", ".webm", ".ogv", ".mov", ".mp3", ".ogg", ".wav", ".m4a", ".flac"];

    /// <summary>
    /// True when an image URL actually points at audio/video (a media embed in GitHub/Markdig terms):
    /// a known streaming host or a media file extension. Such targets must not be fetched as images.
    /// </summary>
    internal static bool IsMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) 
        {
            var ext = Path.GetExtension(url.Split('?', '#')[0]).ToLowerInvariant();
            return MediaExtensions.Contains(ext);
        }
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("youtube.com") || host.EndsWith("youtu.be") || host.EndsWith("vimeo.com")
            || host.EndsWith("youtube-nocookie.com") || host.EndsWith("dailymotion.com"))
            return true;
        var pathExt = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return MediaExtensions.Contains(pathExt);
    }

    private static string GetInlineText(ContainerInline container)
    {
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            if (inline is LiteralInline lit) sb.Append(lit.Content.AsSpan());
            else if (inline is HtmlEntityInline entity) sb.Append(entity.Transcoded.AsSpan());
            else if (inline is Markdig.Extensions.SmartyPants.SmartyPant pant) sb.Append(TocExtractor.SmartyPantText(pant));
            else if (inline is Markdig.Extensions.Abbreviations.AbbreviationInline abbr) sb.Append(abbr.Abbreviation.Label);
            else if (inline is CodeInline code) sb.Append(code.Content);
            else if (inline is ContainerInline c) sb.Append(GetInlineText(c));
        }
        return sb.ToString();
    }

    private int RegisterLink(string url)
    {
        var id = _links.Count;
        _links.Add(new TerminalLink(id, NormalizeHref(url)));
        return id;
    }

    /// <summary>
    /// Normalizes a link target so it actually opens when followed: bare <c>www.</c> GFM autolinks
    /// get an <c>https://</c> scheme, and bare email addresses get a <c>mailto:</c> scheme.
    /// </summary>
    private static string NormalizeHref(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var u = url.Trim();
        if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return "https://" + u;
        // Looks like a bare email (has @, no scheme, no spaces/slashes).
        if (!u.Contains("://") && !u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            && u.Contains('@') && !u.Contains(' ') && !u.Contains('/'))
            return "mailto:" + u;
        return u;
    }

    private static readonly string[] SuperscriptDigits = { "⁰", "¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹" };

    /// <summary>
    /// Optionally appends a small superscript ordinal after a link. Off by default: links are
    /// followed via hint mode (<c>f</c>), Tab cycling, or clicks, so the markers only add noise.
    /// </summary>
    private void AppendLinkMarker(List<StyledSpan> spans, int id)
    {
        if (!LinkMarkers) return;
        int ordinal = id + 1;
        if (ordinal < 1 || ordinal > 9) return;
        spans.Add(new StyledSpan(SuperscriptDigits[ordinal], theme.Accent, CellStyle.Bold, id));
    }

    /// <summary>Show superscript ordinals after the first nine links (legacy 1–9 navigation aid).</summary>
    public bool LinkMarkers { get; init; }

    /// <summary>Renders a footnote reference ([^1]) as a superscript number.</summary>
    private void AppendFootnoteRef(Markdig.Extensions.Footnotes.FootnoteLink footnote, List<StyledSpan> spans, CellStyle style, int? linkId)
    {
        if (footnote.IsBackLink) return;   // we don't render the back-reference arrows
        int n = footnote.Footnote?.Order ?? footnote.Index + 1;
        spans.Add(new StyledSpan(Glyphs.Superscript(n), theme.Accent, style | CellStyle.Bold, linkId));
    }

    /// <summary>Renders sub/superscript emphasis (~x~ / ^x^) by converting the inner text to Unicode
    /// super/subscript glyphs.</summary>
    private void AppendScripted(EmphasisInline emphasis, List<StyledSpan> spans, Rgb color, CellStyle style, int? linkId, bool subscript)
    {
        var inner = GetInlineText(emphasis);
        spans.Add(new StyledSpan(MathRenderer.ToScript(inner, superscript: !subscript), color, style, linkId));
    }

    /// <summary>Converts a non-negative integer to its Unicode superscript form.</summary>
    private static string ToSuperscript(int n)
    {
        if (n < 0) return "";
        var s = n.ToString();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(SuperscriptDigits[ch - '0']);
        return sb.ToString();
    }

    // ---------------- helpers for line emission live in Emit.cs (partial) ----------------
    private void AddBlank()
    {
        if (_lines.Count > 0 && _lines[^1].VisibleLength == 0 && _lines[^1].DiagramKey is null) return;
        _lines.Add(DisplayLine.Empty());
    }

    private void EnsureBlankBefore()
    {
        if (_lines.Count == 0) return;
        if (_lines[^1].VisibleLength != 0) _lines.Add(DisplayLine.Empty());
    }

    private void TrimTrailingBlank()
    {
        while (_lines.Count > 0 && _lines[^1].VisibleLength == 0 && _lines[^1].DiagramKey is null)
            _lines.RemoveAt(_lines.Count - 1);
    }

    internal int Width => _width;
    internal TerminalTheme Theme => theme;
    internal List<DisplayLine> Lines => _lines;
}

public sealed record TerminalLink(int Id, string Url);
