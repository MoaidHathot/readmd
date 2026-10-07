namespace Readmd.Core;

/// <summary>
/// Enumerates and searches the Markdown files under a sandbox root, for the browser's and the
/// terminal's project-wide search and quick-open palette. Bounded by file-count and file-size caps
/// so a huge folder can't make readmd slow or memory-hungry.
/// </summary>
public static class WikiIndex
{
    private const int MaxFiles = 2000;
    private const long MaxFileBytes = 2 * 1024 * 1024; // skip files larger than 2 MB
    private const int MaxResults = 100;
    private const int MaxPerFile = 5;

    /// <summary>A markdown file in the sandbox, with a path relative to the root for display.</summary>
    public sealed record TreeEntry(string Path, string Relative, string Title);

    /// <summary>One search hit: the file, the matching line number, and a trimmed snippet.</summary>
    public sealed record SearchHit(string Path, string Relative, string Title, int Line, string Snippet);

    /// <summary>Lists markdown files under <paramref name="root"/> (sorted, capped), skipping common noise dirs.</summary>
    public static IReadOnlyList<TreeEntry> ListFiles(string root, bool withTitles = true)
    {
        var results = new List<TreeEntry>();
        foreach (var path in EnumerateMarkdown(root))
        {
            var rel = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
            results.Add(new TreeEntry(path, rel, withTitles ? TitleFor(path, rel) : System.IO.Path.GetFileName(rel)));
            if (results.Count >= MaxFiles) break;
        }
        results.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>Searches all markdown files for <paramref name="query"/> (case-insensitive substring).</summary>
    public static IReadOnlyList<SearchHit> Search(string root, string query, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return hits;

        foreach (var path in EnumerateMarkdown(root))
        {
            if (ct.IsCancellationRequested || hits.Count >= MaxResults) break;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes) continue;

                var rel = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                string? title = null;
                int perFile = 0;
                int lineNo = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNo++;
                    var idx = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    title ??= TitleFor(path, rel);
                    hits.Add(new SearchHit(path, rel, title, lineNo, Snippet(line, idx, query.Length)));
                    if (++perFile >= MaxPerFile) break;
                    if (hits.Count >= MaxResults) break;
                }
            }
            catch { /* unreadable file - skip */ }
        }
        return hits;
    }

    /// <summary>
    /// Fuzzy-filters entries the way editor quick-open palettes do: every query character must
    /// appear in order; matches at word/path-segment starts, consecutive runs and the file name
    /// score higher. Returns entries ordered best-first (all entries when the query is empty).
    /// </summary>
    public static IReadOnlyList<TreeEntry> FuzzyFilter(IReadOnlyList<TreeEntry> entries, string query, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(query)) return entries.Take(max).ToList();
        var scored = new List<(int Score, TreeEntry Entry)>();
        foreach (var e in entries)
        {
            int pathScore = FuzzyScore(e.Relative, query);
            int titleScore = FuzzyScore(e.Title, query);
            if (titleScore != int.MinValue) titleScore -= 2;   // a path match is slightly preferred (guard: MinValue - 2 would overflow)
            int best = Math.Max(pathScore, titleScore);
            if (best > int.MinValue) scored.Add((best, e));
        }
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Entry.Relative.Length).Select(s => s.Entry).Take(max).ToList();
    }

    /// <summary>Score of <paramref name="query"/> against <paramref name="text"/>, or int.MinValue when it doesn't match.</summary>
    public static int FuzzyScore(string text, string query)
    {
        if (query.Length == 0) return 0;
        int score = 0, ti = 0, lastMatch = -2;
        int nameStart = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\')) + 1;
        foreach (var qc in query)
        {
            int found = -1;
            for (int i = ti; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(qc)) { found = i; break; }
            }
            if (found < 0) return int.MinValue;
            score += 1;
            if (found == lastMatch + 1) score += 3;                       // consecutive
            if (found == 0 || "/\\-_. ".Contains(text[found - 1])) score += 4;   // segment / word start
            if (found >= nameStart) score += 2;                            // in the file name
            if (char.IsUpper(text[found]) && found > 0 && char.IsLower(text[found - 1])) score += 2; // camelCase hump
            lastMatch = found;
            ti = found + 1;
        }
        return score - (text.Length / 16);   // shorter paths win ties
    }

    private static IEnumerable<string> EnumerateMarkdown(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MaxRecursionDepth = 16,
        };
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", options);
        }
        catch { yield break; }

        foreach (var path in files)
        {
            if (!LinkResolver.IsMarkdownFile(path)) continue;
            if (IsNoisePath(System.IO.Path.GetRelativePath(root, path))) continue;
            yield return path;
        }
    }

    /// <summary>
    /// True when the path passes through a directory that never holds a user's own documentation
    /// (dependency caches, build output, VCS metadata). Applied to recursive enumeration and to the
    /// wildcard part of a glob — never to a directory the user named explicitly.
    /// </summary>
    public static bool IsNoisePath(string path)
    {
        var norm = "/" + path.Replace('\\', '/').Trim('/') + "/";
        return norm.Contains("/node_modules/") || norm.Contains("/.git/") || norm.Contains("/bin/") || norm.Contains("/obj/")
            || norm.Contains("/.venv/") || norm.Contains("/venv/") || norm.Contains("/target/") || norm.Contains("/dist/") || norm.Contains("/.next/");
    }

    // A display title: the first ATX/Setext H1 if present, else the file name.
    public static string TitleFor(string path, string relative)
    {
        try
        {
            string? prev = null;
            foreach (var raw in File.ReadLines(path).Take(40))
            {
                var line = raw.Trim();
                if (line.StartsWith("# ")) return line[2..].Trim();
                if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase) && line.Length > 6) return line[6..].Trim().Trim('"', '\'');
                if (prev is not null && line.Length >= 3 && line.All(c => c == '=') ) return prev;
                prev = line.Length > 0 ? line : null;
            }
        }
        catch { /* ignore */ }
        return System.IO.Path.GetFileName(relative);
    }

    private static string Snippet(string line, int matchStart, int matchLen)
    {
        const int pad = 40;
        var start = Math.Max(0, matchStart - pad);
        var end = Math.Min(line.Length, matchStart + matchLen + pad);
        var s = line[start..end].Trim();
        if (start > 0) s = "…" + s;
        if (end < line.Length) s += "…";
        return s;
    }
}
