namespace Readmd.Core;

public enum DiagramStatus
{
    Pending,
    Ready,
    Failed,
}

/// <summary>
/// The rendered output of a diagram, in whichever formats were produced: a PNG (always, for
/// raster display), optionally the SVG source (crisp re-rasterization at any zoom), and
/// optionally a <see cref="Text"/> rendering (ASCII/Unicode art) for terminals without graphics,
/// plain-text output and screen readers.
/// </summary>
public sealed record DiagramResult(
    string Key,
    DiagramStatus Status,
    byte[]? Png,
    string? Svg,
    int PixelWidth,
    int PixelHeight,
    string? Error)
{
    /// <summary>Text-mode rendering of the diagram (box-drawing art), when the backend can produce one.</summary>
    public string? Text { get; init; }

    /// <summary>Which backend produced this result (e.g. "d2", "mmdc", "kroki"), for diagnostics.</summary>
    public string? Backend { get; init; }

    public static DiagramResult Pending(string key) =>
        new(key, DiagramStatus.Pending, null, null, 0, 0, null);

    public static DiagramResult Fail(string key, string error) =>
        new(key, DiagramStatus.Failed, null, null, 0, 0, error);

    /// <summary>A text-only result (no raster): shown as box art in the terminal.</summary>
    public static DiagramResult TextOnly(string key, string text, string backend) =>
        new(key, DiagramStatus.Ready, null, null, 0, 0, null) { Text = text, Backend = backend };

    /// <summary>True when there is something drawable (a PNG or SVG).</summary>
    public bool HasImage => Png is not null || Svg is not null;
}

/// <summary>
/// Renders diagrams to images/SVG. Implementations cache by <see cref="DiagramRequest.Key"/>
/// so unchanged diagrams are produced once and reused across live-reloads and both front-ends.
/// </summary>
public interface IDiagramRenderer : IAsyncDisposable
{
    /// <summary>Returns a cached result immediately if present, otherwise null.</summary>
    DiagramResult? TryGet(string key);

    /// <summary>
    /// Renders (or returns cached) output for the request. <paramref name="theme"/> selects a
    /// light/dark palette. Safe to call repeatedly with the same key.
    /// </summary>
    Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct = default);

    /// <summary>
    /// Renders at a higher pixel density — roughly <paramref name="scale"/>× the normal resolution —
    /// for a zoomed/detailed view (e.g. the terminal's focused-image zoom). Results are not written to
    /// the normal cache. The default implementation falls back to the base render.
    /// </summary>
    Task<DiagramResult> RenderAtScaleAsync(DiagramRequest request, DiagramTheme theme, double scale, CancellationToken ct = default)
        => RenderAsync(request, theme, ct);

    /// <summary>
    /// Renders the diagram as text (ASCII/Unicode box art) when a backend can (D2 ≥ 0.7, PlantUML),
    /// else null. Used for <c>graphics: none</c>, <c>--print</c> and as a fallback when no image
    /// renderer is available. The default implementation returns null.
    /// </summary>
    Task<string?> RenderTextAsync(DiagramRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

public enum DiagramTheme
{
    Light,
    Dark,
}
