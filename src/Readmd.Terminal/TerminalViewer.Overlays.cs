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
    // ---- palette derived from the active theme (so custom themes recolour overlays too) ----
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
        var t = _theme;
        return new OverlayPalette(
            PageBg: t.Background,
            Panel: t.BackgroundElevated,
            PanelAlt: t.IsDark ? t.BackgroundElevated.Lighten(0.03) : t.BackgroundElevated.Darken(0.03),
            Border: t.Rule,
            Fg: t.Text,
            Muted: t.Muted,
            Link: t.Link,
            Accent: t.Heading,
            KeyBg: t.Background,
            KeyBorder: t.Rule,
            KeyFg: t.Link,
            SelBg: t.SelectionBackground,
            SelFg: t.SelectionForeground);
    }

    // =====================================================================================
    //  Scrim — dims the document behind the modal (emulates rgba(0,0,0,.45) + blur)
    // =====================================================================================
    private void DrawScrim(OverlayPalette p)
    {
        var scrim = _theme.IsDark ? p.PageBg.Darken(0.5) : p.PageBg.Mix(new Rgb(0, 0, 0), 0.42);
        double dim = _theme.IsDark ? 0.72 : 0.55;
        _scrimBgCache = scrim;

        int height = ViewportHeight;
        int offset = ContentOffset;
        for (int row = 0; row < _screen.Height; row++)
        {
            _screen.MoveTo(row, 0).SetBackground(scrim).ClearLineToEnd();
            int lineIndex = _scroll + row;
            if (row >= height || lineIndex >= _lines.Count) continue;

            var line = _lines[lineIndex];
            if (line.DiagramKey is not null) continue;

            _screen.MoveTo(row, offset);
            int col = 0, width = _screen.Width - offset;
            var lineBg = line.LineBackground is { } lb ? lb.Mix(scrim, dim) : scrim;
            foreach (var span in line.Spans)
            {
                if (col >= width) break;
                var text = span.Text;
                int w = TextWidth.Of(text);
                if (col + w > width) { text = TextWidth.TrimToWidth(text, Math.Max(0, width - col)); w = TextWidth.Of(text); }
                if (text.Length == 0) continue;
                var fg = (span.Color ?? _theme.Text).Mix(scrim, dim);
                _screen.SetBackground(lineBg).SetForeground(fg).SetStyle(CellStyle.None).Write(text);
                col += w;
            }
            if (col < width) _screen.SetBackground(scrim).Write(new string(' ', width - col));
        }
        _screen.Reset();
    }

    // =====================================================================================
    //  Card chrome — pseudo-rounded border + soft drop shadow, browser-styled
    // =====================================================================================
    private (int Top, int Left, int Width, int Height) DrawCard(
        OverlayPalette p, int boxTop, int boxLeft, int boxW, int boxH)
    {
        int inner = boxW - 2;

        // --- soft drop shadow (offset down-right), drawn first so the card sits on top ---
        var backdrop = _scrimBgCache;
        var shadow = backdrop.Mix(new Rgb(0, 0, 0), _theme.IsDark ? 0.45 : 0.30);
        for (int i = 1; i <= boxH; i++)
        {
            int r = boxTop + i;
            if (r >= _screen.Height) break;
            _screen.MoveTo(r, boxLeft + boxW).SetBackground(backdrop).SetForeground(shadow).Write(Glyphs.Shadow.ToString());
        }
        int sb = boxTop + boxH;
        if (sb < _screen.Height)
        {
            _screen.MoveTo(sb, boxLeft + 1).SetBackground(backdrop).SetForeground(shadow).Write(new string(Glyphs.Shadow, inner + 1));
        }

        // --- card body: rounded corners, vertical edges, filled interior ---
        _screen.SetBackground(_scrimBgCache).SetForeground(p.Border);
        _screen.MoveTo(boxTop, boxLeft).Write(Glyphs.CornerTL);
        _screen.SetForeground(p.Border).Write(new string(Glyphs.Rule, inner));
        _screen.Write(Glyphs.CornerTR);

        for (int i = 1; i < boxH - 1; i++)
        {
            int r = boxTop + i;
            _screen.MoveTo(r, boxLeft).SetBackground(_scrimBgCache).SetForeground(p.Border).Write(Glyphs.BoxV.ToString());
            _screen.SetBackground(p.Panel).Write(new string(' ', inner));
            _screen.SetBackground(_scrimBgCache).SetForeground(p.Border).Write(Glyphs.BoxV.ToString());
        }

        _screen.MoveTo(boxTop + boxH - 1, boxLeft).SetBackground(_scrimBgCache).SetForeground(p.Border);
        _screen.Write(Glyphs.CornerBL + new string(Glyphs.Rule, inner) + Glyphs.CornerBR);

        return (boxTop + 1, boxLeft + 1, inner, boxH - 2);
    }

    // cached scrim background used when drawing the card border so corners blend with the dim backdrop
    private Rgb _scrimBgCache;

    /// <summary>Draws the header bar (accent title + divider) and footer bar (divider + hint) inside the card.</summary>
    private void DrawHeaderFooter(OverlayPalette p, int cTop, int cLeft, int cW, int cH, string title, string footer)
    {
        _screen.MoveTo(cTop, cLeft).SetBackground(p.Panel).SetForeground(p.Accent).SetStyle(CellStyle.Bold);
        string ttl = TextWidth.TrimToWidth(" " + title, cW);
        _screen.Write(ttl);
        _screen.SetStyle(CellStyle.None).SetBackground(p.Panel);
        int tw = TextWidth.Of(ttl);
        if (tw < cW) _screen.Write(new string(' ', cW - tw));

        _screen.MoveTo(cTop + 1, cLeft).SetBackground(p.Panel).SetForeground(p.Border).Write(new string(Glyphs.Rule, cW));

        int divRow = cTop + cH - 2;
        int footRow = cTop + cH - 1;
        _screen.MoveTo(divRow, cLeft).SetBackground(p.Panel).SetForeground(p.Border).Write(new string(Glyphs.Rule, cW));
        _screen.MoveTo(footRow, cLeft).SetBackground(p.Panel).Write(new string(' ', cW));
        if (!string.IsNullOrEmpty(footer))
        {
            footer = TextWidth.TrimToWidth(footer, cW);
            int fl = TextWidth.Of(footer);
            int fcol = cLeft + Math.Max(0, (cW - fl) / 2);
            _screen.MoveTo(footRow, fcol).SetBackground(p.Panel).SetForeground(p.Muted).Write(footer);
        }
    }

    /// <summary>Writes a raised "kbd" key cap (browser-like keycap) and returns columns consumed.</summary>
    private int WriteKey(OverlayPalette p, string key)
    {
        _screen.SetBackground(p.KeyBg).SetForeground(p.KeyFg).SetStyle(CellStyle.Bold);
        _screen.Write(" " + key + " ");
        _screen.SetStyle(CellStyle.None).SetBackground(p.Panel);
        return TextWidth.Of(key) + 2;
    }

    // =====================================================================================
    //  Help overlay (?)
    // =====================================================================================
    // Built from the live key map so remapped keys show their actual binding. Every default-bound
    // action appears here (a test enforces it); the layout adapts to 1–3 columns and scrolls when
    // the terminal is too short, so nothing is ever silently cut off.
    private int _helpScroll;

    private (string Heading, (string Keys, string Desc)[] Items)[] HelpSections()
    {
        var k = _keyMap;
        string K(EditorAction a, string dflt) => k.Label(a, dflt);
        // "Ctrl-d  Ctrl-u" → "Ctrl-d/u" when both keys share a modifier: keeps the key column narrow
        // so two columns fit a 100-column terminal.
        string K2(EditorAction a, EditorAction b, string da, string db)
        {
            string x = K(a, da), y = K(b, db);
            int dash = x.LastIndexOf('-');
            if (dash > 0 && dash == y.LastIndexOf('-') && x[..dash] == y[..dash] && x.Length - dash == 2 && y.Length - dash == 2)
                return $"{x}/{y[^1]}";
            return $"{x}  {y}";
        }
        return
        [
            ("MOVE", new[]
            {
                (K2(EditorAction.ScrollDown, EditorAction.ScrollUp, "j", "k"), "line down / up (count: 5j)"),
                (K2(EditorAction.HalfPageDown, EditorAction.HalfPageUp, "Ctrl-d", "Ctrl-u"), "half page down / up"),
                (K2(EditorAction.PageDown, EditorAction.PageUp, "Ctrl-f", "Ctrl-b"), "page down / up (Space, b)"),
                (K2(EditorAction.GoTop, EditorAction.GoBottom, "gg", "G"), "top / bottom (12gg: line 12)"),
                (K2(EditorAction.NextHeading, EditorAction.PrevHeading, "]]", "[["), "next / previous heading"),
                (K2(EditorAction.NextBlock, EditorAction.PrevBlock, "}", "{"), "next / previous block"),
                (K2(EditorAction.NextCode, EditorAction.PrevCode, "]c", "[c"), "next / previous code block"),
                (K2(EditorAction.NextImage, EditorAction.PrevImage, "]i", "[i"), "next / previous image"),
            }),
            ("FIND", new[]
            {
                (K(EditorAction.Search, "/"), "search (Ctrl-r regex · Ctrl-i Aa)"),
                (K2(EditorAction.SearchNext, EditorAction.SearchPrev, "n", "N"), "next / previous match"),
                (K(EditorAction.ClearSearch, "Esc"), "clear search / selection"),
                (K(EditorAction.Toc, "t"), "contents overlay (type to filter)"),
                (K(EditorAction.TocSidebar, "T"), "TOC sidebar (click to jump)"),
                (K(EditorAction.ProjectSearch, "Ctrl-g"), "search across all files"),
            }),
            ("LINKS", new[]
            {
                (K(EditorAction.LinkHints, "f"), "link hints: type a label to open"),
                (K(EditorAction.CopyLinkUrl, "F"), "link hints: copy a link's URL"),
                (K(EditorAction.NextLink, "Tab"), $"cycle links ({K(EditorAction.PrevLink, "Shift-Tab")} back)"),
                (K(EditorAction.FollowLink, "Enter"), "open the active / first link"),
                (K2(EditorAction.Back, EditorAction.Forward, "←", "→"), "back / forward (also Bksp)"),
                (K(EditorAction.OpenInBrowser, "o"), "open the document in a browser"),
            }),
            ("FILES", new[]
            {
                (K(EditorAction.QuickOpen, "Ctrl-p"), "quick-open a file (fuzzy)"),
                (K2(EditorAction.NextFile, EditorAction.PrevFile, "]f", "[f"), "next / previous file"),
                ($"{K(EditorAction.Bookmark, "M")}a  {K(EditorAction.GoBookmark, "'")}a", "set / jump to bookmark a ('')"),
                (K(EditorAction.Export, "e"), "export to HTML / PDF"),
                (K(EditorAction.ToggleWatch, "W"), "pause / resume live reload"),
                (K(EditorAction.Follow, "Alt-f"), "follow mode (tail the file)"),
                (K(EditorAction.Rerender, "r"), "re-render from disk"),
            }),
            ("TOP BLOCK", new[]
            {
                (K(EditorAction.Yank, "y"), "copy it: code, diagram, table"),
                (K(EditorAction.YankAll, "Y"), "copy the whole document"),
                ($"{K(EditorAction.FoldToggle, "za")}  {K(EditorAction.FoldClose, "zc")}  {K(EditorAction.FoldOpen, "zo")}", "fold toggle / close / open"),
                (K2(EditorAction.FoldAll, EditorAction.UnfoldAll, "zM", "zR"), "fold / unfold everything"),
                (K(EditorAction.ToggleDiagramSource, "s"), "diagram: image ↔ source"),
                (K(EditorAction.ViewImage, "v"), "focus image (zoom, pan, n/p)"),
                ($"{K(EditorAction.ZoomIn, "+")} {K(EditorAction.ZoomOut, "-")} {K(EditorAction.ZoomReset, "Ctrl-0")}", "zoom diagrams (Ctrl-wheel)"),
            }),
            ("VIEW", new[]
            {
                (K2(EditorAction.ToggleTheme, EditorAction.ToggleBackground, "[", "]"), "cycle theme / solid background"),
                (K(EditorAction.ToggleSelectionMode, "m"), "mark mode: drag selects, y copies"),
                (K(EditorAction.Command, ":"), "command line (Tab completes)"),
                (K(EditorAction.Slides, "Ctrl-s"), _slideMode ? "leave slides (n/p, N notes, 5gg)" : "slides: present --- sections"),
                (K2(EditorAction.Help, EditorAction.Quit, "?", "q"), "this help / quit"),
            }),
            ("MOUSE", new[]
            {
                ("wheel", "scroll (Ctrl-wheel: zoom)"),
                ("click", "open link (Ctrl: no confirm)"),
                ("drag", "select text (in mark mode)"),
                ("right-click", "copy the selection"),
                ("click TOC", "jump to heading (sidebar, t)"),
            }),
        ];
    }

    private enum HelpRowKind { Heading, Item, Blank }
    private readonly record struct HelpRow(HelpRowKind Kind, string Keys, string Desc, int CapW);

    /// <summary>Flattens a section into rows (heading, items, trailing blank) with the section's key-column width.</summary>
    private static List<HelpRow> HelpRowsOf((string Heading, (string Keys, string Desc)[] Items) s)
    {
        int capW = s.Items.Max(it => TextWidth.Of(it.Keys) + 2);
        var rows = new List<HelpRow> { new(HelpRowKind.Heading, s.Heading, "", capW) };
        foreach (var (keys, desc) in s.Items) rows.Add(new HelpRow(HelpRowKind.Item, keys, desc, capW));
        rows.Add(new HelpRow(HelpRowKind.Blank, "", "", capW));
        return rows;
    }

    private static int HelpSectionWidth((string Heading, (string Keys, string Desc)[] Items) s)
    {
        int capW = s.Items.Max(it => TextWidth.Of(it.Keys) + 2);
        int widest = s.Items.Max(it => capW + 3 + TextWidth.Of(it.Desc));
        return Math.Max(TextWidth.Of(LetterSpace(s.Heading)), widest);
    }

    /// <summary>
    /// Lays the sections out in the most columns that fit the width (balanced by row count, each
    /// column as wide as its widest section). Returns the columns, their widths and the row count.
    /// </summary>
    private (List<List<HelpRow>> Columns, List<int> Widths, int Rows) HelpLayout(int availableWidth)
    {
        var sections = HelpSections();
        const int gap = 3;
        for (int cols = 3; cols >= 1; cols--)
        {
            int totalRows = sections.Sum(s => s.Items.Length + 2);
            int target = (int)Math.Ceiling(totalRows / (double)cols);
            var columns = new List<List<(string Heading, (string Keys, string Desc)[] Items)>> { new() };
            int rowsInCurrent = 0;
            foreach (var s in sections)
            {
                int rows = s.Items.Length + 2;
                if (rowsInCurrent > 0 && rowsInCurrent + rows > target + 1 && columns.Count < cols) { columns.Add(new()); rowsInCurrent = 0; }
                columns[^1].Add(s);
                rowsInCurrent += rows;
            }
            var widths = columns.Select(col => col.Max(HelpSectionWidth)).ToList();
            int total = widths.Sum() + gap * (widths.Count - 1);
            if (total <= availableWidth || cols == 1)
            {
                if (cols == 1) widths[0] = Math.Min(widths[0], availableWidth);
                var rowLists = columns.Select(col => col.SelectMany(HelpRowsOf).ToList()).ToList();
                foreach (var rl in rowLists) if (rl.Count > 0 && rl[^1].Kind == HelpRowKind.Blank) rl.RemoveAt(rl.Count - 1);
                return (rowLists, widths, rowLists.Max(r => r.Count));
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    private int _helpBodyRows = 1, _helpTotalRows = 1;

    private void DrawHelpOverlay()
    {
        var p = Palette();
        DrawScrim(p);
        const int padX = 1, gap = 3;
        int maxContent = _screen.Width - 4 - 2 - 2 * padX;      // card margins, borders, padding
        var (columns, widths, totalRows) = HelpLayout(maxContent);
        int contentW = widths.Sum() + gap * (widths.Count - 1);

        int cardW = Math.Min(_screen.Width - 4, contentW + 2 * padX + 2);
        int cardH = Math.Min(_screen.Height - 2, totalRows + 8);
        int boxTop = Math.Max(0, (_screen.Height - cardH) / 2);
        int boxLeft = Math.Max(0, (_screen.Width - cardW) / 2);

        var (cTop, cLeft, cW, cH) = DrawCard(p, boxTop, boxLeft, cardW, cardH);
        int bodyTop = cTop + 2;
        int bodyHeight = Math.Max(1, (cTop + cH - 2) - bodyTop);
        _helpBodyRows = bodyHeight;
        _helpTotalRows = totalRows;
        int maxScroll = Math.Max(0, totalRows - bodyHeight);
        _helpScroll = Math.Clamp(_helpScroll, 0, maxScroll);
        bool scrolls = maxScroll > 0;

        string footer;
        if (scrolls)
        {
            string up = _helpScroll > 0 ? "▴ " : "  ", down = _helpScroll < maxScroll ? " ▾" : "  ";
            string pos = $"{_helpScroll + 1}–{Math.Min(totalRows, _helpScroll + bodyHeight)}/{totalRows}";
            footer = $"{up}j/k scroll · Space/b page · esc closes   {pos}{down}";
            if (TextWidth.Of(footer) > cW) footer = $"{up}j/k · Space/b · esc   {pos}{down}";   // narrow card
        }
        else footer = "esc  or  any key to close";
        if (Glyphs.Ascii) footer = footer.Replace("▴", "^").Replace("▾", "v").Replace("·", "-");
        DrawHeaderFooter(p, cTop, cLeft, cW, cH, "Keybindings" + (_slideMode ? "  ·  slides" : ""), footer);

        int left = cLeft + padX;
        for (int i = 0; i < columns.Count; i++)
        {
            RenderHelpColumn(p, columns[i], bodyTop, left, widths[i], bodyHeight, _helpScroll);
            left += widths[i] + gap;
        }
        // Fill the gaps between columns so the panel background is continuous.
        _screen.Reset();
    }

    private void RenderHelpColumn(OverlayPalette p, List<HelpRow> rows, int top, int left, int width, int height, int scroll)
    {
        for (int i = 0; i < height; i++)
        {
            int screenRow = top + i, idx = scroll + i;
            _screen.MoveTo(screenRow, left).SetBackground(p.Panel);
            if (idx < 0 || idx >= rows.Count) { _screen.Write(new string(' ', width)); continue; }
            var row = rows[idx];
            switch (row.Kind)
            {
                case HelpRowKind.Heading:
                {
                    _screen.SetForeground(p.Accent).SetStyle(CellStyle.Bold);
                    string h = TextWidth.TrimToWidth(LetterSpace(row.Keys), width);
                    _screen.Write(h);
                    _screen.SetStyle(CellStyle.None).SetBackground(p.Panel);
                    int hw = TextWidth.Of(h);
                    if (hw < width) _screen.Write(new string(' ', width - hw));
                    break;
                }
                case HelpRowKind.Item:
                {
                    int used = WriteKey(p, row.Keys);
                    _screen.SetBackground(p.Panel).Write(new string(' ', Math.Max(0, row.CapW - used)));
                    _screen.Write("   ");
                    int descRoom = width - row.CapW - 3;
                    string d = TextWidth.TrimToWidth(row.Desc, Math.Max(1, descRoom));
                    _screen.SetForeground(p.Muted).Write(d);
                    int written = row.CapW + 3 + TextWidth.Of(d);
                    if (written < width) _screen.SetBackground(p.Panel).Write(new string(' ', width - written));
                    break;
                }
                default:
                    _screen.Write(new string(' ', width));
                    break;
            }
        }
    }

    /// <summary>Keys while the help overlay is open: scroll it, or close it.</summary>
    private void HandleHelpKey(KeyEvent key)
    {
        int page = Math.Max(1, _helpBodyRows - 1);
        int maxScroll = Math.Max(0, _helpTotalRows - _helpBodyRows);
        void Scroll(int delta) { _helpScroll = Math.Clamp(_helpScroll + delta, 0, maxScroll); _dirty = true; }
        switch (key.Kind)
        {
            case KeyKind.Down or KeyKind.MouseScrollDown: Scroll(key.Kind == KeyKind.Down ? 1 : 3); return;
            case KeyKind.Up or KeyKind.MouseScrollUp: Scroll(key.Kind == KeyKind.Up ? -1 : -3); return;
            case KeyKind.PageDown: Scroll(page); return;
            case KeyKind.PageUp: Scroll(-page); return;
            case KeyKind.Home: Scroll(-maxScroll); return;
            case KeyKind.End: Scroll(maxScroll); return;
            case KeyKind.MouseDrag or KeyKind.MouseDragEnd or KeyKind.MouseRightClick or KeyKind.MouseMiddleClick: return;
            case KeyKind.Char when !key.Alt:
                switch (key.Char)
                {
                    case 'j' when !key.Ctrl: Scroll(1); return;
                    case 'k' when !key.Ctrl: Scroll(-1); return;
                    case ' ' when !key.Ctrl: Scroll(page); return;
                    case 'b' when !key.Ctrl: Scroll(-page); return;
                    case 'd' when key.Ctrl: Scroll(page / 2); return;
                    case 'u' when key.Ctrl: Scroll(-page / 2); return;
                    case 'f' when key.Ctrl: Scroll(page); return;
                    case 'b' when key.Ctrl: Scroll(-page); return;
                    case 'g': Scroll(-maxScroll); return;
                    case 'G': Scroll(maxScroll); return;
                }
                break;
        }
        // Esc, q, ?, Enter, a click — or any other key — dismisses the overlay.
        _helpMode = false;
        _helpScroll = 0;
        _dirty = true;
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
    private (int Top, int Left, int Height, int Start) _tocGeometry;

    private void DrawTocOverlay()
    {
        var p = Palette();
        DrawScrim(p);
        var entries = FilteredToc;
        if (_tocIndex >= entries.Count) _tocIndex = Math.Max(0, entries.Count - 1);

        int longest = _toc.Count == 0 ? 0 : _toc.Max(e => (e.Level - 1) * 2 + TextWidth.Of(e.Title));
        int padX = 1, marker = 2;
        int contentW = Math.Clamp(longest + padX * 2 + marker, 30, Math.Max(30, _screen.Width - 6));

        int avail = _screen.Height - 10;
        int bodyRows = Math.Min(Math.Max(entries.Count, 1), Math.Max(3, avail));

        int cardW = Math.Min(_screen.Width - 4, contentW + 2);
        int cardH = Math.Min(_screen.Height - 2, bodyRows + 8);   // borders + header + divider + filter + footer divider + footer
        int boxTop = Math.Max(0, (_screen.Height - cardH) / 2);
        int boxLeft = Math.Max(0, (_screen.Width - cardW) / 2);

        var (cTop, cLeft, cW, cH) = DrawCard(p, boxTop, boxLeft, cardW, cardH);
        DrawHeaderFooter(p, cTop, cLeft, cW, cH, "Contents",
            $"{(entries.Count == 0 ? 0 : _tocIndex + 1)}/{entries.Count}   type to filter   ↑↓ move   enter jump   esc close");

        // Filter row under the header divider.
        int filterRow = cTop + 2;
        _screen.MoveTo(filterRow, cLeft).SetBackground(p.Panel).SetForeground(_tocFilter.Length == 0 ? p.Muted : p.Fg);
        string filterText = TextWidth.TrimToWidth(" " + (_tocFilter.Length == 0 ? "filter" + Glyphs.Ellipsis : "/" + _tocFilter + Glyphs.Cursor), cW);
        _screen.Write(filterText);
        int fw = TextWidth.Of(filterText);
        if (fw < cW) _screen.Write(new string(' ', cW - fw));

        int bodyTop = cTop + 3;
        int bodyHeight = (cTop + cH - 2) - bodyTop;

        if (entries.Count == 0)
        {
            _screen.MoveTo(bodyTop, cLeft + padX).SetBackground(p.Panel).SetForeground(p.Muted)
                   .Write(TextWidth.TrimToWidth(_toc.Count == 0 ? "(no headings)" : "(no match)", cW - padX).PadRight(cW - padX));
            for (int i = 1; i < bodyHeight; i++) _screen.MoveTo(bodyTop + i, cLeft).SetBackground(p.Panel).Write(new string(' ', cW));
            _tocGeometry = (bodyTop, cLeft, bodyHeight, 0);
            _screen.Reset();
            return;
        }

        int start = Math.Max(0, Math.Min(_tocIndex - bodyHeight / 2, Math.Max(0, entries.Count - bodyHeight)));
        _tocGeometry = (bodyTop, cLeft, bodyHeight, start);
        // The section the viewport is currently in (so it can be marked distinctly from the cursor).
        int currentSection = CurrentSectionIndex(entries);
        for (int i = 0; i < bodyHeight; i++)
        {
            int r = bodyTop + i, idx = start + i;
            _screen.MoveTo(r, cLeft);
            if (idx >= entries.Count) { _screen.SetBackground(p.Panel).Write(new string(' ', cW)); continue; }

            var entry = entries[idx];
            bool sel = idx == _tocIndex;
            bool current = idx == currentSection;
            var bg = sel ? p.SelBg : (current ? p.PanelAlt : p.Panel);
            _screen.SetBackground(bg);
            _screen.SetForeground(sel ? p.SelFg : p.Accent).Write(sel ? Glyphs.Selected : (current ? Glyphs.Current : " "));

            var indent = new string(' ', (entry.Level - 1) * 2);
            string label = " " + indent + entry.Title;
            int room = cW - 1;
            if (TextWidth.Of(label) > room) label = TruncateVisible(label, room);

            if (sel) _screen.SetForeground(p.SelFg).SetStyle(CellStyle.Bold);
            else _screen.SetForeground(entry.Level == 1 ? p.Fg : p.Muted).SetStyle(entry.Level == 1 ? CellStyle.Bold : CellStyle.None);
            _screen.Write(label);
            _screen.SetStyle(CellStyle.None);
            int pad = room - TextWidth.Of(label);
            if (pad > 0) _screen.SetBackground(bg).Write(new string(' ', pad));
        }
        _screen.Reset();
    }

    /// <summary>Index (into <paramref name="entries"/>) of the heading the viewport's top line belongs to.</summary>
    private int CurrentSectionIndex(IReadOnlyList<TocEntry> entries)
    {
        int best = -1;
        var rows = HeadingRows();
        for (int i = 0; i < entries.Count; i++)
        {
            if (rows.TryGetValue(entries[i].Id, out int line) && line <= _scroll) best = i;
        }
        return best;
    }

    /// <summary>Maps a mouse click inside the TOC overlay to an entry index.</summary>
    private bool TocRowAt(int screenRow, int screenCol, out int index)
    {
        index = -1;
        var (top, left, height, start) = _tocGeometry;
        if (screenRow < top || screenRow >= top + height) return false;
        if (screenCol < left) return false;
        index = start + (screenRow - top);
        return index >= 0 && index < FilteredToc.Count;
    }

    private static string TruncateVisible(string s, int room)
    {
        if (room <= 1) return s.Length > 0 ? "…" : "";
        if (TextWidth.Of(s) <= room) return s;
        return TextWidth.TrimToWidth(s, Math.Max(0, room - 1)) + "…";
    }

    // =====================================================================================
    //  Link hints — labels drawn over every visible link (f / F)
    // =====================================================================================
    private void DrawHintLabels()
    {
        if (!_hintMode || _hints.Count == 0) return;
        var labelBg = _theme.IsDark ? Rgb.FromHex("#f0c674") : Rgb.FromHex("#9a6700");
        var labelFg = _theme.IsDark ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
        var matchedBg = _theme.IsDark ? Rgb.FromHex("#56d364") : Rgb.FromHex("#1a7f37");
        int offset = ContentOffset;
        var byLink = _hints.ToDictionary(kv => kv.Value, kv => kv.Key);
        var drawn = new HashSet<int>();
        for (int row = 0; row < ViewportHeight; row++)
        {
            int lineIndex = _scroll + row;
            if (lineIndex >= _lines.Count) break;
            var line = _lines[lineIndex];
            int col = 0;
            foreach (var span in line.Spans)
            {
                int w = TextWidth.Of(span.Text);
                if (span.LinkId is { } id && byLink.TryGetValue(id, out var label) && drawn.Add(id))
                {
                    bool prefixMatch = _hintTyped.Length > 0 && label.StartsWith(_hintTyped, StringComparison.OrdinalIgnoreCase);
                    int at = Math.Max(0, col - 1);
                    _screen.MoveTo(row, offset + at).SetBackground(prefixMatch ? matchedBg : labelBg).SetForeground(labelFg).SetStyle(CellStyle.Bold);
                    _screen.Write(label.ToUpperInvariant());
                    _screen.Reset();
                }
                col += w;
            }
            if (line.ImageLinkId is { } il && byLink.TryGetValue(il, out var ilabel) && drawn.Add(il))
            {
                _screen.MoveTo(row, offset).SetBackground(labelBg).SetForeground(labelFg).SetStyle(CellStyle.Bold).Write(ilabel.ToUpperInvariant());
                _screen.Reset();
            }
        }
    }

    // =====================================================================================
    //  Dev/testing capture hook — renders to an in-memory ANSI buffer (no terminal I/O).
    // =====================================================================================
    private TerminalViewer(bool dark, int width, int height, IReadOnlyList<TocEntry> toc, TerminalViewerOptions? options = null)
    {
        _options = options ?? new TerminalViewerOptions { FilePath = "capture.md", DarkTerminal = dark };
        _diagrams = new NullDiagramRenderer();
        _currentPath = "capture.md";
        var root = _options.Root is not null ? Path.GetFullPath(_options.Root) : Directory.GetCurrentDirectory();
        _resolver = new LinkResolver(root);
        _imageLoader = new Readmd.Diagrams.ImageLoader(root);
        _watcher = null;
        _theme = _options.Theme ?? TerminalTheme.For(dark);
        _keyMap = _options.KeyMap ?? KeyMap.Default;
        _graphicsMode = GraphicsMode.None;
        _toc = toc;
        _screen = AnsiScreen.CreateCapture(width, height);
    }

    /// <summary>
    /// Creates a headless viewer over <paramref name="markdown"/> for tests: no terminal, no
    /// watcher, deterministic (synchronous) highlighting, images/diagrams never render. Drive it with
    /// <see cref="Press"/> and inspect <see cref="FrameText"/>.
    /// </summary>
    internal static TerminalViewer CreateHeadless(string markdown, int width = 80, int height = 24, bool dark = true, TerminalViewerOptions? options = null, string path = "capture.md")
    {
        options ??= new TerminalViewerOptions { FilePath = path, DarkTerminal = dark, SynchronousHighlighting = true, RememberPosition = false };
        var v = new TerminalViewer(dark, width, height, [], options);
        v._currentPath = Path.GetFullPath(path);
        v.LoadMarkdown(v._currentPath, markdown, pushHistory: true);
        return v;
    }

    private sealed class NullDiagramRenderer : IDiagramRenderer
    {
        public DiagramResult? TryGet(string key) => null;
        public Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct = default) =>
            Task.FromResult(DiagramResult.Pending(request.Key));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Headless: feeds key events as the render loop would.</summary>
    internal void Press(params KeyEvent[] keys)
    {
        foreach (var k in keys) HandleKeyEvent(k);
    }

    /// <summary>Headless: feeds a chord spec such as "j", "gg", "ctrl+d", "enter", "esc", "/abc enter".</summary>
    internal void Press(string spec)
    {
        foreach (var token in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ev = KeyEventFromToken(token);
            if (ev is not null) HandleKeyEvent(ev);
            else foreach (var ch in token) HandleKeyEvent(new KeyEvent(KeyKind.Char, ch));
        }
    }

    /// <summary>Headless: the help overlay's content as "keys<TAB>description" lines (every section, no layout).</summary>
    internal IEnumerable<string> HelpEntries() =>
        HelpSections().SelectMany(s => s.Items.Select(it => it.Keys + "\t" + it.Desc));

    internal bool InHelpMode => _helpMode;

    /// <summary>Headless: simulates a terminal resize followed by the debounced reflow.</summary>
    internal void Resize(int width, int height)
    {
        _screen.SetCaptureSize(width, height);
        ReflowForResize();
    }

    /// <summary>Headless: fires a pending chord prefix (a bare "[" or "g") as the render loop would after the timeout.</summary>
    internal void FlushPendingChord()
    {
        _pendingChordUntil = DateTime.MinValue;
        TickPendingChord();
    }

    /// <summary>Headless: types literal text (every character, spaces included) as key events.</summary>
    internal void Type(string text)
    {
        foreach (var ch in text) HandleKeyEvent(new KeyEvent(KeyKind.Char, ch));
    }

    internal static KeyEvent? KeyEventFromToken(string token)
    {
        bool ctrl = false, alt = false, shift = false;
        string key = token;
        if (token.Contains('+') && token.Length > 1)
        {
            var parts = token.Split('+');
            key = parts[^1]; if (key.Length == 0) key = "+";
            foreach (var m in parts[..^1]) { if (m == "ctrl") ctrl = true; else if (m == "alt") alt = true; else if (m == "shift") shift = true; }
        }
        KeyKind? kind = key.ToLowerInvariant() switch
        {
            "enter" => KeyKind.Enter, "esc" => KeyKind.Escape, "tab" => KeyKind.Tab, "backspace" => KeyKind.Backspace,
            "up" => KeyKind.Up, "down" => KeyKind.Down, "left" => KeyKind.Left, "right" => KeyKind.Right,
            "home" => KeyKind.Home, "end" => KeyKind.End, "pageup" => KeyKind.PageUp, "pagedown" => KeyKind.PageDown,
            "space" => KeyKind.Char, "delete" => KeyKind.Delete, _ => null,
        };
        if (kind is not null) return new KeyEvent(kind.Value, key == "space" ? ' ' : '\0', ctrl, Alt: alt, Shift: shift);
        if (key.Length == 1) return new KeyEvent(KeyKind.Char, key[0], ctrl, Alt: alt, Shift: shift);
        return null;
    }

    /// <summary>Headless: the current scroll position (visible line index).</summary>
    internal int Scroll => _scroll;
    internal IReadOnlyList<DisplayLine> VisibleLines => _lines;
    internal IReadOnlyList<DisplayLine> AllLines => _allLines;
    internal int SearchHitCount => _searchHits.Count;
    internal bool IsRunning => _running;
    internal bool InHintMode => _hintMode;
    internal IReadOnlyDictionary<string, int> Hints => _hints;
    internal string? StatusMessage => _statusMessage;
    /// <summary>Headless: clears a transient status message as the render loop would after its timeout.</summary>
    internal void ExpireStatus() { _statusMessage = null; _dirty = true; }
    internal IReadOnlyCollection<int> FoldedIds => _folded;
    internal string CurrentPath => _currentPath;

    /// <summary>Headless: renders a frame and returns the raw ANSI buffer.</summary>
    internal string Frame()
    {
        TickPendingChord();
        _screen.BeginFrame();
        Draw();
        return _screen.CaptureBuffer;
    }

    /// <summary>Headless: renders a frame and returns it as plain text rows (ANSI stripped).</summary>
    internal List<string> FrameText()
    {
        var ansi = Frame();
        return AnsiText.ToRows(ansi, _screen.Width, _screen.Height);
    }

    public static string CaptureHelpOverlay(bool dark, int width, int height)
    {
        var v = new TerminalViewer(dark, width, height, []);
        v._lines = SampleContent(v._theme, width, height);
        v._allLines = v._lines;
        v._screen.BeginFrame();
        v.PaintBaseForCapture();
        v.DrawHelpOverlay();
        return v._screen.CaptureBuffer;
    }

    public static string CaptureTocOverlay(bool dark, int width, int height, IReadOnlyList<TocEntry> toc, int selected)
    {
        var v = new TerminalViewer(dark, width, height, toc);
        v._lines = SampleContent(v._theme, width, height);
        v._allLines = v._lines;
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
        var result = renderer.Render(ast, meta.Toc, meta.FrontMatter, markdown);
        v._allLines = result.Lines;
        v._lines = new List<DisplayLine>(result.Lines);
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
        var result = renderer.Render(doc, null, null, markdown);
        v._allLines = result.Lines;
        v._lines = new List<DisplayLine>(result.Lines);
        v._selectionMode = true;
        v._selAnchor = (aLine, aCol);
        v._selCursor = (bLine, bCol);
        v._screen.BeginFrame();
        v.PaintBaseForCapture();
        v._screen.Reset();
        return v._screen.CaptureBuffer;
    }
}
