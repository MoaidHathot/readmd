using Readmd.Diagrams;

namespace Readmd.Tests;

/// <summary>The on-disk memo for tool probes (d2/mmdc versions) that keeps --print from spawning processes every run.</summary>
public class ToolProbeCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "readmd-probe-" + Guid.NewGuid().ToString("N"));
    private readonly string _savedPath;

    public ToolProbeCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _savedPath = ToolProbeCache.Path_;
        ToolProbeCache.Path_ = Path.Combine(_dir, "probes.tsv");
        ToolProbeCache.Disabled = false;   // the test run disables the disk memo globally (TestSetup)
        ToolProbeCache.Reset();
    }

    public void Dispose()
    {
        ToolProbeCache.Path_ = _savedPath;
        ToolProbeCache.Disabled = true;
        ToolProbeCache.Reset();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Round_trips_through_disk_and_invalidates_when_the_tool_changes()
    {
        var tool = Path.Combine(_dir, "d2.exe");
        File.WriteAllText(tool, "v1");
        Assert.Null(ToolProbeCache.TryGet("d2-ascii", tool));
        ToolProbeCache.Store("d2-ascii", tool, "0");
        Assert.Equal("0", ToolProbeCache.TryGet("d2-ascii", tool));

        // A fresh process (memory cleared) reads it back from disk.
        ToolProbeCache.Reset();
        Assert.Equal("0", ToolProbeCache.TryGet("d2-ascii", tool));
        Assert.True(File.Exists(ToolProbeCache.Path_));

        // Upgrading the tool (size/mtime change) drops the memo.
        File.WriteAllText(tool, "v2 — a newer, larger build");
        File.SetLastWriteTimeUtc(tool, DateTime.UtcNow.AddMinutes(1));
        ToolProbeCache.Reset();
        Assert.Null(ToolProbeCache.TryGet("d2-ascii", tool));

        // Different probes of the same tool are independent; unknown tools are never cached.
        ToolProbeCache.Store("d2", tool, "ok");
        Assert.Null(ToolProbeCache.TryGet("d2-ascii", tool));
        Assert.Equal("ok", ToolProbeCache.TryGet("d2", tool));
        Assert.Null(ToolProbeCache.TryGet("d2", Path.Combine(_dir, "missing.exe")));
    }

    [Fact]
    public void Negative_results_expire_and_corrupt_files_are_ignored()
    {
        var tool = Path.Combine(_dir, "mmdc.cmd");
        File.WriteAllText(tool, "@echo off");
        ToolProbeCache.Store("mmdc", tool, "fail", negative: true);
        Assert.Equal("fail", ToolProbeCache.TryGet("mmdc", tool));
        // Age the stored entry past the negative TTL by rewriting its timestamp column.
        var lines = File.ReadAllLines(ToolProbeCache.Path_);
        var f = lines[0].Split('\t');
        f[3] = DateTime.UtcNow.AddHours(-1).Ticks.ToString();
        File.WriteAllLines(ToolProbeCache.Path_, [string.Join('\t', f)]);
        ToolProbeCache.Reset();
        Assert.Null(ToolProbeCache.TryGet("mmdc", tool));

        File.WriteAllText(ToolProbeCache.Path_, "garbage\twithout\tenough");
        ToolProbeCache.Reset();
        Assert.Null(ToolProbeCache.TryGet("mmdc", tool));
        ToolProbeCache.Store("mmdc", tool, "ok");   // writes cleanly over the corrupt file
        ToolProbeCache.Reset();
        Assert.Equal("ok", ToolProbeCache.TryGet("mmdc", tool));
    }
}
