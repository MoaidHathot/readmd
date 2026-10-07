using Readmd.Core;
using Readmd.Diagrams;

namespace Readmd.Tests;

/// <summary>
/// Live Kroki round-trip. Opt-in (READMD_LIVE_TESTS=1) because it needs the network; skipped
/// otherwise so the suite stays hermetic.
/// </summary>
public class KrokiLiveTests
{
    [SkippableFact]
    public async Task Ditaa_renders_through_kroki()
    {
        Skip.If(Environment.GetEnvironmentVariable("READMD_LIVE_TESTS") != "1", "set READMD_LIVE_TESTS=1 to run network tests");
        await using var renderer = new DiagramRenderer(new DiagramRendererOptions
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "readmd-live-" + Guid.NewGuid().ToString("N")),
        });
        var req = DiagramRequest.Create(DiagramKind.Ditaa, "+-------+     +-------+\n| Alpha |---->| Beta  |\n+-------+     +-------+");
        var result = await renderer.RenderAsync(req, DiagramTheme.Light);
        Assert.Equal(DiagramStatus.Ready, result.Status);
        Assert.Equal("kroki", result.Backend);
        Assert.NotNull(result.Png);
        Assert.True(result.PixelWidth > 50 && result.PixelHeight > 20);
    }
}
