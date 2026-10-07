using Readmd.Core;

namespace Readmd.Terminal;

// A generic list palette (quick-open, project search, directory picker, recent files) and
// multi-file navigation (]f / [f over a file set, directory mode).
public sealed partial class TerminalViewer
{
    // ---------------- palette ----------------
    private sealed record PaletteItem(string Label, string Detail, string? Path, int Line, object? Tag = null);

    private bool _paletteMode;
    private string _paletteTitle = "";
    private string _paletteQuery = "";
    private int _paletteIndex;
    private int _paletteStart;   // first visible row (for the frame geometry)
    private IReadOnlyList<PaletteItem> _paletteAll = [];
    private IReadOnlyList<PaletteItem> _paletteShown = [];
    private Func<string, IReadOnlyList<PaletteItem>>? _paletteFilter;
    private Action<PaletteItem>? _paletteAccept;
    private CancellationTokenSource? _paletteSearchCts;
    private bool _paletteBusy;
    private string _paletteHint = "";

    private void OpenPalette(string title, string hint, IReadOnlyList<PaletteItem> items, Func<string, IReadOnlyList<PaletteItem>> filter, Action<PaletteItem> accept)
    {
        _paletteMode = true;
        _paletteTitle = title;
        _paletteHint = hint;
        _paletteQuery = "";
        _paletteIndex = 0;
        _paletteAll = items;
        _paletteShown = filter("");
        _paletteFilter = filter;
        _paletteAccept = accept;
        _dirty = true;
    }

    private void ClosePalette()
    {
        _paletteMode = false;
        _paletteSearchCts?.Cancel();
        _paletteSearchCts = null;
        _paletteBusy = false;
        _dirty = true;
    }

    private void HandlePaletteKey(KeyEvent key)
    {
        if (key.Kind == KeyKind.Escape) { ClosePalette(); return; }
        if (key.Kind == KeyKind.Down || (key.Ctrl && key.Char is 'n' or 'j')) { _paletteIndex = Math.Min(_paletteIndex + 1, Math.Max(0, _paletteShown.Count - 1)); _dirty = true; return; }
        if (key.Kind == KeyKind.Up || (key.Ctrl && key.Char is 'p' or 'k')) { _paletteIndex = Math.Max(_paletteIndex - 1, 0); _dirty = true; return; }
        if (key.Kind == KeyKind.PageDown) { _paletteIndex = Math.Min(_paletteIndex + 10, Math.Max(0, _paletteShown.Count - 1)); _dirty = true; return; }
        if (key.Kind == KeyKind.PageUp) { _paletteIndex = Math.Max(_paletteIndex - 10, 0); _dirty = true; return; }
        if (key.Kind == KeyKind.Home) { _paletteIndex = 0; _dirty = true; return; }
        if (key.Kind == KeyKind.End) { _paletteIndex = Math.Max(0, _paletteShown.Count - 1); _dirty = true; return; }
        if (key.Kind == KeyKind.MouseScrollDown) { _paletteIndex = Math.Min(_paletteIndex + 3, Math.Max(0, _paletteShown.Count - 1)); _dirty = true; return; }
        if (key.Kind == KeyKind.MouseScrollUp) { _paletteIndex = Math.Max(_paletteIndex - 3, 0); _dirty = true; return; }
        if (key.Kind == KeyKind.MouseClick)
        {
            int row = key.MouseRow - _paletteGeometry.BodyTop;
            if (row < 0 || row >= _paletteGeometry.BodyHeight || key.MouseCol < _paletteGeometry.Left || key.MouseCol >= _paletteGeometry.Left + _paletteGeometry.Width) { ClosePalette(); return; }
            int idx = _paletteStart + row;
            if (idx >= 0 && idx < _paletteShown.Count) { _paletteIndex = idx; AcceptPalette(); }
            return;
        }
        if (IsMouse(key)) return;
        if (key.Kind == KeyKind.Enter) { AcceptPalette(); return; }
        if (key.Kind == KeyKind.Backspace)
        {
            if (_paletteQuery.Length > 0) { _paletteQuery = _paletteQuery[..^1]; RefilterPalette(); }
            return;
        }
        if (key.Kind == KeyKind.Paste && key.Text is { } t) { _paletteQuery += t.Split('\n')[0].TrimEnd('\r'); RefilterPalette(); return; }
        if (key.Kind == KeyKind.Char && !key.Ctrl && !key.Alt && !char.IsControl(key.Char))
        {
            _paletteQuery += key.Text ?? key.Char.ToString();
            RefilterPalette();
        }
    }

    private void RefilterPalette()
    {
        if (_paletteFilter is null) return;
        _paletteShown = _paletteFilter(_paletteQuery);
        _paletteIndex = Math.Clamp(_paletteIndex, 0, Math.Max(0, _paletteShown.Count - 1));
        _dirty = true;
    }

    private void AcceptPalette()
    {
        if (_paletteIndex < 0 || _paletteIndex >= _paletteShown.Count) { ClosePalette(); return; }
        var item = _paletteShown[_paletteIndex];
        var accept = _paletteAccept;
        ClosePalette();
        accept?.Invoke(item);
    }

    private (int BodyTop, int Left, int Width, int BodyHeight) _paletteGeometry;

    private void DrawPaletteOverlay()
    {
        var p = Palette();
        DrawScrim(p);
        int cardW = Math.Min(_screen.Width - 4, Math.Max(40, _screen.Width * 3 / 4));
        int avail = _screen.Height - 9;
        int bodyRows = Math.Clamp(Math.Max(_paletteShown.Count, 1), 3, Math.Max(3, avail));
        int cardH = Math.Min(_screen.Height - 2, bodyRows + 8);
        int boxTop = Math.Max(0, (_screen.Height - cardH) / 3);
        int boxLeft = Math.Max(0, (_screen.Width - cardW) / 2);
        var (cTop, cLeft, cW, cH) = DrawCard(p, boxTop, boxLeft, cardW, cardH);
        string footer = _paletteBusy ? "searching…" : $"{(_paletteShown.Count == 0 ? 0 : _paletteIndex + 1)}/{_paletteShown.Count}   {_paletteHint}";
        DrawHeaderFooter(p, cTop, cLeft, cW, cH, _paletteTitle, footer);

        // Query row.
        int queryRow = cTop + 2;
        _screen.MoveTo(queryRow, cLeft).SetBackground(p.Panel).SetForeground(p.Fg);
        string q = TextWidth.TrimToWidth(" › " + _paletteQuery + Glyphs.Cursor, cW);
        _screen.Write(q);
        int qw = TextWidth.Of(q);
        if (qw < cW) _screen.Write(new string(' ', cW - qw));

        int bodyTop = cTop + 3;
        int bodyHeight = (cTop + cH - 2) - bodyTop;
        int start = Math.Max(0, Math.Min(_paletteIndex - bodyHeight / 2, Math.Max(0, _paletteShown.Count - bodyHeight)));
        _paletteStart = start;
        _paletteGeometry = (bodyTop, cLeft, cW, bodyHeight);
        for (int i = 0; i < bodyHeight; i++)
        {
            int r = bodyTop + i, idx = start + i;
            _screen.MoveTo(r, cLeft);
            if (idx >= _paletteShown.Count)
            {
                _screen.SetBackground(p.Panel);
                if (i == 0 && _paletteShown.Count == 0) _screen.SetForeground(p.Muted).Write(TextWidth.TrimToWidth(_paletteBusy ? " searching…" : " (no matches)", cW).PadRight(cW));
                else _screen.Write(new string(' ', cW));
                continue;
            }
            var item = _paletteShown[idx];
            bool sel = idx == _paletteIndex;
            var bg = sel ? p.SelBg : p.Panel;
            _screen.SetBackground(bg).SetForeground(sel ? p.SelFg : p.Accent).Write(sel ? Glyphs.Selected : " ");
            // label (bold) + detail (muted), detail truncated to fit.
            int room = cW - 1;
            string label = " " + item.Label;
            string detail = item.Detail.Length > 0 ? "  " + item.Detail : "";
            int lw = TextWidth.Of(label);
            if (lw > room) { label = TextWidth.TrimToWidth(label, room - 1) + Glyphs.Ellipsis; lw = TextWidth.Of(label); detail = ""; }
            detail = TextWidth.TrimToWidth(detail, Math.Max(0, room - lw));
            _screen.SetForeground(sel ? p.SelFg : p.Fg).SetStyle(CellStyle.Bold).Write(label);
            _screen.SetStyle(CellStyle.None).SetForeground(sel ? p.SelFg : p.Muted).Write(detail);
            int used = lw + TextWidth.Of(detail);
            if (used < room) _screen.SetBackground(bg).Write(new string(' ', room - used));
        }
        _screen.Reset();
    }

    // ---------------- quick-open (Ctrl+P) ----------------
    private IReadOnlyList<WikiIndex.TreeEntry>? _wikiFiles;
    private DateTime _wikiFilesAt;
    private string? _wikiFilesRoot;

    /// <summary>The Markdown files under the root, re-enumerated when the cached list is stale or for another root.</summary>
    private IReadOnlyList<WikiIndex.TreeEntry> WikiFiles()
    {
        var root = _resolver.Root;
        if (_wikiFiles is null || _wikiFilesRoot != root || DateTime.UtcNow - _wikiFilesAt > TimeSpan.FromSeconds(5))
        {
            // Titles would mean opening every file; the picker shows paths (titles come with search hits).
            _wikiFiles = WikiIndex.ListFiles(root, withTitles: false);
            _wikiFilesAt = DateTime.UtcNow;
            _wikiFilesRoot = root;
        }
        return _wikiFiles;
    }

    private void OpenQuickOpen()
    {
        var root = _resolver.Root;
        var recent = _state?.Recent ?? [];
        var files = WikiFiles();
        var byEntry = new Dictionary<WikiIndex.TreeEntry, PaletteItem>(ReferenceEqualityComparer.Instance);
        var items = new List<PaletteItem>(files.Count);
        foreach (var f in files)
        {
            var item = new PaletteItem(f.Relative, RecentMark(recent, f.Path), f.Path, 0, f);
            items.Add(item);
            byEntry[f] = item;
        }
        if (items.Count == 0) { SetStatus("No other Markdown files under " + root); return; }
        OpenPalette("Open file", "type to filter   ↑↓ move   enter open   esc close", items,
            q =>
            {
                if (q.Length == 0)
                {
                    // Recent files first, then the rest alphabetically.
                    var set = new HashSet<string>(recent, StringComparer.OrdinalIgnoreCase);
                    return items.OrderBy(i => set.Contains(i.Path!) ? recent.ToList().FindIndex(r => string.Equals(r, i.Path, StringComparison.OrdinalIgnoreCase)) : int.MaxValue).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
                }
                var filtered = WikiIndex.FuzzyFilter(files, q);
                return filtered.Select(f => byEntry[f]).ToList();
            },
            item => { if (item.Path is not null) NavigateTo(item.Path, null); });
    }

    private static string RecentMark(IReadOnlyList<string> recent, string path) =>
        recent.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase)) ? "recent" : "";

    // ---------------- project search (Ctrl+G) ----------------
    private void OpenProjectSearch()
    {
        var root = _resolver.Root;
        OpenPalette("Search all files", "type to search   enter open at line   esc close", [],
            q =>
            {
                _paletteSearchCts?.Cancel();
                if (q.Length < 2) { _paletteBusy = false; return []; }
                // Debounced async search; results land via the render loop's dirty flag.
                var cts = new CancellationTokenSource();
                _paletteSearchCts = cts;
                _paletteBusy = true;
                var query = q;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120, cts.Token);
                        var hits = WikiIndex.Search(root, query, cts.Token);
                        if (cts.IsCancellationRequested) return;
                        lock (_stateLock)
                        {
                            if (!_paletteMode || _paletteQuery != query) return;
                            _paletteShown = hits.Select(h => new PaletteItem($"{h.Relative}:{h.Line}", h.Snippet, h.Path, h.Line)).ToList();
                            _paletteIndex = 0;
                            _paletteBusy = false;
                            _dirty = true;
                        }
                    }
                    catch (OperationCanceledException) { }
                });
                return _paletteShown;   // keep showing the previous results until the new ones arrive
            },
            item => { if (item.Path is not null) NavigateToLine(item.Path, item.Line); });
    }

    private void NavigateToLine(string absolutePath, int sourceLine)
    {
        if (string.Equals(absolutePath, _currentPath, StringComparison.OrdinalIgnoreCase)) { GoToSourceLine(sourceLine); return; }
        _scrollMemory[_currentPath] = _scroll;
        SavePosition();
        _ = LoadAsync(absolutePath, pushHistory: true).ContinueWith(_ => { lock (_stateLock) { GoToSourceLine(sourceLine); } });
    }

    // ---------------- file set: directory mode / multiple files ----------------
    /// <summary>The ordered set of files readmd was launched with (directory mode or several arguments).</summary>
    private IReadOnlyList<string> FileSet => _options.FileSet ?? [];

    private void StepFile(int step)
    {
        var set = FileSet;
        if (set.Count <= 1)
        {
            // No explicit set: step through the sibling Markdown files of the current document.
            var dir = Path.GetDirectoryName(_currentPath);
            if (dir is null) { SetStatus("No other files"); return; }
            set = Directory.EnumerateFiles(dir).Where(LinkResolver.IsMarkdownFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (set.Count <= 1) { SetStatus("No other Markdown files in this folder"); return; }
        }
        int idx = set.ToList().FindIndex(f => string.Equals(Path.GetFullPath(f), _currentPath, StringComparison.OrdinalIgnoreCase));
        int next = ((idx + step) % set.Count + set.Count) % set.Count;
        if (next == idx) return;
        _scrollMemory[_currentPath] = _scroll;
        SavePosition();
        var target = Path.GetFullPath(set[next]);
        _ = LoadAsync(target, pushHistory: true).ContinueWith(_ => { RestoreSavedPosition(); SetStatus($"{Path.GetFileName(target)}  ({next + 1}/{set.Count})", 2); });
    }

    /// <summary>Directory mode: a picker over the files found under the folder, most recently modified first.</summary>
    private void OpenFilePicker(IReadOnlyList<string> files, string title)
    {
        var items = files.Select(f =>
        {
            var info = new FileInfo(f);
            var rel = Path.GetRelativePath(_resolver.Root, f).Replace('\\', '/');
            return new PaletteItem(rel, info.Exists ? $"{Ago(info.LastWriteTimeUtc)}  {SizeText(info.Length)}" : "", f, 0);
        }).ToList();
        OpenPalette(title, "type to filter   ↑↓ move   enter open   esc close", items,
            q => q.Length == 0 ? items : items.Where(i => WikiIndex.FuzzyScore(i.Label, q) > int.MinValue).OrderByDescending(i => WikiIndex.FuzzyScore(i.Label, q)).ToList(),
            item => { if (item.Path is not null) NavigateTo(item.Path, null); });
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private static string SizeText(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    /// <summary>Opens the directory picker for the current file's root (used by `readmd .`).</summary>
    internal void ShowDirectoryPicker()
    {
        var files = WikiFiles().Select(f => f.Path).OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } }).ToList();
        if (files.Count == 0) { SetStatus("No Markdown files under " + _resolver.Root); return; }
        OpenFilePicker(files, Path.GetFileName(_resolver.Root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : _resolver.Root);
    }
}
