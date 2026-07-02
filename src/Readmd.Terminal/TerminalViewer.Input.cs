using System.Diagnostics;
using Readmd.Core;

namespace Readmd.Terminal;

public sealed partial class TerminalViewer
{
    // ---------------- diagram rendering ----------------
    private void KickoffDiagramRenders(bool forceDiagramRerender = false)
    {
        // Pre-seed any cached results synchronously (skip when forcing a theme re-render).
        if (!forceDiagramRerender)
        {
            foreach (var (key, request) in _pendingDiagrams)
            {
                var cached = _diagrams.TryGet(key);
                if (cached is { Status: DiagramStatus.Ready })
                {
                    lock (_stateLock) { _diagramResults[key] = cached; ReserveDiagramRows(key, cached); }
                }
            }
        }

        foreach (var (key, request) in _pendingDiagrams)
        {
            if (_diagramRequested.Contains(key)) continue;
            if (!forceDiagramRerender && _diagramResults.TryGetValue(key, out var existing) && existing.Status == DiagramStatus.Ready) continue;
            _diagramRequested.Add(key);
            var req = request;
            _ = Task.Run(async () =>
            {
                var result = await _diagrams.RenderAsync(req, _diagramTheme, _lifetimeCts.Token);
                lock (_stateLock)
                {
                    // Only replace if we got a good render (keeps the old image visible on failure).
                    if (result.Status == DiagramStatus.Ready)
                    {
                        _diagramResults[key] = result;
                        ReserveDiagramRows(key, result);
                    }
                    else if (!_diagramResults.ContainsKey(key) || _diagramResults[key].Status != DiagramStatus.Ready)
                    {
                        // No good image to fall back to: show the error inline so the user knows why
                        // the diagram is blank (e.g. d2 missing, Chromium download failed).
                        _diagramResults[key] = result;
                        ShowDiagramError(key, result.Error);
                    }
                    ClearDiagramCaches();
                    _forceHardClear = true;
                    _dirty = true;
                }
            });
        }

        KickoffImageLoads();
    }

    private void KickoffImageLoads()
    {
        // Pre-seed cached images synchronously.
        foreach (var (key, _) in _pendingImages)
        {
            var cached = _imageLoader.TryGet(key);
            if (cached is { Status: DiagramStatus.Ready })
                lock (_stateLock) { _diagramResults[key] = cached; ReserveDiagramRows(key, cached); }
        }

        foreach (var (key, url) in _pendingImages)
        {
            if (_diagramRequested.Contains(key)) continue;
            if (_diagramResults.TryGetValue(key, out var existing) && existing.Status == DiagramStatus.Ready) continue;
            _diagramRequested.Add(key);
            var imgUrl = url;
            var docPath = _currentPath;
            _ = Task.Run(async () =>
            {
                var result = await _imageLoader.LoadAsync(imgUrl, docPath);
                lock (_stateLock)
                {
                    _diagramResults[key] = result;
                    if (result.Status == DiagramStatus.Ready) ReserveDiagramRows(key, result);
                    ClearDiagramCaches();
                    _dirty = true;
                }
            });
        }

        // Image groups (badges side-by-side): compose into one strip per group.
        foreach (var (groupKey, urls) in _pendingImageGroups)
        {
            if (_diagramRequested.Contains(groupKey)) continue;
            if (_diagramResults.TryGetValue(groupKey, out var existing) && existing.Status == DiagramStatus.Ready) continue;
            _diagramRequested.Add(groupKey);
            var groupUrls = urls;
            var docPath = _currentPath;
            _ = Task.Run(async () =>
            {
                var result = await _imageLoader.LoadGroupAsync(groupKey, groupUrls, docPath);
                lock (_stateLock)
                {
                    _diagramResults[groupKey] = result;
                    if (result.Status == DiagramStatus.Ready) ReserveDiagramRows(groupKey, result);
                    ClearDiagramCaches();
                    _forceHardClear = true;
                    _dirty = true;
                }
            });
        }
    }

    /// <summary>
    /// Inserts blank reservation lines beneath each anchor for a diagram/image so the text after it
    /// flows below the rendered image instead of being painted over. Handles the same image/diagram
    /// appearing multiple times in the document.
    /// </summary>
    private void ReserveDiagramRows(string key, DiagramResult result, bool adjustScroll = true)
    {
        int needed = DiagramRows(result);
        var list = _lines as List<DisplayLine> ?? _lines.ToList();

        // Find all anchor indices for this key.
        var anchors = new List<int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].DiagramKey == key) anchors.Add(i);
        if (anchors.Count == 0) return;

        // Process bottom-up so earlier insertions don't invalidate later anchor indices.
        for (int a = anchors.Count - 1; a >= 0; a--)
        {
            int anchor = anchors[a];
            int existing = 0;
            for (int i = anchor + 1; i < list.Count && list[i].DiagramKey == "reserve:" + key; i++) existing++;

            int delta = needed - existing;
            if (delta > 0)
            {
                for (int i = 0; i < delta; i++)
                    list.Insert(anchor + 1, new DisplayLine { DiagramKey = "reserve:" + key });
            }
            else if (delta < 0)
            {
                for (int i = 0; i < -delta; i++)
                    list.RemoveAt(anchor + 1);
            }

            // Keep the viewport stable for async single-diagram resolution.
            if (adjustScroll && delta != 0 && anchor < _scroll)
                _scroll = Math.Max(0, _scroll + delta);
        }
        _lines = list;
    }

    /// <summary>
    /// Renders a failed diagram's error inline: recolours each anchor caption red and inserts the
    /// wrapped error message on the reserved rows beneath it, so the user sees why nothing rendered.
    /// </summary>
    private void ShowDiagramError(string key, string? error)
    {
        var list = _lines as List<DisplayLine> ?? _lines.ToList();
        var msg = string.IsNullOrWhiteSpace(error) ? "diagram could not be rendered" : error!.Trim();

        int width = Math.Max(20, _screen.Width - 6);
        var wrapped = WrapPlain(msg, width);
        var errColor = _theme.IsDark ? Rgb.FromHex("#f08c8c") : Rgb.FromHex("#cf222e");
        string reserveTag = "reserve:" + key;

        var anchors = new List<int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].DiagramKey == key) anchors.Add(i);

        for (int a = anchors.Count - 1; a >= 0; a--)
        {
            int anchor = anchors[a];
            // Recolour the caption to signal failure.
            var cap = list[anchor];
            cap.Spans.Clear();
            cap.Spans.Add(new StyledSpan("✖ ", errColor, CellStyle.Bold));
            cap.Spans.Add(new StyledSpan("diagram error", errColor, CellStyle.Bold));

            // Remove existing reserved rows for this anchor, then insert the error lines.
            int existing = 0;
            while (anchor + 1 + existing < list.Count && list[anchor + 1 + existing].DiagramKey == reserveTag) existing++;
            for (int i = 0; i < existing; i++) list.RemoveAt(anchor + 1);

            for (int i = wrapped.Count - 1; i >= 0; i--)
            {
                var line = new DisplayLine { DiagramKey = reserveTag };
                line.Spans.Add(new StyledSpan("  " + wrapped[i], _theme.Muted));
                list.Insert(anchor + 1, line);
            }
            // Keep the viewport stable if rows were added above the current top.
            int delta = wrapped.Count - existing;
            if (delta != 0 && anchor < _scroll) _scroll = Math.Max(0, _scroll + delta);
        }
        _lines = list;
    }

    /// <summary>Greedy word-wrap of a plain string to the given width.</summary>
    private static List<string> WrapPlain(string text, int width)
    {
        var lines = new List<string>();
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            var words = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { lines.Add(""); continue; }
            var sb = new System.Text.StringBuilder();
            foreach (var w in words)
            {
                if (sb.Length == 0) sb.Append(w);
                else if (sb.Length + 1 + w.Length <= width) { sb.Append(' ').Append(w); }
                else { lines.Add(sb.ToString()); sb.Clear(); sb.Append(w); }
                while (sb.Length > width) { lines.Add(sb.ToString(0, width)); sb.Remove(0, width); }
            }
            if (sb.Length > 0) lines.Add(sb.ToString());
        }
        return lines;
    }

    // ---------------- input ----------------
    private void HandleKeyEvent(KeyEvent key)
    {
        if (_searchMode) { HandleSearchKey(key); return; }
        if (_tocMode) { HandleTocKey(key); return; }
        if (_focusMode) { HandleFocusKey(key); return; }
        if (_pendingExternalUrl is not null)
        {
            // Awaiting confirmation to open a remote link. Only respond to keyboard keys — mouse
            // events (notably the button-release that follows a click that opened this prompt, plus
            // moves/scrolls) must not count as an answer, or clicking a link could never be
            // confirmed because its own release would immediately cancel the prompt.
            if (key.Kind is KeyKind.MouseClick or KeyKind.MouseDrag or KeyKind.MouseDragEnd
                or KeyKind.MouseRightClick or KeyKind.MouseScrollUp or KeyKind.MouseScrollDown)
                return;

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

        bool ctrl = key.Ctrl;
        char ch = key.Kind == KeyKind.Char ? char.ToLowerInvariant(key.Char) : '\0';
        bool shift = key.Kind == KeyKind.Char && char.IsUpper(key.Char);

        // Handle a pending 'g' prefix (vim 'gg' = top). Any non-'g' key cancels the prefix.
        bool gPending = _pendingGPrefix && DateTime.UtcNow <= _pendingGUntil;
        _pendingGPrefix = false;
        if (gPending && ch == 'g' && !ctrl && !shift)
        {
            ScrollTo(0); // gg -> top
            return;
        }

        // Non-char keys first (arrows, page, home/end, enter, backspace, escape, mouse wheel).
        switch (key.Kind)
        {
            case KeyKind.MouseScrollUp when key.Ctrl: ZoomDiagrams(1); return;
            case KeyKind.MouseScrollDown when key.Ctrl: ZoomDiagrams(-1); return;
            case KeyKind.MouseScrollUp: ScrollBy(-1); return;
            case KeyKind.MouseScrollDown: ScrollBy(1); return;
            case KeyKind.MouseClick when _selectionMode: SelectionBegin(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseClick: HandleClick(key.MouseRow, key.MouseCol, ctrl: key.Ctrl); return;
            case KeyKind.MouseDrag when _selectionMode: SelectionExtend(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseDragEnd when _selectionMode: SelectionExtend(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseRightClick when _selectionMode: CopySelection(); return;
            case KeyKind.MouseDrag: return;
            case KeyKind.MouseDragEnd: return;
            case KeyKind.MouseRightClick: return;
            case KeyKind.Down: ScrollBy(1); return;
            case KeyKind.Up: ScrollBy(-1); return;
            case KeyKind.PageDown: ScrollBy(ViewportHeight - 2); return;
            case KeyKind.PageUp: ScrollBy(-(ViewportHeight - 2)); return;
            case KeyKind.Home: ScrollTo(0); return;
            case KeyKind.End: ScrollTo(_lines.Count); return;
            case KeyKind.Left: GoBack(); return;
            case KeyKind.Right: GoForward(); return;
            case KeyKind.Backspace: GoBack(); return;
            case KeyKind.Enter: FollowFirstVisibleLink(); return;
            case KeyKind.Escape:
                ClearSelection();   // dismiss an active mark-mode selection
                return;
            case KeyKind.Tab: return;
        }

        // Ctrl+letter / Ctrl+digit combinations.
        if (ctrl)
        {
            switch (ch)
            {
                case 'e': ScrollBy(1); return;                       // vim Ctrl+e
                case 'y': ScrollBy(-1); return;                      // vim Ctrl+y
                case 'd': ScrollBy(ViewportHeight / 2); return;      // half page down
                case 'u': ScrollBy(-ViewportHeight / 2); return;     // half page up
                case 'f': ScrollBy(ViewportHeight - 2); return;      // full page down
                case 'b': ScrollBy(-(ViewportHeight - 2)); return;   // full page up
                case '0': ResetZoom(); return;                       // Ctrl+0 -> reset diagram zoom
            }
            // Some terminals send Ctrl+0 as the raw char '0' too.
            if (key.Char == '0') { ResetZoom(); return; }
            return;
        }

        // Plain character keys. The 'g' prefix and digit link-following keep fixed bindings; the
        // common actions are resolved through the (optionally user-configured) key map.
        switch (ch)
        {
            case 'g' when shift: ScrollTo(_lines.Count); return; // G -> bottom
            case 'g': // g -> arm 'gg' prefix
                _pendingGPrefix = true;
                _pendingGUntil = DateTime.UtcNow.AddMilliseconds(700);
                return;
            case 'n' when shift: JumpSearch(-1); return; // N -> previous match (Shift+n)
            case >= '1' and <= '9': FollowLinkByOrdinal(ch - '0'); return;
        }

        if (RunMappedAction(_keyMap.Resolve(ch))) return;
    }

    // Dispatches a remappable editor action. Returns true if the action was handled.
    private bool RunMappedAction(EditorAction action)
    {
        switch (action)
        {
            case EditorAction.Quit: _running = false; return true;
            case EditorAction.ScrollDown: ScrollBy(1); return true;
            case EditorAction.ScrollUp: ScrollBy(-1); return true;
            case EditorAction.PageDown: ScrollBy(ViewportHeight - 2); return true;
            case EditorAction.PageUp: ScrollBy(-(ViewportHeight - 2)); return true;
            case EditorAction.GoBottom: ScrollTo(_lines.Count); return true;
            case EditorAction.Search: EnterSearch(); return true;
            case EditorAction.SearchNext: JumpSearch(1); return true;
            case EditorAction.SearchPrev: JumpSearch(-1); return true;
            case EditorAction.Toc: OpenToc(); return true;
            case EditorAction.OpenInBrowser: OpenInBrowser(); return true;
            case EditorAction.ViewImage: OpenFocusView(); return true;
            case EditorAction.ToggleSelectionMode: ToggleSelectionMode(); return true;
            case EditorAction.Rerender: Rerender(); return true;
            case EditorAction.ToggleTheme: ToggleTheme(); return true;
            case EditorAction.ToggleBackground: ToggleSolidBackground(); return true;
            case EditorAction.Help: _helpMode = true; _dirty = true; return true;
            default: return false;
        }
    }

    // ---------------- scrolling ----------------
    private void ScrollBy(int delta) => ScrollTo(_scroll + delta);

    private void ScrollTo(int target)
    {
        int maxScroll = Math.Max(0, _lines.Count - ViewportHeight);
        int newScroll = Math.Clamp(target, 0, maxScroll);
        if (newScroll != _scroll)
        {
            _scroll = newScroll;
            _sixelCacheNeedsRedraw();
            _dirty = true;
        }
    }

    private void _sixelCacheNeedsRedraw() { /* images redrawn each frame from cache; scrolling is fine */ }

    // ---------------- search ----------------
    private void EnterSearch()
    {
        _searchMode = true;
        _searchQuery = "";
        _searchHits.Clear();
        _searchHitIndex = -1;
        _dirty = true;
    }

    private void HandleSearchKey(KeyEvent key)
    {
        // Be defensive: accept both the decoded Kind and the raw control char, since some
        // terminals/console modes deliver Esc/Enter/Backspace as characters (27/13/8) instead of
        // dedicated key codes.
        bool isEscape = key.Kind == KeyKind.Escape || (key.Kind == KeyKind.Char && key.Char == (char)27);
        bool isEnter = key.Kind == KeyKind.Enter || (key.Kind == KeyKind.Char && (key.Char == '\r' || key.Char == '\n'));
        bool isBackspace = key.Kind == KeyKind.Backspace
            || (key.Kind == KeyKind.Char && (key.Char == (char)8 || key.Char == (char)127))
            || (key.Kind == KeyKind.Char && key.Ctrl && char.ToLowerInvariant(key.Char) == 'h');

        if (isEscape)
        {
            _searchMode = false; _searchHits.Clear(); _searchHitIndex = -1; _dirty = true;
            return;
        }
        if (isEnter)
        {
            _searchMode = false;
            if (_searchHits.Count > 0) ScrollToHit(_searchHitIndex);
            _dirty = true;
            return;
        }
        if (isBackspace)
        {
            if (_searchQuery.Length > 0) _searchQuery = _searchQuery[..^1];
            RecomputeSearch();
            return;
        }
        // Printable character (ignore control chars and Ctrl combos).
        if (key.Kind == KeyKind.Char && !key.Ctrl && !char.IsControl(key.Char))
        {
            _searchQuery += key.Char;
            RecomputeSearch();
        }
    }

    private void RecomputeSearch()
    {
        _searchHits.Clear();
        _searchHitIndex = -1;
        if (_searchQuery.Length >= 1)
        {
            var q = _searchQuery.ToLowerInvariant();
            for (int i = 0; i < _lines.Count; i++)
            {
                var text = _lines[i].PlainText;
                var lower = text.ToLowerInvariant();
                int idx = 0;
                while ((idx = lower.IndexOf(q, idx, StringComparison.Ordinal)) >= 0)
                {
                    _searchHits.Add((i, idx, _searchQuery.Length));
                    idx += q.Length;
                }
            }
            if (_searchHits.Count > 0)
            {
                // pick the first hit at or after current scroll
                _searchHitIndex = _searchHits.FindIndex(h => h.Line >= _scroll);
                if (_searchHitIndex < 0) _searchHitIndex = 0;
                ScrollToHit(_searchHitIndex);
            }
        }
        _dirty = true;
    }

    private void JumpSearch(int dir)
    {
        if (_searchHits.Count == 0) return;
        _searchHitIndex = (_searchHitIndex + dir + _searchHits.Count) % _searchHits.Count;
        ScrollToHit(_searchHitIndex);
        _dirty = true;
    }

    private void ScrollToHit(int hitIndex)
    {
        if (hitIndex < 0 || hitIndex >= _searchHits.Count) return;
        int line = _searchHits[hitIndex].Line;
        if (line < _scroll || line >= _scroll + ViewportHeight)
            ScrollTo(line - ViewportHeight / 2);
    }

    // ---------------- TOC ----------------
    private void OpenToc()
    {
        if (_toc.Count == 0) { SetStatus("No headings"); return; }
        _tocMode = true;
        // start near the heading closest to current scroll
        _tocIndex = 0;
        for (int i = 0; i < _toc.Count; i++)
            if (FindHeadingLine(_toc[i].Id) <= _scroll) _tocIndex = i;
        _dirty = true;
    }

    private void HandleTocKey(KeyEvent key)
    {
        char ch = key.Kind == KeyKind.Char ? char.ToLowerInvariant(key.Char) : '\0';
        if (key.Kind == KeyKind.Escape || ch is 't' or 'q')
        {
            _tocMode = false; _dirty = true; return;
        }
        if (key.Kind == KeyKind.Down || ch == 'j')
        {
            _tocIndex = Math.Min(_tocIndex + 1, _toc.Count - 1); _dirty = true; return;
        }
        if (key.Kind == KeyKind.Up || ch == 'k')
        {
            _tocIndex = Math.Max(_tocIndex - 1, 0); _dirty = true; return;
        }
        if (key.Kind == KeyKind.Enter)
        {
            _tocMode = false;
            var id = _toc[_tocIndex].Id;
            int line = FindHeadingLine(id);
            if (line >= 0) ScrollTo(line);
            _dirty = true;
        }
    }

    private int FindHeadingLine(string id)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].HeadingId == id) return i;
        return -1;
    }

    // ---------------- links & navigation ----------------
    private void FollowFirstVisibleLink()
    {
        // Find the first link occurring on a visible line.
        for (int row = 0; row < ViewportHeight; row++)
        {
            int lineIndex = _scroll + row;
            if (lineIndex >= _lines.Count) break;
            foreach (var span in _lines[lineIndex].Spans)
            {
                if (span.LinkId is { } id && id < _links.Count)
                {
                    FollowLink(_links[id]);
                    return;
                }
            }
        }
        SetStatus("No link on screen (use number keys for links)");
    }

    private void FollowLinkByOrdinal(int ordinal)
    {
        // The superscript markers show each link's document id (1..9); follow that link directly.
        int id = ordinal - 1;
        if (id >= 0 && id < _links.Count) FollowLink(_links[id]);
        else SetStatus($"No link {ordinal}");
    }

    /// <summary>
    /// Handles a left mouse click at the given screen row/col: if it lands on a link, follow it;
    /// if it lands on a TOC heading in the overlay it's handled there. Coordinates are 0-based.
    /// </summary>
    private void HandleClick(int screenRow, int screenCol, bool ctrl = false)
    {
        if (_helpMode) { _helpMode = false; _dirty = true; return; }
        if (_tocMode) return; // overlay is keyboard-driven
        // Status bar row is the bottom; ignore clicks there.
        if (screenRow >= ViewportHeight) return;

        int lineIndex = _scroll + screenRow;
        if (lineIndex < 0 || lineIndex >= _lines.Count) return;
        var line = _lines[lineIndex];

        // 1) Click directly on a link span (text links and clickable image captions). Columns are
        //    measured in display width (wide/emoji glyphs occupy two cells), matching how the line is
        //    drawn, so hit-testing lines up with what the user sees.
        int col = 0;
        foreach (var span in line.Spans)
        {
            int w = TextWidth.Of(span.Text);
            if (screenCol >= col && screenCol < col + w)
            {
                if (span.LinkId is { } id && id >= 0 && id < _links.Count)
                {
                    FollowLink(_links[id], bypassConfirm: ctrl);
                    return;
                }
                break;
            }
            col += w;
        }

        // 2) Click on an image/diagram's rendered area. A clickable (linked) image follows its
        //    link; any other image/diagram opens the focused full-screen view of that image.
        var anchor = AnchorForRow(lineIndex);
        if (anchor?.ImageLinkId is { } imgLink && imgLink >= 0 && imgLink < _links.Count)
        {
            FollowLink(_links[imgLink], bypassConfirm: ctrl);
        }
        else if (anchor?.DiagramKey is { } dkey && !dkey.StartsWith("reserve:"))
        {
            OpenFocusViewForKey(dkey);
        }
    }

    /// <summary>Finds the image/diagram anchor that owns the given line (anchor itself or its reserved rows).</summary>
    private DisplayLine? AnchorForRow(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Count) return null;
        var line = _lines[lineIndex];
        // The anchor line has a non-"reserve:" DiagramKey; reservation rows are "reserve:<key>".
        if (line.DiagramKey is { } dk)
        {
            if (!dk.StartsWith("reserve:")) return line;
            // Walk up to the anchor with the matching key.
            var key = dk["reserve:".Length..];
            for (int i = lineIndex - 1; i >= 0; i--)
                if (_lines[i].DiagramKey == key) return _lines[i];
        }
        return null;
    }

    private void FollowLink(TerminalLink link, bool bypassConfirm = false)
    {
        var resolved = _resolver.Resolve(link.Url, _currentPath);
        switch (resolved.Kind)
        {
            case LinkKind.External:
                if (bypassConfirm)
                {
                    // Ctrl+click (or another explicit gesture) is a deliberate action, so open the
                    // link straight away without the confirmation prompt.
                    OpenUrl(link.Url);
                    SetStatus("Opened: " + link.Url);
                    _dirty = true;
                    break;
                }
                // Opening an external URL launches the system browser / handler. Confirm first so a
                // crafted link in (possibly untrusted) Markdown can't silently open arbitrary sites.
                _pendingExternalUrl = link.Url;
                SetStatus($"Open external link? {Truncate(link.Url, 60)}   [y / N]   (Ctrl+click to skip)", 30);
                _dirty = true;
                break;
            case LinkKind.LocalFile when resolved.AbsolutePath is not null:
                _ = LoadAsync(resolved.AbsolutePath, pushHistory: true).ContinueWith(_ => { _scroll = 0; });
                break;
            case LinkKind.Anchor when resolved.Anchor is not null:
                int line = FindHeadingLine(resolved.Anchor);
                if (line >= 0) ScrollTo(line); else SetStatus("Anchor not found");
                break;
            default:
                SetStatus("Cannot open: " + link.Url);
                break;
        }
    }

    private void GoBack()
    {
        if (_historyIndex <= 0) { SetStatus("No previous page"); return; }
        _historyIndex--;
        _ = LoadAsync(_history[_historyIndex], pushHistory: false).ContinueWith(_ => { _scroll = 0; });
    }

    private void GoForward()
    {
        if (_historyIndex >= _history.Count - 1) { SetStatus("No next page"); return; }
        _historyIndex++;
        _ = LoadAsync(_history[_historyIndex], pushHistory: false).ContinueWith(_ => { _scroll = 0; });
    }

    // ---------------- theme / appearance ----------------
    private void ToggleTheme()
    {
        lock (_stateLock)
        {
            int savedScroll = _scroll;   // preserve the reading position across the re-render
            _theme = TerminalTheme.For(!_theme.IsDark);
            _diagramTheme = _theme.IsDark ? DiagramTheme.Dark : DiagramTheme.Light;
            // Diagram/image colors are theme-dependent: drop the rendered-bitmap caches so they
            // re-encode for the new theme, but KEEP the source results so images stay on screen
            // (the diagrams will be re-requested for the new theme below).
            ClearDiagramCaches();
            _forceHardClear = true;
            try
            {
                var markdown = File.ReadAllText(_currentPath);
                var parsed = ParseToLines(_currentPath, markdown);
                _lines = parsed.Lines;
                _links = parsed.Links;
                _pendingDiagrams = parsed.Diagrams;
                _pendingImages = parsed.Images;
                _pendingImageGroups = parsed.ImageGroups;
            }
            catch { /* keep old layout */ }

            // Re-reserve rows for the diagrams/images we already have so the layout (and the images
            // themselves) stay intact. Don't let the reservation shift scroll — we restore it below.
            foreach (var (key, result) in _diagramResults)
                if (result.Status == DiagramStatus.Ready)
                    ReserveDiagramRows(key, result, adjustScroll: false);

            // Restore the reading position (the re-parse + re-reserve reproduces the same layout).
            _scroll = Math.Clamp(savedScroll, 0, Math.Max(0, _lines.Count - 1));

            // Diagrams (mermaid/D2) are themed: re-request them in the background but KEEP the old
            // image on screen until the new one arrives, so they don't blink out. Images aren't
            // themed, so they just keep their existing result.
            foreach (var key in _pendingDiagrams.Keys)
                _diagramRequested.Remove(key);
            _dirty = true;
        }
        KickoffDiagramRenders(forceDiagramRerender: true);
        SetStatus(_theme.IsDark ? "Theme: dark" : "Theme: light", 1.5);
    }

    private void ToggleSolidBackground()
    {
        _solidBackground = !_solidBackground;
        // Sixel images flatten transparency onto the theme background, so re-encode them and force
        // a hard clear so the old image pixels are wiped and the new ones draw this frame.
        lock (_stateLock) { ClearDiagramCaches(); _forceHardClear = true; _dirty = true; }
        SetStatus(_solidBackground ? "Background: solid" : "Background: terminal (transparent)", 1.5);
    }

    /// <summary>
    /// Full from-scratch redraw ('r'): wipes the screen (text + Sixel graphics), drops all rendered
    /// caches, re-parses the document, and re-reserves/redraws every diagram and image. Clears any
    /// leftover artifacts (e.g. between composite image groups after a theme change).
    /// </summary>
    private void Rerender()
    {
        int savedScroll = _scroll;
        lock (_stateLock)
        {
            ClearDiagramCaches();
            _forceHardClear = true;
            _refreshWipe = true;   // clean-wipe: clear-only frame, then repaint (clears Sixel artifacts)
            try
            {
                var markdown = File.ReadAllText(_currentPath);
                var parsed = ParseToLines(_currentPath, markdown);
                _lines = parsed.Lines;
                _links = parsed.Links;
                _pendingDiagrams = parsed.Diagrams;
                _pendingImages = parsed.Images;
                _pendingImageGroups = parsed.ImageGroups;
            }
            catch { /* keep old layout */ }

            // Re-reserve rows for everything already rendered (don't shift scroll; we restore it).
            foreach (var (key, result) in _diagramResults)
                if (result.Status == DiagramStatus.Ready)
                    ReserveDiagramRows(key, result, adjustScroll: false);

            _scroll = Math.Clamp(savedScroll, 0, Math.Max(0, _lines.Count - 1));
            _dirty = true;
        }
        // Re-request anything still pending (or that failed before).
        KickoffDiagramRenders();
        SetStatus("Re-rendered", 1.0);
    }

    /// <summary>Zooms diagrams in/out (Ctrl+wheel). Affects only diagram image size, not text.</summary>
    private void ZoomDiagrams(int delta)
    {
        int next = Math.Clamp(_diagramZoom + delta, -3, 6);
        if (next == _diagramZoom) return;
        lock (_stateLock)
        {
            _diagramZoom = next;
            ClearDiagramCaches();
            _forceHardClear = true;
            foreach (var (key, result) in _diagramResults)
                if (result.Status == DiagramStatus.Ready)
                    ReserveDiagramRows(key, result);
            _dirty = true;
        }
        SetStatus($"Diagram zoom: {(_diagramZoom >= 0 ? "+" : "")}{_diagramZoom}", 1.2);
    }

    /// <summary>Resets diagram zoom to its default (Ctrl+0).</summary>
    private void ResetZoom()
    {
        if (_diagramZoom == 0) return;
        lock (_stateLock)
        {
            _diagramZoom = 0;
            ClearDiagramCaches();
            _forceHardClear = true;
            foreach (var (key, result) in _diagramResults)
                if (result.Status == DiagramStatus.Ready)
                    ReserveDiagramRows(key, result);
            _dirty = true;
        }
        SetStatus("Diagram zoom: reset", 1.2);
    }

    /// <summary>
    /// Toggles "select" mode: turns off our mouse-wheel capture so the terminal's native
    /// click-drag text selection works. Toggle back to restore wheel scrolling.
    /// </summary>
    private void ToggleSelectionMode()
    {
        _selectionMode = !_selectionMode;
        // In mark mode we keep capturing the mouse (capture stays ON) so readmd can track the drag
        // selection itself and copy it on right-click. Outside mark mode the wheel scrolls.
        KeyReader.SetMouseCapture(true);
        if (!_selectionMode) ClearSelection();
        SetStatus(_selectionMode
            ? "Select mode ON — drag to select, right-click to copy; press m to exit"
            : "Select mode OFF — mouse wheel scrolls", 4);
        _dirty = true;
    }

    // ---------------- browser handoff ----------------
    private void OpenInBrowser()
    {
        if (_options.OpenInBrowser is null) { SetStatus("Browser mode unavailable"); return; }
        SetStatus("Opening in browser…");
        _ = _options.OpenInBrowser(_currentPath);
    }

    // ---------------- focused image view ----------------
    /// <summary>Opens the focused full-screen view for the image/diagram nearest the top of the viewport.</summary>
    private void OpenFocusView()
    {
        var key = NearestVisibleImageKey();
        if (key is null) { SetStatus("No image or diagram on screen to view"); return; }
        OpenFocusViewForKey(key);
    }

    /// <summary>Enters the focused full-screen view for a specific image/diagram key.</summary>
    private void OpenFocusViewForKey(string key)
    {
        lock (_stateLock)
        {
            if (!_diagramResults.TryGetValue(key, out var r) || r.Status != DiagramStatus.Ready || r.Png is null)
            {
                SetStatus("Image is still rendering…");
                return;
            }
            _focusMode = true;
            _focusKey = key;
            _focusZoom = 0;
            _focusPanRows = 0;
            _focusPanCols = 0;
            _focusDragging = false;
            // Drop any hi-res render from a previously focused diagram.
            _focusHiResKey = null;
            _focusHiResResult = null;
            _focusHiResScale = 1;
            _focusHiResInFlight = 0;
            _forceHardClear = true;
            _dirty = true;
        }
    }

    /// <summary>Finds the ready image/diagram whose anchor is visible and closest to the top of the viewport.</summary>
    private string? NearestVisibleImageKey()
    {
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var (key, result) in _diagramResults)
        {
            if (result.Status != DiagramStatus.Ready || result.Png is null) continue;
            int rowsTall = DiagramRows(result);
            foreach (int anchor in FindAllDiagramLines(key))
            {
                int top = anchor - _scroll;
                int bottom = top + 1 + rowsTall;      // caption + image rows
                if (bottom <= 0 || top >= ViewportHeight) continue;   // off-screen
                int dist = Math.Abs(top);
                if (dist < bestDist) { bestDist = dist; best = key; }
            }
        }
        return best;
    }

    private void HandleFocusKey(KeyEvent key)
    {
        char ch = key.Kind == KeyKind.Char ? char.ToLowerInvariant(key.Char) : '\0';

        switch (key.Kind)
        {
            case KeyKind.Escape: CloseFocusView(); return;
            case KeyKind.MouseScrollUp: FocusZoom(1); return;
            case KeyKind.MouseScrollDown: FocusZoom(-1); return;
            case KeyKind.Down: FocusPan(2, 0); return;
            case KeyKind.Up: FocusPan(-2, 0); return;
            case KeyKind.Right: FocusPan(0, 4); return;
            case KeyKind.Left: FocusPan(0, -4); return;
            case KeyKind.MouseClick: FocusDragBegin(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseDrag: FocusDragTo(key.MouseRow, key.MouseCol); return;
            case KeyKind.MouseDragEnd: FocusDragTo(key.MouseRow, key.MouseCol); _focusDragging = false; return;
            case KeyKind.MouseRightClick: return;   // ignore right-click in the focus view
        }

        switch (ch)
        {
            case 'q': case 'v': CloseFocusView(); return;
            case '+': case '=': FocusZoom(1); return;
            case '-': case '_': FocusZoom(-1); return;
            case '0': _focusZoom = 0; _focusPanRows = 0; _focusPanCols = 0; _forceHardClear = true; _dirty = true; return;
            case 'j': FocusPan(2, 0); return;
            case 'k': FocusPan(-2, 0); return;
            case 'l': FocusPan(0, 4); return;
            case 'h': FocusPan(0, -4); return;
            case 'o': if (_focusKey is not null) OpenImageExternally(_focusKey, browser: false); return;
            case 'b': if (_focusKey is not null) OpenImageExternally(_focusKey, browser: true); return;
        }
    }

    private void FocusZoom(int delta)
    {
        int next = Math.Clamp(_focusZoom + delta, 0, 12);
        if (next == _focusZoom) return;
        lock (_stateLock) { _focusZoom = next; _forceHardClear = true; _dirty = true; }
    }

    private void FocusPan(int deltaRows, int deltaCols)
    {
        lock (_stateLock)
        {
            _focusPanRows += deltaRows;
            _focusPanCols += deltaCols;
            _forceHardClear = true;
            _dirty = true;
        }
    }

    /// <summary>Begins a mouse drag-to-pan in the focus view: records the grab point and current pan.</summary>
    private void FocusDragBegin(int row, int col)
    {
        lock (_stateLock)
        {
            _focusDragging = true;
            _focusDragStartRow = row;
            _focusDragStartCol = col;
            _focusDragStartPanRows = _focusPanRows;
            _focusDragStartPanCols = _focusPanCols;
        }
    }

    /// <summary>
    /// Continues a drag-to-pan: the image follows the cursor like a hand tool (drag down reveals the
    /// top). Pan is derived from the total offset since the grab so it tracks the cursor exactly.
    /// </summary>
    private void FocusDragTo(int row, int col)
    {
        if (!_focusDragging) return;
        lock (_stateLock)
        {
            _focusPanRows = _focusDragStartPanRows - (row - _focusDragStartRow);
            _focusPanCols = _focusDragStartPanCols - (col - _focusDragStartCol);
            _forceHardClear = true;
            _dirty = true;
        }
    }

    private void CloseFocusView()
    {
        lock (_stateLock)
        {
            _focusMode = false;
            _focusKey = null;
            _focusDragging = false;
            _focusHiResKey = null;
            _focusHiResResult = null;
            _focusHiResInFlight = 0;
            _forceHardClear = true;   // wipe the full-screen image; the document repaints next frame
            _dirty = true;
        }
    }

    /// <summary>
    /// Writes the image/diagram to a temp file and hands it off: to the OS default image viewer
    /// (raw PNG, or the original local file when available), or to the default browser (a small HTML
    /// page embedding the image so a browser — not the image app — handles it).
    /// </summary>
    private void OpenImageExternally(string key, bool browser)
    {
        DiagramResult? result;
        lock (_stateLock) { _diagramResults.TryGetValue(key, out result); }
        if (result is null || result.Status != DiagramStatus.Ready || result.Png is null)
        {
            SetStatus("Image not ready");
            return;
        }

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "readmd");
            Directory.CreateDirectory(dir);
            string safe = key.Replace(':', '_');

            if (browser)
            {
                // Prefer a true-vector page so the browser can zoom infinitely without blur:
                //  - mermaid: re-render the source client-side via mermaid.js;
                //  - D2/Graphviz/PlantUML/SVG images: inline the SVG;
                //  - raster images: embed the PNG.
                string html;
                DiagramRequest? diagram = null;
                lock (_stateLock) { _pendingDiagrams.TryGetValue(key, out diagram); }
                if (diagram is { Kind: DiagramKind.Mermaid })
                {
                    html = Readmd.Diagrams.MermaidHtml.BuildStandalonePage(diagram.Source, _theme.IsDark);
                }
                else
                {
                    string body = result.Svg is not null
                        ? result.Svg   // inline SVG renders crisply (foreignObject labels included)
                        : "<img src=\"data:image/png;base64," + Convert.ToBase64String(result.Png) + "\" alt=\"image\">";
                    html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>readmd image</title>" +
                        "<style>html,body{margin:0;height:100%}body{display:flex;align-items:center;justify-content:center;background:#fff}" +
                        "img,svg{max-width:100vw;max-height:100vh;width:auto;height:auto}</style></head><body>" +
                        body + "</body></html>";
                }
                var htmlPath = Path.Combine(dir, safe + ".html");
                File.WriteAllText(htmlPath, html);
                OpenUrl(htmlPath);
                SetStatus("Opened in browser");
            }
            else
            {
                // OS image viewers show a raster, so resolution is fixed once written. For mermaid
                // (PNG-only) render a large high-resolution PNG on demand so the viewer has real
                // detail to zoom into; other types use the original file or their existing PNG.
                DiagramRequest? diagram = null;
                lock (_stateLock) { _pendingDiagrams.TryGetValue(key, out diagram); }
                if (diagram is { Kind: DiagramKind.Mermaid })
                {
                    OpenMermaidHiResInViewer(diagram, result, dir, safe);
                    return;
                }
                // Prefer the original local file (best fidelity, e.g. animated GIF) when we have it.
                string? local = TryResolveLocalImage(key);
                string path = local ?? Path.Combine(dir, safe + ".png");
                if (local is null) File.WriteAllBytes(path, result.Png);
                OpenUrl(path);
                SetStatus("Opened in image viewer");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Open failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Renders the mermaid diagram at a high scale (targeting a large long-edge) and opens that PNG in
    /// the OS image viewer, so a wide/long diagram is big enough to inspect. Runs in the background
    /// because the re-render goes through the browser backend.
    /// </summary>
    private void OpenMermaidHiResInViewer(DiagramRequest request, DiagramResult baseResult, string dir, string safe)
    {
        SetStatus("Rendering high-resolution image…", 10);
        var theme = _diagramTheme;
        int basePx = Math.Max(baseResult.PixelWidth, baseResult.PixelHeight);
        // Scale (relative to the base 2× render) so the long edge lands near ~6000px, capped at the
        // renderer's max (4 ⇒ device scale 8).
        double scale = basePx > 0 ? Math.Clamp(6000.0 / basePx, 1.0, 4.0) : 4.0;
        _ = Task.Run(async () =>
        {
            try
            {
                var hi = await _diagrams.RenderAtScaleAsync(request, theme, scale, _lifetimeCts.Token);
                var png = hi is { Status: DiagramStatus.Ready, Png: not null } ? hi.Png : baseResult.Png;
                if (png is null) { SetStatus("Image not ready"); return; }
                var path = Path.Combine(dir, safe + ".png");
                await File.WriteAllBytesAsync(path, png, _lifetimeCts.Token);
                OpenUrl(path);
                SetStatus("Opened in image viewer");
            }
            catch (Exception ex) { SetStatus("Open failed: " + ex.Message); }
        });
    }

    /// <summary>Resolves an image key back to its on-disk source path when it's a local (non-remote) image.</summary>
    private string? TryResolveLocalImage(string key)
    {
        if (!_pendingImages.TryGetValue(key, out var url)) return null;
        if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("data:")) return null;
        try
        {
            var baseDir = Path.GetDirectoryName(_currentPath);
            if (baseDir is null) return null;
            var full = Path.GetFullPath(Path.Combine(baseDir, url));
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }
}
