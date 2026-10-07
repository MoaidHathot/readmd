using Markdig;
using Readmd.Core;

namespace Readmd.Terminal;

public sealed class TerminalViewerOptions
{
    public required string FilePath { get; init; }
    public DiagramTheme DiagramTheme { get; init; } = DiagramTheme.Dark;
    public bool DarkTerminal { get; init; } = true;
    public string? Root { get; init; }

    /// <summary>
    /// When true, readmd paints a solid background behind the document (overriding terminal
    /// transparency, like OpenCode). When false, the terminal's own background shows through.
    /// </summary>
    public bool SolidBackground { get; init; }

    /// <summary>Optional custom color palette (from config). When set, overrides the dark/light default.</summary>
    public TerminalTheme? Theme { get; init; }

    /// <summary>Optional key bindings (from config). Defaults to the built-in bindings.</summary>
    public KeyMap? KeyMap { get; init; }

    /// <summary>Inline-graphics mode: how diagrams/images are drawn (Sixel, half-block, or none).</summary>
    public GraphicsMode GraphicsMode { get; init; } = GraphicsMode.Sixel;

    /// <summary>Invoked when the user presses 'o' to open the current file in the browser.</summary>
    public Func<string, Task>? OpenInBrowser { get; init; }

    /// <summary>Maximum reading width in columns (0 = use the full terminal width). Narrower content is centred.</summary>
    public int MaxWidth { get; init; }

    /// <summary>Show line numbers in code blocks (a fence's own nonums/showLineNumbers overrides).</summary>
    public bool LineNumbers { get; init; } = true;

    /// <summary>Tab stop used when expanding tabs in code blocks.</summary>
    public int TabSize { get; init; } = 4;

    /// <summary>Apply typographic replacements (smart quotes, dashes, ellipses).</summary>
    public bool SmartPunctuation { get; init; }

    /// <summary>Ask before opening external (http/mailto) links; Ctrl+click always skips the prompt.</summary>
    public bool ConfirmExternalLinks { get; init; } = true;

    /// <summary>The graphics mode was auto-detected (so terminal probe replies may upgrade it).</summary>
    public bool GraphicsAuto { get; init; }

    /// <summary>The theme was "auto" (so the terminal's reported background colour may pick dark/light).</summary>
    public bool ThemeAuto { get; init; }

    /// <summary>Colour depth for SGR output.</summary>
    public ColorMode Colors { get; init; } = ColorMode.TrueColor;

    /// <summary>Emit OSC 8 hyperlinks for links.</summary>
    public bool Hyperlinks { get; init; } = true;

    /// <summary>Lines scrolled per mouse-wheel notch.</summary>
    public int WheelLines { get; init; } = 3;

    /// <summary>Exports the current document: (sourcePath, outputPath) → error message or null.</summary>
    public Func<string, string, Task<string?>>? Export { get; init; }

    /// <summary>Name of the custom theme (for `:theme <name>`), when <see cref="Theme"/> comes from config.</summary>
    public string? ThemeName { get; init; }

    /// <summary>All named themes available to `:theme <name>` (built-in + config).</summary>
    public IReadOnlyDictionary<string, TerminalTheme>? NamedThemes { get; init; }

    /// <summary>Remember the reading position per file (and search history) across sessions.</summary>
    public bool RememberPosition { get; init; } = true;

    /// <summary>Override for the state file location (tests).</summary>
    public string? StatePath { get; init; }

    /// <summary>Show a scrollbar in the right-most column when the document is taller than the screen.</summary>
    public bool Scrollbar { get; init; } = true;

    /// <summary>Fetch remote (http/https) images. Off in --offline mode.</summary>
    public bool AllowNetwork { get; init; } = true;

    /// <summary>Live-reload when the file changes (W toggles it at runtime).</summary>
    public bool Watch { get; init; } = true;

    /// <summary>Open at this 1-based source line (`+N`).</summary>
    public int? StartLine { get; init; }

    /// <summary>Open with this search active and jump to the first match (`+/pattern`).</summary>
    public string? StartSearch { get; init; }

    /// <summary>Test harness: highlight code on the calling thread so frames are deterministic.</summary>
    internal bool SynchronousHighlighting { get; init; }
}

/// <summary>
/// The interactive terminal viewer: a hand-rolled render loop that owns the alternate screen,
/// renders the document to styled lines, draws diagrams inline via Sixel, and handles scrolling,
/// search, a table-of-contents overlay, link navigation with history, folding and live reload.
/// </summary>
/// <remarks>
/// Layout model: the renderer produces <see cref="_allLines"/> (the complete document). The viewer
/// <em>projects</em> that into <see cref="_lines"/> — the visible list — by collapsing folded regions
/// into one placeholder row each and then inserting blank reservation rows under image/diagram
/// anchors. Everything positional (scroll, drawing, clicks) works on <see cref="_lines"/>; anything
/// that must survive re-projection (search hits, the active block) references the
/// <see cref="DisplayLine"/> objects themselves rather than indices.
/// </remarks>
public sealed partial class TerminalViewer : IAsyncDisposable
{
    private readonly TerminalViewerOptions _options;
    private readonly IDiagramRenderer _diagrams;
    private readonly LinkResolver _resolver;
    private TerminalTheme _theme;
    private KeyMap _keyMap = KeyMap.Default;
    private GraphicsMode _graphicsMode = GraphicsMode.Sixel;
    private bool _solidBackground;
    private DiagramTheme _diagramTheme;
    private readonly DocumentWatcher? _watcher;

    private AnsiScreen _screen = null!;
    private readonly object _stateLock = new();

    // Document state
    private string _currentPath;
    private string _currentMarkdown = "";
    private IReadOnlyList<DisplayLine> _allLines = [];
    private List<DisplayLine> _lines = [];
    private IReadOnlyList<TerminalLink> _links = [];
    // Anchor id -> line index for in-document "#fragment" navigation (heading ids + explicit
    // HTML <a id>/<a name> anchors). Replaced on every (re)parse alongside _lines.
    private IReadOnlyDictionary<string, int> _anchors = new Dictionary<string, int>();
    private IReadOnlyList<TocEntry> _toc = [];
    private IReadOnlyDictionary<int, BlockInfo> _blocks = new Dictionary<int, BlockInfo>();
    private IReadOnlyList<FoldRegion> _foldRegions = [];
    private readonly HashSet<int> _folded = [];
    // User fold decisions keyed by header text so they survive a live reload (fold ids are
    // regenerated on every render).
    private readonly Dictionary<string, bool> _foldOverrides = new(StringComparer.Ordinal);
    private string _title = "";

    // View state
    private int _scroll;
    private bool _dirty = true;
    private volatile bool _running = true;
    private readonly CancellationTokenSource _lifetimeCts = new();   // cancels in-flight renders on exit
    private bool _selectionMode;   // mark mode: readmd tracks a drag selection and copies it on right-click
    private string? _pendingExternalUrl;   // remote URL awaiting y/N confirmation before opening

    // Text selection in mark mode. Anchor/Cursor are (document line index, display column). A null
    // anchor means no active selection.
    private (int Line, int Col)? _selAnchor;
    private (int Line, int Col) _selCursor;

    // Navigation history
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    // Search state. Hits reference line OBJECTS so re-projection (folds, image rows) can't stale them.
    private bool _searchMode;
    private string _searchQuery = "";
    private readonly List<(DisplayLine Line, int Col, int Len)> _searchHits = [];
    private readonly HashSet<DisplayLine> _hitLines = [];
    private int _searchHitIndex = -1;

    // TOC overlay
    private bool _tocMode;
    private int _tocIndex;

    // Help overlay
    private bool _helpMode;

    // Focused single-image view (v / click a non-link image): renders ONE image/diagram scaled to
    // the full viewport with zoom + pan, and can hand it off to the OS image viewer or the browser.
    private bool _focusMode;
    private string? _focusKey;
    private int _focusZoom;       // 0 = fit-to-viewport; each step is +25%
    private int _focusPanRows;    // vertical pan (rows); positive reveals lower part of the image
    private int _focusPanCols;    // horizontal pan (cols); positive reveals the right part

    // Mouse drag-to-pan state for the focus view (grab and move the enlarged image).
    private bool _focusDragging;
    private int _focusDragStartRow, _focusDragStartCol;
    private int _focusDragStartPanRows, _focusDragStartPanCols;

    // On-demand high-resolution re-render of the focused diagram (mermaid is PNG-only, so zooming in
    // must re-render via the browser backend at a higher scale to stay crisp).
    private string? _focusHiResKey;          // key the hi-res result belongs to
    private DiagramResult? _focusHiResResult; // higher-resolution render of the focused diagram
    private double _focusHiResScale;         // scale bucket the hi-res was rendered at (1 = base)
    private double _focusHiResInFlight;      // scale currently being rendered (0 = none)

    // Diagram placement: key -> (startLine, rows). Images share the same result map + draw path.
    private readonly Dictionary<string, DiagramResult> _diagramResults = new();
    private readonly HashSet<string> _diagramRequested = [];
    private IReadOnlyDictionary<string, DiagramRequest> _pendingDiagrams = new Dictionary<string, DiagramRequest>();
    private IReadOnlyDictionary<string, string> _pendingImages = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, List<string>> _pendingImageGroups = new Dictionary<string, List<string>>();
    private readonly Readmd.Diagrams.ImageLoader _imageLoader;

    // Cell pixel geometry (estimated; refined from the terminal's reported window pixel size).
    private int _cellWidthPx = 10;
    private int _cellHeightPx = 20;
    private int _windowPixelWidth;   // from CSI 14t; 0 = unknown (use estimate)
    private int _windowPixelHeight;
    private int _diagramZoom;        // user zoom steps via Ctrl+wheel (-N..+N)

    private string? _statusMessage;
    private DateTime _statusUntil;

    public TerminalViewer(TerminalViewerOptions options, IDiagramRenderer diagrams)
    {
        _options = options;
        _diagrams = diagrams;
        _currentPath = Path.GetFullPath(options.FilePath);
        var root = options.Root is not null ? Path.GetFullPath(options.Root) : Path.GetDirectoryName(_currentPath)!;
        _resolver = new LinkResolver(root);
        _imageLoader = new Readmd.Diagrams.ImageLoader(root) { AllowRemote = options.AllowNetwork };
        _theme = options.Theme ?? TerminalTheme.For(options.DarkTerminal);
        _keyMap = options.KeyMap ?? KeyMap.Default;
        _graphicsMode = options.GraphicsMode;
        _solidBackground = options.SolidBackground;
        _diagramTheme = options.DiagramTheme;
        _watcher = new DocumentWatcher(_currentPath);
        _watcher.Changed += OnFileChanged;
        if (options.RememberPosition)
        {
            try { _state = new StateStore(options.StatePath); _searchHistory.AddRange(_state.SearchHistory); }
            catch { _state = null; }
        }
    }

    public async Task RunAsync()
    {
        _screen = new AnsiScreen { Colors = _options.Colors, Hyperlinks = _options.Hyperlinks };
        _wheelLines = Math.Max(1, _options.WheelLines);
        _watchPaused = !_options.Watch;
        try
        {
            // Paint a minimal frame immediately so the terminal doesn't sit on a blank alternate
            // screen while the first parse/layout runs (large documents can take a moment).
            DrawLoadingFrame();
            await LoadAsync(_currentPath, pushHistory: true);
            ApplyStartPosition();
            if (_statusMessage is null) SetStatus("press ? for help · f: link hints · Enter opens the first link", 5);
            RenderLoop();
            SavePosition();
        }
        finally
        {
            _screen.Dispose();
            AnsiScreen.RestoreInputMode();
        }
        await Task.CompletedTask;
    }

    /// <summary>Start position: an explicit +N / +/pattern wins over the remembered position.</summary>
    internal void ApplyStartPosition()
    {
        if (_options.StartLine is { } startLine) GoToSourceLine(startLine);
        else if (!string.IsNullOrEmpty(_options.StartSearch)) { _searchQuery = _options.StartSearch; RecomputeSearch(); }
        else RestoreSavedPosition();
    }

    private void DrawLoadingFrame()
    {
        try
        {
            _screen.BeginFrame();
            if (_solidBackground) _screen.FillBackground(_theme.Background, _screen.Height, _screen.Width);
            _screen.MoveTo(0, 0).SetForeground(_theme.Muted).Write("Loading " + Path.GetFileName(_currentPath) + "…");
            _screen.Reset();
            _screen.Flush();
        }
        catch { /* purely cosmetic */ }
    }

    // ---------------- loading & layout ----------------
    private async Task LoadAsync(string path, bool pushHistory)
    {
        string markdown;
        try { markdown = await DocumentWatcher.ReadWithRetryAsync(path); }
        catch (Exception ex) { SetStatus("Failed to read: " + ex.Message); return; }
        LoadMarkdown(path, markdown, pushHistory);
    }

    /// <summary>Installs a document from its source text (used by LoadAsync and the test harness).</summary>
    internal void LoadMarkdown(string path, string markdown, bool pushHistory)
    {
        var parsed = ParseToLines(path, markdown);
        bool newFile = !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase) || _allLines.Count == 0;

        lock (_stateLock)
        {
            _currentPath = path;
            _currentMarkdown = markdown;
            if (newFile) _foldOverrides.Clear();
            ApplyParsed(parsed, keepScrollAnchor: !newFile);
            _diagramRequested.Clear();
            _dirty = true;
        }

        if (pushHistory)
        {
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(path);
            _historyIndex = _history.Count - 1;
        }

        _watcher?.Retarget(path);
        KickoffDiagramRenders();
        KickoffHighlights(parsed.PendingHighlights);
        // The layout changed under any active search: re-run it against the new lines (without
        // jumping the viewport) so highlights don't point at stale lines.
        if (_searchQuery.Length > 0) RecomputeSearch(jumpToFirst: false);
    }

    /// <summary>
    /// Installs a freshly parsed document (lines, links, anchors, TOC, folds, pending media) and
    /// projects it. When <paramref name="keepScrollAnchor"/> is set the viewport stays on the same
    /// content: the block at the top of the screen is located again in the new layout (a live
    /// reload that inserts text above the viewport no longer shifts what you were reading).
    /// Caller holds the state lock.
    /// </summary>
    private void ApplyParsed(ParsedDoc parsed, bool keepScrollAnchor)
    {
        var anchor = keepScrollAnchor ? TopAnchor() : null;
        _allLines = parsed.Lines;
        _links = parsed.Links;
        _anchors = parsed.Anchors;
        _toc = parsed.Toc;
        _title = parsed.Title;
        _blocks = parsed.Blocks;
        _foldRegions = parsed.Folds;
        _pendingDiagrams = parsed.Diagrams;
        _pendingImages = parsed.Images;
        _pendingImageGroups = parsed.ImageGroups;
        _tocIndex = Math.Clamp(_tocIndex, 0, Math.Max(0, _toc.Count - 1));
        ResetFoldState();
        Project(anchor);
    }

    /// <summary>Describes what is at the top of the viewport, for re-finding it after a layout change.</summary>
    private (string? HeadingId, int SourceLine, string Text, int Offset)? TopAnchor()
    {
        if (_lines.Count == 0) return null;
        int idx = Math.Clamp(_scroll, 0, _lines.Count - 1);
        // Prefer the nearest non-blank, non-reserved line at or below the top.
        for (int i = idx; i < Math.Min(_lines.Count, idx + 5); i++)
        {
            var l = _lines[i];
            if (l.Spans.Count == 0 || (l.DiagramKey?.StartsWith("reserve:") ?? false)) continue;
            return (l.HeadingId, l.SourceLine, l.PlainText, i - idx);
        }
        return (null, 0, "", 0);
    }

    /// <summary>Finds the best matching line for a saved anchor in the current projection.</summary>
    private int ResolveAnchor((string? HeadingId, int SourceLine, string Text, int Offset)? anchor)
    {
        if (anchor is not { } a) return _scroll;
        int found = -1;
        if (a.HeadingId is not null) found = FindHeadingLine(a.HeadingId);
        if (found < 0 && a.Text.Length > 0)
        {
            // Identical text is the strongest signal (the reader is looking at these words); among
            // duplicates prefer the one whose source line moved least.
            int best = -1, bestDist = int.MaxValue;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].PlainText != a.Text) continue;
                int d = Math.Abs(_lines[i].SourceLine - a.SourceLine);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            found = best;
        }
        if (found < 0 && a.SourceLine > 0)
        {
            int best = -1, bestDist = int.MaxValue;
            for (int i = 0; i < _lines.Count; i++)
            {
                int sl = _lines[i].SourceLine;
                if (sl <= 0) continue;
                int d = Math.Abs(sl - a.SourceLine);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            found = best;
        }
        return found < 0 ? _scroll : Math.Max(0, found - a.Offset);
    }

    private void ResetFoldState()
    {
        _folded.Clear();
        foreach (var region in _foldRegions)
        {
            string key = FoldKey(region);
            bool folded = !region.DefaultOpen;
            if (_foldOverrides.TryGetValue(key, out var o)) folded = o;
            if (folded) _folded.Add(region.Id);
        }
    }

    private string FoldKey(FoldRegion region) =>
        (region.IsHeading ? "h:" : "d:") + (region.HeaderLine < _allLines.Count ? _allLines[region.HeaderLine].PlainText.Trim() : region.Id.ToString());

    /// <summary>
    /// Builds the visible line list from the full layout: folded regions collapse into a single
    /// placeholder row, then reservation rows are re-inserted for every ready image/diagram. The
    /// scroll position is re-anchored to the content that was at the top of the viewport.
    /// </summary>
    private void Project((string? HeadingId, int SourceLine, string Text, int Offset)? anchor = null)
    {
        anchor ??= TopAnchor();
        var hidden = new List<(int Start, int End, FoldRegion Region)>();
        foreach (var region in _foldRegions)
            if (_folded.Contains(region.Id)) hidden.Add((region.StartLine, region.EndLine, region));
        hidden.Sort((a, b) => a.Start.CompareTo(b.Start));

        var lines = new List<DisplayLine>(_allLines.Count);
        int h = 0;
        for (int i = 0; i < _allLines.Count; i++)
        {
            while (h < hidden.Count && hidden[h].End < i) h++;
            if (h < hidden.Count && i >= hidden[h].Start && i <= hidden[h].End)
            {
                if (i == hidden[h].Start)
                {
                    int count = hidden[h].End - hidden[h].Start + 1;
                    lines.Add(FoldPlaceholder(hidden[h].Region, count));
                }
                continue;
            }
            lines.Add(_allLines[i]);
        }
        _lines = lines;

        foreach (var (key, result) in _diagramResults)
            if (result.Status == DiagramStatus.Ready)
                ReserveDiagramRows(key, result, adjustScroll: false);

        _scroll = Math.Clamp(ResolveAnchor(anchor), 0, Math.Max(0, _lines.Count - 1));
        _dirty = true;
    }

    private DisplayLine FoldPlaceholder(FoldRegion region, int count)
    {
        var line = new DisplayLine { FoldPlaceholder = region.Id, BlockId = region.HeaderLine < _allLines.Count ? _allLines[region.HeaderLine].BlockId : 0 };
        int indent = region.IsHeading ? 2 : 4;
        line.Spans.Add(new StyledSpan(new string(' ', indent)));
        line.Spans.Add(new StyledSpan(Glyphs.Fold, _theme.Accent));
        line.Spans.Add(new StyledSpan($"{count} line{(count == 1 ? "" : "s")} folded", _theme.Muted, CellStyle.Italic));
        line.Spans.Add(new StyledSpan("  (za / Enter to expand)", _theme.Muted, CellStyle.Dim));
        return line;
    }

    /// <summary>
    /// Re-lays out the current document from the in-memory source (no disk read) — used after a
    /// resize, theme change or explicit refresh — preserving folds and the scroll position.
    /// Caller holds the lock.
    /// </summary>
    private void Relayout()
    {
        ParsedDoc? parsed = null;
        try
        {
            parsed = ParseToLines(_currentPath, _currentMarkdown);
            ApplyParsed(parsed, keepScrollAnchor: true);
        }
        catch { /* keep old layout */ }
        _dirty = true;
        if (parsed is not null) KickoffHighlights(parsed.PendingHighlights);
    }

    // ---------------- background syntax highlighting ----------------
    // Code blocks are painted in plain colour on first layout; a single worker highlights them in
    // the background (TextMate grammar compilation is the slowest part of rendering a document) and
    // restyles the lines in place, nearest-to-viewport first. Results are cached, so reloads, theme
    // toggles and resizes of unchanged blocks are instant.
    private readonly object _highlightGate = new();
    private readonly Queue<(MarkdownTerminalRenderer.PendingHighlight Pending, List<DisplayLine> Lines, TerminalTheme Theme, int Width)> _highlightQueue = new();
    private bool _highlightWorkerRunning;

    private void KickoffHighlights(IReadOnlyList<MarkdownTerminalRenderer.PendingHighlight> pending)
    {
        if (pending.Count == 0 || _options.SynchronousHighlighting) { if (_options.SynchronousHighlighting) HighlightNow(pending); return; }
        List<(MarkdownTerminalRenderer.PendingHighlight, List<DisplayLine>, TerminalTheme, int)> items;
        lock (_stateLock)
        {
            // Capture the concrete DisplayLine objects now: even if the layout is replaced before the
            // worker gets to them, restyling the old objects is harmless and the result is cached.
            var byBlock = new Dictionary<int, List<DisplayLine>>();
            var firstIndex = new Dictionary<int, int>();
            for (int i = 0; i < _allLines.Count; i++)
            {
                int id = _allLines[i].BlockId;
                if (id == 0 || _allLines[i].Kind != BlockKind.Code) continue;
                if (!byBlock.TryGetValue(id, out var list)) { byBlock[id] = list = []; firstIndex[id] = i; }
                list.Add(_allLines[i]);
            }
            int scroll = _scroll;
            int width = ContentWidth;
            items = pending
                .Where(p => byBlock.ContainsKey(p.BlockId))
                .OrderBy(p => Math.Abs(firstIndex[p.BlockId] - scroll))
                .Select(p => (p, byBlock[p.BlockId], _theme, width))
                .ToList();
        }

        lock (_highlightGate)
        {
            foreach (var item in items) _highlightQueue.Enqueue(item);
            if (_highlightWorkerRunning) return;
            _highlightWorkerRunning = true;
        }
        _ = Task.Run(HighlightWorker);
    }

    // Test harness path: highlight on the calling thread so frames are deterministic.
    private void HighlightNow(IReadOnlyList<MarkdownTerminalRenderer.PendingHighlight> pending)
    {
        foreach (var p in pending)
        {
            var lines = _allLines.Where(l => l.BlockId == p.BlockId).ToList();
            var highlighted = CodeHighlighter.Highlight(p.Code, p.Language, _theme);
            MarkdownTerminalRenderer.ApplyHighlight(lines, p, highlighted, _theme, ContentWidth, _options.LineNumbers);
        }
    }

    private void HighlightWorker()
    {
        while (true)
        {
            (MarkdownTerminalRenderer.PendingHighlight Pending, List<DisplayLine> Lines, TerminalTheme Theme, int Width) item;
            lock (_highlightGate)
            {
                if (_highlightQueue.Count == 0 || _lifetimeCts.IsCancellationRequested)
                {
                    _highlightWorkerRunning = false;
                    return;
                }
                item = _highlightQueue.Dequeue();
            }
            try
            {
                var highlighted = CodeHighlighter.Highlight(item.Pending.Code, item.Pending.Language, item.Theme);
                lock (_stateLock)
                {
                    MarkdownTerminalRenderer.ApplyHighlight(item.Lines, item.Pending, highlighted, item.Theme, item.Width, _options.LineNumbers);
                    _dirty = true;
                }
            }
            catch { /* leave the block plain */ }
        }
    }

    private sealed record ParsedDoc(
        IReadOnlyList<DisplayLine> Lines,
        IReadOnlyList<TerminalLink> Links,
        IReadOnlyDictionary<string, int> Anchors,
        IReadOnlyList<TocEntry> Toc,
        string Title,
        IReadOnlyDictionary<string, DiagramRequest> Diagrams,
        IReadOnlyDictionary<string, string> Images,
        IReadOnlyDictionary<string, List<string>> ImageGroups,
        IReadOnlyList<MarkdownTerminalRenderer.PendingHighlight> PendingHighlights,
        IReadOnlyDictionary<int, BlockInfo> Blocks,
        IReadOnlyList<FoldRegion> Folds);

    private ParsedDoc ParseToLines(string path, string markdown)
    {
        // Parse once with the shared terminal pipeline and derive TOC/title/front-matter from that
        // same AST — no second parse, and no HTML render that the terminal never displays.
        var (body, toml) = FrontMatter.StripToml(markdown);
        var ast = Timing.Measure("parse", () => Markdig.Markdown.Parse(body, TerminalPipeline.For(_options.SmartPunctuation)));
        var meta = MarkdownRenderer.ExtractMetadata(ast, path, toml);
        // Code is highlighted in the background (see KickoffHighlights) so the first paint is fast.
        var renderer = new MarkdownTerminalRenderer(_theme, ContentWidth) { DeferHighlighting = true, LineNumbers = _options.LineNumbers, TabSize = _options.TabSize };
        var result = Timing.Measure("layout", () => renderer.Render(ast, meta.Toc, meta.FrontMatter, body));
        return new ParsedDoc(result.Lines, result.Links, result.Anchors, meta.Toc, meta.Title, renderer.PendingDiagrams,
            renderer.PendingImages, renderer.PendingImageGroups, renderer.PendingHighlights, result.Blocks, result.Folds);
    }

    /// <summary>
    /// The width content is laid out to: the terminal width (minus the scrollbar column and any TOC
    /// sidebar) capped by the configured reading width, if any. Content narrower than the remaining
    /// space is centred.
    /// </summary>
    private int ContentWidth
    {
        get
        {
            int full = Math.Max(20, _screen.Width - 1 - SidebarWidth);
            int max = _maxWidthOverride >= 0 ? _maxWidthOverride : _options.MaxWidth;
            return max > 0 ? Math.Min(full, max) : full;
        }
    }

    /// <summary>Left margin (columns): the sidebar plus centring of a width-capped document.</summary>
    private int ContentOffset
    {
        get
        {
            int full = Math.Max(20, _screen.Width - 1 - SidebarWidth);
            int content = ContentWidth;
            return SidebarWidth + (content < full ? (full - content) / 2 : 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }
        _watcher?.Dispose();
        if (_imageLoader is not null) await _imageLoader.DisposeAsync();
        _lifetimeCts.Dispose();
    }

    private void SetStatus(string message, double seconds = 3)
    {
        _statusMessage = message;
        _statusUntil = DateTime.UtcNow.AddSeconds(seconds);
        _dirty = true;
    }

    private void OnFileChanged(string path)
    {
        if (_watchPaused) return;
        _ = Task.Run(async () =>
        {
            await LoadAsync(path, pushHistory: false);
            if (_follow) lock (_stateLock) { _scroll = Math.Max(0, _lines.Count - ViewportHeight); _dirty = true; }
            SetStatus(_follow ? "Reloaded (following)" : "Reloaded", 1.2);
        });
    }
}
