using System.Diagnostics;
using System.Text;
using Readmd.Core;

namespace Readmd.Terminal;

public sealed partial class TerminalViewer
{
    // ---------------- diagram rendering ----------------
    private void KickoffDiagramRenders(bool forceDiagramRerender = false)
    {
        // Text-only output (graphics: none): render each diagram as box art (D2/PlantUML) when a
        // backend can, else show its source — never a blank caption.
        if (_graphicsMode == GraphicsMode.None)
        {
            KickoffTextDiagrams();
            KickoffImageLoads();
            return;
        }

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
            lock (_stateLock) { ReservePlaceholderRows(key); _dirty = true; }
            _ = Task.Run(async () =>
            {
                var result = await _diagrams.RenderAsync(req, _diagramTheme, _lifetimeCts.Token);
                // A failed image render still has a useful text fallback for D2/PlantUML.
                string? text = result.Status == DiagramStatus.Ready ? null : await _diagrams.RenderTextAsync(req, _lifetimeCts.Token);
                lock (_stateLock)
                {
                    // Only replace if we got a good render (keeps the old image visible on failure).
                    if (result.Status == DiagramStatus.Ready)
                    {
                        _diagramResults[key] = result;
                        ReserveDiagramRows(key, result);
                        if (result.Backend == "kroki") NoteKroki();
                    }
                    else if (!_diagramResults.ContainsKey(key) || _diagramResults[key].Status != DiagramStatus.Ready)
                    {
                        // No good image to fall back to: show the error inline so the user knows why
                        // the diagram is blank (e.g. d2 missing, Chromium download failed), plus the
                        // text rendering or source so the content isn't lost.
                        _diagramResults[key] = result;
                        ShowDiagramError(key, result.Error, textArt: text);
                    }
                    ClearDiagramCaches();
                    _forceHardClear = true;
                    _dirty = true;
                }
            });
        }

        KickoffImageLoads();
    }

    private bool _krokiNoted;
    private void NoteKroki()
    {
        if (_krokiNoted) return;
        _krokiNoted = true;
        SetStatus("Diagram rendered via Kroki (diagram text was sent to the server) — set krokiUrl for a local server, or --offline", 6);
    }

    /// <summary>graphics: none — fill each diagram's rows with its text rendering (or source).</summary>
    private void KickoffTextDiagrams()
    {
        foreach (var (key, request) in _pendingDiagrams)
        {
            if (_diagramRequested.Contains(key)) continue;
            _diagramRequested.Add(key);
            var req = request;
            // Source immediately (so nothing is blank), replaced by box art when it arrives.
            lock (_stateLock) { ShowDiagramText(key, null, req); }
            _ = Task.Run(async () =>
            {
                var text = await _diagrams.RenderTextAsync(req, _lifetimeCts.Token);
                if (text is null) return;
                lock (_stateLock) { ShowDiagramText(key, text, req); _dirty = true; }
            });
        }
    }

    /// <summary>
    /// Replaces the reserved rows under a diagram anchor with its text rendering (box art) or, when
    /// none is available, its source as a code block.
    /// </summary>
    private void ShowDiagramText(string key, string? textArt, DiagramRequest request)
    {
        var list = _lines;
        string reserveTag = "reserve:" + key;
        for (int anchor = list.Count - 1; anchor >= 0; anchor--)
        {
            if (list[anchor].DiagramKey != key) continue;
            int existing = 0;
            while (anchor + 1 + existing < list.Count && list[anchor + 1 + existing].DiagramKey == reserveTag) existing++;
            for (int i = 0; i < existing; i++) list.RemoveAt(anchor + 1);
            var rows = textArt is not null
                ? TextArtLines(textArt, list[anchor].DiagramIndent)
                : SourceBlockLines(request.Source, request.Kind.ToString().ToLowerInvariant(), list[anchor].DiagramIndent);
            for (int i = rows.Count - 1; i >= 0; i--) { rows[i].DiagramKey = reserveTag; list.Insert(anchor + 1, rows[i]); }
            int delta = rows.Count - existing;
            if (delta != 0 && anchor < _scroll) _scroll = Math.Max(0, _scroll + delta);
        }
    }

    /// <summary>Lays out box-art text as display lines (monospace, no wrapping — art must not reflow).</summary>
    private List<DisplayLine> TextArtLines(string art, int indent)
    {
        var lines = new List<DisplayLine>();
        int width = Math.Max(10, ContentWidth - indent - 2);
        foreach (var raw in art.Replace("\r\n", "\n").Split('\n'))
        {
            var line = new DisplayLine();
            line.Spans.Add(new StyledSpan(new string(' ', indent + 2)));
            line.Spans.Add(new StyledSpan(TextWidth.TrimToWidth(raw.TrimEnd(), width), _theme.Text));
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].PlainText.Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
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
            lock (_stateLock) { ReservePlaceholderRows(key); _dirty = true; }
            _ = Task.Run(async () =>
            {
                var result = await _imageLoader.LoadAsync(imgUrl, docPath);
                lock (_stateLock)
                {
                    _diagramResults[key] = result;
                    if (result.Status == DiagramStatus.Ready) ReserveDiagramRows(key, result);
                    else ShowDiagramError(key, result.Error, "image error");   // say why it's blank
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
            lock (_stateLock) { ReservePlaceholderRows(groupKey); _dirty = true; }
            _ = Task.Run(async () =>
            {
                var result = await _imageLoader.LoadGroupAsync(groupKey, groupUrls, docPath);
                lock (_stateLock)
                {
                    _diagramResults[groupKey] = result;
                    if (result.Status == DiagramStatus.Ready) ReserveDiagramRows(groupKey, result);
                    else ShowDiagramError(groupKey, result.Error, "image error");
                    ClearDiagramCaches();
                    _forceHardClear = true;
                    _dirty = true;
                }
            });
        }
    }

    /// <summary>
    /// While a diagram/image is still rendering, reserve a few rows beneath its anchor showing a
    /// "rendering…" hint, so content below doesn't jump when the image lands (its final height
    /// replaces the placeholder rows). Uses the last known height for this key when available.
    /// </summary>
    private void ReservePlaceholderRows(string key)
    {
        if (_graphicsMode == GraphicsMode.None) return;
        if (_diagramResults.TryGetValue(key, out var r) && r.Status == DiagramStatus.Ready) return;
        int rows = _lastKnownRows.TryGetValue(key, out var known) ? known : 3;
        string reserveTag = "reserve:" + key;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].DiagramKey != key) continue;
            int existing = 0;
            while (i + 1 + existing < _lines.Count && _lines[i + 1 + existing].DiagramKey == reserveTag) existing++;
            if (existing > 0) { i += existing; continue; }
            for (int k = 0; k < rows; k++)
            {
                var line = new DisplayLine { DiagramKey = reserveTag, IsPlaceholder = true };
                if (k == 1) line.Spans.Add(new StyledSpan(new string(' ', 2 + _lines[i].DiagramIndent) + Glyphs.Rendering, _theme.Muted, CellStyle.Italic));
                _lines.Insert(i + 1 + k, line);
            }
            if (i < _scroll) _scroll += rows;
            i += rows;
        }
    }

    // Remembered image heights (rows) per key, so a placeholder can be sized right on reload.
    private readonly Dictionary<string, int> _lastKnownRows = new();

    /// <summary>
    /// Inserts blank reservation lines beneath each anchor for a diagram/image so the text after it
    /// flows below the rendered image instead of being painted over. Handles the same image/diagram
    /// appearing multiple times in the document.
    /// </summary>
    private void ReserveDiagramRows(string key, DiagramResult result, bool adjustScroll = true)
    {
        // A diagram toggled to its source (or text-only mode) gets source rows instead of image rows.
        if (ShowsSource(key) && _pendingDiagrams.TryGetValue(key, out var req))
        {
            ShowDiagramText(key, result.Text, req);
            return;
        }
        int needed = DiagramRows(result);
        _lastKnownRows[key] = needed;
        var list = _lines;

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

            // Placeholder rows (with their "rendering…" text) are replaced by clean blank rows.
            for (int i = 0; i < existing; i++) list.RemoveAt(anchor + 1);
            for (int i = 0; i < needed; i++)
                list.Insert(anchor + 1, new DisplayLine { DiagramKey = "reserve:" + key });

            int delta = needed - existing;
            // Keep the viewport stable for async single-diagram resolution.
            if (adjustScroll && delta != 0 && anchor < _scroll)
                _scroll = Math.Max(0, _scroll + delta);
        }
    }

    /// <summary>
    /// Renders a failed diagram's error inline: recolours each anchor caption red and inserts the
    /// wrapped error message on the reserved rows beneath it, so the user sees why nothing rendered.
    /// </summary>
    private void ShowDiagramError(string key, string? error, string label = "diagram error", string? textArt = null)
    {
        var list = _lines;
        var msg = string.IsNullOrWhiteSpace(error) ? "could not be rendered" : error!.Trim();

        int width = Math.Max(20, ContentWidth - 6);
        var wrapped = WrapPlain(msg, width);
        var errColor = _theme.IsDark ? Rgb.FromHex("#f08c8c") : Rgb.FromHex("#cf222e");
        string reserveTag = "reserve:" + key;

        var anchors = new List<int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].DiagramKey == key) anchors.Add(i);

        for (int a = anchors.Count - 1; a >= 0; a--)
        {
            int anchor = anchors[a];
            // Recolour the caption to signal failure (keep any gutter/indent spans in front).
            var cap = list[anchor];
            int keep = 0;
            while (keep < cap.Spans.Count && cap.Spans[keep].Text.Trim().Length == 0) keep++;
            cap.Spans.RemoveRange(keep, cap.Spans.Count - keep);
            cap.Spans.Add(new StyledSpan(Glyphs.Error, errColor, CellStyle.Bold));
            cap.Spans.Add(new StyledSpan(label, errColor, CellStyle.Bold));

            // Remove existing reserved rows for this anchor, then insert the error lines.
            int existing = 0;
            while (anchor + 1 + existing < list.Count && list[anchor + 1 + existing].DiagramKey == reserveTag) existing++;
            for (int i = 0; i < existing; i++) list.RemoveAt(anchor + 1);

            // For diagrams, show the text rendering (or the source) beneath the error so the
            // content isn't lost.
            var sourceLines = new List<DisplayLine>();
            if (textArt is not null) sourceLines = TextArtLines(textArt, cap.DiagramIndent);
            else if (_blocks.TryGetValue(cap.BlockId, out var info) && info.Kind == BlockKind.Diagram && info.Text is { Length: > 0 } src)
                sourceLines = SourceBlockLines(src, info.Language ?? "", cap.DiagramIndent);

            for (int i = sourceLines.Count - 1; i >= 0; i--) { sourceLines[i].DiagramKey = reserveTag; list.Insert(anchor + 1, sourceLines[i]); }
            for (int i = wrapped.Count - 1; i >= 0; i--)
            {
                var line = new DisplayLine { DiagramKey = reserveTag };
                line.Spans.Add(new StyledSpan(new string(' ', 2 + cap.DiagramIndent) + wrapped[i], _theme.Muted));
                list.Insert(anchor + 1, line);
            }
            // Keep the viewport stable if rows were added above the current top.
            int delta = wrapped.Count + sourceLines.Count - existing;
            if (delta != 0 && anchor < _scroll) _scroll = Math.Max(0, _scroll + delta);
        }
    }

    /// <summary>Lays out diagram source as a code block (for failures, graphics:none and the `s` toggle).</summary>
    private List<DisplayLine> SourceBlockLines(string source, string language, int indent)
    {
        var scratch = new MarkdownTerminalRenderer(_theme, ContentWidth) { LineNumbers = _options.LineNumbers, TabSize = _options.TabSize };
        return scratch.RenderCodeLines(source, language, indent);
    }

    /// <summary>Greedy word-wrap of a plain string to the given width.</summary>
    private static List<string> WrapPlain(string text, int width)
    {
        var lines = new List<string>();
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            var words = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { lines.Add(""); continue; }
            var sb = new StringBuilder();
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

    // ---------------- scrolling ----------------
    private void ScrollBy(int delta) => ScrollTo(_scroll + delta);

    private void ScrollTo(int target)
    {
        int maxScroll = Math.Max(0, _lines.Count - ViewportHeight);
        int newScroll = Math.Clamp(target, 0, maxScroll);
        if (newScroll != _scroll)
        {
            _scroll = newScroll;
            _dirty = true;
        }
    }

    // ---------------- search ----------------
    private void EnterSearch()
    {
        _searchMode = true;
        _searchQuery = "";
        _searchHits.Clear();
        _hitLines.Clear();
        _searchHitIndex = -1;
        _searchHistoryIndex = -1;
        _dirty = true;
    }

    private readonly List<string> _searchHistory = [];
    private int _searchHistoryIndex = -1;
    private bool _searchRegex;
    private bool _searchCaseSensitive;

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
            _searchMode = false; _searchHits.Clear(); _hitLines.Clear(); _searchHitIndex = -1; _dirty = true;
            return;
        }
        if (isEnter)
        {
            _searchMode = false;
            if (_searchQuery.Length > 0 && (_searchHistory.Count == 0 || _searchHistory[^1] != _searchQuery)) _searchHistory.Add(_searchQuery);
            if (_searchHits.Count > 0) ScrollToHit(_searchHitIndex);
            _dirty = true;
            return;
        }
        // Ctrl+R toggles regex, Ctrl+I/Alt+C toggles case sensitivity while typing.
        if (key.Kind == KeyKind.Char && key.Ctrl && char.ToLowerInvariant(key.Char) == 'r') { _searchRegex = !_searchRegex; RecomputeSearch(); return; }
        if (key.Kind == KeyKind.Char && ((key.Ctrl && char.ToLowerInvariant(key.Char) == 'i') || (key.Alt && char.ToLowerInvariant(key.Char) == 'c'))) { _searchCaseSensitive = !_searchCaseSensitive; RecomputeSearch(); return; }
        // History: Up/Down recall earlier queries.
        if (key.Kind is KeyKind.Up or KeyKind.Down && _searchHistory.Count > 0)
        {
            if (_searchHistoryIndex < 0) _searchHistoryIndex = _searchHistory.Count;
            _searchHistoryIndex = Math.Clamp(_searchHistoryIndex + (key.Kind == KeyKind.Up ? -1 : 1), 0, _searchHistory.Count);
            _searchQuery = _searchHistoryIndex < _searchHistory.Count ? _searchHistory[_searchHistoryIndex] : "";
            RecomputeSearch();
            return;
        }
        if (isBackspace)
        {
            if (_searchQuery.Length > 0)
            {
                // Remove one grapheme (not one UTF-16 unit) so emoji/combining input deletes cleanly.
                var si = new System.Globalization.StringInfo(_searchQuery);
                _searchQuery = si.LengthInTextElements <= 1 ? "" : si.SubstringByTextElements(0, si.LengthInTextElements - 1);
            }
            RecomputeSearch();
            return;
        }
        // Pasted text goes into the query verbatim (first line only — a query is single-line).
        if (key.Kind == KeyKind.Paste && key.Text is { Length: > 0 } pasted)
        {
            var firstLine = pasted.Split('\n')[0].TrimEnd('\r');
            if (firstLine.Length > 0) { _searchQuery += firstLine; RecomputeSearch(); }
            return;
        }
        // Printable character (ignore control chars and Ctrl combos).
        if (key.Kind == KeyKind.Char && !key.Ctrl && !key.Alt && !char.IsControl(key.Char))
        {
            _searchQuery += key.Text ?? key.Char.ToString();
            RecomputeSearch();
        }
    }

    /// <summary>Smart case: a query with any uppercase letter is case-sensitive unless overridden.</summary>
    private bool EffectiveCaseSensitive => _searchCaseSensitive || _searchQuery.Any(char.IsUpper);

    private void RecomputeSearch(bool jumpToFirst = true)
    {
        _searchHits.Clear();
        _hitLines.Clear();
        _searchHitIndex = -1;
        _searchError = null;
        if (_searchQuery.Length >= 1)
        {
            var comparison = EffectiveCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            System.Text.RegularExpressions.Regex? regex = null;
            if (_searchRegex)
            {
                try
                {
                    regex = new System.Text.RegularExpressions.Regex(_searchQuery,
                        (EffectiveCaseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                }
                catch (ArgumentException ex) { _searchError = ex.Message; _dirty = true; return; }
            }
            // Search the FULL layout (folded regions included) so a hit inside a collapsed section
            // can be jumped to; jumping unfolds it.
            foreach (var line in _allLines)
            {
                var text = line.PlainText;
                if (text.Length == 0) continue;
                if (regex is not null)
                {
                    try
                    {
                        foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
                        {
                            if (m.Length == 0) continue;
                            AddHit(line, text, m.Index, m.Length);
                        }
                    }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
                    continue;
                }
                int idx = 0;
                while ((idx = text.IndexOf(_searchQuery, idx, comparison)) >= 0)
                {
                    AddHit(line, text, idx, _searchQuery.Length);
                    idx += _searchQuery.Length;
                }
            }
            if (_searchHits.Count > 0)
            {
                // Pick the first hit at or after the current scroll. One pass over the visible lines
                // gives every hit's row; a per-hit linear scan would be hits × lines on a common word.
                var rowOf = new Dictionary<DisplayLine, int>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < _lines.Count; i++) rowOf.TryAdd(_lines[i], i);
                _searchHitIndex = _searchHits.FindIndex(h => rowOf.TryGetValue(h.Line, out var row) && row >= _scroll);
                if (_searchHitIndex < 0) _searchHitIndex = 0;
                if (jumpToFirst) ScrollToHit(_searchHitIndex);
            }
        }
        _dirty = true;
    }

    private string? _searchError;

    private void AddHit(DisplayLine line, string text, int idx, int length)
    {
        // Hits are stored in DISPLAY columns (wide/emoji glyphs are two cells) so the highlight
        // lands exactly where the match is drawn, not shifted right of it.
        int col = TextWidth.OfPrefix(text, idx);
        int len = TextWidth.Of(text.Substring(idx, Math.Min(length, text.Length - idx)));
        _searchHits.Add((line, col, Math.Max(1, len)));
        _hitLines.Add(line);
    }

    /// <summary>Index of a line object in the visible projection, or -1 when hidden by a fold.</summary>
    private int VisibleIndexOf(DisplayLine line)
    {
        // Lines are reference-compared; the visible list is small enough for a linear scan.
        for (int i = 0; i < _lines.Count; i++) if (ReferenceEquals(_lines[i], line)) return i;
        return -1;
    }

    private void JumpSearch(int dir)
    {
        if (_searchHits.Count == 0) { if (_searchQuery.Length > 0) SetStatus("No matches"); return; }
        _searchHitIndex = ((_searchHitIndex + dir) % _searchHits.Count + _searchHits.Count) % _searchHits.Count;
        ScrollToHit(_searchHitIndex);
        _dirty = true;
    }

    private void ScrollToHit(int hitIndex)
    {
        if (hitIndex < 0 || hitIndex >= _searchHits.Count) return;
        var line = _searchHits[hitIndex].Line;
        int idx = VisibleIndexOf(line);
        if (idx < 0)
        {
            // Hidden inside a folded region: unfold every region containing it, then re-project.
            int raw = IndexOfRaw(line);
            if (raw >= 0 && UnfoldContaining(raw)) idx = VisibleIndexOf(line);
        }
        if (idx < 0) return;
        if (idx < _scroll || idx >= _scroll + ViewportHeight)
            ScrollTo(idx - ViewportHeight / 2);
    }

    private int IndexOfRaw(DisplayLine line)
    {
        for (int i = 0; i < _allLines.Count; i++) if (ReferenceEquals(_allLines[i], line)) return i;
        return -1;
    }

    // ---------------- TOC ----------------
    private void OpenToc()
    {
        if (_toc.Count == 0) { SetStatus("No headings"); return; }
        _tocMode = true;
        _tocFilter = "";
        // start near the heading closest to current scroll
        _tocIndex = 0;
        for (int i = 0; i < _toc.Count; i++)
            if (FindHeadingLine(_toc[i].Id) is var l && l >= 0 && l <= _scroll) _tocIndex = i;
        _dirty = true;
    }

    private string _tocFilter = "";

    /// <summary>TOC entries matching the type-to-filter text (all when empty).</summary>
    private IReadOnlyList<TocEntry> FilteredToc =>
        _tocFilter.Length == 0 ? _toc : _toc.Where(e => e.Title.Contains(_tocFilter, StringComparison.OrdinalIgnoreCase)).ToList();

    private void HandleTocKey(KeyEvent key)
    {
        var entries = FilteredToc;
        char ch = key.Kind == KeyKind.Char ? key.Char : '\0';
        if (key.Kind == KeyKind.Escape)
        {
            if (_tocFilter.Length > 0) { _tocFilter = ""; _tocIndex = 0; _dirty = true; return; }
            _tocMode = false; _dirty = true; return;
        }
        if (key.Kind == KeyKind.Down || key.Kind == KeyKind.MouseScrollDown || (ch == 'j' && _tocFilter.Length == 0) || (key.Ctrl && ch == 'n'))
        {
            _tocIndex = Math.Min(_tocIndex + 1, Math.Max(0, entries.Count - 1)); _dirty = true; return;
        }
        if (key.Kind == KeyKind.Up || key.Kind == KeyKind.MouseScrollUp || (ch == 'k' && _tocFilter.Length == 0) || (key.Ctrl && ch == 'p'))
        {
            _tocIndex = Math.Max(_tocIndex - 1, 0); _dirty = true; return;
        }
        if (key.Kind == KeyKind.PageDown) { _tocIndex = Math.Min(_tocIndex + 10, Math.Max(0, entries.Count - 1)); _dirty = true; return; }
        if (key.Kind == KeyKind.PageUp) { _tocIndex = Math.Max(_tocIndex - 10, 0); _dirty = true; return; }
        if (key.Kind == KeyKind.Home || (ch == 'g' && _tocFilter.Length == 0)) { _tocIndex = 0; _dirty = true; return; }
        if (key.Kind == KeyKind.End || (ch == 'G' && _tocFilter.Length == 0)) { _tocIndex = Math.Max(0, entries.Count - 1); _dirty = true; return; }
        if (key.Kind == KeyKind.Backspace)
        {
            if (_tocFilter.Length > 0) { _tocFilter = _tocFilter[..^1]; _tocIndex = 0; _dirty = true; }
            return;
        }
        if (key.Kind == KeyKind.Enter || key.Kind == KeyKind.MouseClick)
        {
            if (key.Kind == KeyKind.MouseClick)
            {
                if (!TocRowAt(key.MouseRow, key.MouseCol, out var clicked)) { _tocMode = false; _dirty = true; return; }
                _tocIndex = clicked;
            }
            _tocMode = false;
            if (_tocIndex >= 0 && _tocIndex < entries.Count)
            {
                var id = entries[_tocIndex].Id;
                int line = FindHeadingLine(id);
                if (line < 0 && UnfoldHeading(id)) line = FindHeadingLine(id);
                if (line >= 0) ScrollTo(line);
            }
            _dirty = true;
            return;
        }
        if ((ch == 't' || ch == 'q') && _tocFilter.Length == 0) { _tocMode = false; _dirty = true; return; }
        // Type to filter.
        if (key.Kind == KeyKind.Char && !key.Ctrl && !key.Alt && !char.IsControl(ch))
        {
            _tocFilter += ch; _tocIndex = 0; _dirty = true;
        }
    }

    private int FindHeadingLine(string id)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].HeadingId == id) return i;
        return -1;
    }

    /// <summary>
    /// Resolves a "#fragment" link target to a display line. Mirrors the browser: it matches heading
    /// ids AND explicit HTML anchors (<c>&lt;a id&gt;</c>/<c>&lt;a name&gt;</c>), URL-decodes the
    /// fragment, and finally falls back to a slug-tolerant compare so GitHub-style author anchors
    /// (e.g. <c>#1-intro</c>, <c>#a--b</c>) still resolve against Markdig's default heading ids
    /// (<c>intro</c>, <c>a-b</c>). Returns -1 when nothing matches.
    /// </summary>
    private int FindAnchorLine(string rawAnchor)
    {
        // Anchor indices refer to the full layout; map to the visible projection (unfolding if needed).
        int raw = AnchorResolver.Resolve(_anchors, rawAnchor);
        if (raw < 0 || raw >= _allLines.Count) return -1;
        var line = _allLines[raw];
        int idx = VisibleIndexOf(line);
        if (idx < 0 && UnfoldContaining(raw)) idx = VisibleIndexOf(line);
        return idx;
    }

    // ---------------- folding ----------------
    private enum FoldOp { Toggle, Close, Open }

    /// <summary>Folds/unfolds the region whose header is the active block (or that contains it).</summary>
    private void FoldActive(FoldOp op)
    {
        var region = FoldRegionForActive();
        if (region is null) { SetStatus("Nothing to fold here"); return; }
        bool folded = _folded.Contains(region.Id);
        bool target = op switch { FoldOp.Close => true, FoldOp.Open => false, _ => !folded };
        if (target == folded) return;
        lock (_stateLock)
        {
            if (target) _folded.Add(region.Id); else _folded.Remove(region.Id);
            _foldOverrides[FoldKey(region)] = target;
            // Keep the header in view.
            var header = _allLines[region.HeaderLine];
            Project();
            int idx = VisibleIndexOf(header);
            if (idx >= 0 && (idx < _scroll || idx >= _scroll + ViewportHeight)) ScrollTo(idx);
            _forceHardClear = true;
        }
        SetStatus(target ? "Folded" : "Unfolded", 1);
    }

    private void FoldAll(bool fold)
    {
        lock (_stateLock)
        {
            _folded.Clear();
            foreach (var region in _foldRegions)
            {
                _foldOverrides[FoldKey(region)] = fold;
                if (fold) _folded.Add(region.Id);
            }
            Project();
            _forceHardClear = true;
        }
        SetStatus(fold ? "All sections folded" : "All sections unfolded", 1.2);
    }

    /// <summary>The fold region headed by the active block, else the innermost region containing it.</summary>
    private FoldRegion? FoldRegionForActive()
    {
        int visIdx = ActiveLineIndex();
        if (visIdx < 0) return null;
        var line = _lines[visIdx];
        if (line.FoldPlaceholder != 0) return _foldRegions.FirstOrDefault(r => r.Id == line.FoldPlaceholder);
        if (line.FoldHeader != 0) return _foldRegions.FirstOrDefault(r => r.Id == line.FoldHeader);
        // Any line of the header block (wrapped heading title / rule) counts as the header.
        if (line.BlockId != 0)
        {
            foreach (var r in _foldRegions)
                if (r.HeaderLine < _allLines.Count && _allLines[r.HeaderLine].BlockId == line.BlockId) return r;
        }
        int raw = IndexOfRaw(line);
        if (raw < 0) return null;
        FoldRegion? best = null;
        foreach (var r in _foldRegions)
            if (raw >= r.StartLine && raw <= r.EndLine && (best is null || r.StartLine >= best.StartLine)) best = r;
        return best;
    }

    /// <summary>Unfolds every folded region containing the raw line; returns true if anything changed.</summary>
    private bool UnfoldContaining(int rawIndex)
    {
        bool changed = false;
        foreach (var r in _foldRegions)
        {
            if (rawIndex >= r.StartLine && rawIndex <= r.EndLine && _folded.Remove(r.Id))
            {
                _foldOverrides[FoldKey(r)] = false;
                changed = true;
            }
        }
        if (changed) lock (_stateLock) { Project(); _forceHardClear = true; }
        return changed;
    }

    private bool UnfoldHeading(string id)
    {
        for (int i = 0; i < _allLines.Count; i++)
            if (_allLines[i].HeadingId == id) return UnfoldContaining(i);
        return false;
    }

    // ---------------- active block & block motions ----------------
    /// <summary>
    /// The "active" block is the one at the top of the viewport (skipping blank separators). Block
    /// actions — yank, fold, toggle diagram source, focus — operate on it, so there is no cursor to
    /// manage: scroll until the block you want is at the top.
    /// </summary>
    private int ActiveLineIndex()
    {
        for (int i = _scroll; i < Math.Min(_lines.Count, _scroll + ViewportHeight); i++)
        {
            var l = _lines[i];
            if (l.Spans.Count == 0 && l.FoldPlaceholder == 0) continue;
            if (l.DiagramKey?.StartsWith("reserve:") ?? false) continue;
            return i;
        }
        return _lines.Count > 0 ? Math.Clamp(_scroll, 0, _lines.Count - 1) : -1;
    }

    private BlockInfo? ActiveBlock()
    {
        int idx = ActiveLineIndex();
        if (idx < 0) return null;
        var line = _lines[idx];
        if (line.DiagramKey?.StartsWith("reserve:") ?? false)
        {
            var anchor = AnchorForRow(idx);
            if (anchor is not null) line = anchor;
        }
        return _blocks.TryGetValue(line.BlockId, out var info) ? info : null;
    }

    /// <summary>Lines of the active block in the visible projection.</summary>
    private List<DisplayLine> ActiveBlockLines()
    {
        var info = ActiveBlock();
        if (info is null) return [];
        return _lines.Where(l => l.BlockId == info.Id && !(l.DiagramKey?.StartsWith("reserve:") ?? false)).ToList();
    }

    /// <summary>Moves the viewport so the n-th next/previous block start matching the predicate is at the top.</summary>
    private void MoveToBlock(int steps, Func<DisplayLine, bool> predicate)
    {
        if (_lines.Count == 0) return;
        int dir = Math.Sign(steps);
        int remaining = Math.Abs(steps);
        int pos = _scroll;
        int currentBlock = _lines[Math.Clamp(ActiveLineIndex(), 0, _lines.Count - 1)].BlockId;
        int i = pos;
        while (remaining > 0)
        {
            int next = -1;
            if (dir > 0)
            {
                for (int j = i + 1; j < _lines.Count; j++)
                {
                    if (!IsBlockStart(j) || !predicate(_lines[j])) continue;
                    next = j; break;
                }
            }
            else
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    if (!IsBlockStart(j) || !predicate(_lines[j])) continue;
                    next = j; break;
                }
            }
            if (next < 0) break;
            i = next;
            remaining--;
        }
        if (i == pos) { SetStatus(dir > 0 ? "No further block" : "No previous block", 1); return; }
        ScrollTo(i);
        _ = currentBlock;
    }

    private bool IsBlockStart(int idx)
    {
        var l = _lines[idx];
        if (l.BlockId == 0 || l.Spans.Count == 0) return false;
        if (l.DiagramKey?.StartsWith("reserve:") ?? false) return false;
        return idx == 0 || _lines[idx - 1].BlockId != l.BlockId;
    }

    // ---------------- yank ----------------
    /// <summary>
    /// Copies the active block's content without any frame chrome: code without gutters/line
    /// numbers, a diagram's source, a table as tab-separated text, otherwise the block's visible
    /// text with bullets/quote bars stripped.
    /// </summary>
    private void YankActiveBlock()
    {
        // A mark-mode drag selection takes precedence: y completes the copy from the keyboard.
        if (_selectionMode && _selAnchor is not null) { CopySelection(); return; }
        var info = ActiveBlock();
        if (info is null) { SetStatus("Nothing to copy here"); return; }
        string text = BlockText(info);
        if (string.IsNullOrEmpty(text)) { SetStatus("Nothing to copy here"); return; }
        Clipboard.Copy(text, _screen);
        int n = text.Split('\n').Length;
        SetStatus($"Copied {info.Kind.ToString().ToLowerInvariant()} ({n} line{(n == 1 ? "" : "s")})", 2);
    }

    private string BlockText(BlockInfo info)
    {
        if (info.Text is { Length: > 0 } && info.Kind is BlockKind.Code or BlockKind.Diagram or BlockKind.Math or BlockKind.Image)
            return info.Text;
        var lines = _allLines.Where(l => l.BlockId == info.Id).ToList();
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            // Skip rule rows and strip gutters (bullets, quote bars, table rules) — keep the words.
            var t = l.PlainText;
            if (t.Length > 0 && t.All(c => c is '─' or '━' or '┬' or '┴' or '┼' or '┌' or '┐' or '└' or '┘' or '├' or '┤' or '-' or '+' or '=' or ' ')) continue;
            if (info.Kind == BlockKind.Table)
            {
                // Table rows → tab-separated cells (trimmed), so the copy pastes into a spreadsheet.
                var cells = t.Split('│', '|').Select(c => c.Trim()).ToList();
                if (cells.Count > 0 && cells[0].Length == 0) cells.RemoveAt(0);
                if (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
                sb.Append(string.Join('\t', cells)).Append('\n');
                continue;
            }
            t = t.Replace("▌", "").TrimStart(' ', '•', '◦', '▪', '▸', '▾', '●', '◗', '·', '☑', '☐', '*', '-', '+', '>', '|');
            sb.Append(t.Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private void YankDocument()
    {
        Clipboard.Copy(_currentMarkdown, _screen);
        SetStatus("Copied the whole document (Markdown source)", 2);
    }

    // ---------------- diagram source toggle ----------------
    private readonly HashSet<string> _showSource = [];

    /// <summary>Toggles between a diagram's rendered image and its source code (`s`).</summary>
    private void ToggleDiagramSource()
    {
        var info = ActiveBlock();
        if (info is null || info.Kind != BlockKind.Diagram || info.DiagramKey is null) { SetStatus("Not on a diagram (scroll it to the top of the screen)"); return; }
        var key = info.DiagramKey;
        lock (_stateLock)
        {
            bool showing = !_showSource.Remove(key);
            if (showing) _showSource.Add(key);
            Project();
            ClearDiagramCaches();
            _forceHardClear = true;
        }
        SetStatus(_showSource.Contains(key) ? "Showing diagram source (s to show the image)" : "Showing diagram image", 2);
    }

    /// <summary>True when this diagram's rows should show text (source/box art) instead of the image.</summary>
    private bool ShowsSource(string key) => _showSource.Contains(key) || _graphicsMode == GraphicsMode.None;

    // ---------------- links & navigation ----------------
    private int _activeLinkOrdinal = -1;   // Tab-cycled link (index into _links), -1 = none

    private void FollowFirstVisibleLink()
    {
        if (_selectionMode && _selAnchor is not null) { CopySelection(); return; }   // Enter copies a mark-mode selection
        if (_activeLinkOrdinal >= 0 && _activeLinkOrdinal < _links.Count) { FollowLink(_links[_activeLinkOrdinal]); return; }
        // A fold placeholder at the top: Enter expands it.
        int top = ActiveLineIndex();
        if (top >= 0 && _lines[top].FoldPlaceholder != 0) { FoldActive(FoldOp.Open); return; }
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
        SetStatus("No link on screen (f: link hints)");
    }

    /// <summary>Tab/Shift+Tab: steps the active link through the links visible on screen.</summary>
    private void CycleLink(int step)
    {
        var visible = VisibleLinkIds();
        if (visible.Count == 0) { SetStatus("No links on screen"); return; }
        int pos = visible.IndexOf(_activeLinkOrdinal);
        pos = pos < 0 ? (step > 0 ? 0 : visible.Count - 1) : ((pos + step) % visible.Count + visible.Count) % visible.Count;
        _activeLinkOrdinal = visible[pos];
        SetStatus(Truncate(_links[_activeLinkOrdinal].Url, Math.Max(20, _screen.Width - 20)), 4);
        _dirty = true;
    }

    private List<int> VisibleLinkIds()
    {
        var ids = new List<int>();
        for (int row = 0; row < ViewportHeight; row++)
        {
            int lineIndex = _scroll + row;
            if (lineIndex >= _lines.Count) break;
            foreach (var span in _lines[lineIndex].Spans)
                if (span.LinkId is { } id && id < _links.Count && !ids.Contains(id)) ids.Add(id);
            if (_lines[lineIndex].ImageLinkId is { } il && il < _links.Count && !ids.Contains(il)) ids.Add(il);
        }
        return ids;
    }

    // ---- link hints (vimium-style) ----
    private bool _hintMode;
    private bool _hintCopyOnly;
    private string _hintTyped = "";
    private readonly Dictionary<string, int> _hints = new(StringComparer.OrdinalIgnoreCase);   // label -> link id

    private void EnterHintMode(bool copyOnly)
    {
        var ids = VisibleLinkIds();
        if (ids.Count == 0) { SetStatus("No links on screen"); return; }
        _hints.Clear();
        var labels = HintLabels(ids.Count);
        for (int i = 0; i < ids.Count; i++) _hints[labels[i]] = ids[i];
        _hintMode = true;
        _hintCopyOnly = copyOnly;
        _hintTyped = "";
        _dirty = true;
    }

    /// <summary>Generates short home-row labels: single letters first, then two-letter combinations.</summary>
    internal static List<string> HintLabels(int count)
    {
        const string alphabet = "asdfghjklqwertyuiopzxcvbnm";
        var labels = new List<string>(count);
        if (count <= alphabet.Length)
        {
            for (int i = 0; i < count; i++) labels.Add(alphabet[i].ToString());
            return labels;
        }
        for (int i = 0; i < alphabet.Length && labels.Count < count; i++)
            for (int j = 0; j < alphabet.Length && labels.Count < count; j++)
                labels.Add(alphabet[i].ToString() + alphabet[j]);
        return labels;
    }

    private void HandleHintKey(KeyEvent key)
    {
        if (key.Kind == KeyKind.Escape || IsMouse(key)) { _hintMode = false; _dirty = true; return; }
        if (key.Kind == KeyKind.Backspace) { if (_hintTyped.Length > 0) _hintTyped = _hintTyped[..^1]; _dirty = true; return; }
        if (key.Kind != KeyKind.Char || key.Ctrl || key.Alt) return;
        _hintTyped += char.ToLowerInvariant(key.Char);
        if (_hints.TryGetValue(_hintTyped, out var id))
        {
            _hintMode = false;
            _dirty = true;
            var link = _links[id];
            if (_hintCopyOnly)
            {
                Clipboard.Copy(link.Url, _screen);
                SetStatus("Copied link: " + Truncate(link.Url, Math.Max(20, _screen.Width - 20)), 3);
            }
            else FollowLink(link);
            return;
        }
        if (!_hints.Keys.Any(k => k.StartsWith(_hintTyped, StringComparison.OrdinalIgnoreCase)))
        {
            _hintMode = false; _dirty = true;
            SetStatus("No such hint", 1);
        }
        _dirty = true;
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
        if (SidebarClick(screenRow, screenCol)) return;

        int lineIndex = _scroll + screenRow;
        if (lineIndex < 0 || lineIndex >= _lines.Count) return;
        var line = _lines[lineIndex];
        screenCol -= ContentOffset;   // content may be centred when a reading width is set

        // Clicking a fold placeholder or a foldable header toggles the fold.
        if (line.FoldPlaceholder != 0 || line.FoldHeader != 0)
        {
            var region = _foldRegions.FirstOrDefault(r => r.Id == (line.FoldPlaceholder != 0 ? line.FoldPlaceholder : line.FoldHeader));
            if (region is not null && (line.FoldPlaceholder != 0 || screenCol < 2))
            {
                lock (_stateLock)
                {
                    if (!_folded.Remove(region.Id)) _folded.Add(region.Id);
                    _foldOverrides[FoldKey(region)] = _folded.Contains(region.Id);
                    Project();
                    _forceHardClear = true;
                }
                return;
            }
        }

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
        _activeLinkOrdinal = -1;
        var resolved = _resolver.Resolve(link.Url, _currentPath);
        switch (resolved.Kind)
        {
            case LinkKind.External:
                if (bypassConfirm || !_options.ConfirmExternalLinks)
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
                NavigateTo(resolved.AbsolutePath, resolved.Anchor);
                break;
            case LinkKind.Anchor when resolved.Anchor is not null:
                int line = FindAnchorLine(resolved.Anchor);
                if (line >= 0) ScrollTo(line); else SetStatus("Anchor not found");
                break;
            case LinkKind.Image when resolved.AbsolutePath is not null:
                // A link straight to an image: show it in the focus view.
                OpenImageFile(resolved.AbsolutePath);
                break;
            case LinkKind.Unknown when resolved.AbsolutePath is not null && File.Exists(resolved.AbsolutePath):
                // Other local files (.txt, .pdf, …) open with the OS default handler.
                OpenUrl(resolved.AbsolutePath);
                SetStatus("Opened: " + Path.GetFileName(resolved.AbsolutePath));
                break;
            default:
                SetStatus("Cannot open: " + link.Url);
                break;
        }
    }

    // Per-file scroll memory so Back/Forward return to where you were.
    private readonly Dictionary<string, int> _scrollMemory = new(StringComparer.OrdinalIgnoreCase);

    private void NavigateTo(string absolutePath, string? anchor)
    {
        _scrollMemory[_currentPath] = _scroll;
        SavePosition();
        _ = LoadAsync(absolutePath, pushHistory: true).ContinueWith(_ =>
        {
            lock (_stateLock)
            {
                _scroll = 0;
                if (anchor is not null)
                {
                    int line = FindAnchorLine(anchor);
                    if (line >= 0) _scroll = Math.Min(line, Math.Max(0, _lines.Count - 1));
                }
                _dirty = true;
            }
            if (anchor is null) RestoreSavedPosition();
        });
    }

    private void GoBack()
    {
        if (_historyIndex <= 0) { SetStatus("No previous page"); return; }
        _scrollMemory[_currentPath] = _scroll;
        SavePosition();
        _historyIndex--;
        var target = _history[_historyIndex];
        _ = LoadAsync(target, pushHistory: false).ContinueWith(_ => RestoreScroll(target));
    }

    private void GoForward()
    {
        if (_historyIndex >= _history.Count - 1) { SetStatus("No next page"); return; }
        _scrollMemory[_currentPath] = _scroll;
        SavePosition();
        _historyIndex++;
        var target = _history[_historyIndex];
        _ = LoadAsync(target, pushHistory: false).ContinueWith(_ => RestoreScroll(target));
    }

    private void RestoreScroll(string path)
    {
        lock (_stateLock)
        {
            _scroll = _scrollMemory.TryGetValue(path, out var s) ? Math.Clamp(s, 0, Math.Max(0, _lines.Count - 1)) : 0;
            _dirty = true;
        }
    }

    private void OpenImageFile(string absolutePath)
    {
        var url = Path.GetRelativePath(Path.GetDirectoryName(_currentPath) ?? ".", absolutePath).Replace('\\', '/');
        var key = MarkdownTerminalRenderer.ImageKey(url);
        var docPath = _currentPath;
        _ = Task.Run(async () =>
        {
            var result = await _imageLoader.LoadAsync(url, docPath);
            lock (_stateLock) { _diagramResults[key] = result; }
            if (result.Status == DiagramStatus.Ready) OpenFocusViewForKey(key);
            else SetStatus("Image load failed: " + result.Error);
        });
    }

    // ---------------- theme / appearance ----------------
    /// <summary>The ordered theme names '[' cycles through: the configured cycle, else every named theme.</summary>
    private IReadOnlyList<string> ThemeCycle
    {
        get
        {
            if (_options.ThemeCycle is { Count: > 0 } cycle) return cycle;
            if (_options.NamedThemes is { Count: > 0 } named) return named.Keys.ToList();
            var list = new List<string> { "dark", "light" };
            if (_options.Theme is not null) list.Insert(0, _options.Theme.Name.Length > 0 ? _options.Theme.Name : "custom");
            return list;
        }
    }

    /// <summary>Resolves a theme name: configured/named themes first, then the built-ins.</summary>
    private TerminalTheme? FindTheme(string name)
    {
        if (_options.Theme is not null && (_options.ThemeName?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false
                                            || name.Equals("custom", StringComparison.OrdinalIgnoreCase)
                                            || _options.Theme.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return _options.Theme;
        if (_options.NamedThemes is not null && _options.NamedThemes.TryGetValue(name, out var t)) return t;
        return BuiltInThemes.Find(name);
    }

    /// <summary>'[' — steps to the next theme in the cycle (the configured palette is never lost: it comes back around).</summary>
    private void ToggleTheme(int step = 1)
    {
        var cycle = ThemeCycle;
        int idx = -1;
        for (int i = 0; i < cycle.Count; i++)
            if (string.Equals(cycle[i], _theme.Name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        for (int tries = 0; tries < cycle.Count; tries++)
        {
            idx = ((idx + step) % cycle.Count + cycle.Count) % cycle.Count;
            var next = FindTheme(cycle[idx]);
            if (next is not null && !ReferenceEquals(next, _theme)) { ApplyTheme(next); return; }
        }
        // Nothing else to cycle to (one theme configured): fall back to flipping dark/light.
        ApplyTheme(TerminalTheme.For(!_theme.IsDark));
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
        lock (_stateLock)
        {
            ClearDiagramCaches();
            _forceHardClear = true;
            _refreshWipe = true;   // clean-wipe: clear-only frame, then repaint (clears Sixel artifacts)
            Relayout();
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
            ? "Mark mode ON — drag to select; y, Enter or right-click copies; m exits"
            : "Mark mode OFF — mouse wheel scrolls", 4);
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

    /// <summary>Ordered keys of all ready images/diagrams in document order (for n/p in the focus view).</summary>
    private List<string> ReadyImageKeysInOrder()
    {
        var keys = new List<string>();
        foreach (var line in _lines)
            if (line.DiagramKey is { } k && !k.StartsWith("reserve:") && !keys.Contains(k)
                && _diagramResults.TryGetValue(k, out var r) && r.Status == DiagramStatus.Ready && r.Png is not null)
                keys.Add(k);
        return keys;
    }

    private void HandleFocusKey(KeyEvent key)
    {
        char ch = key.Kind == KeyKind.Char ? key.Char : '\0';

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
            case 'n': case 'p': FocusStep(ch == 'n' ? 1 : -1); return;
            case 'y': if (_focusKey is not null && _diagramResults.TryGetValue(_focusKey, out var fr) && fr.Png is not null) { CopyFocusedSource(); } return;
            case 'o': if (_focusKey is not null) OpenImageExternally(_focusKey, browser: false); return;
            case 'b': if (_focusKey is not null) OpenImageExternally(_focusKey, browser: true); return;
        }
    }

    private void FocusStep(int dir)
    {
        var keys = ReadyImageKeysInOrder();
        if (keys.Count == 0 || _focusKey is null) return;
        int i = keys.IndexOf(_focusKey);
        int next = ((i + dir) % keys.Count + keys.Count) % keys.Count;
        if (keys[next] == _focusKey) return;
        OpenFocusViewForKey(keys[next]);
    }

    private void CopyFocusedSource()
    {
        if (_focusKey is null) return;
        if (_pendingDiagrams.TryGetValue(_focusKey, out var req)) { Clipboard.Copy(req.Source, _screen); SetStatus("Copied diagram source", 2); return; }
        if (_pendingImages.TryGetValue(_focusKey, out var url)) { Clipboard.Copy(url, _screen); SetStatus("Copied image URL", 2); }
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : TextWidth.TrimToWidth(s, Math.Max(1, max - 1)) + "…";
}
