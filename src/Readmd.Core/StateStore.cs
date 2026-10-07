using System.Text.Json;
using System.Text.Json.Serialization;

namespace Readmd.Core;

/// <summary>
/// Small persisted viewer state (per-file reading position, search history, bookmarks), kept in
/// the platform state directory: <c>$XDG_STATE_HOME/readmd/state.json</c> (default
/// <c>~/.local/state</c>) on Unix, <c>%LOCALAPPDATA%\readmd\state.json</c> on Windows. Loading
/// and saving never throw; a corrupt file is simply replaced.
/// </summary>
public sealed class StateStore
{
    private const int MaxFiles = 300;

    public sealed class FileState
    {
        [JsonPropertyName("scroll")] public int Scroll { get; set; }
        [JsonPropertyName("topText")] public string? TopText { get; set; }
        [JsonPropertyName("sourceLine")] public int SourceLine { get; set; }
        [JsonPropertyName("opened")] public DateTime Opened { get; set; }
        [JsonPropertyName("bookmarks")] public Dictionary<string, int>? Bookmarks { get; set; }
    }

    private sealed class Document
    {
        [JsonPropertyName("files")] public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("searchHistory")] public List<string> SearchHistory { get; set; } = [];
        [JsonPropertyName("recent")] public List<string> Recent { get; set; } = [];
    }

    private readonly string _path;
    private readonly Document _doc;
    private readonly object _gate = new();
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public StateStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _doc = Load(_path);
    }

    /// <summary>Where the state file lives for this OS.</summary>
    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "readmd", "state.json");
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var baseDir = !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(baseDir, "readmd", "state.json");
    }

    private static Document Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions) ?? new Document();
        }
        catch { /* corrupt or unreadable: start fresh */ }
        return new Document();
    }

    public FileState? Get(string filePath)
    {
        lock (_gate) return _doc.Files.TryGetValue(Path.GetFullPath(filePath), out var s) ? s : null;
    }

    public void SetPosition(string filePath, int scroll, string? topText, int sourceLine)
    {
        lock (_gate)
        {
            var key = Path.GetFullPath(filePath);
            if (!_doc.Files.TryGetValue(key, out var s)) _doc.Files[key] = s = new FileState();
            s.Scroll = scroll; s.TopText = topText; s.SourceLine = sourceLine; s.Opened = DateTime.UtcNow;
            _doc.Recent.Remove(key);
            _doc.Recent.Insert(0, key);
            if (_doc.Recent.Count > 50) _doc.Recent.RemoveRange(50, _doc.Recent.Count - 50);
            _dirty = true;
        }
    }

    public void SetBookmark(string filePath, string mark, int scroll)
    {
        lock (_gate)
        {
            var key = Path.GetFullPath(filePath);
            if (!_doc.Files.TryGetValue(key, out var s)) _doc.Files[key] = s = new FileState { Opened = DateTime.UtcNow };
            s.Bookmarks ??= new Dictionary<string, int>();
            s.Bookmarks[mark] = scroll;
            _dirty = true;
        }
    }

    public IReadOnlyList<string> SearchHistory { get { lock (_gate) return _doc.SearchHistory.ToList(); } }

    public void AddSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        lock (_gate)
        {
            _doc.SearchHistory.Remove(query);
            _doc.SearchHistory.Add(query);
            if (_doc.SearchHistory.Count > 100) _doc.SearchHistory.RemoveAt(0);
            _dirty = true;
        }
    }

    public IReadOnlyList<string> Recent { get { lock (_gate) return _doc.Recent.Where(File.Exists).ToList(); } }

    /// <summary>Writes the state if anything changed. Safe to call often (no-op when clean).</summary>
    public void Save()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try
            {
                // Keep the file bounded: drop the least recently opened entries.
                if (_doc.Files.Count > MaxFiles)
                {
                    foreach (var old in _doc.Files.OrderBy(kv => kv.Value.Opened).Take(_doc.Files.Count - MaxFiles).Select(kv => kv.Key).ToList())
                        _doc.Files.Remove(old);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_doc, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
                _dirty = false;
            }
            catch { /* best effort */ }
        }
    }
}
