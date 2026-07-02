using System.Diagnostics;
using Readmd.Core;

namespace Readmd.Diagrams;

/// <summary>
/// Renders mermaid diagrams by shelling out to a local <c>mmdc</c> (mermaid-cli), which uses its
/// own bundled headless browser to produce a PNG. This is a lighter alternative to readmd's own
/// Playwright/Chromium download: when <c>mmdc</c> is on PATH (or configured), no extra browser is
/// fetched. We let mmdc emit PNG directly (rather than SVG that we'd rasterize in-process) so the
/// text, label centering, and gantt grid lines are rendered by a real browser and look identical to
/// the browser front-end — the in-process SVG rasterizer mishandles mermaid's tspan/em positioning
/// and <c>currentColor</c>.
/// </summary>
internal sealed class MermaidCliRenderer
{
    private readonly string _mmdcPath;

    public MermaidCliRenderer(string? mmdcPath = null) => _mmdcPath = mmdcPath ?? "mmdc";

    public bool IsAvailable()
    {
        try
        {
            var psi = ExecutableResolver.Resolve(_mmdcPath, ["--version"]);
            if (psi is null) return false;
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(2500);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double renderScale = 1.0)
    {
        var dir = Path.Combine(Path.GetTempPath(), "readmd-mmdc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "in.mmd");
        var output = Path.Combine(dir, "out.png");
        var configFile = Path.Combine(dir, "config.json");
        var cssFile = Path.Combine(dir, "style.css");
        try
        {
            await File.WriteAllTextAsync(input, request.Source, ct);
            // Keep htmlLabels:true — mmdc's browser renders the HTML labels natively, so we get
            // correctly centered/wrapped text.
            await File.WriteAllTextAsync(configFile, MermaidTheme.ConfigJson(theme == DiagramTheme.Dark), ct);
            // Set the SVG's `color` so elements drawn with stroke="currentColor" (notably the gantt
            // grid/tick lines, which ignore the gridColor theme variable) resolve to a VISIBLE color
            // instead of the browser default of black. This mirrors what the browser front-end and
            // the Playwright renderer do by setting the page text color.
            await File.WriteAllTextAsync(cssFile, CssFor(theme), ct);

            await RunMmdcAsync(input, output, configFile, cssFile, renderScale, ct);
            if (!File.Exists(output))
                return DiagramResult.Fail(request.Key, "mmdc produced no output.");

            var png = await File.ReadAllBytesAsync(output, ct);
            var (width, height) = ImageInfo.GetPngSize(png);
            return new DiagramResult(request.Key, DiagramStatus.Ready, png, null, width, height, null);
        }
        catch (MmdcNotFoundException)
        {
            return DiagramResult.Fail(request.Key, $"mmdc executable not found ('{_mmdcPath}').");
        }
        catch (OperationCanceledException)
        {
            return DiagramResult.Fail(request.Key, "Mermaid (mmdc) render timed out.");
        }
        catch (Exception ex)
        {
            return DiagramResult.Fail(request.Key, "Mermaid (mmdc) render failed: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // CSS injected into mmdc's page to correct gantt rendering that the mermaid v11 "base" theme
    // gets wrong for a dark palette: (1) currentColor (gantt grid/tick lines) resolves to a visible
    // color; (2) the grid/ticks use the theme grid color; (3) the section bands use the theme's dark
    // section colors instead of mermaid's algorithmically-computed light defaults, which otherwise
    // render as a jarring light band on a dark background.
    private static string CssFor(DiagramTheme theme)
    {
        var (fg, grid, section, altSection) = theme == DiagramTheme.Dark
            ? ("#e6edf3", "#6b78c0", "#161b2e", "#1b2236")   // dark
            : ("#1f2328", "#c2ccff", "#eef1ff", "#f6f8fa");  // light
        return $$"""
            svg { color: {{fg}}; }
            .grid .tick line { stroke: {{grid}}; opacity: 0.5; }
            .grid .tick text { fill: {{fg}}; }
            .grid path { stroke: {{grid}}; }
            g.today line { stroke: #f08c8c; }
            .section, .section0, .section2 { fill: {{section}} !important; opacity: 0.5 !important; }
            .section1, .section3 { fill: {{altSection}} !important; opacity: 0.5 !important; }
            """;
    }

    private async Task RunMmdcAsync(string input, string output, string configFile, string cssFile, double renderScale, CancellationToken ct)
    {
        // -s N renders at N× for crisp text when scaled into the terminal; -b transparent keeps the
        // diagram background see-through so the theme background shows behind it; -C applies our CSS
        // (the SVG `color` that makes currentColor-driven gantt grid lines visible). The base scale is
        // 2; a higher renderScale (zoomed view) bumps it, clamped so it can't explode.
        int scale = Math.Clamp((int)Math.Round(2.0 * renderScale), 2, 8);
        var psi = ExecutableResolver.Resolve(_mmdcPath,
            ["-i", input, "-o", output, "-c", configFile, "-C", cssFile, "-b", "transparent", "-s", scale.ToString()])
            ?? throw new MmdcNotFoundException();

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new MmdcNotFoundException(); }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
        var linked = timeoutCts.Token;
        try
        {
            proc.StandardInput.Close();
            var stderrTask = proc.StandardError.ReadToEndAsync(linked);
            await proc.StandardOutput.ReadToEndAsync(linked);
            await proc.WaitForExitAsync(linked);
            if (proc.ExitCode != 0)
            {
                var err = await stderrTask;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "mmdc exited with an error." : err.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
    }
}

/// <summary>Signals that the configured mmdc executable could not be started (not found on PATH).</summary>
internal sealed class MmdcNotFoundException : Exception;
