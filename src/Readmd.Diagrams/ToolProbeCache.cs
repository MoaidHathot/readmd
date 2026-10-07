using System.Collections.Concurrent;
using System.Globalization;

namespace Readmd.Diagrams;

/// <summary>
/// Remembers the outcome of "does this tool exist / which version is it" probes across runs so a
/// <c>--print</c> or a viewer start doesn't spawn <c>d2 --version</c> / <c>mmdc --version</c> (each
/// 100 ms – 2 s) every time. Entries are keyed by the tool's resolved path and invalidated when the
/// executable's size or modification time changes (an upgrade); negative results expire after ten
/// minutes so a transiently broken tool is retried soon.
/// </summary>
internal static class ToolProbeCache
{
    private sealed class Entry
    {
        public long Mtime { get; set; }
        public long Size { get; set; }
        public string Value { get; set; } = "";
        public DateTime StoredUtc { get; set; }
        public bool Negative { get; set; }
    }

    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, string> Memory = new();
    private static Dictionary<string, Entry>? _disk;

    // A tab-separated line per entry rather than JSON: System.Text.Json's first use costs ~25 ms of
    // JIT, which is most of what this cache exists to save on a `--print` run.
    /// <summary>Where the cache lives (tests can redirect it).</summary>
    internal static string Path_ { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "readmd", "tool-probes.tsv");

    /// <summary>Set to true to bypass the disk cache (tests).</summary>
    internal static bool Disabled { get; set; }

    /// <summary>The cached value for <paramref name="probe"/> of the tool at <paramref name="resolvedPath"/>, or null.</summary>
    public static string? TryGet(string probe, string resolvedPath)
    {
        if (Disabled) return null;
        var key = probe + "|" + resolvedPath;
        if (Memory.TryGetValue(key, out var mem)) return mem;
        try
        {
            var info = new FileInfo(resolvedPath);
            if (!info.Exists) return null;
            lock (Gate)
            {
                _disk ??= Load();
                if (!_disk.TryGetValue(key, out var e)) return null;
                if (e.Mtime != info.LastWriteTimeUtc.Ticks || e.Size != info.Length) return null;
                if (e.Negative && DateTime.UtcNow - e.StoredUtc > NegativeTtl) return null;
                Memory[key] = e.Value;
                return e.Value;
            }
        }
        catch { return null; }
    }

    /// <summary>Records a probe result. <paramref name="negative"/> results are kept only briefly.</summary>
    public static void Store(string probe, string resolvedPath, string value, bool negative = false)
    {
        var key = probe + "|" + resolvedPath;
        Memory[key] = value;
        if (Disabled) return;
        try
        {
            var info = new FileInfo(resolvedPath);
            if (!info.Exists) return;
            lock (Gate)
            {
                _disk ??= Load();
                _disk[key] = new Entry { Mtime = info.LastWriteTimeUtc.Ticks, Size = info.Length, Value = value, StoredUtc = DateTime.UtcNow, Negative = negative };
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
                var tmp = Path_ + ".tmp";
                var sb = new System.Text.StringBuilder();
                foreach (var (k, e) in _disk)
                    sb.Append(k).Append('\t').Append(e.Mtime).Append('\t').Append(e.Size).Append('\t')
                      .Append(e.StoredUtc.Ticks).Append('\t').Append(e.Negative ? '1' : '0').Append('\t').Append(e.Value.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, Path_, overwrite: true);
            }
        }
        catch { /* best effort */ }
    }

    private static Dictionary<string, Entry> Load()
    {
        var result = new Dictionary<string, Entry>();
        try
        {
            if (!File.Exists(Path_)) return result;
            foreach (var line in File.ReadLines(Path_))
            {
                var f = line.Split('\t');
                if (f.Length < 6) continue;
                result[f[0]] = new Entry
                {
                    Mtime = long.Parse(f[1], CultureInfo.InvariantCulture),
                    Size = long.Parse(f[2], CultureInfo.InvariantCulture),
                    StoredUtc = new DateTime(long.Parse(f[3], CultureInfo.InvariantCulture), DateTimeKind.Utc),
                    Negative = f[4] == "1",
                    Value = f[5],
                };
            }
        }
        catch { result.Clear(); /* corrupt cache: start over */ }
        return result;
    }

    /// <summary>Forgets everything (tests).</summary>
    internal static void Reset()
    {
        Memory.Clear();
        lock (Gate) _disk = null;
    }
}
