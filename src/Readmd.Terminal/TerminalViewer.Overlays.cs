using System.Text;
using Markdig;
using Readmd.Core;

namespace Readmd.Terminal;

// Modal overlays for help (?) and TOC (t), styled to mirror the BROWSER front-end's controls as
// closely as a character grid allows: the same palette (app.css variables), a soft blurred drop
// shadow, pseudo-rounded card corners (half-block glyphs), an accent header with a divider, uppercase
// letter-spaced section labels, and raised "kbd" key caps. This is a render of the same document
// model as the browser — not HTML — so we emulate CSS (radius/shadow/keycaps) with Unicode + truecolor.
public sealed partial class TerminalViewer
{
    // ---- palette mirrored from src/Readmd.Web/assets/app.css :root variables ----
    private readonly record struct OverlayPalette(
        Rgb PageBg,       // --bg
        Rgb Panel,        // --bg-elev (card)
        Rgb PanelAlt,     // subtle zebra
        Rgb Border,       // --border
        Rgb Fg,           // --fg
        Rgb Muted,        // --fg-muted
        Rgb Link,         // --link
        Rgb Accent,       // --accent
        Rgb KeyBg,        // kbd background (--bg)
        Rgb KeyBorder,    // kbd border (--border)
        Rgb KeyFg,        // kbd text (--link)
        Rgb SelBg,        // selected row (accent)
        Rgb SelFg);       // selected row text

    private OverlayPalette Palette()
    {
        if (_theme.IsDark)
            return new OverlayPalette(
                PageBg: Rgb.FromHex("#0d1117"),
                Panel: Rgb.FromHex("#161b22"),
                PanelAlt: Rgb.FromHex("#1b212b"),
                Border: Rgb.FromHex("#30363d"),
                Fg: Rgb.FromHex("#e6edf3"),
                Muted: Rgb.FromHex("#9da7b3"),
                Link: Rgb.FromHex("#58a6ff"),
                Accent: Rgb.FromHex("#1f6feb"),
                KeyBg: Rgb.FromHex("#0d1117"),
                KeyBorder: Rgb.FromHex("#30363d"),
                KeyFg: Rgb.FromHex("#58a6ff"),
                SelBg: Rgb.FromHex("#1f6feb"),
                SelFg: Rgb.FromHex("#ffffff"));

        return new OverlayPalette(
            PageBg: Rgb.FromHex("#ffffff"),
            Panel: Rgb.FromHex("#f6f8fa"),
            PanelAlt: Rgb.FromHex("#eef1f4"),
            Border: Rgb.FromHex("#d0d7de"),
            Fg: Rgb.FromHex("#1f2328"),
            Muted: Rgb.FromHex("#636c76"),
            Link: Rgb.FromHex("#0969da"),
            Accent: Rgb.FromHex("#0969da"),
            KeyBg: Rgb.FromHex("#ffffff"),
            KeyBorder: Rgb.FromHex("#d0d7de"),
            KeyFg: Rgb.FromHex("#0969da"),
            SelBg: Rgb.FromHex("#0969da"),
            SelFg: Rgb.FromHex("#ffffff"));
    }

    // =====================================================================================
    //  Scrim — dims the document behind the modal (emulates rgba(0,0,0,.45) + blur)
    // =====================================================================================
    private void DrawScrim(OverlayPalette p)
    {
        // Pull content toward black like the browser's translucent black scrim.
        var scrim = p.PageBg.Darken(_theme.IsDark ? 0.5 : 0.0).Mix(new Rgb(0, 0, 0), _theme.IsDark ? 0.0 : 0.45);
        if (!_theme.IsDark) scrim = p.PageBg.Mix(new Rgb(0, 0, 0), 0.42);
        else scrim = p.PageBg.Darken(0.5);
        double dim = _theme.IsDark ? 0.72 : 0.55;

        int height = ViewportHeight;
        for (int row = 0; row < _screen.Height; row++)
        {
            _screen.MoveTo(row, 0).SetBackground(scrim).ClearLineToEnd();
            int lineIndex = _scroll + row;
            if (row >= height || lineIndex >= _lines.Count) continue;

            var line = _lines[lineIndex];
            if (line.DiagramKey is not null) continue;

            _screen.MoveTo(row, 0);
            int col = 0, width = _screen.Width;
            var lineBg = line.LineBackground is { } lb ? lb.Mix(scrim, dim) : scrim;
            foreach (var span in line.Spans)
            {
                if (col >= width) break;
                var text = span.Text;
                if (col + text.Length > width) text = text[..Math.Max(0, width - col)];
                if (text.Length == 0) continue;
                var fg = (span.Color ?? _theme.Text).Mix(scrim, dim);
                _screen.SetBackground(lineBg).SetForeground(fg).SetStyle(CellStyle.None).Write(text);
                col += text.Length;
            }
            if (col < width) _screen.SetBackground(scrim).Write(new string(' ', width - col));
        }
        _screen.Reset();
    }

    // =====================================================================================
    //  Card chrome — pseudo-rounded border + soft drop shadow, browser-styled
    // =====================================================================================
    // Half-block + rounded box pieces. Rounded corners use the rounded box-drawing glyphs; the soft
    // shadow uses a shaded block blended with the (already-dimmed) backdrop for a CSS-like blur.
    private (int Top, int Left, int Width, int Height) DrawCard(
        OverlayPalette p, int boxTop, int boxLeft, int boxW, int boxH)
    {
        int inner = boxW - 2;

        // --- soft drop shadow (offset down-right), drawn first so the card sits on top ---
        // Browser: box-shadow: 0 12px 48px rgba(0,0,0,.4). We approximate with a shaded edge that
        // fades into the dimmed backdrop using a light-shade glyph blended toward black.
        var backdrop = _scrimBgCache;
        var shadow = backdrop.Mix(new Rgb(0, 0, 0), _theme.IsDark ? 0.45 : 0.30);
        // right edge (one column), shifted down by 1 row
        for (int i = 1; i <= boxH; i++)
        {
            int r = boxTop + i;
            if (r >= _screen.Height) break;
            _screen.MoveTo(r, boxLeft + boxW).SetBackground(backdrop).SetForeground(shadow).Write("░");
        }
        // bottom edge (one row), shifted right by 1 col
        int sb = boxTop + boxH;
        if (sb < _screen.Height)
        {
            _screen.MoveTo(sb, boxLeft + 1).SetBackground(backdrop).SetForeground(shadow).Write(new string('░', inner + 1));
        }

        // --- card body: rounded corners, vertical edges, filled interior ---
        // top border with rounded corners (painted over the dimmed scrim so the rounding reads)
        _screen.SetBackground(_scrimBgCache).SetForeground(p.Border);
        _screen.MoveTo(boxTop, boxLeft).Write("╭");
        _screen.SetForeground(p.Border).Write(new string('─', inner));
        _screen.Write("╮");

        for (int i = 1; i < boxH - 1; i++)
        {
            int r = boxTop + i;
            _screen.MoveTo(r, boxLeft).SetBackground(_scrimBgCache).SetForeground(p.Border).Write("│");
            _screen.SetBackground(p.Panel).Write(new string(' ', inner));
            _screen.SetBackground(_scrimBgCache).SetForeground(p.Border).Write("│");
        }

        _screen.MoveTo(boxTop + boxH - 1, boxLeft).SetBackground(_scrimBgCache).SetForeground(p.Border);
        _screen.Write("╰" + new string('─', inner) + "╯");

        return (boxTop + 1, boxLeft + 1, inner, boxH - 2);
    }

    // cached scrim background used when drawing the card border so corners blend with the dim backdrop
    private Rgb _scrimBgCache;

    /// <summary>Draws the header bar (accent title + divider) and footer bar (divider + hint) inside the card.</summary>
    private void DrawHeaderFooter(OverlayPalette p, int cTop, int cLeft, int cW, int cH, string title, string footer)
    {
        // Header: title row + a divider line beneath it.
        _screen.MoveTo(cTop, cLeft).SetBackground(p.Panel).SetForeground(p.Accent).SetStyle(CellStyle.Bold);
        string ttl = " " + title;
        if (ttl.Length > cW) ttl = ttl[..cW];
        _screen.Write(ttl);
        _screen.SetStyle(CellStyle.None).SetBackground(p.Panel);
        if (ttl.Length < cW) _screen.Write(new string(' ', cW - ttl.Length));

        _screen.MoveTo(cTop + 1, cLeft).SetBackground(p.Panel).SetForeground(p.Border).Write(new string('─', cW));

        // Footer: divider + centered muted hint on the last interior row.
        int divRow = cTop + cH - 2;
        int footRow = cTop + cH - 1;
        _screen.MoveTo(divRow, cLeft).SetBackground(p.Panel).SetForeground(p.Border).Write(new string('─', cW));
        _screen.MoveTo(footRow, cLeft).SetBackground(p.Panel).Write(new string(' ', cW));
        if (!string.IsNullOrEmpty(footer))
        {
            int fl = VisibleWidth(footer);
            int fcol = cLeft + Math.Max(0, (cW - fl) / 2);
            _screen.MoveTo(footRow, fcol).SetBackground(p.Panel).SetForeground(p.Muted).Write(footer);
        }
    }

    /// <summary>Writes a raised "kbd" key cap (browser-like keycap) and returns columns consumed.</summary>
    private int WriteKey(OverlayPalette p, string key)
    {
        // Browser kbd: background var(--bg) on a var(--bg-elev) panel, var(--border) outline,
        // var(--link) text. We mirror that: the keycap background is the PAGE bg (a step off the
        // panel) so each cap reads as a distinct chip, with link-colored bold text.
        _screen.SetBackground(p.KeyBg).SetForeground(p.KeyFg).SetStyle(CellStyle.Bold);
        _screen.Write(" " + key + " ");
        _screen.SetStyle(CellStyle.None).SetBackground(p.Panel);
        return key.Length + 2;
    }

    private static int VisibleWidth(string s)
    {
        int w = 0;
        foreach (var ch in s)
            w += ch >= 0x1100 && (ch <= 0x115F || (ch >= 0x2E80 && ch <= 0xA4CF) ||
                (ch >= 0xAC00 && ch <= 0xD7A3) || (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFF00 && ch <= 0xFF60)) ? 2 : 1;
        return w;
    }

    // =====================================================================================
    //  Help overlay (?)
    // =====================================================================================
    // Built from the live key map so remapped keys show their actual binding.
    private (string Heading, (string Keys, string Desc)[] Items)[] HelpSections()
    {
        var k = _keyMap;
        string K(EditorAction a, string dflt) => k.Label(a, dflt);
        return
        [
            ("MOVE", new[]
            {
                ($"{K(EditorAction.ScrollDown, "j")}  {K(EditorAction.ScrollUp, "k")}", "line down / up (or arrows)"),
                ("Ctrl-d  Ctrl-u", "half page"),
                ($"Ctrl-f  {K(EditorAction.PageDown, "space")}  /  Ctrl-b  {K(EditorAction.PageUp, "b")}", "page down / up"),
                ("gg  G", "top / bottom (Home / End)"),
                ("wheel", "scroll"),
            }),
            ("FIND & GO", new[]
            {
                (K(EditorAction.Search, "/"), "search (type to match live)"),
                ($"{K(EditorAction.SearchNext, "n")}  N", "next / prev match"),
                (K(EditorAction.Toc, "t"), "table of contents"),
                ("1-9  Enter", "follow link #n / first visible link"),
                ("<-  ->  Bksp", "back / forward in history"),
            }),
            ("VIEW", new[]
            {
                (K(EditorAction.ToggleTheme, "["), "cycle theme"),
                (K(EditorAction.ToggleBackground, "]"), "toggle solid background"),
                ("Ctrl-wheel", "zoom diagrams"),
                ("Ctrl-0", "reset zoom"),
                (K(EditorAction.Rerender, "r"), "re-render / clear artifacts"),
            }),
            ("MORE", new[]
            {
                (K(EditorAction.ToggleSelectionMode, "m"), "mark mode (drag = select, right-click = copy)"),
                (K(EditorAction.OpenInBrowser, "o"), "open in browser"),
                (K(EditorAction.ViewImage, "v"), "focus image (zoom/pan · o app · b browser)"),
                (K(EditorAction.Help, "?"), "this help"),
                ($"{K(EditorAction.Quit, "q")}  Ctrl-c", "quit"),
            }),
        ];
    }

    private void DrawHelpOverlay()
    {
        var p = Palette();
        DrawScrim(p);
        _scrimBgCache = _theme.IsDark ? p.PageBg.Darken(0.5) : p.PageBg.Mix(new Rgb(0, 0, 0), 0.42);
        var sections = HelpSections();

        // Column natural width. Within a section every row's keycap is padded to the section's
        // widest keycap, so the description room is (sectionCapW + 3 + desc). Size the column to the
        // widest such row across both halves (and never narrower than the letter-spaced heading).
        int SectionWidth((string Heading, (string Keys, string Desc)[] Items) s)
        {
            int capW = s.Items.Max(it => it.Keys.Length + 2);
            int rows = s.Items.Max(it => capW + 3 + it.Desc.Length);
            return Math.Max(LetterSpace(s.Heading).Length, rows);
        }
        int half = (sections.Length + 1) / 2;
        var left = sections.Take(half).ToArray();
        var right = sections.Skip(half).ToArray();

        int ColW((string Heading, (string Keys, string Desc)[] Items)[] col) =>
            col.Length == 0 ? 0 : col.Max(SectionWidth);
        int colW = Math.Max(Math.Max(ColW(left), ColW(right)), 22);

        int padX = 2, gap = 4;
        int contentW = padX + colW + gap + colW + padX;

        int RowsFor((string Heading, (string Keys, string Desc)[] Items)[] col) =>
            col.Sum(s => 1 + s.Items.Length + 1) - (col.Length > 0 ? 1 : 0);
        int bodyRows = Math.Max(RowsFor(left), RowsFor(right));

        // Card = top border + header(1) + divider(1) + padding(1) + body + padding(1) + divider(1) + footer(1) + bottom border
        int cardW = Math.Min(_screen.Width - 4, contentW + 2);
        int cardH = Math.Min(_screen.Height - 2, bodyRows + 9);
        int boxTop = Math.Max(0, (_screen.Height - cardH) / 2);
        int boxLeft = Math.Max(0, (_screen.Width - cardW) / 2);

        var (cTop, cLeft, cW, cH) = DrawCard(p, boxTop, boxLeft, cardW, cardH);
        DrawHeaderFooter(p, cTop, cLeft, cW, cH, "Keybindings", "esc  or  any key to close");

        // Body region: between header divider (cTop+1) and footer divider (cTop+cH-2), with 1 row padding.
        int bodyTop = cTop + 3;
        int bodyLeft = cLeft + padX;
        int bodyHeight = (cTop + cH - 2) - bodyTop - 1;

        RenderHelpColumn(p, left, bodyTop, bodyLeft, colW, bodyHeight);
        RenderHelpColumn(p, right, bodyTop, bodyLeft + colW + gap, colW, bodyHeight);
        _screen.Reset();
    }

    private void RenderHelpColumn(OverlayPalette p,
        (string Heading, (string Keys, string Desc)[] Items)[] col,
        int top, int left, int width, int maxRows)
    {
        int row = top, endRow = top + maxRows;
        foreach (var (heading, items) in col)
        {
            if (row >= endRow) break;
            // Section heading: uppercase, accent, letter-spaced (insert a space between letters).
            _screen.MoveTo(row, left).SetBackground(p.Panel).SetForeground(p.Accent).SetStyle(CellStyle.Bold);
            string h = LetterSpace(heading);
            if (h.Length > width) h = h[..width];
            _screen.Write(h);
            _screen.SetStyle(CellStyle.None).SetBackground(p.Panel);
            if (h.Length < width) _screen.Write(new string(' ', width - h.Length));
            row++;

            int capW = items.Max(it => it.Keys.Length + 2);
            foreach (var (keys, desc) in items)
            {
                if (row >= endRow) break;
                _screen.MoveTo(row, left).SetBackground(p.Panel);
                int used = WriteKey(p, keys);
                _screen.SetBackground(p.Panel).Write(new string(' ', Math.Max(0, capW - used)));
                _screen.Write("   ");
                int descRoom = width - capW - 3;
                string d = desc.Length > descRoom && descRoom > 1 ? desc[..descRoom] : desc;
                _screen.SetForeground(p.Muted).Write(d);
                int written = capW + 3 + d.Length;
                if (written < width) _screen.SetBackground(p.Panel).Write(new string(' ', width - written));
                row++;
            }
            if (row < endRow) { _screen.MoveTo(row, left).SetBackground(p.Panel).Write(new string(' ', width)); row++; }
        }
        for (; row < endRow; row++)
            _screen.MoveTo(row, left).SetBackground(p.Panel).Write(new string(' ', width));
    }

    private static string LetterSpace(string s)
    {
        if (s.Length <= 1) return s;
        var sb = new StringBuilder(s.Length * 2);
        for (int i = 0; i < s.Length; i++) { sb.Append(s[i]); if (i < s.Length - 1) sb.Append(' '); }
        return sb.ToString();
    }

    // =====================================================================================
    //  TOC overlay (t)
    // =====================================================================================
    private void DrawTocOverlay()
    {
        var p = Palette();
        DrawScrim(p);
        _scrimBgCache = _theme.IsDark ? p.PageBg.Darken(0.5) : p.PageBg.Mix(new Rgb(0, 0, 0), 0.42);

        int longest = _toc.Count == 0 ? 0 : _toc.Max(e => (e.Level - 1) * 2 + e.Title.Length);
        int padX = 1, marker = 2;
        int contentW = Math.Clamp(longest + padX * 2 + marker, 30, _screen.Width - 6);

        int avail = _screen.Height - 9;
        int bodyRows = Math.Min(Math.Max(_toc.Count, 1), Math.Max(3, avail));

        int cardW = Math.Min(_screen.Width - 4, contentW + 2);
        int cardH = Math.Min(_screen.Height - 2, bodyRows + 7);   // borders + header + divider + footer divider + footer
        int boxTop = Math.Max(0, (_screen.Height - cardH) / 2);
        int boxLeft = Math.Max(0, (_screen.Width - cardW) / 2);

        var (cTop, cLeft, cW, cH) = DrawCard(p, boxTop, boxLeft, cardW, cardH);
        DrawHeaderFooter(p, cTop, cLeft, cW, cH, "Contents",
            $"{_tocIndex + 1}/{Math.Max(1, _toc.Count)}   up/down move   enter jump   esc close");

        int bodyTop = cTop + 2;
        int bodyHeight = (cTop + cH - 2) - bodyTop;

        if (_toc.Count == 0)
        {
            _screen.MoveTo(bodyTop, cLeft + padX).SetBackground(p.Panel).SetForeground(p.Muted)
                   .Write("(no headings)".PadRight(cW - padX));
            _screen.Reset();
            return;
        }

        int start = Math.Max(0, Math.Min(_tocIndex - bodyHeight / 2, Math.Max(0, _toc.Count - bodyHeight)));
        for (int i = 0; i < bodyHeight; i++)
        {
            int r = bodyTop + i, idx = start + i;
            _screen.MoveTo(r, cLeft);
            if (idx >= _toc.Count) { _screen.SetBackground(p.Panel).Write(new string(' ', cW)); continue; }

            var entry = _toc[idx];
            bool sel = idx == _tocIndex;
            var bg = sel ? p.SelBg : p.Panel;
            _screen.SetBackground(bg);
            // active marker (accent bar like the browser's left-border highlight)
            _screen.SetForeground(sel ? p.SelFg : p.Accent).Write(sel ? "▌" : " ");

            var indent = new string(' ', (entry.Level - 1) * 2);
            string label = " " + indent + entry.Title;
            int room = cW - 1;
            if (VisibleWidth(label) > room) label = Truncate(label, room);

            if (sel) _screen.SetForeground(p.SelFg).SetStyle(CellStyle.Bold);
            else _screen.SetForeground(entry.Level == 1 ? p.Fg : p.Muted).SetStyle(entry.Level == 1 ? CellStyle.Bold : CellStyle.None);
            _screen.Write(label);
            _screen.SetStyle(CellStyle.None);
            int pad = room - VisibleWidth(label);
            if (pad > 0) _screen.SetBackground(bg).Write(new string(' ', pad));
        }
        _screen.Reset();
    }

    private static string Truncate(string s, int room)
    {
        if (room <= 1) return s.Length > 0 ? "…" : "";
        if (VisibleWidth(s) <= room) return s;
        var sb = new StringBuilder();
        int w = 0;
        foreach (var ch in s)
        {
            int cw = VisibleWidth(ch.ToString());
            if (w + cw > room - 1) break;
            sb.Append(ch); w += cw;
        }
        sb.Append('…');
        return sb.ToString();
    }

    // =====================================================================================
    //  Dev/testing capture hook — renders an overlay to an in-memory ANSI buffer (no terminal I/O).
    // =====================================================================================
    private TerminalViewer(bool dark, int width, int height, IReadOnlyList<TocEntry> toc)
    {
        _options = null!;
        _diagrams = null!;
        _currentPath = "capture.md";
        _resolver = null!;
        _imageLoader = null!;
        _watcher = null!;
        _theme = TerminalTheme.For(dark);
        _toc = toc;
        _screen = AnsiScreen.CreateCapture(width, height);
    }

    public static string CaptureHelpOverlay(bool dark, int width, int height)
    {
        var v = new TerminalViewer(dark, width, height, []);
        v._lines = SampleContent(v._theme, width, height);
        v._screen.BeginFrame();
        v.PaintBaseForCapture();
        v.DrawHelpOverlay();
        return v._screen.CaptureBuffer;
    }

    public static string CaptureTocOverlay(bool dark, int width, int height, IReadOnlyList<TocEntry> toc, int selected)
    {
        var v = new TerminalViewer(dark, width, height, toc);
        v._lines = SampleContent(v._theme, width, height);
        v._tocIndex = Math.Clamp(selected, 0, Math.Max(0, toc.Count - 1));
        v._screen.BeginFrame();
        v.PaintBaseForCapture();
        v.DrawTocOverlay();
        return v._screen.CaptureBuffer;
    }

    private static List<DisplayLine> SampleContent(TerminalTheme theme, int width, int height)
    {
        var lines = new List<DisplayLine>();
        DisplayLine L(params StyledSpan[] spans) { var d = new DisplayLine(); d.Spans.AddRange(spans); return d; }

        lines.Add(L(new StyledSpan("Markdown Viewer", theme.H1, CellStyle.Bold)));
        lines.Add(new DisplayLine());
        lines.Add(L(new StyledSpan("A terminal-first Markdown viewer with mermaid and D2 diagrams,", theme.Text)));
        lines.Add(L(new StyledSpan("syntax-highlighted code, tables, math, and live reload.", theme.Text)));
        lines.Add(new DisplayLine());
        lines.Add(L(new StyledSpan("Getting started", theme.H2, CellStyle.Bold)));
        lines.Add(new DisplayLine());
        lines.Add(L(new StyledSpan("  - ", theme.Accent), new StyledSpan("Run ", theme.Text),
                    new StyledSpan("dnx readmd report.md", theme.Code), new StyledSpan(" to view a file.", theme.Text)));
        lines.Add(L(new StyledSpan("  - ", theme.Accent), new StyledSpan("Press ", theme.Text),
                    new StyledSpan("?", theme.Link), new StyledSpan(" for keybindings, ", theme.Text),
                    new StyledSpan("t", theme.Link), new StyledSpan(" for contents.", theme.Text)));
        lines.Add(new DisplayLine());
        var code = L(new StyledSpan("  var doc = Markdown.Parse(source);", theme.Code));
        code.LineBackground = theme.CodeBackground;
        lines.Add(code);
        var code2 = L(new StyledSpan("  Render(doc, theme);", theme.Code));
        code2.LineBackground = theme.CodeBackground;
        lines.Add(code2);
        lines.Add(new DisplayLine());
        lines.Add(L(new StyledSpan("See the README for the full feature list and usage.", theme.Muted)));
        while (lines.Count < height + 4) lines.Add(L(new StyledSpan("Lorem ipsum dolor sit amet, consectetur adipiscing elit.", theme.Muted)));
        return lines;
    }

    private void PaintBaseForCapture()
    {
        int height = ViewportHeight;
        for (int row = 0; row < _screen.Height; row++)
        {
            _screen.MoveTo(row, 0).SetBackground(_theme.Background).ClearLineToEnd();
            int lineIndex = _scroll + row;
            if (row < height && lineIndex < _lines.Count)
                DrawLine(_lines[lineIndex], lineIndex, row, _screen.Width);
        }
        _screen.Reset();
    }

    /// <summary>
    /// Dev/testing: renders a full Markdown document to an ANSI string (no Sixel — diagram/image
    /// anchors show as their caption rows). Used by the SixelView tool to verify document rendering.
    /// </summary>
    public static string CaptureDocument(bool dark, int width, int height, string markdown, int scroll = 0)
    {
        var v = new TerminalViewer(dark, width, height, []);
        var ast = Markdown.Parse(markdown, TerminalPipeline.Instance);
        var meta = MarkdownRenderer.ExtractMetadata(ast, "capture.md");
        var renderer = new MarkdownTerminalRenderer(v._theme, width - 1);
        var result = renderer.Render(ast, meta.Toc, meta.FrontMatter);
        v._lines = result.Lines;
        v._scroll = Math.Clamp(scroll, 0, Math.Max(0, v._lines.Count - 1));
        v._screen.BeginFrame();
        v.PaintBaseForCapture();
        v._screen.Reset();
        return v._screen.CaptureBuffer;
    }

    /// <summary>Dev/testing: captures a document with a mark-mode selection applied, to verify the
    /// selection highlight rendering.</summary>
    public static string CaptureDocumentWithSelection(bool dark, int width, int height, string markdown,
        int aLine, int aCol, int bLine, int bCol)
    {
        var v = new TerminalViewer(dark, width, height, []);
        var doc = Markdown.Parse(markdown, TerminalPipeline.Instance);
        var renderer = new MarkdownTerminalRenderer(v._theme, width - 1);
        var result = renderer.Render(doc, null);
        v._lines = result.Lines;
        v._selectionMode = true;
        v._selAnchor = (aLine, aCol);
        v._selCursor = (bLine, bCol);
        v._screen.BeginFrame();
        v.PaintBaseForCapture();
        v._screen.Reset();
        return v._screen.CaptureBuffer;
    }
}
