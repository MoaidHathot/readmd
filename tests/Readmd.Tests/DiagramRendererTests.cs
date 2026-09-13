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

    // A trimmed-down browsers.json in the shape Playwright ships next to its driver.
    private const string Manifest = """
        {
          "browsers": [
            { "name": "chromium", "revision": "1228", "installByDefault": true, "browserVersion": "149.0.7827.55" },
            { "name": "chromium-headless-shell", "revision": "1228", "installByDefault": true, "browserVersion": "149.0.7827.55" },
            { "name": "chromium-tip-of-tree", "revision": "1432", "installByDefault": false },
            { "name": "firefox", "revision": "1532", "installByDefault": true },
            { "name": "ffmpeg", "revision": "1011", "installByDefault": true }
          ]
        }
        """;

    [Fact]
    public void Manifest_selects_the_headless_shell_build_a_headless_launch_uses()
    {
        var build = PdfProvisioning.ParseChromiumBuild(Manifest);

        Assert.NotNull(build);
        Assert.Equal("chromium-headless-shell", build.Value.Name);
        // Playwright's registry names the directory <name with '-' as '_'>-<revision>.
        Assert.Equal(["chromium_headless_shell-1228"], build.Value.DirectoryNames);
    }

    [Fact]
    public void Manifest_without_a_headless_shell_falls_back_to_full_chromium()
    {
        const string legacy = """{ "browsers": [ { "name": "chromium", "revision": 1000 }, { "name": "webkit", "revision": "1500" } ] }""";

        var build = PdfProvisioning.ParseChromiumBuild(legacy);

        Assert.NotNull(build);
        Assert.Equal("chromium", build.Value.Name);
        Assert.Equal(["chromium-1000"], build.Value.DirectoryNames);
    }

    [Fact]
    public void Manifest_revision_overrides_add_platform_special_directories()
    {
        const string pinned = """
            { "browsers": [ { "name": "chromium-headless-shell", "revision": "1300",
                              "revisionOverrides": { "ubuntu20.04-x64": "1290", "mac12-arm64": "1280" } } ] }
            """;

        var build = PdfProvisioning.ParseChromiumBuild(pinned);

        Assert.NotNull(build);
        Assert.Equal(
            ["chromium_headless_shell-1300",
             "chromium_headless_shell_ubuntu20.04_x64_special-1290",
             "chromium_headless_shell_mac12_arm64_special-1280"],
            build.Value.DirectoryNames);
    }

    [Theory]
    [InlineData("""{ "browsers": [] }""")]
    [InlineData("""{ "browsers": [ { "name": "firefox", "revision": "1532" } ] }""")]
    [InlineData("""{ "browsers": [ { "name": "chromium" } ] }""")]
    [InlineData("""{ "comment": "no browsers key" }""")]
    [InlineData("[]")]
    public void Manifest_without_a_usable_chromium_entry_yields_null(string json)
    {
        Assert.Null(PdfProvisioning.ParseChromiumBuild(json));
    }

    [Fact]
    public void Cache_detection_requires_the_pinned_revision_with_a_completed_install()
    {
        var cache = Directory.CreateTempSubdirectory("readmd-pw-cache-").FullName;
        try
        {
            string[] expected = ["chromium_headless_shell-1228"];

            // Empty cache.
            Assert.False(PdfProvisioning.IsChromiumInstalledIn(cache, expected));

            // Only builds pinned by OTHER Playwright versions (the situation that produced
            // "Executable doesn't exist at ...chromium_headless_shell-1228...").
            Complete(Path.Combine(cache, "chromium-1208"));
            Complete(Path.Combine(cache, "chromium_headless_shell-1208"));
            Complete(Path.Combine(cache, "chromium_headless_shell-1234"));
            Assert.False(PdfProvisioning.IsChromiumInstalledIn(cache, expected));

            // The right revision, but the download never finished unpacking (no marker).
            Directory.CreateDirectory(Path.Combine(cache, "chromium_headless_shell-1228"));
            Assert.False(PdfProvisioning.IsChromiumInstalledIn(cache, expected));

            // Completed install of the pinned revision.
            Complete(Path.Combine(cache, "chromium_headless_shell-1228"));
            Assert.True(PdfProvisioning.IsChromiumInstalledIn(cache, expected));

            // Any of the accepted directory names is enough (platform "special" pins).
            Assert.True(PdfProvisioning.IsChromiumInstalledIn(cache,
                ["chromium_headless_shell_ubuntu20.04_x64_special-1290", "chromium_headless_shell-1228"]));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }

        static void Complete(string browserDir)
        {
            Directory.CreateDirectory(browserDir);
            File.WriteAllText(Path.Combine(browserDir, "INSTALLATION_COMPLETE"), "");
        }
    }

    [Fact]
    public void Cache_detection_without_a_manifest_accepts_any_chromium_build()
    {
        var cache = Directory.CreateTempSubdirectory("readmd-pw-cache-").FullName;
        try
        {
            Assert.False(PdfProvisioning.IsChromiumInstalledIn(cache, expectedDirectories: null));

            Directory.CreateDirectory(Path.Combine(cache, "chromium_headless_shell-1208"));
            Assert.True(PdfProvisioning.IsChromiumInstalledIn(cache, expectedDirectories: null));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public void Cache_detection_handles_a_missing_cache_directory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "readmd-pw-cache-" + Guid.NewGuid().ToString("N"));

        Assert.False(PdfProvisioning.IsChromiumInstalledIn(missing, ["chromium_headless_shell-1228"]));
        Assert.False(PdfProvisioning.IsChromiumInstalledIn(missing, expectedDirectories: null));
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

    [Fact]
    public void Includes_a_custom_pan_zoom_viewer()
    {
        var html = MermaidHtml.BuildStandalonePage("graph TD; A-->B;", dark: false);
        // A wide/long diagram must be enlargeable past the browser's ~500% zoom ceiling, so the page
        // ships its own transform-based zoom/pan (wheel + drag) rather than relying on browser zoom.
        Assert.Contains("id=\"stage\"", html);
        Assert.Contains("addEventListener('wheel'", html);
        Assert.Contains("function zoom", html);
        Assert.DoesNotContain("__CONFIG__", html);    // all tokens were substituted
        Assert.DoesNotContain("__MERMAIDJS__", html);
    }
}


