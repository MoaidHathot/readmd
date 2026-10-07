using Markdig.Extensions.Alerts;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Readmd.Core;

namespace Readmd.Terminal;

public sealed partial class MarkdownTerminalRenderer
{
    // ---------------- word-wrap emission ----------------
    /// <summary>
    /// Appends spans as one or more display lines, wrapping at word boundaries to fit
    /// <c>Width - indent</c>. Hard line breaks ("\n" spans) force a new line. An optional
    /// <paramref name="gutter"/> is drawn in the indent area of the first line; it is repeated on
    /// continuation lines only when <paramref name="repeatGutter"/> is true (quote bars, alert bars),
    /// and replaced by plain indentation otherwise (list bullets, footnote numbers).
    /// </summary>
    private void EmitWrapped(List<StyledSpan> spans, int indent, string? headingId = null, int sourceLine = 0,
        Rgb? gutterColor = null, string? gutter = null, bool repeatGutter = true, Rgb? lineBackground = null)
    {
        int max = Math.Max(8, _width - indent);
        var current = NewLine(indent, headingId, sourceLine, gutterColor, gutter, lineBackground);
        int used = 0;

        void Flush()
        {
            _lines.Add(current);
            current = NewLine(indent, null, sourceLine, gutterColor, repeatGutter ? gutter : null, lineBackground);
            used = 0;
        }

        foreach (var span in spans)
        {
            if (span.Text == "\n")
            {
                Flush();
                continue;
            }

            // Split span into words but keep spaces.
            foreach (var token in Tokenize(span.Text))
            {
                if (token == "\n") { Flush(); continue; }
                int tokenLen = TextWidth.Of(token);

                // A single token longer than the whole line (e.g. a long URL or path) would otherwise
                // be clipped at the screen edge. Hard-break it across lines at grapheme boundaries.
                if (tokenLen > max && token.Trim().Length > 0)
                {
                    if (used > 0) Flush();
                    foreach (var g in TextWidth.Graphemes(token))
                    {
                        int gw = TextWidth.ElementWidth(g);
                        if (used + gw > max && used > 0) Flush();
                        current.Spans.Add(span with { Text = g });
                        used += gw;
                    }
                    continue;
                }

                if (used + tokenLen > max && used > 0)
                {
                    Flush();
                    if (token.Trim().Length == 0) continue; // don't carry leading space to new line
                }
                if (used == 0 && token.Trim().Length == 0) continue; // skip leading spaces
                current.Spans.Add(span with { Text = token });
                used += tokenLen;
            }
        }

        _lines.Add(current);
    }

    private DisplayLine NewLine(int indent, string? headingId, int sourceLine, Rgb? gutterColor, string? gutter, Rgb? lineBackground = null)
    {
        var line = new DisplayLine { HeadingId = headingId, SourceLine = sourceLine, LineBackground = lineBackground };
        if (indent > 0)
        {
            if (gutter is not null)
            {
                var pad = indent - TextWidth.Of(gutter);
                if (pad > 0) line.Spans.Add(new StyledSpan(new string(' ', pad)));
                line.Spans.Add(new StyledSpan(gutter, gutterColor ?? Theme.Muted));
            }
            else
            {
                line.Spans.Add(new StyledSpan(new string(' ', indent)));
            }
        }
        return line;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n') { yield return "\n"; i++; continue; }
            int start = i;
            bool space = char.IsWhiteSpace(text[i]);
            while (i < text.Length && text[i] != '\n' && char.IsWhiteSpace(text[i]) == space) i++;
            yield return text.Substring(start, i - start);
        }
    }

    // ---------------- lists ----------------
    private void RenderList(ListBlock list, int indent)
    {
        // Ordered lists honor their start number (e.g. "5." continues from 5). ListExtras also allows
        // alpha ("a.") and roman ("i.") numbering, signalled by BulletType, and a ')' delimiter.
        int number = list.IsOrdered ? ParseListStart(list.OrderedStart, list.BulletType) : 1;
        char delimiter = list.IsOrdered && list.OrderedDelimiter == ')' ? ')' : '.';
        // Unordered bullets vary by nesting depth: • ◦ ▪ (cycling).
        string[] bullets = { "• ", "◦ ", "▪ " };
        string bullet = bullets[_listDepth % bullets.Length];
        bool loose = list.IsLoose;
        _listDepth++;

        bool firstItem = true;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem) continue;
            // A loose list separates items with a blank line.
            if (loose && !firstItem) AddBlank();
            firstItem = false;

            var marker = list.IsOrdered ? FormatListNumber(number, list.BulletType) + delimiter + " " : bullet;
            number++;
            int childIndent = indent + TextWidth.Of(marker);
            bool firstChild = true;
            foreach (var child in listItem)
            {
                if (firstChild && child is ParagraphBlock para && para.Inline is not null)
                {
                    // The marker is a hanging gutter: continuation lines of a wrapped item align under
                    // the item text, not under the bullet (so bullets never repeat mid-item).
                    EmitInlineBlock(para.Inline, childIndent, sourceLine: para.Line + 1, gutterColor: Theme.Accent, gutter: marker, repeatGutter: false);
                    firstChild = false;
                }
                else if (firstChild)
                {
                    // Item whose first child isn't a paragraph (e.g. a nested list or code block):
                    // still show the bullet on its own line so the item isn't anonymous.
                    var line = NewLine(childIndent, null, 0, Theme.Accent, marker);
                    _lines.Add(line);
                    firstChild = false;
                    RenderBlock(child, childIndent);
                }
                else
                {
                    RenderBlock(child, childIndent);
                }
            }
        }
        _listDepth--;
        AddBlank();
    }

    private int _listDepth;

    /// <summary>Parses an ordered-list start marker ("5", "a", "C", "iv") to its ordinal number.</summary>
    private static int ParseListStart(string? start, char bulletType)
    {
        if (string.IsNullOrEmpty(start)) return 1;
        if (int.TryParse(start, out var n)) return Math.Max(1, n);
        switch (char.ToLowerInvariant(bulletType))
        {
            case 'a':
                return start.Length == 1 && char.IsLetter(start[0]) ? char.ToLowerInvariant(start[0]) - 'a' + 1 : 1;
            case 'i':
                return Math.Max(1, FromRoman(start));
        }
        return 1;
    }

    /// <summary>Formats an ordinal in the list's numbering style: 1, a/A, or i/I.</summary>
    private static string FormatListNumber(int n, char bulletType) => bulletType switch
    {
        'a' => ToAlpha(n, upper: false),
        'A' => ToAlpha(n, upper: true),
        'i' => ToRoman(n).ToLowerInvariant(),
        'I' => ToRoman(n),
        _ => n.ToString(),
    };

    private static string ToAlpha(int n, bool upper)
    {
        // 1 → a, 26 → z, 27 → aa (bijective base-26, like spreadsheet columns).
        var sb = new System.Text.StringBuilder();
        n = Math.Max(1, n);
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)((upper ? 'A' : 'a') + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    private static readonly (int Value, string Numeral)[] RomanNumerals =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    private static string ToRoman(int n)
    {
        if (n <= 0 || n >= 4000) return n.ToString();
        var sb = new System.Text.StringBuilder();
        foreach (var (value, numeral) in RomanNumerals)
            while (n >= value) { sb.Append(numeral); n -= value; }
        return sb.ToString();
    }

    private static int FromRoman(string s)
    {
        s = s.ToUpperInvariant();
        int total = 0, i = 0;
        foreach (var (value, numeral) in RomanNumerals)
            while (i + numeral.Length <= s.Length && string.CompareOrdinal(s, i, numeral, 0, numeral.Length) == 0)
            { total += value; i += numeral.Length; }
        return i == s.Length && total > 0 ? total : 0;
    }

    // ---------------- quotes & alerts ----------------
    private void RenderQuote(QuoteBlock quote, int indent)
    {
        EnsureBlankBefore();
        foreach (var child in quote)
        {
            if (child is LeafBlock leaf && leaf.Inline is not null)
            {
                EmitInlineBlock(leaf.Inline, indent + 2, sourceLine: leaf.Line + 1, baseColor: Theme.Quote, baseStyle: CellStyle.Italic, gutterColor: Theme.Rule, gutter: "▌ ");
            }
            else
            {
                RenderBlock(child, indent + 2);
            }
        }
        AddBlank();
    }

    /// <summary>
    /// Renders a GitHub-style alert ([!NOTE], [!TIP], [!IMPORTANT], [!WARNING], [!CAUTION]) with a
    /// colored gutter, an icon + title header, and the body — matching the browser's alert styling.
    /// </summary>
    private void RenderAlert(AlertBlock alert, int indent)
    {
        EnsureBlankBefore();
        var kind = alert.Kind.ToString().ToUpperInvariant();
        (string icon, string title, Rgb color) = kind switch
        {
            "NOTE" => ("ℹ", "Note", Theme.IsDark ? Rgb.FromHex("#4493f8") : Rgb.FromHex("#0969da")),
            "TIP" => ("✔", "Tip", Theme.IsDark ? Rgb.FromHex("#3fb950") : Rgb.FromHex("#1a7f37")),
            "IMPORTANT" => ("◆", "Important", Theme.IsDark ? Rgb.FromHex("#ab7df8") : Rgb.FromHex("#8250df")),
            "WARNING" => ("⚠", "Warning", Theme.IsDark ? Rgb.FromHex("#d29922") : Rgb.FromHex("#9a6700")),
            "CAUTION" => ("⛔", "Caution", Theme.IsDark ? Rgb.FromHex("#f85149") : Rgb.FromHex("#cf222e")),
            _ => ("▌", TitleCase(kind), Theme.Accent),
        };


        // Header: icon + bold title in the alert color.
        var header = new List<StyledSpan>
        {
            new(icon + " ", color, CellStyle.Bold),
            new(title, color, CellStyle.Bold),
        };
        EmitWrapped(header, indent + 2, gutterColor: color, gutter: "▌ ");

        // Body: each child block, with the colored gutter continued.
        foreach (var child in alert)
        {
            if (child is LeafBlock leaf && leaf.Inline is not null)
            {
                EmitInlineBlock(leaf.Inline, indent + 2, sourceLine: leaf.Line + 1, gutterColor: color, gutter: "▌ ");
            }
            else
            {
                RenderBlock(child, indent + 2);
            }
        }
        AddBlank();
    }

    private static string TitleCase(string s) =>
        string.IsNullOrEmpty(s) ? "Note" : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    // ---------------- footnotes ----------------
    /// <summary>Renders the footnote definitions at the end of the document: a labeled rule, then a
    /// numbered list where each entry is "ⁿ. body" (the same numbers used by the inline references).</summary>
    private void RenderFootnotes(Markdig.Extensions.Footnotes.FootnoteGroup group)
    {
        var notes = group.OfType<Markdig.Extensions.Footnotes.Footnote>().OrderBy(f => f.Order).ToList();
        if (notes.Count == 0) return;

        EnsureBlankBefore();
        // A short labeled separator: ── Footnotes ───
        var sep = new DisplayLine();
        int dash = Math.Max(0, Math.Min(_width - 1, 40) - " Footnotes ".Length);
        sep.Spans.Add(new StyledSpan("── ", Theme.Rule));
        sep.Spans.Add(new StyledSpan("Footnotes", Theme.Muted, CellStyle.Bold));
        sep.Spans.Add(new StyledSpan(" " + new string('─', dash), Theme.Rule));
        _lines.Add(sep);
        AddBlank();

        foreach (var note in notes)
        {
            string num = note.Order + ". ";
            bool first = true;
            foreach (var child in note)
            {
                if (child is Markdig.Syntax.LeafBlock leaf && leaf.Inline is not null)
                {
                    // The number is a hanging gutter on the first paragraph; later paragraphs and
                    // wrapped continuation lines align under the note text.
                    EmitInlineBlock(leaf.Inline, 2 + num.Length, sourceLine: leaf.Line + 1, gutterColor: Theme.Accent,
                        gutter: first ? num : null, repeatGutter: false);
                    first = false;
                }
                else
                {
                    RenderBlock(child, 2 + num.Length);
                }
            }
        }
        AddBlank();
    }

    // ---------------- definition lists ----------------
    /// <summary>Renders a definition list: each term is bold, and its definitions are indented and
    /// prefixed with a ':' marker (like the Markdown source), so terms and definitions are distinct.</summary>
    private void RenderDefinitionList(Markdig.Extensions.DefinitionLists.DefinitionList list, int indent)
    {
        EnsureBlankBefore();
        foreach (var itemBlock in list)
        {
            if (itemBlock is not Markdig.Extensions.DefinitionLists.DefinitionItem item) continue;
            foreach (var child in item)
            {
                if (child is Markdig.Extensions.DefinitionLists.DefinitionTerm term && term.Inline is not null)
                {
                    var spans = InlineToSpans(term.Inline, Theme.Heading, CellStyle.Bold);
                    EmitWrapped(spans, indent);
                }
                else if (child is Markdig.Syntax.LeafBlock leaf && leaf.Inline is not null)
                {
                    EmitInlineBlock(leaf.Inline, indent + 2, sourceLine: leaf.Line + 1, gutterColor: Theme.Accent, gutter: ": ", repeatGutter: false);
                }
                else
                {
                    RenderBlock(child, indent + 2);
                }
            }
        }
        AddBlank();
    }

    // ---------------- code blocks ----------------
    private void RenderFenced(FencedCodeBlock fenced, int indent)
    {
        var kind = DiagramExtractor.MapKind(fenced.Info);
        if (kind is not null)
        {
            var source = fenced.Lines.ToString();
            var request = DiagramRequest.Create(kind.Value, source);
            EmitDiagramAnchor(request, kind.Value, indent);
            return;
        }

        var language = (fenced.Info ?? "").Trim();
        // GitHub renders ```math (and ```latex / ```tex) fences as display math.
        if (language.Equals("math", StringComparison.OrdinalIgnoreCase) || language.Equals("latex", StringComparison.OrdinalIgnoreCase) || language.Equals("tex", StringComparison.OrdinalIgnoreCase))
        {
            EmitMathBlock(fenced.Lines.ToString(), indent);
            return;
        }

        EnsureBlankBefore();
        var code = ExpandTabs(fenced.Lines.ToString().TrimEnd('\n'));
        var opts = CodeBlockOptions.Parse(fenced.Arguments, fenced.TryGetAttributes());
        EmitHighlightedOrDeferred(code, language, indent, opts);
        AddBlank();
    }

    /// <summary>
    /// Presentation options carried on a fence's info string after the language — the common
    /// Docusaurus/MkDocs/Markdig conventions: <c>title="file.cs"</c> (or <c>filename=</c>), highlighted
    /// line ranges <c>{1,3-5}</c> / <c>hl_lines="1 3-5"</c>, and <c>showLineNumbers</c>/<c>nonums</c>.
    /// </summary>
    public sealed record CodeBlockOptions(string? Title, HashSet<int>? HighlightLines, bool? LineNumbers)
    {
        public static readonly CodeBlockOptions None = new(null, null, null);

        public static CodeBlockOptions Parse(string? arguments, Markdig.Renderers.Html.HtmlAttributes? attributes)
        {
            string? title = null;
            HashSet<int>? lines = null;
            bool? numbers = null;

            void Range(string spec)
            {
                foreach (var part in spec.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = part.Trim();
                    int dash = p.IndexOf('-');
                    if (dash > 0 && int.TryParse(p[..dash], out var a) && int.TryParse(p[(dash + 1)..], out var b))
                    {
                        lines ??= [];
                        for (int i = Math.Min(a, b); i <= Math.Max(a, b) && i - Math.Min(a, b) < 10_000; i++) lines.Add(i);
                    }
                    else if (int.TryParse(p, out var n)) { lines ??= []; lines.Add(n); }
                }
            }

            if (attributes?.Properties is { } props)
            {
                foreach (var (k, v) in props)
                {
                    if (k.Equals("title", StringComparison.OrdinalIgnoreCase) || k.Equals("filename", StringComparison.OrdinalIgnoreCase)) title = v;
                    else if (k.Equals("hl_lines", StringComparison.OrdinalIgnoreCase) || k.Equals("highlight", StringComparison.OrdinalIgnoreCase)) Range(v ?? "");
                    else if (k.Equals("linenos", StringComparison.OrdinalIgnoreCase) || k.Equals("showLineNumbers", StringComparison.OrdinalIgnoreCase)) numbers = v is null || v != "false";
                }
            }
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                foreach (System.Text.RegularExpressions.Match m in ArgRegex.Matches(arguments))
                {
                    if (m.Groups["range"].Success) { Range(m.Groups["range"].Value); continue; }
                    var key = m.Groups["key"].Value;
                    var val = m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["bare"].Value;
                    if (key.Length == 0) { if (val.Equals("showLineNumbers", StringComparison.OrdinalIgnoreCase) || val.Equals("linenos", StringComparison.OrdinalIgnoreCase)) numbers = true; else if (val.Equals("nonums", StringComparison.OrdinalIgnoreCase) || val.Equals("nolinenos", StringComparison.OrdinalIgnoreCase)) numbers = false; continue; }
                    if (key.Equals("title", StringComparison.OrdinalIgnoreCase) || key.Equals("filename", StringComparison.OrdinalIgnoreCase) || key.Equals("file", StringComparison.OrdinalIgnoreCase)) title = val;
                    else if (key.Equals("hl_lines", StringComparison.OrdinalIgnoreCase) || key.Equals("highlight", StringComparison.OrdinalIgnoreCase)) Range(val.Trim('{', '}'));
                    else if (key.Equals("showLineNumbers", StringComparison.OrdinalIgnoreCase) || key.Equals("linenos", StringComparison.OrdinalIgnoreCase)) numbers = val != "false";
                }
            }
            if (title is null && lines is null && numbers is null) return None;
            return new CodeBlockOptions(title, lines, numbers);
        }

        private static readonly System.Text.RegularExpressions.Regex ArgRegex = new(
            @"\{(?<range>[\d,\s\-]+)\}|(?:(?<key>[\w:-]+)\s*=\s*)?(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s""']+))",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    }

    /// <summary>
    /// Emits a code block. When highlighting is deferred (interactive viewer) and no cached highlight
    /// exists, the block is painted in the plain code colour immediately and queued in
    /// <see cref="PendingHighlights"/>; the viewer restyles the lines in place once the background
    /// highlighter finishes. Synchronous callers (print/export) get the final colours directly.
    /// </summary>
    private void EmitHighlightedOrDeferred(string code, string language, int indent, CodeBlockOptions? options = null)
    {
        options ??= CodeBlockOptions.None;
        int blockId = ++_blockCounter;
        List<List<StyledSpan>> spans;
        var cached = CodeHighlighter.TryGetCached(code, language, Theme);
        if (cached is not null) spans = cached;
        else if (DeferHighlighting && CodeHighlighter.HasGrammar(language))
        {
            spans = CodeHighlighter.Plain(code, Theme);
            _pendingHighlights.Add(new PendingHighlight(blockId, code, language, indent, options));
        }
        else spans = CodeHighlighter.Highlight(code, language, Theme);
        EmitCodeBlock(spans, indent, language, blockId, options);
    }

    /// <summary>When true, code blocks are emitted unhighlighted and queued for background highlighting.</summary>
    public bool DeferHighlighting { get; init; }

    /// <summary>Whether code blocks show a line-number gutter by default (a fence can override).</summary>
    public bool LineNumbers { get; init; } = true;

    /// <summary>A code block emitted in plain colour that still needs syntax highlighting.</summary>
    public sealed record PendingHighlight(int BlockId, string Code, string Language, int Indent, CodeBlockOptions Options);

    private readonly List<PendingHighlight> _pendingHighlights = [];
    public IReadOnlyList<PendingHighlight> PendingHighlights => _pendingHighlights;
    private int _blockCounter;

    /// <summary>
    /// Re-renders a deferred code block with real highlighting and copies the new spans into the
    /// existing display lines (which keep their identity, so scroll/search/reservations are
    /// untouched). Row structure is identical because wrapping depends only on display width.
    /// </summary>
    public static void ApplyHighlight(IReadOnlyList<DisplayLine> blockLines, PendingHighlight pending,
        List<List<StyledSpan>> highlighted, TerminalTheme theme, int width, bool lineNumbers = true)
    {
        var scratch = new MarkdownTerminalRenderer(theme, width) { LineNumbers = lineNumbers };
        scratch.EmitCodeBlock(highlighted, pending.Indent, pending.Language, pending.BlockId, pending.Options);
        if (scratch._lines.Count != blockLines.Count) return;   // layout mismatch: leave plain
        for (int i = 0; i < blockLines.Count; i++)
            blockLines[i].ReplaceSpans(scratch._lines[i].Spans);
    }

    private void RenderIndentedCode(CodeBlock code, int indent)
    {
        EnsureBlankBefore();
        var text = ExpandTabs(code.Lines.ToString().TrimEnd('\n'));
        var lines = text.Split('\n')
            .Select(raw => (List<StyledSpan>)[new StyledSpan(raw, Theme.Text)])
            .ToList();
        EmitCodeBlock(lines, indent, language: "", ++_blockCounter);
        AddBlank();
    }

    /// <summary>Tab size used when expanding tabs inside code blocks (terminal tab stops would
    /// otherwise misalign wrapping and clipping, which count a tab as one cell).</summary>
    public int TabSize { get; init; } = 4;

    /// <summary>Expands tabs to spaces at <see cref="TabSize"/> stops, per line.</summary>
    private string ExpandTabs(string text)
    {
        if (text.IndexOf('\t') < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length + 16);
        int col = 0;
        foreach (var ch in text)
        {
            if (ch == '\t')
            {
                int n = TabSize - (col % TabSize);
                sb.Append(' ', n);
                col += n;
            }
            else if (ch == '\n') { sb.Append(ch); col = 0; }
            else { sb.Append(ch); col++; }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emits a code block in the style of <c>bat</c>: a header line with the language, a thin grid
    /// with line numbers in a left gutter separated by a vertical rule, and top/bottom borders.
    /// No heavy background fill — the structure (numbers + grid) is what sets it apart from prose.
    /// </summary>
    private void EmitCodeBlock(List<List<StyledSpan>> codeLines, int indent, string language, int blockId = 0, CodeBlockOptions? options = null)
    {
        options ??= CodeBlockOptions.None;
        var rule = Theme.CodeBorder;          // border / gutter color
        var numColor = Theme.Muted;           // line-number color
        string pad = indent > 0 ? new string(' ', indent) : "";
        bool numbers = options.LineNumbers ?? LineNumbers;

        int lineCount = codeLines.Count;
        // Without line numbers the gutter is a single blank cell (keeps the │ frame).
        int numWidth = numbers ? Math.Max(2, lineCount.ToString().Length) : 0;
        string gutter = new string(' ', numWidth + 1);   // gutter width before the '│'
        int frameWidth = Math.Max(20, _width - indent - 1);
        int interior = Math.Max(8, frameWidth - (numWidth + 1) - 2); // minus gutter, "│ "
        int firstLine = _lines.Count;
        var highlightBg = Theme.IsDark ? Theme.Background.Lighten(0.12) : Theme.Background.Darken(0.08);

        // Top border:  ───────┬────────────────
        EmitGridBorder(pad, numWidth, frameWidth, '┬', rule);

        // Header:      <lang> · title  │
        var langLabel = language.Split([' ', '\t', '{'], 2)[0].ToLowerInvariant();
        if (!string.IsNullOrEmpty(langLabel) || options.Title is not null)
        {
            var header = new DisplayLine();
            if (pad.Length > 0) header.Spans.Add(new StyledSpan(pad));
            header.Spans.Add(new StyledSpan(gutter, rule));
            header.Spans.Add(new StyledSpan("│ ", rule));
            if (!string.IsNullOrEmpty(langLabel))
                header.Spans.Add(new StyledSpan(langLabel, Theme.Heading, CellStyle.Bold));
            if (options.Title is { Length: > 0 } title)
            {
                if (!string.IsNullOrEmpty(langLabel)) header.Spans.Add(new StyledSpan("  ·  ", Theme.Muted));
                header.Spans.Add(new StyledSpan(TextWidth.TrimToWidth(title, Math.Max(4, interior - langLabel.Length - 5)), Theme.Text, CellStyle.Italic));
            }
            _lines.Add(header);
            // Header separator:  ───────┼────────────────
            EmitGridBorder(pad, numWidth, frameWidth, '┼', rule);
        }

        int n = 1;
        foreach (var spans in codeLines)
        {
            // Soft-wrap a long code line across multiple visual rows (instead of clipping it at the
            // right border). The first row shows the line number; continuation rows show a ↳ marker.
            var wrapped = WrapCodeSpans(spans, interior);
            if (wrapped.Count == 0) wrapped.Add([]);
            bool highlighted = options.HighlightLines is not null && options.HighlightLines.Contains(n);

            for (int w = 0; w < wrapped.Count; w++)
            {
                var line = new DisplayLine();
                if (pad.Length > 0) line.Spans.Add(new StyledSpan(pad));
                // Line number on the first row, a continuation arrow on wrapped rows. The gutter is
                // exactly (numWidth + 1) wide so the '│' lines up with the ┬/┼/┴ junctions.
                string gutterText = numbers
                    ? (w == 0 ? n.ToString().PadLeft(numWidth) + " " : "↳".PadLeft(numWidth) + " ")
                    : (w == 0 ? " " : "↳");
                line.Spans.Add(new StyledSpan(gutterText, w == 0 ? (highlighted ? Theme.Accent : numColor) : rule, highlighted && w == 0 ? CellStyle.Bold : CellStyle.None));
                line.Spans.Add(new StyledSpan("│ ", rule));
                if (highlighted)
                {
                    // Emphasized line: tint its content cells (the `{1,3-5}` / hl_lines convention).
                    foreach (var s in wrapped[w]) line.Spans.Add(s with { Background = highlightBg });
                    int used = wrapped[w].Sum(s => TextWidth.Of(s.Text));
                    if (used < interior) line.Spans.Add(new StyledSpan(new string(' ', interior - used), null, CellStyle.None, null, highlightBg));
                }
                else line.Spans.AddRange(wrapped[w]);
                _lines.Add(line);
            }
            n++;
        }

        // Bottom border:  ───────┴────────────────
        EmitGridBorder(pad, numWidth, frameWidth, '┴', rule);

        // Tag every row of the block so a background highlighter can restyle it in place.
        if (blockId != 0)
            for (int i = firstLine; i < _lines.Count; i++) _lines[i].BlockId = blockId;
    }

    /// <summary>Wraps a code line's styled spans to <paramref name="width"/> display columns at
    /// grapheme boundaries (code has no word breaks), preserving each span's style/color.</summary>
    private static List<List<StyledSpan>> WrapCodeSpans(List<StyledSpan> spans, int width)
    {
        var rows = new List<List<StyledSpan>>();
        var current = new List<StyledSpan>();
        int used = 0;
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;
            var buffer = new System.Text.StringBuilder();
            void FlushSegment()
            {
                if (buffer.Length > 0) { current.Add(span with { Text = buffer.ToString() }); buffer.Clear(); }
            }
            foreach (var g in TextWidth.Graphemes(span.Text))
            {
                int gw = TextWidth.ElementWidth(g);
                if (used + gw > width && (current.Count > 0 || buffer.Length > 0))
                {
                    FlushSegment();
                    rows.Add(current);
                    current = [];
                    used = 0;
                }
                buffer.Append(g);
                used += gw;
            }
            FlushSegment();
        }
        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    private void EmitGridBorder(string pad, int numWidth, int frameWidth, char junction, Rgb color)
    {
        int left = numWidth + 1;              // gutter chars before the junction
        int right = Math.Max(1, frameWidth - left - 1);
        var bar = new string('─', left) + junction + new string('─', right);
        var line = new DisplayLine();
        if (pad.Length > 0) line.Spans.Add(new StyledSpan(pad));
        line.Spans.Add(new StyledSpan(bar, color));
        _lines.Add(line);
    }

    // ---------------- thematic break ----------------
    private void RenderRule()
    {
        EnsureBlankBefore();
        var line = new DisplayLine();
        line.Spans.Add(new StyledSpan(new string('─', Math.Max(8, _width - 1)), Theme.Rule));
        _lines.Add(line);
        AddBlank();
    }

    // ---------------- custom containers (:::) & figures ----------------
    /// <summary>
    /// Renders a fenced custom container (<c>::: name</c>) with a colored gutter and the container
    /// name as a label, similar to alerts. Azure DevOps embeds diagrams as <c>::: mermaid</c> blocks:
    /// those are turned into diagram anchors, reconstructing the source from the raw document.
    /// </summary>
    private void RenderCustomContainer(Markdig.Extensions.CustomContainers.CustomContainer container, int indent)
    {
        var name = (container.Info ?? "").Trim();
        var diagramKind = DiagramExtractor.MapKind(name);
        if (diagramKind is not null && _source is not null)
        {
            var source = ContainerBody(container);
            if (!string.IsNullOrWhiteSpace(source))
            {
                EmitDiagramAnchor(DiagramRequest.Create(diagramKind.Value, source), diagramKind.Value, indent);
                return;
            }
        }

        EnsureBlankBefore();
        var color = Theme.Accent;
        if (!string.IsNullOrEmpty(name))
            EmitWrapped([new StyledSpan(TitleCase(name), color, CellStyle.Bold)], indent + 2, gutterColor: color, gutter: "▌ ");
        foreach (var child in container)
        {
            if (child is Markdig.Syntax.LeafBlock leaf && leaf.Inline is not null)
                EmitInlineBlock(leaf.Inline, indent + 2, sourceLine: leaf.Line + 1, gutterColor: color, gutter: "▌ ");
            else
                RenderBlock(child, indent + 2);
        }
        AddBlank();
    }

    /// <summary>Renders a figure caption in muted italic, distinct from body text.</summary>
    private void RenderFigureCaption(Markdig.Extensions.Figures.FigureCaption caption, int indent)
    {
        if (caption.Inline is null) return;
        var spans = new List<StyledSpan> { new("— ", Theme.Muted) };
        spans.AddRange(InlineToSpans(caption.Inline, Theme.Muted, CellStyle.Italic));
        EmitWrapped(spans, indent + 2);
    }

    /// <summary>Renders a page footer (^^ text ^^) in muted/dim text with a subtle marker.</summary>
    private void RenderFooter(Markdig.Extensions.Footers.FooterBlock footer, int indent)
    {
        EnsureBlankBefore();
        foreach (var child in footer)
        {
            if (child is Markdig.Syntax.LeafBlock leaf && leaf.Inline is not null)
            {
                var spans = new List<StyledSpan> { new("‖ ", Theme.Rule) };
                spans.AddRange(InlineToSpans(leaf.Inline, Theme.Muted, CellStyle.Italic | CellStyle.Dim));
                EmitWrapped(spans, indent);
            }
            else
            {
                RenderBlock(child, indent);
            }
        }
        AddBlank();
    }

    // ---------------- display math ($$...$$) ----------------
    private void RenderMathBlock(Markdig.Extensions.Mathematics.MathBlock math, int indent) =>
        EmitMathBlock(math.Lines.ToString(), indent);

    /// <summary>Lays out display math (from <c>$$</c> or a <c>```math</c> fence) centred as a block.</summary>
    private void EmitMathBlock(string latex, int indent)
    {
        EnsureBlankBefore();
        var rows = MathRenderer.ToUnicodeBlock(latex);
        // Center the block as a whole on its widest row.
        int blockWidth = rows.Count == 0 ? 0 : rows.Max(TextWidth.Of);
        int blockPad = Math.Max(indent + 2, (_width - blockWidth) / 2);
        foreach (var raw in rows)
        {
            var line = new DisplayLine();
            line.Spans.Add(new StyledSpan(new string(' ', blockPad)));
            line.Spans.Add(new StyledSpan(raw, Theme.Heading));
            _lines.Add(line);
        }
        AddBlank();
    }

    // ---------------- diagram anchor ----------------
    private void EmitDiagramAnchor(DiagramRequest request, DiagramKind kind, int indent)
    {
        EnsureBlankBefore();
        // Caption line for the diagram. The rendered image is drawn on the rows below this line;
        // the caption is just a label so it's clear what the image is (and where it begins).
        var label = DiagramLabel(kind);
        var anchor = NewLine(indent, null, 0, null, null);
        anchor.DiagramKey = request.Key;
        anchor.DiagramIndent = indent;
        anchor.Spans.Add(new StyledSpan("◆ ", Theme.Accent));
        anchor.Spans.Add(new StyledSpan(label + " diagram", Theme.Muted, CellStyle.Italic));
        _lines.Add(anchor);
        _pendingDiagrams[request.Key] = request;
        AddBlank();
    }

    internal static string DiagramLabel(DiagramKind kind) => kind switch
    {
        DiagramKind.Mermaid => "Mermaid",
        DiagramKind.D2 => "D2",
        DiagramKind.Graphviz => "Graphviz",
        DiagramKind.PlantUml => "PlantUML",
        _ => kind.ToString(),
    };

    private readonly Dictionary<string, DiagramRequest> _pendingDiagrams = [];
    public IReadOnlyDictionary<string, DiagramRequest> PendingDiagrams => _pendingDiagrams;

    // ---------------- inline images ----------------
    private readonly Dictionary<string, string> _pendingImages = []; // key -> url
    public IReadOnlyDictionary<string, string> PendingImages => _pendingImages;

    /// <summary>
    /// Emits an anchor line for an image (PNG/SVG/remote). The viewer loads and draws it inline via
    /// Sixel below the caption, the same way diagrams are drawn. If the image is wrapped in a link
    /// (<c>[![alt](img)](href)</c>), the caption becomes clickable and follows that link. The
    /// optional <paramref name="gutter"/> (list bullet, quote bar) is drawn in the indent area.
    /// </summary>
    internal void EmitImageAnchor(string url, string altText, string? linkUrl = null, int indent = 0, Rgb? gutterColor = null, string? gutter = null)
    {
        EnsureBlankBefore();
        var key = ImageKey(url);
        var caption = string.IsNullOrWhiteSpace(altText) ? "image" : altText;
        var anchor = NewLine(indent, null, 0, gutterColor, gutter);
        anchor.DiagramKey = key;
        anchor.DiagramIndent = indent;

        if (!string.IsNullOrEmpty(linkUrl))
        {
            // Clickable image: caption is a link, and the reserved image rows are tagged with the
            // same link id so clicking anywhere on the image follows it.
            var id = RegisterLink(linkUrl);
            anchor.ImageLinkId = id;
            anchor.Spans.Add(new StyledSpan("▣ ", Theme.Accent, LinkId: id));
            anchor.Spans.Add(new StyledSpan(caption, Theme.Link, CellStyle.Underline, id));
            AppendLinkMarker(anchor.Spans, id);
        }
        else
        {
            anchor.Spans.Add(new StyledSpan("▣ ", Theme.Accent));
            anchor.Spans.Add(new StyledSpan(caption, Theme.Muted, CellStyle.Italic));
        }
        _lines.Add(anchor);
        _pendingImages[key] = url;
        AddBlank();
    }

    internal static string ImageKey(string url) => "img-" + Hashing.Sha256Hex(url)[..16];

    // ---------------- inline image groups (badges side-by-side) ----------------
    private readonly Dictionary<string, List<string>> _pendingImageGroups = []; // groupKey -> urls
    public IReadOnlyDictionary<string, List<string>> PendingImageGroups => _pendingImageGroups;

    /// <summary>
    /// Emits a single anchor for several images laid out horizontally (e.g. a row of badges). The
    /// viewer loads each image, composes them side-by-side into one strip, and draws it.
    /// </summary>
    internal void EmitImageGroupAnchor(IReadOnlyList<(string Url, string Alt, string? LinkUrl)> images, int indent = 0, Rgb? gutterColor = null, string? gutter = null)
    {
        EnsureBlankBefore();
        var urls = images.Select(i => i.Url).ToList();
        var groupKey = "imgrp-" + Hashing.Sha256Hex(string.Join("|", urls))[..16];

        var anchor = NewLine(indent, null, 0, gutterColor, gutter);
        anchor.DiagramKey = groupKey;
        anchor.DiagramIndent = indent;
        anchor.Spans.Add(new StyledSpan("▣ ", Theme.Accent));
        anchor.Spans.Add(new StyledSpan($"{images.Count} images", Theme.Muted, CellStyle.Italic));
        _lines.Add(anchor);
        _pendingImageGroups[groupKey] = urls;
        AddBlank();
    }

    // ---------------- source access (for constructs Markdig doesn't expose verbatim) ----------------
    private string? _source;

    /// <summary>
    /// The raw text between a container's opening and closing fence lines (e.g. the body of an
    /// Azure DevOps <c>::: mermaid</c> block), sliced from the original document.
    /// </summary>
    private string ContainerBody(Markdig.Syntax.ContainerBlock container)
    {
        if (_source is null) return "";
        var span = container.Span;
        if (span.Start < 0 || span.End < span.Start || span.End >= _source.Length + 1) return "";
        int end = Math.Min(_source.Length, span.End + 1);
        var text = _source[span.Start..end];
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith(":::")) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith(":::")) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }
}
