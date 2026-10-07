using System.IO.Enumeration;

namespace Readmd.Core;

/// <summary>
/// Expands command-line document arguments: a plain path, a directory (all Markdown files beneath
/// it, recursively), or a glob such as <c>docs/**/*.md</c> or <c>*.md</c>. Shells on Windows don't
/// expand globs, and even on Unix a quoted <c>"**/*.md"</c> reaches us verbatim.
/// </summary>
public static class FileArguments
{
    /// <summary>
    /// Resolves one argument to absolute file paths. <paramref name="isDirectory"/> is true when the
    /// argument named a folder (so the caller can open a picker rather than the first file).
    /// </summary>
    public static IReadOnlyList<string> Expand(string arg, out bool isDirectory)
    {
        isDirectory = false;
        if (Directory.Exists(arg))
        {
            isDirectory = true;
            var root = Path.GetFullPath(arg);
            return WikiIndex.ListFiles(root, withTitles: false).Select(e => e.Path).ToList();
        }
        if (File.Exists(arg)) return [Path.GetFullPath(arg)];
        if (!HasGlob(arg)) return [];

        // Split the glob into a literal base directory and a pattern relative to it.
        var normalized = arg.Replace('\\', '/');
        var segments = normalized.Split('/');
        int firstWild = Array.FindIndex(segments, s => s.IndexOfAny(['*', '?', '[']) >= 0);
        var baseDir = firstWild <= 0 ? "." : string.Join('/', segments.Take(firstWild));
        if (firstWild == 0 && normalized.StartsWith('/')) baseDir = "/";
        var pattern = string.Join('/', segments.Skip(Math.Max(0, firstWild)));
        var fullBase = Path.GetFullPath(baseDir);
        if (!Directory.Exists(fullBase)) return [];

        var results = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = pattern.Contains("**") || pattern.Contains('/'), IgnoreInaccessible = true, MaxRecursionDepth = 16 };
        foreach (var file in Directory.EnumerateFiles(fullBase, "*", options))
        {
            var rel = Path.GetRelativePath(fullBase, file).Replace('\\', '/');
            // The literal base is what the user asked for; only the wildcard-matched part is filtered.
            if (WikiIndex.IsNoisePath(rel)) continue;
            if (GlobMatch(pattern, rel)) results.Add(file);
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    public static bool HasGlob(string arg) => arg.IndexOfAny(['*', '?', '[']) >= 0;

    /// <summary>Matches a relative path against a glob with <c>*</c>, <c>?</c>, <c>[…]</c> and <c>**</c>.</summary>
    public static bool GlobMatch(string pattern, string path)
    {
        // "**/" matches zero or more directories; "**" alone matches everything.
        if (pattern == "**" || pattern == "**/*") return true;
        var pSegs = pattern.Split('/');
        var tSegs = path.Split('/');
        return MatchSegments(pSegs, 0, tSegs, 0);
    }

    private static bool MatchSegments(string[] p, int pi, string[] t, int ti)
    {
        while (pi < p.Length)
        {
            if (p[pi] == "**")
            {
                // Try consuming 0..n path segments.
                for (int skip = 0; ti + skip <= t.Length; skip++)
                    if (MatchSegments(p, pi + 1, t, ti + skip)) return true;
                return false;
            }
            if (ti >= t.Length) return false;
            if (!FileSystemName.MatchesSimpleExpression(p[pi], t[ti], ignoreCase: true)) return false;
            pi++; ti++;
        }
        return ti == t.Length;
    }

    /// <summary>The deepest directory that contains every path (for sandboxing a multi-file set).</summary>
    public static string CommonDirectory(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Directory.GetCurrentDirectory();
        var parts = paths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToList();
        int n = parts.Min(a => a.Length);
        int common = 0;
        for (; common < n; common++)
        {
            var seg = parts[0][common];
            if (!parts.All(a => string.Equals(a[common], seg, StringComparison.OrdinalIgnoreCase))) break;
        }
        if (common == 0) return Path.GetPathRoot(paths[0]) ?? Directory.GetCurrentDirectory();
        var dir = string.Join(Path.DirectorySeparatorChar, parts[0].Take(common));
        if (dir.EndsWith(':')) dir += Path.DirectorySeparatorChar;   // "C:" → "C:\"
        return dir;
    }
}
