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
        IReadOnlyDictionary<string, int> Anchors);

    public RenderResult Render(MarkdownObject document, IReadOnlyList<TocEntry>? toc = null, FrontMatter? frontMatter = null)
    {
        _lines.Clear();
        _links.Clear();
        _anchors.Clear();
        _toc = toc ?? [];
        RenderFrontMatterHeader(frontMatter);
        if (document is ContainerBlock container)
        {
            foreach (var block in container)
                RenderBlock(block, 0);
        }
        TrimTrailingBlank();
        return new RenderResult(_lines, _links, _anchors);
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

    // Renders a compact metadata header (title/subtitle/author·date/tags) from YAML front matter.
    // Emits nothing when there's no recognized metadata, so plain documents look unchanged.
    private void RenderFrontMatterHeader(FrontMatter? fm)
    {
        if (fm is null || fm.IsEmpty) return;
        var title = fm.Get("title");
        var subtitle = fm.Get("subtitle") ?? fm.Get("description");
        var author = fm.Get("author") ?? fm.Get("authors");
        var date = fm.Get("date");
        var tags = fm.GetList("tags") ?? fm.GetList("keywords");

        var hasAny = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(subtitle)
                     || !string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(date)
                     || tags is { Count: > 0 };
        if (!hasAny) return;

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
        rule.Spans.Add(new StyledSpan(new string('─', Math.Max(8, _width - 1)), theme.Rule));
        _lines.Add(rule);
        AddBlank();
    }

    private void RenderBlock(Block block, int indent)
    {
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
    }

    // ---------------- headings ----------------
    private void RenderHeading(HeadingBlock heading)
    {
        EnsureBlankBefore();
        var id = heading.GetAttributes().Id;
        var titleText = TocExtractor.GetPlainText(heading);
        // The heading line is emitted next (at _lines.Count), for both the H1 and H2+ paths below.
        RegisterAnchor(id, _lines.Count);

        if (heading.Level == 1)
        {
            // H1: a bold banner row with a colored background bar.
            var line = new DisplayLine { HeadingId = id, SourceLine = heading.Line + 1, LineBackground = theme.BackgroundElevated };
            line.Spans.Add(new StyledSpan("  ", null, CellStyle.None, null, theme.BackgroundElevated));
            line.Spans.Add(new StyledSpan(titleText, theme.H1, CellStyle.Bold, null, theme.BackgroundElevated));
            _lines.Add(line);
            // underline rule
            var rule = new DisplayLine();
            rule.Spans.Add(new StyledSpan(new string('━', Math.Max(8, Math.Min(_width - 1, titleText.Length + 4))), theme.H1));
            _lines.Add(rule);
            AddBlank();
            return;
        }

        // H2/H3+: a colored marker + bold title; H2 also gets a thin underline.
        var (marker, color) = heading.Level switch
        {
            2 => ("▌ ", theme.H2),
            3 => ("● ", theme.H3),
            4 => ("◗ ", theme.Heading),
            _ => ("· ", theme.Muted),
        };
        var spans = new List<StyledSpan> { new(marker, color, CellStyle.Bold) };
        spans.AddRange(InlineToSpans(heading.Inline, color, CellStyle.Bold));
        EmitWrapped(spans, 0, headingId: id, sourceLine: heading.Line + 1);
        if (heading.Level == 2)
        {
            var rule = new DisplayLine();
            rule.Spans.Add(new StyledSpan(new string('─', Math.Max(8, _width - 1)), theme.Rule));
            _lines.Add(rule);
        }
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

        // If the paragraph contains image(s), render them inline (Sixel). Multiple images with no
        // meaningful text (e.g. a row of badges) are laid out SIDE-BY-SIDE in one strip, like a
        // browser. A single image, or images mixed with text, render one per line.
        var images = CollectImages(para.Inline);
        if (images.Count > 0)
        {
            bool hasText = HasNonImageText(para.Inline);
            if (hasText)
            {
                var textSpans = InlineToSpans(para.Inline, theme.Text)
                    .Where(s => !s.Text.StartsWith("🖼"))
                    .ToList();
                if (textSpans.Any(s => !string.IsNullOrWhiteSpace(s.Text)))
                    EmitWrapped(textSpans, indent, sourceLine: para.Line + 1);
                foreach (var img in images)
                    EmitImageAnchor(img.Url, img.Alt, img.LinkUrl);
            }
            else if (images.Count == 1)
            {
                EmitImageAnchor(images[0].Url, images[0].Alt, images[0].LinkUrl);
            }
            else
            {
                EmitImageGroupAnchor(images);
            }
            AddBlank();
            return;
        }

        var spans = InlineToSpans(para.Inline, theme.Text);
        EmitWrapped(spans, indent, sourceLine: para.Line + 1);
        AddBlank();
    }

    /// <summary>True if the inline tree has any literal/code text that is NOT inside an image.</summary>
    private static bool HasNonImageText(Markdig.Syntax.Inlines.ContainerInline container)
    {
        bool found = false;
        void Walk(Markdig.Syntax.Inlines.Inline inline)
        {
            if (found) return;
            switch (inline)
            {
                case Markdig.Syntax.Inlines.LinkInline { IsImage: true }:
                    return; // skip image subtrees (their alt text isn't shown)
                case Markdig.Syntax.Inlines.LiteralInline lit:
                    if (!string.IsNullOrWhiteSpace(lit.Content.ToString())) found = true;
                    return;
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
        // clickable: [![alt](img)](href).
        void Walk(Markdig.Syntax.Inlines.Inline inline, string? enclosingLink)
        {
            if (inline is Markdig.Syntax.Inlines.LinkInline link)
            {
                if (link.IsImage)
                {
                    var alt = GetInlineText(link);
                    result.Add((link.Url ?? "", alt, enclosingLink));
                    return;
                }
                // A normal link: its children (which may include an image) get this as the target.
                foreach (var child in link) Walk(child, link.Url ?? enclosingLink);
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
        foreach (var entry in _toc)
        {
            var line = new DisplayLine();
            var pad = new string(' ', indent + (entry.Level - 1) * 2);
            var id = RegisterLink("#" + entry.Id);
            line.Spans.Add(new StyledSpan(pad + "• ", theme.Muted));
            line.Spans.Add(new StyledSpan(entry.Title, theme.Link, CellStyle.Underline, id));
            _lines.Add(line);
        }
        AddBlank();
    }

    // ---------------- inline conversion ----------------
    private List<StyledSpan> InlineToSpans(ContainerInline? container, Rgb baseColor, CellStyle baseStyle = CellStyle.None)
    {
        var spans = new List<StyledSpan>();
        if (container is null) return spans;
        foreach (var inline in container)
            AppendInline(inline, spans, baseColor, baseStyle, null);
        return spans;
    }

    private void AppendInline(Inline inline, List<StyledSpan> spans, Rgb color, CellStyle style, int? linkId)
    {
        switch (inline)
        {
            case LiteralInline literal:
                spans.Add(new StyledSpan(literal.Content.ToString(), color, style, linkId));
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
                        spans.Add(new StyledSpan("“", theme.Muted, style, linkId));
                        foreach (var child in emphasis) AppendInline(child, spans, theme.Muted, style | CellStyle.Italic, linkId);
                        spans.Add(new StyledSpan("”", theme.Muted, style, linkId));
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
                spans.Add(new StyledSpan(code.Content, theme.Code, style, linkId));
                break;
            case MathInline math:
                spans.Add(new StyledSpan(MathRenderer.ToUnicode(math.Content.ToString()), theme.Code, style, linkId));
                break;
            case LinkInline link:
            {
                if (link.IsImage)
                {
                    var alt = GetInlineText(link);
                    spans.Add(new StyledSpan($"🖼 {alt} ({link.Url})", theme.Muted, style | CellStyle.Italic, linkId));
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
                spans.Add(new StyledSpan(auto.Url, theme.Link, style | CellStyle.Underline, id));
                AppendLinkMarker(spans, id);
                break;
            }
            case LineBreakInline lb:
                spans.Add(new StyledSpan(lb.IsHard ? "\n" : " ", color, style, linkId));
                break;
            case Markdig.Extensions.TaskLists.TaskList task:
                // GitHub task list checkbox. ☑ when checked, ☐ when unchecked.
                spans.Add(new StyledSpan(task.Checked ? "☑ " : "☐ ",
                    task.Checked ? (theme.IsDark ? Rgb.FromHex("#3fb950") : Rgb.FromHex("#1a7f37")) : theme.Muted,
                    style, linkId));
                break;
            case Markdig.Extensions.Footnotes.FootnoteLink footnote:
                AppendFootnoteRef(footnote, spans, style, linkId);
                break;
            case Markdig.Extensions.Abbreviations.AbbreviationInline abbr:
                // The abbreviated term itself (otherwise it would vanish). The definition/title is
                // shown once dimmed in parentheses so the expansion is visible in a terminal.
                spans.Add(new StyledSpan(abbr.Abbreviation.Label ?? "", color, style | CellStyle.Underline, linkId));
                break;
            case Markdig.Syntax.Inlines.HtmlInline html:
                // Raw inline HTML tags are dropped (we render Markdown, not markup), but an inline
                // <a id="...">/<a name="..."> is a navigation target: register it so "#id" links
                // resolve. Text between tags is a LiteralInline and still shows.
                foreach (var anchorId in ExtractHtmlAnchorIds(html.Tag))
                    RegisterAnchor(anchorId, _lines.Count);
                break;
            case ContainerInline c:
                foreach (var child in c) AppendInline(child, spans, color, style, linkId);
                break;
            default:
                break;
        }
    }

    private static string GetInlineText(ContainerInline container)
    {
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            if (inline is LiteralInline lit) sb.Append(lit.Content.AsSpan());
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

    /// <summary>Appends a small superscript number after a link so users know which key opens it.</summary>
    private void AppendLinkMarker(List<StyledSpan> spans, int id)
    {
        int ordinal = id + 1;            // links are followed by pressing 1..9
        if (ordinal < 1 || ordinal > 9) return;
        spans.Add(new StyledSpan(SuperscriptDigits[ordinal], theme.Accent, CellStyle.Bold, id));
    }

    /// <summary>Renders a footnote reference ([^1]) as a superscript number.</summary>
    private void AppendFootnoteRef(Markdig.Extensions.Footnotes.FootnoteLink footnote, List<StyledSpan> spans, CellStyle style, int? linkId)
    {
        if (footnote.IsBackLink) return;   // we don't render the back-reference arrows
        int n = footnote.Footnote?.Order ?? footnote.Index + 1;
        spans.Add(new StyledSpan(ToSuperscript(n), theme.Accent, style | CellStyle.Bold, linkId));
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
