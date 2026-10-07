using System.Collections.Concurrent;
using Readmd.Core;

namespace Readmd.Diagrams;

public sealed class DiagramRendererOptions
{
    /// <summary>When true, mermaid is NOT rendered via headless browser (skips the Chromium download).</summary>
    public bool BestEffort { get; init; }

    /// <summary>Optional explicit path to the <c>d2</c> binary; defaults to "d2" on PATH.</summary>
    public string? D2Path { get; init; }

    /// <summary>
    /// Optional path to a local <c>mmdc</c> (mermaid-cli). When set (or when "mmdc" is found on
    /// PATH), mermaid is rendered via mmdc instead of the bundled Playwright/Chromium, avoiding the
    /// one-time browser download. A null value still lets readmd auto-detect "mmdc" on PATH.
    /// </summary>
    public string? MermaidCliPath { get; init; }

    /// <summary>Optional explicit path to the Graphviz <c>dot</c> binary; defaults to "dot" on PATH.</summary>
    public string? GraphvizPath { get; init; }

    /// <summary>Optional explicit path to the <c>plantuml</c> launcher; defaults to "plantuml" on PATH.</summary>
    public string? PlantUmlPath { get; init; }

    /// <summary>Directory for the on-disk diagram cache.</summary>
    public string CacheDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "readmd", "diagram-cache");

    /// <summary>
    /// Kroki server used for diagram kinds without a local renderer and as a fallback when a local
    /// tool is missing. Null/empty = https://kroki.io. Ignored when <see cref="AllowNetwork"/> is false.
    /// </summary>
    public string? KrokiUrl { get; init; }

    /// <summary>Allow network access (Kroki). False in --offline mode.</summary>
    public bool AllowNetwork { get; init; } = true;

    /// <summary>Use Kroki when a local tool is missing (default true). False = local tools only.</summary>
    public bool KrokiFallback { get; init; } = true;

    /// <summary>Called once, the first time a diagram is rendered through Kroki (so the UI can mention it).</summary>
    public Action<string>? OnKrokiUsed { get; init; }
}

/// <summary>
/// The composite diagram renderer used by both front-ends. Renders D2/Graphviz/PlantUML via their
/// local tools, mermaid via a local <c>mmdc</c> when available, otherwise headless Chromium; every
/// other kind — and any kind whose local tool is missing — goes through Kroki when the network is
/// allowed. Results are cached by content hash and duplicate concurrent requests are coalesced.
/// </summary>
public sealed class DiagramRenderer : IDiagramRenderer
{
    private readonly DiagramCache _cache;
    private readonly D2Renderer _d2;
    private readonly GraphvizRenderer _graphviz;
    private readonly PlantUmlRenderer _plantUml;
    private readonly bool _bestEffort;
    private readonly MermaidCliRenderer _mmdcCandidate;
    private readonly KrokiRenderer? _kroki;
    private readonly DiagramRendererOptions _options;
    private readonly ConcurrentDictionary<string, Task<DiagramResult>> _inflight = new();
    private readonly ConcurrentDictionary<string, string?> _textCache = new();
    // Cap concurrent renders so a document with many diagrams can't spawn an unbounded number of
    // Chromium pages / d2 processes at once (memory spike protection).
    private readonly SemaphoreSlim _renderGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private int _krokiAnnounced;

    // The mermaid backend is selected lazily on the FIRST mermaid render, not in the constructor:
    // detecting mmdc spawns a child process (an npm shim → pwsh/Node) that can cost ~1s, and doing
    // that at construction time stalled every readmd startup (including --print/--export and docs
    // with no diagrams at all). The selection still does the explicit availability pre-check — it's
    // just deferred and computed at most once.
    private readonly object _mermaidGate = new();
    private bool _mermaidSelected;
    private MermaidCliRenderer? _mmdc;
    private MermaidRenderer? _mermaid;

    // Local-tool availability, probed once per tool on first use.
    private readonly ConcurrentDictionary<string, bool> _toolAvailable = new();

    public DiagramRenderer(DiagramRendererOptions? options = null)
    {
        options ??= new DiagramRendererOptions();
        _options = options;
        _bestEffort = options.BestEffort;
        _cache = new DiagramCache(options.CacheDirectory);
        _cache.EvictOldEntries();   // best-effort cleanup of orphaned/old cache files at startup
        _d2 = new D2Renderer(options.D2Path);
        _graphviz = new GraphvizRenderer(options.GraphvizPath);
        _plantUml = new PlantUmlRenderer(options.PlantUmlPath);
        _mmdcCandidate = new MermaidCliRenderer(options.MermaidCliPath);
        _kroki = options.AllowNetwork ? new KrokiRenderer(options.KrokiUrl) : null;
    }

    /// <summary>The Kroki endpoint in use (null when offline).</summary>
    public string? KrokiUrl => _kroki?.BaseUrl;

    // Chooses the mermaid backend once: prefer a local mmdc (no Chromium download) when available,
    // otherwise the bundled Playwright renderer (unless --best-effort). Runs the mmdc availability
    // probe at most once, on the first mermaid diagram, off the startup path.
    private void EnsureMermaidBackend()
    {
        if (_mermaidSelected) return;
        lock (_mermaidGate)
        {
            if (_mermaidSelected) return;
            if (_mmdcCandidate.IsAvailable())
                _mmdc = _mmdcCandidate;
            else if (!_bestEffort)
                _mermaid = new MermaidRenderer();
            _mermaidSelected = true;
        }
    }

    public DiagramResult? TryGet(string key)
    {
        // Probe both themes; callers that care about theme use RenderAsync.
        return _cache.TryGet(key, DiagramTheme.Dark) ?? _cache.TryGet(key, DiagramTheme.Light);
    }

    public Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct = default)
    {
        var cached = _cache.TryGet(request.Key, theme);
        if (cached is { Status: DiagramStatus.Ready }) return Task.FromResult(cached);

        var composedKey = request.Key + "-" + theme;
        return _inflight.GetOrAdd(composedKey, _ => RenderUncachedAsync(request, theme, composedKey, ct));
    }

    /// <summary>
    /// Renders a fresh, higher-resolution variant for zoomed viewing. These are NOT cached (neither
    /// the on-disk store nor the inflight map) because they're keyed only by content/theme, and we
    /// don't want a hi-res variant to shadow or evict the normal render.
    /// </summary>
    public async Task<DiagramResult> RenderAtScaleAsync(DiagramRequest request, DiagramTheme theme, double scale, CancellationToken ct = default)
    {
        if (scale <= 1.0) return await RenderAsync(request, theme, ct);
        await _renderGate.WaitAsync(ct);
        try
        {
            return await RenderWithBackendsAsync(request, theme, ct, scale);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    public async Task<string?> RenderTextAsync(DiagramRequest request, CancellationToken ct = default)
    {
        if (_textCache.TryGetValue(request.Key, out var cached)) return cached;
        if (_cache.TryGetText(request.Key) is { } disk) { _textCache[request.Key] = disk; return disk; }
        string? text = null;
        if (request.Kind is DiagramKind.D2 && ToolAvailable("d2", _options.D2Path ?? "d2"))
            text = await TextDiagramRenderer.RenderAsync(request, _options.D2Path, _options.PlantUmlPath, ct);
        else if (request.Kind is DiagramKind.PlantUml or DiagramKind.C4PlantUml && ToolAvailable("plantuml", _options.PlantUmlPath ?? "plantuml"))
            text = await TextDiagramRenderer.RenderAsync(request, _options.D2Path, _options.PlantUmlPath, ct);
        _textCache[request.Key] = text;
        if (text is not null) _cache.StoreText(request.Key, text);
        return text;
    }

    private async Task<DiagramResult> RenderUncachedAsync(
        DiagramRequest request, DiagramTheme theme, string composedKey, CancellationToken ct)
    {
        await _renderGate.WaitAsync(ct);
        try
        {
            var result = await RenderWithBackendsAsync(request, theme, ct, 1.0);
            return _cache.Store(request.Key, theme, result);
        }
        finally
        {
            _renderGate.Release();
            _inflight.TryRemove(composedKey, out _);
        }
    }

    /// <summary>
    /// Backend selection: the local tool when present; otherwise Kroki (if allowed); otherwise an
    /// actionable error. A local tool that exists but fails on the diagram is NOT retried via Kroki —
    /// that would mask syntax errors with a slow network round-trip.
    /// </summary>
    private async Task<DiagramResult> RenderWithBackendsAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double scale)
    {
        switch (request.Kind)
        {
            case DiagramKind.D2:
                if (ToolAvailable("d2", _options.D2Path ?? "d2")) return await _d2.RenderAsync(request, theme, ct, scale);
                return await KrokiOr(request, theme, ct, scale, $"d2 executable not found ('{_options.D2Path ?? "d2"}'). Install D2 (https://d2lang.com) and ensure it is on your PATH, or pass --d2-path <path-to-d2>.");
            case DiagramKind.Graphviz:
                if (ToolAvailable("dot", _options.GraphvizPath ?? "dot")) return await _graphviz.RenderAsync(request, theme, ct, scale);
                return await KrokiOr(request, theme, ct, scale, $"Graphviz 'dot' not found ('{_options.GraphvizPath ?? "dot"}'). Install Graphviz (https://graphviz.org) and ensure it is on your PATH.");
            case DiagramKind.PlantUml:
                if (ToolAvailable("plantuml", _options.PlantUmlPath ?? "plantuml")) return await _plantUml.RenderAsync(request, theme, ct, scale);
                return await KrokiOr(request, theme, ct, scale, $"PlantUML not found ('{_options.PlantUmlPath ?? "plantuml"}'). Install PlantUML (https://plantuml.com) and ensure 'plantuml' (and Java) are on your PATH.");
            case DiagramKind.Mermaid:
                return await RenderMermaidAsync(request, theme, ct, scale);
            default:
                return await KrokiOr(request, theme, ct, scale,
                    $"{DiagramExtractor.Label(request.Kind)} diagrams are rendered through Kroki, which needs network access (run without --offline, or set krokiUrl to a local server).");
        }
    }

    private async Task<DiagramResult> KrokiOr(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double scale, string noLocalMessage)
    {
        if (_kroki is not null && _options.KrokiFallback)
        {
            if (Interlocked.Exchange(ref _krokiAnnounced, 1) == 0)
                _options.OnKrokiUsed?.Invoke(_kroki.BaseUrl);
            var result = await _kroki.RenderAsync(request, theme, ct, scale);
            if (result.Status == DiagramStatus.Ready) return result;
            return DiagramResult.Fail(request.Key, noLocalMessage + " Kroki fallback also failed: " + result.Error);
        }
        return DiagramResult.Fail(request.Key, noLocalMessage + (_kroki is null ? " (Kroki fallback disabled: offline)" : ""));
    }

    private bool ToolAvailable(string name, string path) =>
        _toolAvailable.GetOrAdd(name + "|" + path, _ => ExecutableResolver.Find(path) is not null);

    private async Task<DiagramResult> RenderMermaidAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double scale = 1.0)
    {
        EnsureMermaidBackend();
        // Prefer a local mmdc (lighter, no Chromium); fall back to the Playwright renderer.
        if (_mmdc is not null)
            return await _mmdc.RenderAsync(request, theme, ct, scale);
        if (_mermaid is not null)
            return await _mermaid.RenderAsync(request, theme, ct, scale);

        return await KrokiOr(request, theme, ct, scale,
            "Mermaid rendering is disabled in --best-effort mode (and no local 'mmdc' was found). " +
            "Install mermaid-cli (npm i -g @mermaid-js/mermaid-cli) or open in the browser to view this diagram.");
    }

    /// <summary>Reports which backend would handle each kind (for `readmd doctor`).</summary>
    public IReadOnlyDictionary<string, string> DescribeBackends()
    {
        var d = new Dictionary<string, string>();
        d["mermaid"] = _mmdcCandidate.IsAvailable() ? "mmdc (local)" : _bestEffort ? (_kroki is not null ? "kroki" : "unavailable (--best-effort, offline)") : "headless Chromium (Playwright)";
        d["d2"] = ToolAvailable("d2", _options.D2Path ?? "d2") ? "d2 (local)" : _kroki is not null ? "kroki" : "unavailable";
        d["graphviz"] = ToolAvailable("dot", _options.GraphvizPath ?? "dot") ? "dot (local)" : _kroki is not null ? "kroki" : "unavailable";
        d["plantuml"] = ToolAvailable("plantuml", _options.PlantUmlPath ?? "plantuml") ? "plantuml (local)" : _kroki is not null ? "kroki" : "unavailable";
        d["others (ditaa, svgbob, …)"] = _kroki is not null ? "kroki (" + _kroki.BaseUrl + ")" : "unavailable (offline)";
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        if (_mermaid is not null) await _mermaid.DisposeAsync();
        _kroki?.Dispose();
        _renderGate.Dispose();
    }
}
