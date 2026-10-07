using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Readmd.Core;

namespace Readmd.Diagrams;

/// <summary>
/// Renders diagrams through a Kroki server (https://kroki.io, or a self-hosted instance set via
/// <c>krokiUrl</c>). Kroki accepts the diagram source as a deflate+base64 GET path or as a POST body
/// and returns SVG; we POST (no URL-length limits) and rasterize the SVG locally. Used for diagram
/// kinds without a local renderer (ditaa, svgbob, nomnoml, …) and as a fallback when a local tool
/// (mmdc/d2/dot/plantuml) is missing. Sends the diagram text to the configured server — disabled in
/// offline mode.
/// </summary>
public sealed class KrokiRenderer : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public const string DefaultUrl = "https://kroki.io";

    public KrokiRenderer(string? baseUrl = null)
    {
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl).TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("readmd/1.0");
    }

    public string BaseUrl => _baseUrl;

    public async Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double renderScale = 1.0)
    {
        try
        {
            var svg = await FetchSvgAsync(request, theme, ct);
            if (string.IsNullOrWhiteSpace(svg))
                return DiagramResult.Fail(request.Key, "Kroki returned no output.");
            var png = Rasterizer.SvgToPng(svg, out var width, out var height, 1.5f * (float)renderScale);
            return new DiagramResult(request.Key, DiagramStatus.Ready, png, svg, width, height, null) { Backend = "kroki" };
        }
        catch (HttpRequestException ex)
        {
            return DiagramResult.Fail(request.Key, $"Kroki ({_baseUrl}) request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return DiagramResult.Fail(request.Key, $"Kroki ({_baseUrl}) timed out.");
        }
        catch (Exception ex)
        {
            return DiagramResult.Fail(request.Key, "Kroki render failed: " + ex.Message);
        }
    }

    private async Task<string> FetchSvgAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct)
    {
        var type = DiagramExtractor.KrokiName(request.Kind);
        var source = ApplyTheme(request, theme);
        var url = $"{_baseUrl}/{type}/svg";
        using var content = new StringContent(source, Encoding.UTF8, "text/plain");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/svg+xml"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var detail = body.Length > 300 ? body[..300] + "…" : body;
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {detail.Trim()}");
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // Kroki renders with each tool's defaults; nudge the ones that accept an in-source theme.
    private static string ApplyTheme(DiagramRequest request, DiagramTheme theme)
    {
        if (theme != DiagramTheme.Dark) return request.Source;
        switch (request.Kind)
        {
            case DiagramKind.Mermaid:
                return request.Source.Contains("%%{init", StringComparison.OrdinalIgnoreCase)
                    ? request.Source
                    : "%%{init: {'theme': 'dark'}}%%\n" + request.Source;
            case DiagramKind.PlantUml or DiagramKind.C4PlantUml:
                if (request.Source.Contains("!theme", StringComparison.OrdinalIgnoreCase)) return request.Source;
                var nl = request.Source.IndexOf('\n');
                if (nl >= 0 && request.Source.TrimStart().StartsWith("@start", StringComparison.OrdinalIgnoreCase))
                    return request.Source[..(nl + 1)] + "!theme cyborg\n" + request.Source[(nl + 1)..];
                return "!theme cyborg\n" + request.Source;
            default:
                return request.Source;
        }
    }

    /// <summary>The GET form of a Kroki URL (deflate + base64url), handy for "open in browser".</summary>
    public string ShareUrl(DiagramRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(request.Source);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(bytes);
        var b64 = Convert.ToBase64String(ms.ToArray()).Replace('+', '-').Replace('/', '_');
        return $"{_baseUrl}/{DiagramExtractor.KrokiName(request.Kind)}/svg/{b64}";
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Text-mode diagram rendering for terminals without graphics: D2 (≥ 0.7.1) emits box-drawing art
/// when the output file ends in <c>.txt</c>; PlantUML has <c>-tutxt</c>. Results are cached by the
/// caller alongside the raster output.
/// </summary>
internal static class TextDiagramRenderer
{
    public static async Task<string?> RenderAsync(DiagramRequest request, string? d2Path, string? plantUmlPath, CancellationToken ct)
    {
        try
        {
            return request.Kind switch
            {
                DiagramKind.D2 => await D2TextAsync(request.Source, d2Path ?? "d2", ct),
                DiagramKind.PlantUml or DiagramKind.C4PlantUml => await PlantUmlTextAsync(request.Source, plantUmlPath ?? "plantuml", ct),
                _ => null,
            };
        }
        catch { return null; }
    }

    // d2 gained ASCII output in 0.7.1. Probe the version once per process so older installs don't
    // pay for two failing invocations on every diagram.
    private static readonly Dictionary<string, bool> D2SupportsText = new();
    private static readonly object D2Gate = new();

    internal static bool D2HasAsciiOutput(string d2Path)
    {
        lock (D2Gate)
        {
            if (D2SupportsText.TryGetValue(d2Path, out var known)) return known;
            bool ok = false;
            try
            {
                var psi = ExecutableResolver.Resolve(d2Path, ["--version"]);
                if (psi is not null)
                {
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p is not null)
                    {
                        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                        p.WaitForExit(5000);
                        ok = VersionAtLeast(output, 0, 7, 1);
                    }
                }
            }
            catch { ok = false; }
            D2SupportsText[d2Path] = ok;
            return ok;
        }
    }

    internal static bool VersionAtLeast(string versionText, int major, int minor, int patch)
    {
        var m = System.Text.RegularExpressions.Regex.Match(versionText ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return false;
        int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value), c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        return (a, b, c).CompareTo((major, minor, patch)) >= 0;
    }

    private static async Task<string?> D2TextAsync(string source, string d2Path, CancellationToken ct)
    {
        if (!D2HasAsciiOutput(d2Path)) return null;
        // d2 picks the renderer from the output extension; write to a temp .txt and read it back.
        var dir = Path.Combine(Path.GetTempPath(), "readmd", "d2-ascii");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var psi = ExecutableResolver.Resolve(d2Path, ["--ascii-mode=extended", "-", outPath]);
            if (psi is null) return null;
            await ProcessPipe.RunAsync(psi, source, TimeSpan.FromSeconds(20), ct);
            if (!File.Exists(outPath)) return null;
            var text = await File.ReadAllTextAsync(outPath, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(outPath); } catch { /* ignore */ }
        }
    }

    private static async Task<string?> PlantUmlTextAsync(string source, string plantUmlPath, CancellationToken ct)
    {
        var psi = ExecutableResolver.Resolve(plantUmlPath, ["-tutxt", "-pipe"]);
        if (psi is null) return null;
        var text = await ProcessPipe.RunAsync(psi, source, TimeSpan.FromSeconds(30), ct);
        return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd();
    }
}
