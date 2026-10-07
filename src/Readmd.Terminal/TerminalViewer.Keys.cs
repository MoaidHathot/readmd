namespace Readmd.Terminal;

// Key dispatch: converts input events to chord tokens, resolves multi-key sequences (gg, ]], za)
// with a timeout, accumulates vim-style count prefixes (5j), and routes to the right handler for
// the current mode (search prompt, TOC overlay, focus view, link hints, command line, …).
public sealed partial class TerminalViewer
{
    private readonly List<string> _pendingChord = [];
    private DateTime _pendingChordUntil;
    private int _count;                 // accumulated count prefix (0 = none)
    private static readonly TimeSpan ChordTimeout = TimeSpan.FromMilliseconds(900);

    private void HandleKeyEvent(KeyEvent key)
    {
        // Focus/paste events outside a text prompt carry no command meaning.
        if (key.Kind is KeyKind.FocusIn or KeyKind.FocusOut) return;
        if (key.Kind == KeyKind.Paste && !_searchMode && !_commandMode) return;

        if (_searchMode) { HandleSearchKey(key); return; }
        if (_commandMode) { HandleCommandKey(key); return; }
        if (_hintMode) { HandleHintKey(key); return; }
        if (_tocMode) { HandleTocKey(key); return; }
        if (_focusMode) { HandleFocusKey(key); return; }
        if (HandleMarkKey(key)) return;
        if (_pendingExternalUrl is not null)
        {
            // Awaiting confirmation to open a remote link. Only respond to keyboard keys — mouse
            // events (notably the button-release that follows a click that opened this prompt, plus
            // moves/scrolls) must not count as an answer, or clicking a link could never be
            // confirmed because its own release would immediately cancel the prompt.
            if (IsMouse(key)) return;

            char c = key.Kind == KeyKind.Char ? char.ToLowerInvariant(key.Char) : '\0';
            var url = _pendingExternalUrl;
            _pendingExternalUrl = null;
            _dirty = true;
            if (c == 'y')
            {
                OpenUrl(url!);
                SetStatus("Opened: " + url);
            }
            else
            {
                SetStatus("Cancelled");
            }
            return;
        }
        if (_helpMode)
        {
            // Any key dismisses the help overlay.
            _helpMode = false;
            _dirty = true;
            return;
        }

        // Mouse first (never part of a chord).
        switch (key.Kind)
        {
            case KeyKind.MouseScrollUp when key.Ctrl: ZoomDiagrams(1); return;
            case KeyKind.MouseScrollDown when key.Ctrl: ZoomDiagrams(-1); return;
            case KeyKind.MouseScrollUp: ScrollBy(-_wheelLines); return;
            case KeyKind.MouseScrollDown: ScrollBy(_wheelLines); return;
            case KeyKind.MouseClick when _selectionMode: SelectionBegin(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseClick: HandleClick(key.MouseRow, key.MouseCol, ctrl: key.Ctrl); return;
            case KeyKind.MouseDrag when _selectionMode: SelectionExtend(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseDragEnd when _selectionMode: SelectionExtend(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseRightClick when _selectionMode: CopySelection(); return;
            case KeyKind.MouseDrag or KeyKind.MouseDragEnd or KeyKind.MouseRightClick or KeyKind.MouseMiddleClick: return;
        }

        var token = KeyMap.TokenFor(key);
        if (token is null) return;

        // Count prefix: digits accumulate (vim "5j"); a leading 0 is just 0.
        bool chordPending = _pendingChord.Count > 0 && DateTime.UtcNow <= _pendingChordUntil;
        if (!chordPending && key.Kind == KeyKind.Char && key.Plain && char.IsAsciiDigit(key.Char) && (_count > 0 || key.Char != '0'))
        {
            _count = Math.Min(99_999, _count * 10 + (key.Char - '0'));
            SetStatus($"count: {_count}", 2);
            return;
        }

        if (!chordPending) _pendingChord.Clear();
        _pendingChord.Add(token);
        var chord = string.Join(' ', _pendingChord);

        var action = _keyMap.Resolve(chord);
        if (action != EditorAction.None && !_keyMap.IsPrefix(chord))
        {
            _pendingChord.Clear();
            int count = _count; _count = 0;
            Execute(action, count);
            return;
        }
        if (_keyMap.IsPrefix(chord))
        {
            // Wait for the rest of the sequence (gg, ]c, …). If a complete binding also exists for
            // this prefix (rare), the longer sequence wins when it arrives, else it fires on timeout.
            _pendingChordUntil = DateTime.UtcNow + ChordTimeout;
            _pendingPrefixAction = action;
            return;
        }
        if (action != EditorAction.None)
        {
            _pendingChord.Clear();
            int count = _count; _count = 0;
            Execute(action, count);
            return;
        }

        // Not a binding. If we were inside a sequence, retry this key on its own.
        if (_pendingChord.Count > 1)
        {
            _pendingChord.Clear();
            _pendingChord.Add(token);
            var single = _keyMap.Resolve(token);
            if (single != EditorAction.None && !_keyMap.IsPrefix(token))
            {
                _pendingChord.Clear();
                int count = _count; _count = 0;
                Execute(single, count);
                return;
            }
            if (_keyMap.IsPrefix(token)) { _pendingChordUntil = DateTime.UtcNow + ChordTimeout; _pendingPrefixAction = single; return; }
        }
        _pendingChord.Clear();
        _count = 0;
    }

    private EditorAction _pendingPrefixAction;

    /// <summary>Called from the render loop: fires a pending prefix that is also a complete binding once its timeout expires.</summary>
    private void TickPendingChord()
    {
        if (_pendingChord.Count == 0 || DateTime.UtcNow <= _pendingChordUntil) return;
        var action = _pendingPrefixAction;
        _pendingChord.Clear();
        _pendingPrefixAction = EditorAction.None;
        if (action != EditorAction.None) { int count = _count; _count = 0; Execute(action, count); }
    }

    private static bool IsMouse(KeyEvent key) => key.Kind is KeyKind.MouseClick or KeyKind.MouseDrag or KeyKind.MouseDragEnd
        or KeyKind.MouseRightClick or KeyKind.MouseMiddleClick or KeyKind.MouseScrollUp or KeyKind.MouseScrollDown;

    private int _wheelLines = 3;

    /// <summary>Executes an action. <paramref name="count"/> is the typed count prefix (0 = none).</summary>
    internal void Execute(EditorAction action, int count = 0)
    {
        int n = Math.Max(1, count);
        switch (action)
        {
            case EditorAction.Quit: _running = false; return;
            case EditorAction.ScrollDown: ScrollBy(n); return;
            case EditorAction.ScrollUp: ScrollBy(-n); return;
            case EditorAction.HalfPageDown: ScrollBy(n * Math.Max(1, ViewportHeight / 2)); return;
            case EditorAction.HalfPageUp: ScrollBy(-n * Math.Max(1, ViewportHeight / 2)); return;
            case EditorAction.PageDown: ScrollBy(n * Math.Max(1, ViewportHeight - 2)); return;
            case EditorAction.PageUp: ScrollBy(-n * Math.Max(1, ViewportHeight - 2)); return;
            case EditorAction.GoTop: ScrollTo(count > 0 ? count - 1 : 0); return;   // "12gg" → line 12
            case EditorAction.GoBottom: ScrollTo(count > 0 ? count - 1 : _lines.Count); return;
            case EditorAction.Search: EnterSearch(); return;
            case EditorAction.SearchNext: JumpSearch(n); return;
            case EditorAction.SearchPrev: JumpSearch(-n); return;
            case EditorAction.ClearSearch: ClearSearchAndSelection(); return;
            case EditorAction.Toc: OpenToc(); return;
            case EditorAction.TocSidebar: ToggleTocSidebar(); return;
            case EditorAction.Back: GoBack(); return;
            case EditorAction.Forward: GoForward(); return;
            case EditorAction.FollowLink: FollowFirstVisibleLink(); return;
            case EditorAction.LinkHints: EnterHintMode(copyOnly: false); return;
            case EditorAction.CopyLinkUrl: EnterHintMode(copyOnly: true); return;
            case EditorAction.NextLink: CycleLink(n); return;
            case EditorAction.PrevLink: CycleLink(-n); return;
            case EditorAction.OpenInBrowser: OpenInBrowser(); return;
            case EditorAction.ViewImage: OpenFocusView(); return;
            case EditorAction.ToggleSelectionMode: ToggleSelectionMode(); return;
            case EditorAction.Rerender: Rerender(); return;
            case EditorAction.ToggleTheme: ToggleTheme(); return;
            case EditorAction.ToggleBackground: ToggleSolidBackground(); return;
            case EditorAction.Help: _helpMode = true; _dirty = true; return;
            case EditorAction.NextHeading: MoveToBlock(+n, l => l.HeadingLevel > 0); return;
            case EditorAction.PrevHeading: MoveToBlock(-n, l => l.HeadingLevel > 0); return;
            case EditorAction.NextBlock: MoveToBlock(+n, _ => true); return;
            case EditorAction.PrevBlock: MoveToBlock(-n, _ => true); return;
            case EditorAction.NextCode: MoveToBlock(+n, l => l.Kind == BlockKind.Code); return;
            case EditorAction.PrevCode: MoveToBlock(-n, l => l.Kind == BlockKind.Code); return;
            case EditorAction.NextImage: MoveToBlock(+n, l => l.Kind is BlockKind.Image or BlockKind.Diagram); return;
            case EditorAction.PrevImage: MoveToBlock(-n, l => l.Kind is BlockKind.Image or BlockKind.Diagram); return;
            case EditorAction.Yank: YankActiveBlock(); return;
            case EditorAction.YankAll: YankDocument(); return;
            case EditorAction.FoldToggle: FoldActive(FoldOp.Toggle); return;
            case EditorAction.FoldClose: FoldActive(FoldOp.Close); return;
            case EditorAction.FoldOpen: FoldActive(FoldOp.Open); return;
            case EditorAction.FoldAll: FoldAll(true); return;
            case EditorAction.UnfoldAll: FoldAll(false); return;
            case EditorAction.ToggleDiagramSource: ToggleDiagramSource(); return;
            case EditorAction.Command: EnterCommandMode(); return;
            case EditorAction.Export: ExportPrompt(); return;
            case EditorAction.ZoomIn: ZoomDiagrams(n); return;
            case EditorAction.ZoomOut: ZoomDiagrams(-n); return;
            case EditorAction.ZoomReset: ResetZoom(); return;
            case EditorAction.QuickOpen: OpenQuickOpen(); return;
            case EditorAction.ProjectSearch: OpenProjectSearch(); return;
            case EditorAction.NextFile: StepFile(+n); return;
            case EditorAction.PrevFile: StepFile(-n); return;
            case EditorAction.Bookmark: SetBookmarkPrompt(); return;
            case EditorAction.GoBookmark: GoBookmarkPrompt(); return;
            case EditorAction.ToggleWatch: ToggleWatch(); return;
            case EditorAction.Follow: ToggleFollow(); return;
            case EditorAction.Slides: SetStatus("Presentation mode: run with --slides"); return;
        }
    }

    private void ClearSearchAndSelection()
    {
        bool any = _searchHits.Count > 0 || _selAnchor is not null || _activeLinkOrdinal >= 0;
        if (_searchHits.Count > 0) { _searchHits.Clear(); _hitLines.Clear(); _searchHitIndex = -1; _searchQuery = ""; }
        ClearSelection();
        _activeLinkOrdinal = -1;
        if (any) _dirty = true;
    }
}
