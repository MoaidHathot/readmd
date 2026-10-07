using Readmd.Core;

namespace Readmd.Terminal;

// Command line (:), export, TOC sidebar, bookmarks, watch/follow toggles, persisted state.
public sealed partial class TerminalViewer
{
    // ---------------- command line ----------------
    private bool _commandMode;
    private string _commandText = "";
    private readonly List<string> _commandHistory = [];
    private int _commandHistoryIndex = -1;

    private void EnterCommandMode(string initial = "")
    {
        _commandMode = true;
        _commandText = initial;
        _commandHistoryIndex = -1;
        _dirty = true;
    }

    private void HandleCommandKey(KeyEvent key)
    {
        if (key.Kind == KeyKind.Escape) { _commandMode = false; _dirty = true; return; }
        if (key.Kind == KeyKind.Enter)
        {
            _commandMode = false; _dirty = true;
            var cmd = _commandText.Trim();
            if (cmd.Length > 0 && (_commandHistory.Count == 0 || _commandHistory[^1] != cmd)) _commandHistory.Add(cmd);
            RunCommand(cmd);
            return;
        }
        if (key.Kind == KeyKind.Backspace)
        {
            if (_commandText.Length > 0) _commandText = _commandText[..^1]; else _commandMode = false;
            _dirty = true; return;
        }
        if (key.Kind is KeyKind.Up or KeyKind.Down && _commandHistory.Count > 0)
        {
            if (_commandHistoryIndex < 0) _commandHistoryIndex = _commandHistory.Count;
            _commandHistoryIndex = Math.Clamp(_commandHistoryIndex + (key.Kind == KeyKind.Up ? -1 : 1), 0, _commandHistory.Count);
            _commandText = _commandHistoryIndex < _commandHistory.Count ? _commandHistory[_commandHistoryIndex] : "";
            _dirty = true; return;
        }
        if (key.Kind == KeyKind.Tab) { _commandText = CompleteCommand(_commandText); _dirty = true; return; }
        if (key.Kind == KeyKind.Paste && key.Text is { } t) { _commandText += t.Split('\n')[0].TrimEnd('\r'); _dirty = true; return; }
        if (key.Kind == KeyKind.Char && !key.Ctrl && !key.Alt && !char.IsControl(key.Char)) { _commandText += key.Text ?? key.Char.ToString(); _dirty = true; }
    }

    /// <summary>One <c>:</c> command: its canonical name, aliases, argument syntax, description and help group.</summary>
    internal sealed record CommandInfo(string Name, string[] Aliases, string Args, string Desc, string Group)
    {
        public IEnumerable<string> AllNames => new[] { Name }.Concat(Aliases);
        public string Syntax => Args.Length == 0 ? ":" + Name : $":{Name} {Args}";
    }

    /// <summary>
    /// Every <c>:</c> command. This single table drives dispatch (<see cref="RunCommand"/> switches
    /// on the canonical name), Tab completion and the <c>:help commands</c> page, so the three can't
    /// drift apart; a test checks that each entry is accepted by the dispatcher.
    /// </summary>
    internal static readonly CommandInfo[] CommandCatalog =
    [
        new("goto", ["g"], "<line | mark>", "go to a source line or bookmark", "NAVIGATE"),
        new("toc", [], "", "open the table of contents", "NAVIGATE"),
        new("bookmark", ["mark", "m"], "<a–z>", "set a bookmark on the top line", "NAVIGATE"),
        new("marks", [], "", "list the bookmarks", "NAVIGATE"),
        new("nohlsearch", ["noh"], "", "clear search highlights & selection", "NAVIGATE"),

        new("edit", ["e", "open", "o"], "<file[#sec] | url>", "open a file (relative) or a URL", "FILES"),
        new("reload", ["r"], "", "re-read the document from disk", "FILES"),
        new("watch", [], "", "resume live reload", "FILES"),
        new("nowatch", [], "", "pause live reload", "FILES"),
        new("follow", ["tail"], "", "follow mode (tail the file)", "FILES"),
        new("export", ["write", "w"], "[out.html | out.pdf]", "export next to the document", "FILES"),
        new("browser", [], "", "open the document in the browser", "FILES"),

        new("fold", ["foldclose", "zc"], "", "fold the top section or <details>", "BLOCKS"),
        new("unfold", ["foldopen", "zo"], "", "unfold it", "BLOCKS"),
        new("foldall", ["zm"], "", "fold every section", "BLOCKS"),
        new("unfoldall", ["zr"], "", "unfold everything", "BLOCKS"),
        new("copy", ["yank", "y"], "[all]", "copy the top block (all: document)", "BLOCKS"),
        new("source", ["s"], "", "diagram: toggle image ↔ source", "BLOCKS"),
        new("zoom", [], "[level]", "zoom diagrams to a level (none: reset)", "BLOCKS"),

        new("theme", [], "[name]", "switch theme (no name: cycle)", "VIEW"),
        new("graphics", [], "<mode>", "sixel, kitty, iterm2, half-block, none", "VIEW"),
        new("width", [], "<columns | 0>", "reading width in columns (0 = full)", "VIEW"),
        new("ascii", [], "[on | off]", "ASCII glyphs instead of box drawing", "VIEW"),
        new("set", [], "<option>[=value]", "theme, graphics, width, ascii, wheel, bg", "VIEW"),

        new("slides", ["present"], "[on | off]", "presentation mode (one slide per ---)", "SLIDES"),
        new("slide", [], "<N>", "go to slide N (enters slide mode)", "SLIDES"),
        new("notes", [], "", "toggle the speaker notes", "SLIDES"),

        new("help", ["h"], "[commands]", "keybindings, or this command list", "HELP"),
        new("commands", [], "", "this command list (= :help commands)", "HELP"),
        new("quit", ["q", "exit"], "", "quit readmd", "HELP"),
    ];

    /// <summary>Canonical name for a typed command word (alias-aware), or null when unknown.</summary>
    internal static string? ResolveCommand(string word)
    {
        foreach (var c in CommandCatalog)
            if (c.AllNames.Contains(word, StringComparer.OrdinalIgnoreCase)) return c.Name;
        return null;
    }

    /// <summary>Tab completion for the command word: an exact name or alias expands to the canonical command, else a unique prefix (names before aliases).</summary>
    internal static string CompleteCommand(string text)
    {
        if (text.Contains(' ')) return text;
        var word = text;
        if (word.Length == 0) return text;
        if (ResolveCommand(word) is { } exact) return exact + " ";
        var names = CommandCatalog.Select(c => c.Name).Where(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (names.Count == 1) return names[0] + " ";
        if (names.Count > 1) return text;
        var aliases = CommandCatalog.SelectMany(c => c.Aliases).Where(a => a.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        return aliases.Count == 1 ? aliases[0] + " " : text;
    }

    /// <summary>Executes a <c>:</c> command. Unknown commands report an error in the status bar.</summary>
    internal void RunCommand(string command)
    {
        if (command.Length == 0) return;
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";

        // :123 → go to source line 123 (the first display line laid out from it); slide 123 when presenting.
        if (int.TryParse(name, out var lineNo))
        {
            if (_slideMode) GoToSlide(lineNo); else GoToSourceLine(lineNo);
            return;
        }
        switch (ResolveCommand(name))
        {
            case "quit": _running = false; return;
            case "edit":
                if (arg.Length == 0) { SetStatus("Usage: :e <file[#section] or url>"); return; }
                OpenTarget(arg);
                return;
            case "toc": OpenToc(); return;
            case "nohlsearch": ClearSearchAndSelection(); return;
            case "export": StartExport(arg); return;
            case "help": OpenHelp(arg.ToLowerInvariant() is "commands" or "command" or "cmd" or ":" ? 1 : 0); return;
            case "commands": OpenHelp(1); return;
            case "fold": FoldActive(FoldOp.Close); return;
            case "unfold": FoldActive(FoldOp.Open); return;
            case "foldall": FoldAll(true); return;
            case "unfoldall": FoldAll(false); return;
            case "reload": _ = LoadAsync(_currentPath, pushHistory: false); SetStatus("Reloaded"); return;
            case "copy": if (arg == "all") YankDocument(); else YankActiveBlock(); return;
            case "watch": SetWatch(true); return;
            case "nowatch": SetWatch(false); return;
            case "follow": ToggleFollow(); return;
            case "theme": SetTheme(arg); return;
            case "graphics": SetGraphics(arg); return;
            case "width": SetWidth(arg); return;
            case "ascii": SetAscii(arg is "" or "on" or "true" or "1"); return;
            case "bookmark": if (arg.Length == 1) SetBookmark(arg[0]); else SetStatus("Usage: :mark a"); return;
            case "marks": ListBookmarks(); return;
            case "goto": if (int.TryParse(arg, out var n)) GoToSourceLine(n); else if (arg.Length == 1) GoBookmark(arg[0]); else SetStatus("Usage: :goto <line|mark>"); return;
            case "browser": OpenInBrowser(); return;
            case "source": ToggleDiagramSource(); return;
            case "zoom": if (int.TryParse(arg, out var z)) { ZoomDiagrams(z - _diagramZoom); } else ResetZoom(); return;
            case "slides": if (arg is "off" or "0") { if (_slideMode) ExitSlides(); } else if (arg is "on" or "1") { if (!_slideMode) EnterSlides(); } else ToggleSlides(); return;
            case "slide": if (!_slideMode) EnterSlides(); if (int.TryParse(arg, out var sn)) GoToSlide(sn); return;
            case "notes": if (_slideMode) { _showNotes = !_showNotes; _dirty = true; } else SetStatus("Notes are shown in slide mode (:slides)"); return;
            case "set":
            {
                // :set key=value or :set key value
                var kv = arg.Split(['=', ' '], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (kv.Length == 0) { SetStatus("Usage: :set theme=dark | graphics=kitty | width=100 | ascii=on | wheel=5"); return; }
                var value = kv.Length > 1 ? kv[1] : "";
                switch (kv[0].ToLowerInvariant())
                {
                    case "theme": SetTheme(value); return;
                    case "graphics": SetGraphics(value); return;
                    case "width" or "maxwidth": SetWidth(value); return;
                    case "ascii": SetAscii(value is "" or "on" or "true" or "1"); return;
                    case "wheel" or "wheellines": if (int.TryParse(value, out var wl)) { _wheelLines = Math.Clamp(wl, 1, 20); SetStatus($"wheelLines={_wheelLines}"); } return;
                    case "background" or "bg": if ((value is "solid") != _solidBackground) ToggleSolidBackground(); return;
                    case "linenumbers" or "nu": SetStatus("Line numbers are a per-fence option (nonums / showLineNumbers) or config lineNumbers"); return;
                    default: SetStatus("Unknown option: " + kv[0]); return;
                }
            }
            default:
                SetStatus("Unknown command: " + command + "  (:help commands lists them)");
                return;
        }
    }

    /// <summary>Opens the help overlay on a page: 0 = keybindings, 1 = commands.</summary>
    private void OpenHelp(int page)
    {
        _helpMode = true;
        _helpPage = page;
        _helpScroll = 0;
        _dirty = true;
    }

    private void GoToSourceLine(int sourceLine)
    {
        int best = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            int sl = _lines[i].SourceLine;
            if (sl <= 0) continue;
            if (sl >= sourceLine) { best = i; break; }
            best = i;
        }
        if (best < 0) { SetStatus("No such line"); return; }
        ScrollTo(best);
    }

    private void OpenTarget(string target)
    {
        if (LinkResolver.IsExternal(target)) { OpenUrl(target); SetStatus("Opened: " + target); return; }
        // ":e guide.md#section" — a fragment selects the heading/anchor to land on; ":e #section" stays in this file.
        string? anchor = null;
        int hash = target.IndexOf('#');
        if (hash >= 0) { anchor = target[(hash + 1)..]; target = target[..hash]; }
        if (target.Length == 0)
        {
            if (anchor is not null) { int line = FindAnchorLine(anchor); if (line >= 0) ScrollTo(line); else SetStatus("No such section: #" + anchor); }
            return;
        }
        var path = Path.GetFullPath(target, Path.GetDirectoryName(_currentPath) ?? ".");
        if (!File.Exists(path)) { SetStatus("Not found: " + target); return; }
        if (LinkResolver.IsMarkdownFile(path)) NavigateTo(path, string.IsNullOrEmpty(anchor) ? null : anchor);
        else { OpenUrl(path); SetStatus("Opened: " + Path.GetFileName(path)); }
    }

    private void SetTheme(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { ToggleTheme(); return; }
        var theme = FindTheme(name);
        if (theme is null)
        {
            var known = string.Join(", ", ThemeCycle.Take(12));
            SetStatus($"Unknown theme: {name}  (themes: {known})", 4);
            return;
        }
        ApplyTheme(theme);
    }

    private void ApplyTheme(TerminalTheme theme)
    {
        if (ReferenceEquals(theme, _theme)) return;
        lock (_stateLock)
        {
            _theme = theme;
            _diagramTheme = _theme.IsDark ? DiagramTheme.Dark : DiagramTheme.Light;
            // Diagram/image colors are theme-dependent: drop the rendered-bitmap caches so they
            // re-encode for the new theme, but KEEP the source results so images stay on screen
            // (the diagrams are re-requested for the new theme below and swap in when ready).
            ClearDiagramCaches();
            _forceHardClear = true;
            Relayout();
            foreach (var key in _pendingDiagrams.Keys) _diagramRequested.Remove(key);
        }
        KickoffDiagramRenders(forceDiagramRerender: true);
        _ = CodeHighlighter.WarmUpAsync(_theme);
        SetStatus("Theme: " + (_theme.Name.Length > 0 ? _theme.Name : (_theme.IsDark ? "dark" : "light")), 1.5);
    }
    private void SetGraphics(string mode)
    {
        var parsed = TerminalCapabilities.Parse(mode);
        if (parsed is null) { SetStatus("graphics: sixel | kitty | iterm2 | half-block | none"); return; }
        lock (_stateLock)
        {
            _graphicsMode = parsed.Value;
            ClearDiagramCaches();
            foreach (var (key, result) in _diagramResults)
                if (result.Status == DiagramStatus.Ready) ReserveDiagramRows(key, result);
            _forceHardClear = true;
            _dirty = true;
        }
        SetStatus("Graphics: " + parsed.Value, 1.5);
    }

    private int _maxWidthOverride = -1;

    private void SetWidth(string value)
    {
        if (!int.TryParse(value, out var w) || (w != 0 && w < 20)) { SetStatus("Usage: :width <columns> (0 = full)"); return; }
        lock (_stateLock)
        {
            _maxWidthOverride = w;
            ClearDiagramCaches();
            _forceHardClear = true;
            Relayout();
        }
        SetStatus(w == 0 ? "Width: full" : $"Width: {w}", 1.5);
    }

    private void SetAscii(bool on)
    {
        if (Glyphs.Ascii == on) return;
        lock (_stateLock)
        {
            Glyphs.Ascii = on;
            _forceHardClear = true;
            Relayout();
        }
        SetStatus(on ? "ASCII glyphs" : "Unicode glyphs", 1.5);
    }

    // ---------------- export ----------------
    private void ExportPrompt()
    {
        var suggested = Path.ChangeExtension(Path.GetFileName(_currentPath), ".html");
        EnterCommandMode("export " + suggested);
    }

    private void StartExport(string arg)
    {
        if (_options.Export is null) { SetStatus("Export is not available in this mode"); return; }
        var target = arg.Length == 0 ? Path.ChangeExtension(_currentPath, ".html") : Path.GetFullPath(arg, Path.GetDirectoryName(_currentPath) ?? ".");
        SetStatus("Exporting " + Path.GetFileName(target) + "…", 30);
        var source = _currentPath;
        _ = Task.Run(async () =>
        {
            try
            {
                var error = await _options.Export(source, target);
                SetStatus(error is null ? "Wrote " + target : "Export failed: " + error, 6);
            }
            catch (Exception ex) { SetStatus("Export failed: " + ex.Message, 6); }
        });
    }

    // ---------------- TOC sidebar ----------------
    private bool _sidebar;
    private const int SidebarMax = 36;

    /// <summary>Columns taken by the TOC sidebar (0 when hidden or when the terminal is too narrow).</summary>
    private int SidebarWidth
    {
        get
        {
            if (!_sidebar || _toc.Count == 0) return 0;
            int w = Math.Min(SidebarMax, _screen.Width / 3);
            return w < 16 ? 0 : w;
        }
    }

    private void ToggleTocSidebar()
    {
        if (_toc.Count == 0) { SetStatus("No headings"); return; }
        lock (_stateLock)
        {
            _sidebar = !_sidebar;
            ClearDiagramCaches();
            _forceHardClear = true;
            Relayout();
        }
        SetStatus(_sidebar ? "Sidebar on (T to hide, click an entry to jump)" : "Sidebar off", 2);
    }

    private void DrawSidebar()
    {
        int w = SidebarWidth;
        if (w == 0) return;
        var p = Palette();
        int height = ViewportHeight;
        int current = CurrentSectionIndex(_toc);
        // Keep the current section visible: scroll the list so it sits in the upper third.
        int start = Math.Max(0, Math.Min(current - height / 3, Math.Max(0, _toc.Count - height)));
        for (int row = 0; row < height; row++)
        {
            int idx = start + row;
            _screen.MoveTo(row, 0).SetBackground(p.Panel);
            if (idx < _toc.Count)
            {
                var e = _toc[idx];
                bool cur = idx == current;
                _screen.SetForeground(cur ? p.Accent : p.Muted).Write(cur ? Glyphs.Current : " ");
                var label = new string(' ', Math.Min(6, (e.Level - 1))) + e.Title;
                label = TextWidth.TrimToWidth(label, w - 3);
                _screen.SetForeground(cur ? p.Fg : (e.Level == 1 ? p.Fg : p.Muted)).SetStyle(cur || e.Level == 1 ? CellStyle.Bold : CellStyle.None).Write(label);
                _screen.SetStyle(CellStyle.None);
                int used = 1 + TextWidth.Of(label);
                if (used < w - 1) _screen.Write(new string(' ', w - 1 - used));
            }
            else _screen.Write(new string(' ', w - 1));
            _screen.SetForeground(p.Border).Write(Glyphs.BoxV.ToString());
        }
        _screen.Reset();
    }

    /// <summary>Handles a click inside the sidebar; true if consumed.</summary>
    private bool SidebarClick(int screenRow, int screenCol)
    {
        int w = SidebarWidth;
        if (w == 0 || screenCol >= w || screenRow >= ViewportHeight) return false;
        int current = CurrentSectionIndex(_toc);
        int start = Math.Max(0, Math.Min(current - ViewportHeight / 3, Math.Max(0, _toc.Count - ViewportHeight)));
        int idx = start + screenRow;
        if (idx < 0 || idx >= _toc.Count) return true;
        int line = FindHeadingLine(_toc[idx].Id);
        if (line < 0 && UnfoldHeading(_toc[idx].Id)) line = FindHeadingLine(_toc[idx].Id);
        if (line >= 0) ScrollTo(line);
        return true;
    }

    // ---------------- bookmarks ----------------
    private readonly Dictionary<char, int> _bookmarks = [];
    private bool _awaitingMarkKey, _awaitingGoMarkKey;

    private void SetBookmarkPrompt() { _awaitingMarkKey = true; SetStatus("Mark: press a letter (a–z)", 5); }
    private void GoBookmarkPrompt() { _awaitingGoMarkKey = true; SetStatus("Go to mark: press a letter (' again = last position)", 5); }

    /// <summary>Consumes the key following M / ' ; true if handled.</summary>
    private bool HandleMarkKey(KeyEvent key)
    {
        if (!_awaitingMarkKey && !_awaitingGoMarkKey) return false;
        bool set = _awaitingMarkKey;
        _awaitingMarkKey = _awaitingGoMarkKey = false;
        if (key.Kind != KeyKind.Char) { SetStatus("Cancelled", 1); return true; }
        char c = char.ToLowerInvariant(key.Char);
        if (c == '\'' && !set) { if (_lastJump is { } lj) { int cur = _scroll; ScrollTo(lj); _lastJump = cur; } return true; }
        if (!char.IsAsciiLetter(c)) { SetStatus("Marks are letters a–z", 1.5); return true; }
        if (set) SetBookmark(c); else GoBookmark(c);
        return true;
    }

    private int? _lastJump;

    private void SetBookmark(char c)
    {
        _bookmarks[c] = _scroll;
        _state?.SetBookmark(_currentPath, c.ToString(), _scroll);
        SetStatus($"Mark '{c}' set", 1.5);
    }

    private void GoBookmark(char c)
    {
        if (!_bookmarks.TryGetValue(c, out var line))
        {
            var saved = _state?.Get(_currentPath)?.Bookmarks;
            if (saved is null || !saved.TryGetValue(c.ToString(), out line)) { SetStatus($"No mark '{c}'", 1.5); return; }
        }
        _lastJump = _scroll;
        ScrollTo(line);
    }

    private void ListBookmarks()
    {
        var all = new Dictionary<char, int>(_bookmarks);
        var saved = _state?.Get(_currentPath)?.Bookmarks;
        if (saved is not null) foreach (var (k, v) in saved) if (k.Length == 1) all.TryAdd(k[0], v);
        SetStatus(all.Count == 0 ? "No marks" : "Marks: " + string.Join("  ", all.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value + 1}")), 6);
    }

    // ---------------- watch / follow ----------------
    private bool _watchPaused;
    private bool _follow;

    private void ToggleWatch() => SetWatch(_watchPaused);

    private void SetWatch(bool on)
    {
        _watchPaused = !on;
        SetStatus(on ? "Live reload on" : "Live reload paused (W to resume)", 2);
    }

    private void ToggleFollow()
    {
        _follow = !_follow;
        if (_follow) { _watchPaused = false; ScrollTo(_lines.Count); }
        SetStatus(_follow ? $"Follow mode: jumping to the end on every change ({_keyMap.Label(EditorAction.Follow, ":follow")} to stop)" : "Follow mode off", 2.5);
    }

    // ---------------- persisted state ----------------
    private StateStore? _state;

    private void RestoreSavedPosition()
    {
        if (_state is null) return;
        var saved = _state.Get(_currentPath);
        if (saved is null) return;
        lock (_stateLock)
        {
            int target = -1;
            if (!string.IsNullOrEmpty(saved.TopText))
                for (int i = 0; i < _lines.Count; i++) if (_lines[i].PlainText == saved.TopText) { target = i; break; }
            if (target < 0 && saved.SourceLine > 0)
            {
                int best = -1, bestDist = int.MaxValue;
                for (int i = 0; i < _lines.Count; i++)
                {
                    int sl = _lines[i].SourceLine;
                    if (sl <= 0) continue;
                    int d = Math.Abs(sl - saved.SourceLine);
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                target = best;
            }
            if (target < 0) target = saved.Scroll;
            _scroll = Math.Clamp(target, 0, Math.Max(0, _lines.Count - ViewportHeight));
            _dirty = true;
        }
        if (_scroll > 0) SetStatus($"Resumed at {(int)(100.0 * _scroll / Math.Max(1, _lines.Count - ViewportHeight))}% (gg for the top)", 3);
    }

    private void SavePosition()
    {
        if (_state is null) return;
        var anchor = TopAnchor();
        _state.SetPosition(_currentPath, _scroll, anchor?.Text, anchor?.SourceLine ?? 0);
        foreach (var q in _searchHistory) _state.AddSearch(q);
        _state.Save();
    }
}
