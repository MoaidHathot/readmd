using Readmd.Core;
using Readmd.Diagrams;

namespace Readmd.Tests;

public class DiagramRendererTests
{
    private static DiagramRenderer NewRenderer() => new(new DiagramRendererOptions
    {
        BestEffort = true, // never launch Chromium in tests
        D2Path = "readmd-no-such-d2",
        GraphvizPath = "readmd-no-such-dot",
        PlantUmlPath = "readmd-no-such-plantuml",
        MermaidCliPath = "readmd-no-such-mmdc",
        CacheDirectory = Path.Combine(Path.GetTempPath(), "readmd-test-cache-" + Guid.NewGuid().ToString("N")),
    });

    [Theory]
    [InlineData(DiagramKind.D2, "digraph")]
    [InlineData(DiagramKind.Graphviz, "digraph { a -> b }")]
    [InlineData(DiagramKind.PlantUml, "@startuml\nA->B\n@enduml")]
    public async Task Missing_tool_yields_a_failed_result_with_message(DiagramKind kind, string source)
    {
        await using var renderer = NewRenderer();
        var req = DiagramRequest.Create(kind, source);
        var result = await renderer.RenderAsync(req, DiagramTheme.Dark);
        Assert.Equal(DiagramStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task Mermaid_in_best_effort_without_mmdc_fails_with_hint()
    {
        await using var renderer = NewRenderer();
        var req = DiagramRequest.Create(DiagramKind.Mermaid, "graph TD; A-->B;");
        var result = await renderer.RenderAsync(req, DiagramTheme.Dark);
        Assert.Equal(DiagramStatus.Failed, result.Status);
        Assert.Contains("mermaid", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagramRequest_key_is_stable_for_same_content()
    {
        var a = DiagramRequest.Create(DiagramKind.D2, "a -> b\n");
        var b = DiagramRequest.Create(DiagramKind.D2, "a -> b");
        Assert.Equal(a.Key, b.Key); // trailing whitespace normalized
    }
}

public class ExecutableResolverTests
{
    [Fact]
    public void Nonexistent_command_resolves_to_null()
    {
        Assert.Null(ExecutableResolver.Resolve("readmd-definitely-not-a-real-tool-xyz", ["--version"]));
        Assert.Null(ExecutableResolver.Find("readmd-definitely-not-a-real-tool-xyz"));
    }

    [Fact]
    public void Resolves_a_well_known_tool_on_path()
    {
        // 'dotnet' is guaranteed present in the test/build environment. On Windows it may be an
        // .exe (or a shim); on Unix it's a plain executable — either way Find must locate it.
        var found = ExecutableResolver.Find("dotnet");
        Assert.NotNull(found);
        Assert.True(File.Exists(found!) || Path.IsPathRooted(found));
    }

    [Fact]
    public void Resolve_builds_a_runnable_start_info_with_arguments()
    {
        var psi = ExecutableResolver.Resolve("dotnet", ["--version"]);
        Assert.NotNull(psi);
        Assert.False(psi!.UseShellExecute);
        // The final argument list ends with our requested args (a launcher prefix may precede them).
        Assert.Contains("--version", psi.ArgumentList);
    }
}

public class MermaidConfigTests
{
    [Fact]
    public void Default_config_uses_html_labels()
    {
        var json = MermaidTheme.ConfigJson(dark: true);
        Assert.Contains("\"htmlLabels\": true", json);
    }

    [Fact]
    public void Config_can_request_native_text_labels()
    {
        var json = MermaidTheme.ConfigJson(dark: true, htmlLabels: false);
        Assert.Contains("\"htmlLabels\": false", json);
        Assert.DoesNotContain("\"htmlLabels\": true", json);
    }

    [Fact]
    public void Config_includes_theme_variables_for_both_modes()
    {
        Assert.Contains("darkMode", MermaidTheme.ConfigJson(dark: true));
        Assert.Contains("darkMode", MermaidTheme.ConfigJson(dark: false));
    }
}

public class PdfProvisioningTests
{
    [Fact]
    public void CheckReadiness_reports_a_coherent_result()
    {
        var result = PdfProvisioning.CheckReadiness();

        // Ready iff there is no complaint; a non-ready result must carry an explanatory message.
        if (result.IsReady)
        {
            Assert.Equal(PdfReadiness.Ready, result.Readiness);
            Assert.NotNull(result.NodePath);
        }
        else
        {
            Assert.NotEqual(PdfReadiness.Ready, result.Readiness);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
    }

    [Fact]
    public void Readiness_reason_matches_node_availability()
    {
        var node = PdfProvisioning.FindNode();
        var result = PdfProvisioning.CheckReadiness();

        // If no Node.js is discoverable, readiness cannot be Ready/BrowserMissing (both need Node),
        // unless the JS driver itself is missing from this build.
        if (node is null)
            Assert.True(result.Readiness is PdfReadiness.NodeMissing or PdfReadiness.DriverMissing,
                $"expected NodeMissing/DriverMissing without node, got {result.Readiness}");
    }
}

public class SvgRasterizerTests
{
    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\" viewBox=\"0 0 100 50\">" +
        "<rect width=\"100\" height=\"50\" fill=\"#3178c6\"/></svg>";

    [Fact]
    public void Renders_fitted_to_the_requested_box_preserving_aspect()
    {
        using var bmp = SvgRasterizer.RenderToFit(Svg, 200, 200);
        Assert.NotNull(bmp);
        // 100x50 fit into 200x200 is width-bound (scale 2): 200x100.
        Assert.Equal(200, bmp!.Width);
        Assert.Equal(100, bmp.Height);
    }

    [Fact]
    public void Larger_target_re_rasterizes_to_a_larger_bitmap()
    {
        using var small = SvgRasterizer.RenderToFit(Svg, 200, 200);
        using var large = SvgRasterizer.RenderToFit(Svg, 800, 800);
        Assert.NotNull(small);
        Assert.NotNull(large);
        // The point of the fix: bigger request => proportionally bigger pixels (crisp), not an
        // upscaled copy of a fixed-size raster.
        Assert.Equal(800, large!.Width);
        Assert.Equal(400, large.Height);
        Assert.True(large.Width > small!.Width && large.Height > small.Height);
    }

    [Fact]
    public void Empty_or_unparseable_input_returns_null()
    {
        Assert.Null(SvgRasterizer.RenderToFit("", 100, 100));
        Assert.Null(SvgRasterizer.RenderToFit("   ", 100, 100));
        Assert.Null(SvgRasterizer.RenderToFit(Svg, 0, 100));
    }
}

public class MermaidHtmlTests
{
    [Fact]
    public void Builds_a_self_contained_vector_page()
    {
        var html = MermaidHtml.BuildStandalonePage("graph TD; A-->B;", dark: true);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("mermaid.render", html);      // renders client-side (vector, zoomable)
        Assert.Contains("graph TD", html);            // the diagram source is embedded (JSON-encoded)
        // The bundled mermaid.js (~MBs) is inlined so the page is fully self-contained/offline.
        Assert.True(html.Length > 100_000, $"expected mermaid.js inlined, page was {html.Length} bytes");
    }
}


