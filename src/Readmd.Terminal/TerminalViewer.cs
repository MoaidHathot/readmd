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
}

/// <summary>
/// The interactive terminal viewer: a hand-rolled render loop that owns the alternate screen,
/// renders the document to styled lines, draws diagrams inline via Sixel, and handles scrolling,
/// search, a table-of-contents overlay, link navigation with history, and live reload.
/// </summary>
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
    private readonly DocumentWatcher _watcher;

    private AnsiScreen _screen = null!;
    private readonly object _stateLock = new();

    // Document state
    private string _currentPath;
    private IReadOnlyList<DisplayLine> _lines = [];
    private IReadOnlyList<TerminalLink> _links = [];
    // Anchor id -> line index for in-document "#fragment" navigation (heading ids + explicit
    // HTML <a id>/<a name> anchors). Replaced on every (re)parse alongside _lines.
    private IReadOnlyDictionary<string, int> _anchors = new Dictionary<string, int>();
    private IReadOnlyList<TocEntry> _toc = [];
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

    // Pending vim 'g' prefix (for the gg = go-to-top motion).
    private bool _pendingGPrefix;
    private DateTime _pendingGUntil;

    // Navigation history
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    // Search state
    private bool _searchMode;
    private string _searchQuery = "";
    private readonly List<(int Line, int Col, int Len)> _searchHits = [];
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
        _imageLoader = new Readmd.Diagrams.ImageLoader(root);
        _theme = options.Theme ?? TerminalTheme.For(options.DarkTerminal);
        _keyMap = options.KeyMap ?? KeyMap.Default;
        _graphicsMode = options.GraphicsMode;
        _solidBackground = options.SolidBackground;
        _diagramTheme = options.DiagramTheme;
        _watcher = new DocumentWatcher(_currentPath);
        _watcher.Changed += OnFileChanged;
    }

    public async Task RunAsync()
    {
        _screen = new AnsiScreen();
        try
        {
            // Paint a minimal frame immediately so the terminal doesn't sit on a blank alternate
            // screen while the first parse/layout runs (large documents can take a moment).
            DrawLoadingFrame();
            await LoadAsync(_currentPath, pushHistory: true);
            SetStatus("press ? for help · numbered links: press 1-9 · Enter opens the first link", 5);
            RenderLoop();
        }
        finally
        {
            _screen.Dispose();
            AnsiScreen.RestoreInputMode();
        }
        await Task.CompletedTask;
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

        var parsed = ParseToLines(path, markdown);

        lock (_stateLock)
        {
            _currentPath = path;
            _currentMarkdown = markdown;
            ApplyParsed(parsed);
            _diagramRequested.Clear();
            // keep cached diagram results across reloads (cache is keyed by content hash)
            _scroll = Math.Min(_scroll, Math.Max(0, _lines.Count - 1));
            _dirty = true;
        }

        if (pushHistory)
        {
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(path);
            _historyIndex = _history.Count - 1;
        }

        _watcher.Retarget(path);
        KickoffDiagramRenders();
        KickoffHighlights(parsed.PendingHighlights);
        // The layout changed under any active search: re-run it against the new lines (without
        // jumping the viewport) so highlights don't point at stale line indices.
        if (_searchQuery.Length > 0) RecomputeSearch(jumpToFirst: false);
    }

    /// <summary>Installs a freshly parsed document (lines, links, anchors, TOC, pending media). Caller holds the state lock.</summary>
    private void ApplyParsed(ParsedDoc parsed)
    {
        _lines = parsed.Lines;
        _links = parsed.Links;
        _anchors = parsed.Anchors;
        _toc = parsed.Toc;
        _title = parsed.Title;
        _pendingDiagrams = parsed.Diagrams;
        _pendingImages = parsed.Images;
        _pendingImageGroups = parsed.ImageGroups;
        _tocIndex = Math.Clamp(_tocIndex, 0, Math.Max(0, _toc.Count - 1));
    }

    /// <summary>
    /// Re-lays out the current document from the in-memory source (no disk read) — used after a
    /// resize, theme change or explicit refresh — and re-reserves rows for every already-rendered
    /// diagram/image so the layout stays intact. Preserves the scroll position. Caller holds the lock.
    /// </summary>
    private void Relayout()
    {
        int savedScroll = _scroll;
        ParsedDoc? parsed = null;
        try
        {
            parsed = ParseToLines(_currentPath, _currentMarkdown);
            ApplyParsed(parsed);
        }
        catch { /* keep old layout */ }

        foreach (var (key, result) in _diagramResults)
            if (result.Status == DiagramStatus.Ready)
                ReserveDiagramRows(key, result, adjustScroll: false);

        _scroll = Math.Clamp(savedScroll, 0, Math.Max(0, _lines.Count - 1));
        _dirty = true;
        if (parsed is not null) KickoffHighlights(parsed.PendingHighlights);
    }

    private string _currentMarkdown = "";

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
        if (pending.Count == 0) return;
        List<(MarkdownTerminalRenderer.PendingHighlight, List<DisplayLine>, TerminalTheme, int)> items;
        lock (_stateLock)
        {
            // Capture the concrete DisplayLine objects now: even if the layout is replaced before the
            // worker gets to them, restyling the old objects is harmless and the result is cached.
            var byBlock = new Dictionary<int, List<DisplayLine>>();
            var firstIndex = new Dictionary<int, int>();
            for (int i = 0; i < _lines.Count; i++)
            {
                int id = _lines[i].BlockId;
                if (id == 0) continue;
                if (!byBlock.TryGetValue(id, out var list)) { byBlock[id] = list = []; firstIndex[id] = i; }
                list.Add(_lines[i]);
            }
            int scroll = _scroll;
            int width = _screen.Width - 1;
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
                    MarkdownTerminalRenderer.ApplyHighlight(item.Lines, item.Pending, highlighted, item.Theme, item.Width);
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
        IReadOnlyList<MarkdownTerminalRenderer.PendingHighlight> PendingHighlights);

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
            renderer.PendingImages, renderer.PendingImageGroups, renderer.PendingHighlights);
    }

    /// <summary>
    /// The width content is laid out to: the terminal width (minus one column) capped by the
    /// configured reading width, if any. Content narrower than the terminal is centred.
    /// </summary>
    private int ContentWidth
    {
        get
        {
            int full = _screen.Width - 1;
            int max = _options.MaxWidth;
            return max > 0 ? Math.Min(full, max) : full;
        }
    }

    /// <summary>Left margin (columns) that centres a width-capped document in the terminal.</summary>
    private int ContentOffset
    {
        get
        {
            int full = _screen.Width - 1;
            int content = ContentWidth;
            return content < full ? (full - content) / 2 : 0;
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
        _ = Task.Run(async () =>
        {
            await LoadAsync(path, pushHistory: false);
            SetStatus("Reloaded", 1.2);
        });
    }
}
