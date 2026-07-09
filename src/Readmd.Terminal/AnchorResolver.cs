using System.Text;

namespace Readmd.Terminal;

/// <summary>
/// Resolves in-document <c>#fragment</c> links against the anchor map built while rendering
/// (heading ids plus explicit HTML <c>&lt;a id&gt;</c>/<c>&lt;a name&gt;</c> anchors). Mirrors the
/// browser: exact id match first (after URL-decoding the fragment), then a slug-tolerant fallback so
/// GitHub-style author anchors (e.g. <c>#1-intro</c>, <c>#a--b</c>) still resolve against Markdig's
/// default auto-identifier ids (<c>intro</c>, <c>a-b</c>). Kept separate from the viewer so it can be
/// unit-tested in isolation.
/// </summary>
public static class AnchorResolver
{
    /// <summary>
    /// Returns the display-line index a fragment should scroll to, or -1 when nothing matches.
    /// </summary>
    public static int Resolve(IReadOnlyDictionary<string, int> anchors, string rawAnchor)
    {
        if (anchors is null || anchors.Count == 0 || string.IsNullOrEmpty(rawAnchor)) return -1;

        // Browsers URL-decode the fragment before matching (e.g. "#caf%C3%A9" -> "#café").
        string anchor;
        try { anchor = Uri.UnescapeDataString(rawAnchor); }
        catch { anchor = rawAnchor; }

        // 1) Exact id match (explicit HTML anchors and heading ids; the map is case-insensitive).
        if (anchors.TryGetValue(anchor, out var exact)) return exact;
        if (!string.Equals(anchor, rawAnchor, StringComparison.Ordinal)
            && anchors.TryGetValue(rawAnchor, out var exactRaw)) return exactRaw;

        // 2) Slug-tolerant match. Only reached when nothing matched exactly, so it can't hijack a
        //    correct anchor; the lowest line index wins for determinism.
        var want = Normalize(anchor);
        if (want.Length == 0) return -1;
        int best = -1;
        foreach (var (id, lineIndex) in anchors)
            if (Normalize(id) == want && (best < 0 || lineIndex < best))
                best = lineIndex;
        return best;
    }

    /// <summary>
    /// Canonicalizes an anchor/slug for tolerant matching: lower-cases, reduces every run of
    /// non-alphanumerics to a single '-', trims dashes, and drops a leading numeric prefix (which
    /// GitHub keeps in slugs but Markdig's default auto-identifier strips).
    /// </summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool lastDash = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            bool alnum = ch is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (alnum) { sb.Append(ch); lastDash = false; }
            else if (!lastDash) { sb.Append('-'); lastDash = true; }
        }
        var norm = sb.ToString().Trim('-');
        int i = 0;
        while (i < norm.Length && norm[i] is >= '0' and <= '9') i++;
        while (i < norm.Length && norm[i] == '-') i++;
        // Keep the original when stripping would empty it (e.g. a purely numeric heading like "2024").
        return i < norm.Length ? norm[i..] : norm;
    }
}
