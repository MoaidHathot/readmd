using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Readmd.Core;

namespace Readmd.Diagrams;

/// <summary>
/// Two-tier cache for rendered diagrams: an in-memory map for the current session plus an
/// on-disk store keyed by the diagram's content hash, so re-runs and both front-ends reuse
/// the same rendered PNG/SVG. Keys already incorporate source+kind; the theme is appended here.
/// </summary>
internal sealed class DiagramCache(string cacheDirectory)
{
    private readonly ConcurrentDictionary<string, DiagramResult> _memory = new();
    private readonly string _dir = cacheDirectory;

    /// <summary>
    /// A short hash of the theme/style definitions that influence rendered output. Baked into every
    /// cache key so that changing a diagram theme (e.g. mermaid gantt colors) automatically
    /// invalidates previously-cached PNGs/SVGs instead of serving stale images.
    /// </summary>
    private static readonly string StyleVersion = ComputeStyleVersion();

    private static string ComputeStyleVersion()
    {
        var material = MermaidTheme.ConfigJson(true) + "\u0000" + MermaidTheme.ConfigJson(false);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant(); // 8 hex chars
    }

    private static string Compose(string key, DiagramTheme theme) =>
        $"{key}-{theme.ToString().ToLowerInvariant()}-{StyleVersion}";

    public DiagramResult? TryGet(string key, DiagramTheme theme)
    {
        var composed = Compose(key, theme);
        if (_memory.TryGetValue(composed, out var cached)) return cached;

        // Attempt to hydrate from disk.
        var pngPath = Path.Combine(_dir, composed + ".png");
        var svgPath = Path.Combine(_dir, composed + ".svg");
        try
        {
            if (File.Exists(pngPath) || File.Exists(svgPath))
            {
                byte[]? png = File.Exists(pngPath) ? File.ReadAllBytes(pngPath) : null;
                string? svg = File.Exists(svgPath) ? File.ReadAllText(svgPath) : null;
                var (w, h) = png is not null ? ImageInfo.GetPngSize(png) : (0, 0);
                var result = new DiagramResult(key, DiagramStatus.Ready, png, svg, w, h, null);
                _memory[composed] = result;
                return result;
            }
        }
        catch
        {
            // Corrupt cache entry — ignore and re-render.
        }
        return null;
    }

    public DiagramResult Store(string key, DiagramTheme theme, DiagramResult result)
    {
        var composed = Compose(key, theme);
        _memory[composed] = result;
        if (result.Status == DiagramStatus.Ready)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                if (result.Png is not null) File.WriteAllBytes(Path.Combine(_dir, composed + ".png"), result.Png);
                if (result.Svg is not null) File.WriteAllText(Path.Combine(_dir, composed + ".svg"), result.Svg);
            }
            catch
            {
                // Best-effort persistence; in-memory copy still serves this session.
            }
        }
        return result;
    }

    // Text (ASCII art) renderings are theme-independent; stored once per key.
    private readonly ConcurrentDictionary<string, string> _textMemory = new();

    public string? TryGetText(string key)
    {
        if (_textMemory.TryGetValue(key, out var t)) return t;
        try
        {
            var path = Path.Combine(_dir, key + "-" + StyleVersion + ".txt");
            if (File.Exists(path)) { t = File.ReadAllText(path); _textMemory[key] = t; return t; }
        }
        catch { /* ignore */ }
        return null;
    }

    public void StoreText(string key, string text)
    {
        _textMemory[key] = text;
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, key + "-" + StyleVersion + ".txt"), text);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Best-effort cleanup of the on-disk cache so it can't grow without bound across versions:
    /// removes files not accessed in the last 30 days, and if the total still exceeds a size cap,
    /// deletes the oldest files until it's under the cap. Runs once at startup; failures are ignored.
    /// </summary>
    public void EvictOldEntries(long maxBytes = 200L * 1024 * 1024, int maxAgeDays = 30)
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var files = new DirectoryInfo(_dir).GetFiles("*", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            foreach (var f in files.ToList())
            {
                if (f.LastAccessTimeUtc < cutoff)
                {
                    try { f.Delete(); files.Remove(f); } catch { /* ignore */ }
                }
            }

            long total = files.Sum(f => f.Length);
            int i = 0;
            while (total > maxBytes && i < files.Count)
            {
                try { total -= files[i].Length; files[i].Delete(); } catch { /* ignore */ }
                i++;
            }
        }
        catch
        {
            // Eviction is non-critical.
        }
    }
}
