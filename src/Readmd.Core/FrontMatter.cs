using System.Diagnostics.CodeAnalysis;
using Markdig.Syntax;

namespace Readmd.Core;

/// <summary>
/// The YAML front-matter of a document, parsed into simple key/value pairs.
/// </summary>
/// <remarks>
/// This is a deliberately small, dependency-free parser that covers the common subset of
/// front-matter used in Markdown docs: top-level <c>key: value</c> scalars (optionally quoted),
/// and simple inline or block string lists. It does not attempt to be a full YAML implementation;
/// nested maps and anchors are ignored. Keys are matched case-insensitively.
/// </remarks>
public sealed class FrontMatter
{
    private readonly Dictionary<string, string> _scalars;
    private readonly Dictionary<string, IReadOnlyList<string>> _lists;

    /// <summary>An empty front matter (no keys).</summary>
    public static FrontMatter Empty { get; } = new(new(), new());

    private FrontMatter(Dictionary<string, string> scalars, Dictionary<string, IReadOnlyList<string>> lists)
    {
        _scalars = scalars;
        _lists = lists;
    }

    /// <summary>True if the document had no front-matter keys.</summary>
    public bool IsEmpty => _scalars.Count == 0 && _lists.Count == 0;

    /// <summary>All scalar key/value pairs (for enumeration).</summary>
    public IReadOnlyDictionary<string, string> Scalars => _scalars;

    /// <summary>Gets a scalar value by key (case-insensitive), or null if absent.</summary>
    public string? Get(string key) => _scalars.TryGetValue(key, out var v) ? v : null;

    /// <summary>Tries to get a scalar value by key (case-insensitive).</summary>
    public bool TryGet(string key, [NotNullWhen(true)] out string? value) => _scalars.TryGetValue(key, out value);

    /// <summary>Gets a list value by key (case-insensitive), or null if absent / not a list.</summary>
    public IReadOnlyList<string>? GetList(string key) => _lists.TryGetValue(key, out var v) ? v : null;

    /// <summary>Parses a YAML front-matter block found in the Markdig AST.</summary>
    internal static FrontMatter FromBlock(Markdig.Extensions.Yaml.YamlFrontMatterBlock block)
    {
        var sb = new System.Text.StringBuilder();
        var lines = block.Lines.Lines;
        for (var i = 0; i < block.Lines.Count; i++)
        {
            sb.Append(lines[i].Slice.ToString());
            sb.Append('\n');
        }
        return Parse(sb.ToString());
    }

    /// <summary>
    /// Detects and strips a TOML front-matter block (<c>+++ … +++</c>, as used by Hugo/Zola) from
    /// the start of a document, since Markdig only understands the YAML form. Returns the document
    /// without the block and the parsed metadata; when there is no TOML block the input is returned
    /// unchanged with <c>null</c>.
    /// </summary>
    public static (string Markdown, FrontMatter? FrontMatter) StripToml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return (markdown, null);
        int start = markdown[0] == '\uFEFF' ? 1 : 0;
        if (string.CompareOrdinal(markdown, start, "+++", 0, 3) != 0) return (markdown, null);
        int firstNl = markdown.IndexOf('\n', start);
        if (firstNl < 0 || markdown[start..firstNl].TrimEnd('\r').Trim() != "+++") return (markdown, null);
        // Closing fence on its own line.
        var m = System.Text.RegularExpressions.Regex.Match(markdown[(firstNl + 1)..], @"^\+\+\+[ \t]*\r?$", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!m.Success) return (markdown, null);
        var body = markdown.Substring(firstNl + 1, m.Index);
        int after = firstNl + 1 + m.Index + m.Length;
        if (after < markdown.Length && markdown[after] == '\n') after++;
        return (markdown[after..], ParseToml(body));
    }

    /// <summary>Parses the common subset of TOML used in front matter: <c>key = "value"</c>, numbers,
    /// booleans, dates and inline arrays of strings. Tables (<c>[section]</c>) are flattened as <c>section.key</c>.</summary>
    public static FrontMatter ParseToml(string toml)
    {
        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        string prefix = "";
        foreach (var raw in toml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { prefix = line.Trim('[', ']').Trim() + "."; continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = prefix + line[..eq].Trim().Trim('"');
            var value = line[(eq + 1)..].Trim();
            int hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0 && !value.StartsWith('"') && !value.StartsWith('\'')) value = value[..hash].Trim();
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                lists[key] = value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Unquote).ToList();
            }
            else scalars[key] = Unquote(value);
        }
        return new FrontMatter(scalars, lists);
    }

    /// <summary>Parses raw front-matter text (without the surrounding <c>---</c> fences).</summary>
    public static FrontMatter Parse(string yaml)
    {
        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(yaml)) return new FrontMatter(scalars, lists);

        var rawLines = yaml.Replace("\r\n", "\n").Split('\n');
        string? pendingListKey = null;
        List<string>? pendingList = null;

        void FlushList()
        {
            if (pendingListKey is not null && pendingList is not null)
                lists[pendingListKey] = pendingList;
            pendingListKey = null;
            pendingList = null;
        }

        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.TrimStart().StartsWith('#')) continue; // comment

            // Block list item:  "- value" (indented under a "key:" line)
            var trimmed = line.TrimStart();
            if (pendingListKey is not null && trimmed.StartsWith("- "))
            {
                pendingList!.Add(Unquote(trimmed[2..].Trim()));
                continue;
            }

            var colon = FindKeyColon(line);
            if (colon < 0) { FlushList(); continue; }

            FlushList();
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0) continue;

            if (value.Length == 0)
            {
                // Possibly a block list follows on subsequent indented "- " lines.
                pendingListKey = key;
                pendingList = [];
                continue;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                var inner = value[1..^1];
                var items = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(Unquote).ToList();
                lists[key] = items;
            }
            else
            {
                scalars[key] = Unquote(value);
            }
        }

        FlushList();
        return new FrontMatter(scalars, lists);
    }

    // Find the colon that separates a top-level key from its value (skip colons inside quotes).
    private static int FindKeyColon(string line)
    {
        // Only treat as a key/value line when it isn't indented (top-level scalar).
        if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) return -1;
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ':' && !inSingle && !inDouble)
            {
                // "key:" or "key: value" — the colon must be followed by end-of-line or whitespace.
                if (i + 1 >= line.Length || line[i + 1] == ' ' || line[i + 1] == '\t') return i;
            }
        }
        return -1;
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            return v[1..^1];
        return v;
    }
}
