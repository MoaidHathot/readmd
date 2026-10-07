using Readmd.Core;
using Readmd.Diagrams;
using SkiaSharp;

namespace Readmd.Tests;

/// <summary>
/// Rendering-quality regression tests for the local-tool diagram paths (mmdc / d2). These guard
/// against bugs that text-based tests can't see — e.g. mermaid labels rendering blank, gantt grid
/// lines coming out the same color as the background, or a diagram rasterizing to an empty image.
/// They render through the real <see cref="DiagramRenderer"/> and inspect the resulting pixels.
///
/// Because they depend on external tools (mmdc, d2), each test is skipped (not failed) when the
/// tool isn't installed, so the suite still runs everywhere while catching regressions wherever the
/// tools are present (developer machines and any CI image that installs them).
/// </summary>
public class DiagramRenderingRegressionTests
{
    private static DiagramRenderer NewRenderer() => new(new DiagramRendererOptions
    {
        AllowNetwork = false,   // these tests exercise the LOCAL tool paths only
        CacheDirectory = Path.Combine(Path.GetTempPath(), "readmd-rqtest-" + Guid.NewGuid().ToString("N")),
    });

    private static bool ToolAvailable(string name)
    {
        try
        {
            var psi = ExecutableResolver.Resolve(name, ["--version"]);
            if (psi is null) return false;
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Counts pixels whose color differs noticeably from the four-corner background sample.
    private static (int Total, int NonBackground, int Light) AnalyzePixels(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        // Sample the corners to estimate the background (diagrams are rendered on transparent, which
        // decodes as either transparent or the corner color).
        var bg = bmp.GetPixel(0, 0);
        int total = bmp.Width * bmp.Height, nonBg = 0, light = 0;
        for (int y = 0; y < bmp.Height; y += 2)
        {
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha < 16) continue; // transparent => background
                int d = Math.Abs(c.Red - bg.Red) + Math.Abs(c.Green - bg.Green) + Math.Abs(c.Blue - bg.Blue);
                if (d > 24) nonBg++;
                // "light" pixels approximate text/lines drawn in the light foreground.
                if (c.Red > 180 && c.Green > 180 && c.Blue > 180) light++;
            }
        }
        return (total, nonBg, light);
    }

    [SkippableFact]
    public async Task Mermaid_flowchart_renders_visible_text_and_shapes()
    {
        Skip.IfNot(ToolAvailable("mmdc"), "mmdc (mermaid-cli) not installed");
        await using var r = NewRenderer();

        var req = DiagramRequest.Create(DiagramKind.Mermaid, "graph TD; Start-->Decision{Works?}; Decision-->|yes|Ship; Decision-->|no|Fix;");
        var result = await r.RenderAsync(req, DiagramTheme.Dark);

        // mmdc resolves on PATH but its bundled headless browser may be missing/unlaunchable (e.g. a
        // CI image with mermaid-cli but no Chromium). That's an environment gap, not a regression, so
        // skip rather than fail when the render couldn't run.
        Skip.If(result.Status == DiagramStatus.Failed, "mmdc cannot render here: " + result.Error);
        Assert.Equal(DiagramStatus.Ready, result.Status);
        Assert.NotNull(result.Png);
        Assert.True(result.PixelWidth > 50 && result.PixelHeight > 50, "diagram should have real dimensions");

        var (_, nonBg, light) = AnalyzePixels(result.Png!);
        // Shapes => many non-background pixels; rendered text/labels => a meaningful count of light
        // (foreground) pixels. A blank-label regression (the htmlLabels/Svg.Skia bug) collapses this.
        Assert.True(nonBg > 200, $"expected shapes to render (non-bg pixels={nonBg})");
        Assert.True(light > 80, $"expected visible light-colored text/labels (light pixels={light})");
    }

    [SkippableFact]
    public async Task Mermaid_gantt_renders_dark_themed_not_default_light()
    {
        Skip.IfNot(ToolAvailable("mmdc"), "mmdc (mermaid-cli) not installed");
        await using var r = NewRenderer();

        var gantt = "gantt\n  title Roadmap\n  dateFormat YYYY-MM-DD\n  section Core\n  Parser :a1, 2026-01-01, 7d\n  Diagrams :a2, after a1, 5d\n  section UI\n  Terminal :b1, 2026-01-08, 6d\n";
        var req = DiagramRequest.Create(DiagramKind.Mermaid, gantt);
        var result = await r.RenderAsync(req, DiagramTheme.Dark);

        // See the flowchart test: skip when mmdc is present but can't actually render (missing browser).
        Skip.If(result.Status == DiagramStatus.Failed, "mmdc cannot render here: " + result.Error);
        Assert.Equal(DiagramStatus.Ready, result.Status);
        Assert.NotNull(result.Png);

        // Composite onto the dark theme background, exactly as the terminal does, then inspect.
        using var img = CompositeOnDark(result.Png!);

        // Regression guard: the gantt grid/tick lines must be visible against the dark background.
        // They are drawn with stroke="currentColor"; the earlier SVG->Svg.Skia path rasterized them
        // black (invisible) — this is the regression. Count grid-colored (indigo) pixels across the
        // whole image: a healthy gantt has thousands; an all-black grid collapses to near zero. The
        // tight color band also excludes the brighter indigo task-bar borders.
        int gridPixels = CountGridPixels(img);
        Assert.True(gridPixels > 300,
            $"gantt vertical grid/tick lines should be visible against the dark background (grid pixels={gridPixels})");
    }

    private static SKBitmap CompositeOnDark(byte[] png)
    {
        using var src = SKBitmap.Decode(png);
        var outb = new SKBitmap(src.Width, src.Height);
        using var canvas = new SKCanvas(outb);
        canvas.Clear(new SKColor(0x0d, 0x11, 0x17)); // readmd dark theme background
        canvas.DrawBitmap(src, 0, 0);
        return outb;
    }

    private static int CountGridPixels(SKBitmap bmp)
    {
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsGridIndigo(bmp.GetPixel(x, y))) count++;
        return count;
    }

    // The theme grid color is ~#6b78c0 (rgb 107,120,192). Match a tolerant band around it that
    // excludes black grid lines (the regression), the brighter title/label text, AND the even
    // brighter indigo task-bar border (#7c8cf8 = rgb 124,140,248, whose blue is well above 215).
    private static bool IsGridIndigo(SKColor c) =>
        c.Red is >= 70 and <= 140 && c.Green is >= 80 and <= 150 && c.Blue is >= 150 and <= 215
        && c.Blue > c.Red + 30; // bluer than it is red (rules out grays)

    [SkippableFact]
    public async Task D2_renders_a_nonempty_image()
    {
        Skip.IfNot(ToolAvailable("d2"), "d2 not installed");
        await using var r = NewRenderer();

        var req = DiagramRequest.Create(DiagramKind.D2, "a -> b -> c");
        var result = await r.RenderAsync(req, DiagramTheme.Dark);

        Assert.Equal(DiagramStatus.Ready, result.Status);
        Assert.NotNull(result.Png);
        var (_, nonBg, _) = AnalyzePixels(result.Png!);
        Assert.True(nonBg > 100, $"d2 diagram should render visible content (non-bg pixels={nonBg})");
    }
}
