using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Readmd.Core;

namespace Readmd.Terminal;

public sealed partial class MarkdownTerminalRenderer
{
    private readonly HtmlState _html = new();

    /// <summary>
    /// The inline-HTML formatting overlay: a stack of open tags whose combined effect (style
    /// flags, colour, link, sub/superscript, keycap) is applied to subsequent text leaves.
    /// </summary>
    internal sealed class HtmlState
    {
        private readonly List<(string Name, CellStyle Style, Rgb? Color, Rgb? Background, int? LinkId, bool Sup, bool Sub, bool Kbd)> _stack = [];
        public OverlayInfo Overlay { get; private set; } = OverlayInfo.None;

        public readonly record struct OverlayInfo(CellStyle Style, Rgb? Color, Rgb? Background, int? LinkId, bool Superscript, bool Subscript, bool Kbd)
        {
            public static readonly OverlayInfo None = new(CellStyle.None, null, null, null, false, false, false);
            public bool IsEmpty => Style == CellStyle.None && Color is null && Background is null && LinkId is null && !Superscript && !Subscript && !Kbd;
        }

        public void ResetInline()
        {
            _stack.Clear();
            Overlay = OverlayInfo.None;
        }

        public void Push(HtmlFragments.Token tag, MarkdownTerminalRenderer r)
        {
            if (tag.SelfClosing && tag.Name is not "a") return;
            var theme = r.Theme;
            CellStyle style = CellStyle.None;
            Rgb? color = null, bg = null;
            int? linkId = null;
            bool sup = false, sub = false, kbd = false;
            switch (tag.Name)
            {
                case "b" or "strong": style = CellStyle.Bold; break;
                case "i" or "em" or "cite" or "var" or "dfn" or "q": style = CellStyle.Italic; break;
                case "u" or "ins": style = CellStyle.Underline; break;
                case "s" or "del" or "strike": style = CellStyle.Strikethrough; break;
                case "mark": style = CellStyle.Reverse; break;
                case "small": style = CellStyle.Dim; break;
                case "code" or "tt" or "samp": color = theme.Code; break;
                case "sup": sup = true; break;
                case "sub": sub = true; break;
                case "kbd":
                    kbd = true; style = CellStyle.Bold; color = theme.Text; bg = theme.BackgroundElevated; break;
                case "abbr":
                    style = CellStyle.Underline;
                    break;
                case "a":
                    if (tag.Attr("href") is { Length: > 0 } href)
                    {
                        linkId = r.RegisterLink(href);
                        style = CellStyle.Underline;
                        color = theme.Link;
                    }
                    break;
            }
            // Explicit colours: <font color=..>, <span style="color: ...">.
            var styleAttr = tag.Attr("style");
            var colorAttr = tag.Attr("color") ?? ExtractCssColor(styleAttr);
            if (colorAttr is not null && TryParseCssColor(colorAttr, out var c)) color = c;
            if (styleAttr is not null)
            {
                if (styleAttr.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase) || styleAttr.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase)) style |= CellStyle.Bold;
                if (styleAttr.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase) || styleAttr.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase)) style |= CellStyle.Italic;
                if (styleAttr.Contains("line-through", StringComparison.OrdinalIgnoreCase)) style |= CellStyle.Strikethrough;
                if (styleAttr.Contains("text-decoration:underline", StringComparison.OrdinalIgnoreCase) || styleAttr.Contains("text-decoration: underline", StringComparison.OrdinalIgnoreCase)) style |= CellStyle.Underline;
            }
            _stack.Add((tag.Name, style, color, bg, linkId, sup, sub, kbd));
            Recompute();
        }

        public void Pop(string name)
        {
            // Close the innermost matching tag (tolerating mis-nesting); unknown closers are ignored.
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i].Name == name)
                {
                    _stack.RemoveAt(i);
                    Recompute();
                    return;
                }
            }
        }

        /// <summary>The nearest open &lt;a href&gt; link id, if any.</summary>
        public int? CurrentLink
        {
            get { for (int i = _stack.Count - 1; i >= 0; i--) if (_stack[i].LinkId is { } id) return id; return null; }
        }

        private void Recompute()
        {
            if (_stack.Count == 0) { Overlay = OverlayInfo.None; return; }
            CellStyle style = CellStyle.None; Rgb? color = null, bg = null; int? link = null; bool sup = false, sub = false, kbd = false;
            foreach (var e in _stack)
            {
                style |= e.Style;
                if (e.Color is not null) color = e.Color;
                if (e.Background is not null) bg = e.Background;
                if (e.LinkId is not null) link = e.LinkId;
                sup |= e.Sup; sub |= e.Sub; kbd |= e.Kbd;
            }
            Overlay = new OverlayInfo(style, color, bg, link, sup, sub, kbd);
        }

        private static string? ExtractCssColor(string? style)
        {
            if (string.IsNullOrEmpty(style)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(style, @"(?<![\w-])color\s*:\s*([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = "#e5484d", ["green"] = "#2ea043", ["blue"] = "#388bfd", ["orange"] = "#f0883e",
            ["yellow"] = "#d29922", ["purple"] = "#a371f7", ["violet"] = "#a371f7", ["pink"] = "#db61a2",
            ["gray"] = "#8b949e", ["grey"] = "#8b949e", ["white"] = "#ffffff", ["black"] = "#000000",
            ["cyan"] = "#39c5cf", ["teal"] = "#39c5cf", ["magenta"] = "#db61a2", ["brown"] = "#a0522d",
            ["gold"] = "#e3b341", ["lime"] = "#56d364", ["navy"] = "#1f6feb", ["silver"] = "#c0c0c0",
        };

        internal static bool TryParseCssColor(string value, out Rgb color)
        {
            color = default;
            value = value.Trim();
            if (NamedColors.TryGetValue(value, out var hex)) { color = Rgb.FromHex(hex); return true; }
            if (value.StartsWith('#') && value.Length is 4 or 7) { try { color = Rgb.FromHex(value); return true; } catch { return false; } }
            return TryColorChip(value, out color);
        }
    }

    // ---------------- raw HTML blocks ----------------
    // Markdig hands us raw HTML as opaque blocks. Rather than stripping tags (which loses structure
    // such as centred headers, images, tables and collapsible sections that README files rely on),
    // we interpret a pragmatic subset and render it with the same primitives as the Markdown AST.
    // Anything unknown degrades to its text content, so nothing silently disappears.
    private void RenderHtmlBlock(Markdig.Syntax.HtmlBlock html, int indent)
    {
        var raw = html.Lines.ToString();
        // An explicit anchor block (e.g. <a id="ev-05-whoami"></a>) usually has no visible text; register
        // its id first, pointing at the next line to be emitted (typically the heading it precedes).
        foreach (var anchorId in ExtractHtmlAnchorIds(raw))
            RegisterAnchor(anchorId, _lines.Count);
        var walker = new HtmlWalker(this, indent);
        walker.Run(HtmlFragments.Tokenize(raw));
    }

    /// <summary>Extra indentation applied to top-level blocks while inside an open &lt;details&gt;.</summary>
    private int _detailsIndent;
    private int _detailsDepth;

    /// <summary>Returns the extra indentation for top-level blocks (nested inside open HTML containers).</summary>
    private int TopLevelIndent => _detailsIndent;

    private sealed class HtmlWalker(MarkdownTerminalRenderer r, int baseIndent)
    {
        private readonly List<StyledSpan> _para = [];
        private readonly List<(string Url, string Alt, string? Link)> _paraImages = [];
        private readonly List<(string Media, string Src)> _pictureSources = [];
        private bool _inPicture;
        private bool _center, _right;
        private int _headingLevel;
        private int _quoteDepth;
        private readonly List<(bool Ordered, int Counter, char Style)> _lists = [];
        private bool _pendingListMarker;
        private string? _pendingMarker;
        private bool _inPre;
        private string _preLanguage = "";
        private readonly System.Text.StringBuilder _pre = new();
        private bool _inSummary;
        private readonly List<StyledSpan> _summary = [];
        // table state
        private bool _inTable;
        private List<List<List<StyledSpan>>> _rows = [];
        private List<bool> _rowIsHeader = [];
        private List<TableColumnAlign?> _aligns = [];
        private List<List<StyledSpan>>? _row;
        private List<StyledSpan>? _cell;
        private bool _rowHasTh;
        private bool _inThead;

        private TerminalTheme Theme => r.Theme;
        private int Indent => baseIndent + _quoteDepth * 2 + _lists.Count * 2;

        public void Run(List<HtmlFragments.Token> tokens)
        {
            r._html.ResetInline();
            foreach (var tok in tokens)
            {
                if (_inPre)
                {
                    if (tok.Kind == HtmlFragments.TokenKind.Close && tok.Name == "pre") { FlushPre(); continue; }
                    if (tok.Kind == HtmlFragments.TokenKind.Text) _pre.Append(tok.Text);
                    else if (tok.Kind == HtmlFragments.TokenKind.Open && tok.Name == "br") _pre.Append('\n');
                    else if (tok.Kind == HtmlFragments.TokenKind.Open && tok.Name == "code" && tok.Attr("class") is { } cls)
                        _preLanguage = LanguageFromClass(cls);
                    continue;
                }
                switch (tok.Kind)
                {
                    case HtmlFragments.TokenKind.Comment: break;
                    case HtmlFragments.TokenKind.Text: OnText(tok.Text); break;
                    case HtmlFragments.TokenKind.Open: OnOpen(tok); break;
                    case HtmlFragments.TokenKind.Close: OnClose(tok.Name); break;
                }
            }
            if (_inPre) FlushPre();
            if (_inTable) FlushTable();
            FlushParagraph();
            r._html.ResetInline();
        }

        private void OnText(string text)
        {
            if (_skipDepth > 0) return;   // inside <script>/<style>/… — never shown
            if (_inSummary)
            {
                var t = HtmlFragments.CollapseWhitespace(text);
                if (t.Length > 0) _summary.Add(new StyledSpan(t, Theme.Accent, CellStyle.Bold));
                return;
            }
            var collapsed = HtmlFragments.CollapseWhitespace(text);
            var target = _cell ?? _para;
            // Drop a leading space at the very start of a paragraph, and never let two spaces meet
            // across adjacent text runs (HTML semantics).
            if (target.Count == 0 || target[^1].Text == "\n" || target[^1].Text.EndsWith(' ')) collapsed = collapsed.TrimStart();
            if (collapsed.Length == 0) return;
            r.Leaf(target, collapsed, Theme.Text, CellStyle.None, null);
        }

        private void OnOpen(HtmlFragments.Token tok)
        {
            switch (tok.Name)
            {
                case "br":
                    (_cell ?? _para).Add(new StyledSpan("\n"));
                    return;
                case "hr":
                    FlushParagraph();
                    r.RenderRule();
                    return;
                case "img":
                {
                    var src = tok.Attr("src");
                    if (string.IsNullOrEmpty(src)) return;
                    if (_inPicture) { _paraImages.Add((PickPictureSource(src), tok.Attr("alt") ?? "", r._html.CurrentLink is { } l ? r._links[l].Url : null)); _inPicture = false; _pictureSources.Clear(); return; }
                    if (_cell is not null) { r.Leaf(_cell, $"🖼 {tok.Attr("alt") ?? ""}".TrimEnd(), Theme.Muted, CellStyle.Italic, null); return; }
                    _paraImages.Add((src, tok.Attr("alt") ?? "", r._html.CurrentLink is { } lk ? r._links[lk].Url : null));
                    return;
                }
                case "picture":
                    _inPicture = true; _pictureSources.Clear();
                    return;
                case "source":
                    if (_inPicture && tok.Attr("srcset") is { Length: > 0 } srcset)
                        _pictureSources.Add((tok.Attr("media") ?? "", FirstSrc(srcset)));
                    return;
                case "p" or "div" or "section" or "article" or "header" or "footer" or "main" or "nav" or "figure" or "figcaption" or "dl" or "dd" or "dt" or "address":
                    FlushParagraph();
                    ApplyAlign(tok);
                    if (tok.Name == "figcaption") _para.Add(new StyledSpan("— ", Theme.Muted));
                    return;
                case "center":
                    FlushParagraph(); _center = true; return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    FlushParagraph();
                    _headingLevel = tok.Name[1] - '0';
                    ApplyAlign(tok);
                    return;
                case "ul" or "ol":
                    FlushParagraph();
                    _lists.Add((tok.Name == "ol", int.TryParse(tok.Attr("start"), out var st) ? st : 1, (tok.Attr("type") ?? "1")[0]));
                    return;
                case "li":
                    FlushParagraph();
                    if (_lists.Count == 0) _lists.Add((false, 1, '1'));
                    var (ordered, counter, style) = _lists[^1];
                    _pendingMarker = ordered ? FormatListNumber(counter, style == '1' ? '1' : style) + ". " : BulletFor(_lists.Count - 1);
                    _lists[^1] = (ordered, counter + 1, style);
                    _pendingListMarker = true;
                    return;
                case "blockquote":
                    FlushParagraph(); _quoteDepth++; return;
                case "pre":
                    FlushParagraph(); _inPre = true; _pre.Clear(); _preLanguage = LanguageFromClass(tok.Attr("class") ?? "");
                    return;
                case "details":
                    FlushParagraph();
                    r._detailsDepth++;
                    return;
                case "summary":
                    FlushParagraph(); _inSummary = true; _summary.Clear();
                    return;
                case "table":
                    FlushParagraph(); _inTable = true; _rows = []; _rowIsHeader = []; _aligns = []; _inThead = false;
                    return;
                case "thead": _inThead = true; return;
                case "tbody" or "tfoot": _inThead = false; return;
                case "tr":
                    if (!_inTable) return;
                    _row = []; _rowHasTh = false;
                    return;
                case "td" or "th":
                    if (!_inTable) return;
                    _row ??= [];
                    _cell = [];
                    _row.Add(_cell);
                    if (tok.Name == "th") _rowHasTh = true;
                    int colIdx = _row.Count - 1;
                    while (_aligns.Count <= colIdx) _aligns.Add(null);
                    var al = (tok.Attr("align") ?? ExtractTextAlign(tok.Attr("style")))?.ToLowerInvariant();
                    if (al is "center") _aligns[colIdx] ??= TableColumnAlign.Center;
                    else if (al is "right") _aligns[colIdx] ??= TableColumnAlign.Right;
                    if (tok.Name == "th") r._html.Push(new HtmlFragments.Token(HtmlFragments.TokenKind.Open, "b", "", null, false), r);
                    int span = int.TryParse(tok.Attr("colspan"), out var cs) ? cs : 1;
                    for (int i = 1; i < span; i++) { _row.Add([]); while (_aligns.Count < _row.Count) _aligns.Add(null); }
                    return;
                case "a":
                    foreach (var id in ExtractHtmlAnchorIds("<a " + (tok.Attr("id") is { } aid ? $"id=\"{aid}\"" : "") + (tok.Attr("name") is { } an ? $" name=\"{an}\"" : "") + ">"))
                        r.RegisterAnchor(id, r._lines.Count);
                    r._html.Push(tok, r);
                    return;
                case "script" or "style" or "template" or "noscript" or "head" or "title" or "meta" or "link":
                    _skipDepth = 1;   // content of these is never shown
                    return;
                case "span" or "font" or "b" or "strong" or "i" or "em" or "u" or "ins" or "s" or "del" or "strike" or "mark" or "small" or "code" or "tt" or "samp" or "sup" or "sub" or "kbd" or "abbr" or "cite" or "var" or "dfn" or "q" or "label" or "time":
                    r._html.Push(tok, r);
                    return;
                default:
                    return;
            }
        }

        private int _skipDepth;

        private void OnClose(string name)
        {
            switch (name)
            {
                case "script" or "style" or "template" or "noscript" or "head": _skipDepth = 0; return;
                case "p" or "div" or "section" or "article" or "header" or "footer" or "main" or "nav" or "figure" or "figcaption" or "dl" or "dd" or "dt" or "address":
                    FlushParagraph(); _center = false; _right = false; return;
                case "center": FlushParagraph(); _center = false; return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6": FlushParagraph(); _headingLevel = 0; _center = false; _right = false; return;
                case "li": FlushParagraph(); return;
                case "ul" or "ol": FlushParagraph(); if (_lists.Count > 0) _lists.RemoveAt(_lists.Count - 1); if (_lists.Count == 0) r.AddBlank(); return;
                case "blockquote": FlushParagraph(); if (_quoteDepth > 0) _quoteDepth--; r.AddBlank(); return;
                case "summary":
                    _inSummary = false;
                    EmitSummary();
                    return;
                case "details":
                    FlushParagraph();
                    if (r._detailsDepth > 0) { r._detailsDepth--; r._detailsIndent = Math.Max(0, r._detailsIndent - 2); }
                    r.AddBlank();
                    return;
                case "picture": _inPicture = false; return;
                case "td" or "th":
                    if (name == "th") r._html.Pop("b");
                    _cell = null;
                    return;
                case "tr":
                    if (_row is not null) { _rows.Add(_row); _rowIsHeader.Add(_rowHasTh || _inThead); }
                    _row = null; _cell = null;
                    return;
                case "thead": _inThead = false; return;
                case "table": FlushTable(); return;
                default:
                    r._html.Pop(name);
                    return;
            }
        }

        private void ApplyAlign(HtmlFragments.Token tok)
        {
            var align = (tok.Attr("align") ?? ExtractTextAlign(tok.Attr("style")))?.ToLowerInvariant();
            if (align == "center") _center = true;
            else if (align == "right") _right = true;
        }

        private static string? ExtractTextAlign(string? style)
        {
            if (string.IsNullOrEmpty(style)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(style, @"text-align\s*:\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string FirstSrc(string srcset)
        {
            var first = srcset.Split(',')[0].Trim();
            int sp = first.IndexOf(' ');
            return sp > 0 ? first[..sp] : first;
        }

        // <picture>: choose the <source> whose media query matches the active theme, else the <img>.
        private string PickPictureSource(string fallback)
        {
            bool dark = Theme.IsDark;
            foreach (var (media, src) in _pictureSources)
            {
                if (media.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase))
                {
                    bool isDark = media.Contains("dark", StringComparison.OrdinalIgnoreCase);
                    if (isDark == dark) return src;
                }
            }
            // No scheme-specific source matched: prefer a source without media (generic), else fallback.
            foreach (var (media, src) in _pictureSources)
                if (string.IsNullOrWhiteSpace(media)) return src;
            return fallback;
        }

        private static string LanguageFromClass(string cls)
        {
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (c.StartsWith("language-", StringComparison.OrdinalIgnoreCase)) return c["language-".Length..];
                if (c.StartsWith("lang-", StringComparison.OrdinalIgnoreCase)) return c["lang-".Length..];
            }
            return "";
        }

        private static string BulletFor(int depth)
        {
            string[] bullets = { "• ", "◦ ", "▪ " };
            return bullets[depth % bullets.Length];
        }

        private void EmitSummary()
        {
            r.EnsureBlankBefore();
            var spans = new List<StyledSpan> { new("▾ ", Theme.Accent, CellStyle.Bold) };
            if (_summary.Count == 0) spans.Add(new StyledSpan("Details", Theme.Accent, CellStyle.Bold));
            else spans.AddRange(_summary);
            r.EmitWrapped(spans, Indent);
            // Body blocks that follow (in later Markdown blocks) are indented under the summary.
            r._detailsIndent += 2;
        }

        private void FlushParagraph()
        {
            if (_skipDepth > 0) { _para.Clear(); return; }
            bool hasText = _para.Any(s => !string.IsNullOrWhiteSpace(s.Text) && s.Text != "\n");
            int start = r._lines.Count;
            int indent = Indent;
            string? gutter = null; Rgb? gutterColor = null; bool repeat = true;
            if (_pendingListMarker && _pendingMarker is not null)
            {
                gutter = _pendingMarker; gutterColor = Theme.Accent; repeat = false;
                indent = Indent - 2 + TextWidth.Of(_pendingMarker) ; // marker occupies the list indent
                indent = Math.Max(indent, baseIndent + _quoteDepth * 2 + (_lists.Count - 1) * 2 + TextWidth.Of(_pendingMarker));
            }
            else if (_quoteDepth > 0) { gutter = "▌ "; gutterColor = Theme.Rule; }

            if (hasText)
            {
                var spans = _para;
                if (_headingLevel > 0)
                {
                    var plain = string.Concat(spans.Select(s => s.Text)).Trim();
                    r.EmitHeading(_headingLevel, (color, style) => spans.Select(s => s with { Color = s.Color is null || s.Color == Theme.Text ? color : s.Color, Style = s.Style | style }).ToList(), plain, null, 0);
                }
                else
                {
                    r.EmitWrapped(TrimEdges(spans), indent, gutterColor: gutterColor, gutter: gutter, repeatGutter: repeat);
                }
                gutter = repeat ? gutter : null;
            }
            if (_paraImages.Count > 0)
            {
                if (_paraImages.Count > 1 && !hasText) r.EmitImageGroupAnchor(_paraImages, indent, gutterColor, gutter);
                else foreach (var img in _paraImages) { r.EmitImageAnchor(img.Url, img.Alt, img.Link, indent, gutterColor, gutter); gutter = repeat ? gutter : null; }
            }
            else if (!hasText && _pendingListMarker && _pendingMarker is not null && _para.Count == 0)
            {
                // Empty <li> (e.g. one that only contains a nested list): still show the bullet.
                r._lines.Add(r.NewLine(indent, null, 0, gutterColor, gutter));
            }
            if (_center || _right) r.AlignLines(start, _center ? 0.5 : 1.0);
            if (r._lines.Count > start && _headingLevel == 0 && _lists.Count == 0) r.AddBlank();
            _para.Clear();
            _paraImages.Clear();
            _pendingListMarker = false;
            _pendingMarker = null;
        }

        private static List<StyledSpan> TrimEdges(List<StyledSpan> spans)
        {
            // Trim leading/trailing whitespace-only spans, line breaks and edge spaces (HTML collapses them).
            var list = new List<StyledSpan>(spans);
            while (list.Count > 0 && list[0].Text.Trim().Length == 0) list.RemoveAt(0);
            while (list.Count > 0 && list[^1].Text.Trim().Length == 0) list.RemoveAt(list.Count - 1);
            if (list.Count > 0 && list[0].Text.StartsWith(' ')) list[0] = list[0] with { Text = list[0].Text.TrimStart() };
            if (list.Count > 0 && list[^1].Text.EndsWith(' ')) list[^1] = list[^1] with { Text = list[^1].Text.TrimEnd() };
            return list;
        }

        private void FlushPre()
        {
            _inPre = false;
            var code = _pre.ToString().Replace("\r\n", "\n");
            code = System.Net.WebUtility.HtmlDecode(code).Trim('\n');
            // Inner <code> wrapper tags were not tokenized (raw text); strip them if present.
            code = System.Text.RegularExpressions.Regex.Replace(code, @"^\s*<code[^>]*>|</code>\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            r.EnsureBlankBefore();
            r.EmitHighlightedOrDeferred(r.ExpandTabs(code), _preLanguage, Indent);
            r.AddBlank();
            _pre.Clear();
        }

        private void FlushTable()
        {
            _inTable = false;
            if (_row is not null) { _rows.Add(_row); _rowIsHeader.Add(_rowHasTh || _inThead); _row = null; }
            _cell = null;
            if (_rows.Count > 0)
            {
                r.EnsureBlankBefore();
                int cols = _rows.Max(row => row.Count);
                while (_aligns.Count < cols) _aligns.Add(null);
                r.EmitTable(_rows, _rowIsHeader, _aligns, Indent);
            }
            _rows = []; _rowIsHeader = [];
        }
    }

    /// <summary>Horizontally aligns the lines emitted since <paramref name="start"/> (0.5 = centre, 1 = right).</summary>
    private void AlignLines(int start, double fraction)
    {
        for (int i = start; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.DiagramKey is not null && line.DiagramKey.StartsWith("reserve:")) continue;
            var text = line.PlainText.TrimEnd();
            if (text.Trim().Length == 0) continue;   // blank lines stay blank
            int w = TextWidth.Of(text);
            int free = _width - 1 - w;
            if (free <= 0) continue;
            int pad = (int)Math.Floor(free * fraction);
            if (pad <= 0) continue;
            if (line.DiagramKey is not null) line.DiagramIndent += pad;
            line.Spans.Insert(0, new StyledSpan(new string(' ', pad)));
        }
    }
}
