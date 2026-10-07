using Readmd.Core;

namespace Readmd.Terminal;

// Presentation mode (--slides / Ctrl+S / :slides): the document is split into slides (SlideDeck)
// and one slide at a time is laid out through the normal pipeline, vertically centred, with
// progressive reveal (<!-- pause -->), speaker notes (N), a slide counter and an elapsed-time clock.
public sealed partial class TerminalViewer
{
    private bool _slideMode;
    private SlideDeck? _deck;
    private int _slideIndex;
    private int _slideStep;
    private bool _showNotes;
    private DateTime _slideStarted;
    private int _slideClockMinute = -1;

    internal bool InSlideMode => _slideMode;
    internal int SlideIndex => _slideIndex;
    internal int SlideStep => _slideStep;
    internal int SlideCount => _deck?.Count ?? 0;
    internal bool NotesShown => _showNotes;

    /// <summary>
    /// Enters presentation mode on <paramref name="startIndex"/>, else on the slide containing source
    /// line <paramref name="startSourceLine"/> (<c>+N</c>), else on the slide under the viewport.
    /// </summary>
    private void EnterSlides(int? startIndex = null, int? startSourceLine = null)
    {
        var deck = SlideDeck.Split(_currentMarkdown);
        int index;
        if (startIndex is { } s) index = Math.Clamp(s, 0, deck.Count - 1);
        else if (startSourceLine is { } line) index = deck.IndexForSourceLine(line);
        else
        {
            // Continue from where the reader is: the slide holding the top visible source line.
            int top = 0;
            lock (_stateLock)
            {
                for (int i = Math.Clamp(_scroll, 0, Math.Max(0, _lines.Count - 1)); i < _lines.Count; i++)
                    if (_lines[i].SourceLine > 0) { top = _lines[i].SourceLine; break; }
            }
            index = deck.IndexForSourceLine(top);
        }
        lock (_stateLock)
        {
            _deck = deck;
            _slideMode = true;
            _slideStarted = DateTime.UtcNow;
            _slideClockMinute = -1;
            _searchQuery = ""; _searchHits.Clear(); _searchHitIndex = -1;
        }
        ShowSlide(index, 0);
        SetStatus(deck.Count == 1 ? "Slides: one slide (separate slides with --- or <!-- slide -->)  Ctrl+S exits" : $"Slides: {deck.Count}  n/p·Space move, N notes, Ctrl+S exits", 3);
    }

    /// <summary>Leaves presentation mode and returns to the document at the current slide's position.</summary>
    private void ExitSlides()
    {
        int line = _deck is { Count: > 0 } d ? d[Math.Clamp(_slideIndex, 0, d.Count - 1)].SourceLine : 1;
        lock (_stateLock)
        {
            _slideMode = false;
            _showNotes = false;
            Relayout();
            _scroll = 0;
        }
        GoToSourceLine(line);
        SetStatus("Slides off", 1.5);
    }

    private void ToggleSlides()
    {
        if (_slideMode) ExitSlides(); else EnterSlides();
    }

    /// <summary>The Markdown the layout is built from: the current slide fragment in slide mode, else the document.</summary>
    private string LayoutSource(string documentMarkdown)
    {
        if (!_slideMode || _deck is null || _deck.Count == 0) return documentMarkdown;
        var slide = _deck[Math.Clamp(_slideIndex, 0, _deck.Count - 1)];
        return slide.MarkdownUpTo(_slideStep);
    }

    private SlideDeck.Slide? CurrentSlide => _deck is { Count: > 0 } d ? d[Math.Clamp(_slideIndex, 0, d.Count - 1)] : null;

    private void ShowSlide(int index, int step)
    {
        if (_deck is null || _deck.Count == 0) return;
        index = Math.Clamp(index, 0, _deck.Count - 1);
        step = Math.Clamp(step, 0, _deck[index].Fragments.Count - 1);
        ParsedDoc parsed;
        lock (_stateLock)
        {
            _slideIndex = index;
            _slideStep = step;
            parsed = ParseToLines(_currentPath, LayoutSource(_currentMarkdown));
            ApplyParsed(parsed, keepScrollAnchor: false);
            _scroll = 0;
            _diagramRequested.Clear();
            _forceHardClear = true;   // Sixel/kitty graphics of the previous slide must be wiped
            _dirty = true;
        }
        KickoffDiagramRenders();
        KickoffHighlights(parsed.PendingHighlights);
    }

    /// <summary>Next reveal step, else next slide.</summary>
    private void SlideAdvance(int count = 1)
    {
        if (_deck is null) return;
        for (int n = 0; n < Math.Max(1, count); n++)
        {
            var slide = _deck[_slideIndex];
            if (_slideStep < slide.Fragments.Count - 1) { ShowSlide(_slideIndex, _slideStep + 1); continue; }
            if (_slideIndex >= _deck.Count - 1) { SetStatus("End of slides", 1.2); return; }
            ShowSlide(_slideIndex + 1, 0);
        }
    }

    /// <summary>Previous reveal step, else previous slide (fully revealed).</summary>
    private void SlideBack(int count = 1)
    {
        if (_deck is null) return;
        for (int n = 0; n < Math.Max(1, count); n++)
        {
            if (_slideStep > 0) { ShowSlide(_slideIndex, _slideStep - 1); continue; }
            if (_slideIndex == 0) { SetStatus("First slide", 1.2); return; }
            ShowSlide(_slideIndex - 1, _deck[_slideIndex - 1].Fragments.Count - 1);
        }
    }

    private void GoToSlide(int number)
    {
        if (_deck is null) return;
        if (number < 1 || number > _deck.Count) { SetStatus($"Slides are 1–{_deck.Count}", 1.5); return; }
        ShowSlide(number - 1, 0);
    }

    /// <summary>
    /// Keys that only mean something in a presentation (n/p/h/l, N for notes, Esc closes the notes).
    /// Everything else goes through the key map; <see cref="Execute"/> re-targets paging/history
    /// actions to slides. Returns true when consumed.
    /// </summary>
    private bool HandleSlideKey(KeyEvent key)
    {
        if (!_slideMode) return false;
        if (key.Kind == KeyKind.Escape && _showNotes) { _showNotes = false; _dirty = true; return true; }
        if (key.Kind != KeyKind.Char || key.Ctrl || key.Alt || _pendingChord.Count > 0) return false;
        switch (key.Char)
        {
            case 'n' or 'l': { int c = Math.Max(1, _count); _count = 0; SlideAdvance(c); return true; }
            case 'p' or 'h': { int c = Math.Max(1, _count); _count = 0; SlideBack(c); return true; }
            case 'N': _showNotes = !_showNotes; _dirty = true; return true;
            default: return false;
        }
    }

    /// <summary>Re-targets document actions to slide navigation while presenting. Returns true when handled.</summary>
    private bool ExecuteSlideAction(EditorAction action, int count)
    {
        if (!_slideMode) return false;
        switch (action)
        {
            case EditorAction.PageDown or EditorAction.Forward: SlideAdvance(Math.Max(1, count)); return true;
            case EditorAction.PageUp or EditorAction.Back: SlideBack(Math.Max(1, count)); return true;
            case EditorAction.GoTop: if (count > 0) GoToSlide(count); else ShowSlide(0, 0); return true;
            case EditorAction.GoBottom: if (count > 0) GoToSlide(count); else ShowSlide((_deck?.Count ?? 1) - 1, 0); return true;
            case EditorAction.FollowLink when _activeLinkOrdinal < 0: SlideAdvance(); return true;
            case EditorAction.SearchNext when _searchHits.Count == 0: SlideAdvance(Math.Max(1, count)); return true;
            case EditorAction.SearchPrev when _searchHits.Count == 0: _showNotes = !_showNotes; _dirty = true; return true;
            default: return false;
        }
    }

    /// <summary>Called from the render loop tick: redraws when the elapsed-time clock changes.</summary>
    private void TickSlides()
    {
        if (!_slideMode) return;
        int minute = (int)(DateTime.UtcNow - _slideStarted).TotalMinutes;
        if (minute != _slideClockMinute) { _slideClockMinute = minute; _dirty = true; }
    }

    /// <summary>Status-bar text while presenting: "Slide 3/12 · 1/2 · Title · 04:12 · notes".</summary>
    private string SlideStatus()
    {
        var slide = CurrentSlide;
        if (_deck is null || slide is null) return " slides";
        var sb = new System.Text.StringBuilder();
        sb.Append($" Slide {_slideIndex + 1}/{_deck.Count}");
        if (slide.Fragments.Count > 1) sb.Append($"  step {_slideStep + 1}/{slide.Fragments.Count}");
        sb.Append(Glyphs.Ascii ? "  |  " : "  ·  ").Append(Truncate(slide.Title, Math.Max(10, _screen.Width / 3)));
        var elapsed = DateTime.UtcNow - _slideStarted;
        sb.Append(Glyphs.Ascii ? "  |  " : "  ·  ").Append(elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss"));
        if (slide.Notes is not null) sb.Append(_showNotes ? "  [notes]" : "  (N: notes)");
        if (_count > 0) sb.Append($"  {_count}");
        return sb.ToString();
    }

    private string SlideHelpHint(int width) =>
        width >= 100
            ? $"n/p·Space·←→ next/prev  N:notes  5gg:slide 5  {_keyMap.Label(EditorAction.Slides, "Ctrl-s")}:exit slides  {_keyMap.Label(EditorAction.Quit, "q")}:quit "
            : width >= 70 ? $"n/p next/prev  N:notes  {_keyMap.Label(EditorAction.Slides, "Ctrl-s")}:exit  {_keyMap.Label(EditorAction.Quit, "q")}:quit "
            : "n/p  N:notes  q:quit ";

    /// <summary>Speaker notes card along the bottom of the screen; stays open across slides.</summary>
    private void DrawNotesOverlay()
    {
        var slide = CurrentSlide;
        var p = Palette();
        int cardW = Math.Min(_screen.Width - 4, Math.Max(40, _screen.Width * 3 / 4));
        var text = slide?.Notes ?? "(no notes for this slide — add <!-- notes … --> or a ??? line)";
        var wrapped = WrapNotes(text, cardW - 4);
        int bodyRows = Math.Clamp(wrapped.Count, 1, Math.Max(1, _screen.Height / 2 - 4));
        int cardH = bodyRows + 4;
        int boxTop = Math.Max(0, _screen.Height - 1 - cardH - 1);
        int boxLeft = Math.Max(0, (_screen.Width - cardW) / 2);
        var (cTop, cLeft, cW, cH) = DrawCard(p, boxTop, boxLeft, cardW, cardH);
        DrawHeaderFooter(p, cTop, cLeft, cW, cH, $"Notes  {Glyphs.Crumb}  slide {_slideIndex + 1}/{_deck?.Count ?? 0}", "N / esc close   n / p move on");
        for (int i = 0; i < bodyRows; i++)
        {
            string line = i < wrapped.Count ? wrapped[i] : "";
            if (i == bodyRows - 1 && wrapped.Count > bodyRows) line = TextWidth.TrimToWidth(line, cW - 5) + Glyphs.Ellipsis;
            _screen.MoveTo(cTop + 2 + i, cLeft).SetBackground(p.Panel).SetForeground(p.Fg).Write("  " + TextWidth.TrimToWidth(line, cW - 2).PadRight(cW - 2));
        }
        _screen.Reset();
    }

    private static List<string> WrapNotes(string text, int width)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Trim().Length == 0) { result.Add(""); continue; }
            var line = new System.Text.StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && TextWidth.Of(line.ToString()) + 1 + TextWidth.Of(word) > width) { result.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) result.Add(line.ToString());
        }
        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return result;
    }
}
