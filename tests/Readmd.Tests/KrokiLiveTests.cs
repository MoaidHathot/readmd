using Readmd.Core;
using Readmd.Diagrams;

namespace Readmd.Tests;

/// <summary>
/// Live Kroki round-trips. Opt-in (READMD_LIVE_TESTS=1) because they need the network; skipped
/// otherwise so the suite stays hermetic.
/// </summary>
public class KrokiLiveTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    private static DiagramRenderer NewRenderer() => new(new DiagramRendererOptions
    {
        CacheDirectory = Path.Combine(Path.GetTempPath(), "readmd-live-" + Guid.NewGuid().ToString("N")),
        // Force every kind through Kroki so the sample's sources are checked against the real service
        // regardless of which tools this machine has.
        D2Path = "readmd-no-such-tool", MermaidCliPath = "readmd-no-such-tool", GraphvizPath = "readmd-no-such-tool", PlantUmlPath = "readmd-no-such-tool",
        BestEffort = true,
    });

    [SkippableFact]
    public async Task Ditaa_renders_through_kroki()
    {
        Skip.If(Environment.GetEnvironmentVariable("READMD_LIVE_TESTS") != "1", "set READMD_LIVE_TESTS=1 to run network tests");
        await using var renderer = NewRenderer();
        var req = DiagramRequest.Create(DiagramKind.Ditaa, "+-------+     +-------+\n| Alpha |---->| Beta  |\n+-------+     +-------+");
        var result = await renderer.RenderAsync(req, DiagramTheme.Light);
        Assert.Equal(DiagramStatus.Ready, result.Status);
        Assert.Equal("kroki", result.Backend);
        Assert.NotNull(result.Png);
        Assert.True(result.PixelWidth > 50 && result.PixelHeight > 20);
    }

    /// <summary>Every diagram in samples/diagrams.md (except the deliberately broken one) must render on Kroki.</summary>
    [SkippableFact]
    public async Task Diagram_zoo_sample_renders_through_kroki()
    {
        Skip.If(Environment.GetEnvironmentVariable("READMD_LIVE_TESTS") != "1", "set READMD_LIVE_TESTS=1 to run network tests");
        var markdown = File.ReadAllText(RepoFile("samples", "diagrams.md"));
        var body = FrontMatter.StripToml(markdown).Markdown;
        var requests = DiagramExtractor.Extract(Markdig.Markdown.Parse(body, TerminalPipelineForTests()), body)
            .DistinctBy(r => r.Key)
            .Where(r => !r.Source.Contains("syntax error"))
            .ToList();
        Assert.True(requests.Count >= 16, $"expected the zoo to contain at least 16 diagrams, found {requests.Count}");

        await using var renderer = NewRenderer();
        var failures = new List<string>();
        foreach (var req in requests)
        {
            var result = await renderer.RenderAsync(req, DiagramTheme.Light);
            if (result.Status != DiagramStatus.Ready || result.Png is null)
                failures.Add($"{req.Kind}: {result.Error ?? result.Status.ToString()}");
        }
        Assert.True(failures.Count == 0, "Kroki rejected:\n" + string.Join("\n", failures));
    }

    private static Markdig.MarkdownPipeline TerminalPipelineForTests() => Readmd.Terminal.TerminalPipeline.For(false);
}
