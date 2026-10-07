using System.Globalization;

namespace Readmd.Terminal;

/// <summary>
/// Computes the terminal display width of text, accounting for East-Asian wide/fullwidth glyphs
/// (counted as 2 columns), zero-width combining marks/variation selectors (0), and grapheme
/// clusters such as emoji ZWJ sequences and flags (counted once at the base character's width).
/// Shared by the body word-wrapper and the table layout so widths stay consistent.
/// </summary>
internal static class TextWidth
{
    /// <summary>Display width (in terminal cells) of a whole string.</summary>
    public static int Of(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        // Fast path: pure ASCII (the overwhelmingly common case) is one cell per char.
        bool ascii = true;
        foreach (var ch in s) { if (ch >= 0x80 || ch < 0x20) { ascii = false; break; } }
        if (ascii) return s.Length;
        int width = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
            width += ElementWidth((string)e.Current);
        return width;
    }

    /// <summary>Display width of the first <paramref name="charCount"/> chars of <paramref name="s"/>.</summary>
    public static int OfPrefix(string s, int charCount)
    {
        if (string.IsNullOrEmpty(s) || charCount <= 0) return 0;
        if (charCount >= s.Length) return Of(s);
        return Of(s[..charCount]);
    }

    /// <summary>
    /// Maps a display column to the char index of the grapheme that covers it (so a selection or
    /// search column measured on screen can be turned back into a string offset). Returns
    /// <c>s.Length</c> when the column is past the end of the text.
    /// </summary>
    public static int CharIndexAtColumn(string s, int column)
    {
        if (string.IsNullOrEmpty(s) || column <= 0) return 0;
        int width = 0, index = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var g = (string)e.Current;
            int gw = ElementWidth(g);
            if (width + gw > column) return index;
            width += gw;
            index += g.Length;
        }
        return s.Length;
    }

    /// <summary>Width of a single grapheme cluster (text element).</summary>
    public static int ElementWidth(string element)
    {
        if (element.Length == 0) return 0;
        // A cluster's width is governed by its first scalar; combining marks/selectors add 0.
        int cp = char.ConvertToUtf32(element, 0);
        if (cp == 0) return 0;
        if (IsZeroWidth(cp)) return 0;
        // An emoji variation selector (U+FE0F) forces emoji presentation → wide, even for a base
        // symbol that is narrow by default (e.g. "✔️" vs the plain "✔").
        if (element.Contains('\uFE0F')) return 2;
        // Emoji presentation (incl. ZWJ sequences, flags, skin tones) render as 2 cells.
        if (IsEmoji(cp)) return 2;
        return IsWide(cp) ? 2 : 1;
    }

    /// <summary>Enumerates grapheme clusters of a string (so callers can wrap without splitting emoji).</summary>
    public static IEnumerable<string> Graphemes(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) yield return (string)e.Current;
    }

    /// <summary>
    /// Returns the longest prefix of <paramref name="s"/> whose display width does not exceed
    /// <paramref name="maxWidth"/>, cutting on grapheme boundaries so a wide glyph is never split.
    /// </summary>
    public static string TrimToWidth(string s, int maxWidth)
    {
        if (maxWidth <= 0 || string.IsNullOrEmpty(s)) return "";
        int width = 0;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var g in Graphemes(s))
        {
            int gw = ElementWidth(g);
            if (width + gw > maxWidth) break;
            sb.Append(g);
            width += gw;
        }
        return sb.ToString();
    }

    private static bool IsZeroWidth(int cp) =>
        cp == 0x200B ||                              // zero-width space
        cp == 0x200D ||                              // zero-width joiner
        (cp >= 0x0300 && cp <= 0x036F) ||            // combining diacritical marks
        (cp >= 0x1AB0 && cp <= 0x1AFF) ||
        (cp >= 0x1DC0 && cp <= 0x1DFF) ||
        (cp >= 0x20D0 && cp <= 0x20FF) ||            // combining marks for symbols
        (cp >= 0xFE00 && cp <= 0xFE0F) ||            // variation selectors
        (cp >= 0xFE20 && cp <= 0xFE2F);              // combining half marks

    // Only code points that are wide (2 cells) by DEFAULT. Most of the Miscellaneous Symbols and
    // Dingbats blocks (0x2600–0x27BF) are Narrow/Neutral text symbols rendered in one cell (e.g. ✓
    // U+2713, ✗, arrows). Treating the whole block as wide mis-measured tables. These are the
    // specific points/ranges with Unicode Emoji_Presentation=Yes (default-wide), per UTS #51.
    private static bool IsEmoji(int cp) =>
        (cp >= 0x1F000 && cp <= 0x1FAFF) ||          // emoji/pictographs in the SMP (all wide)
        (cp >= 0x1F1E6 && cp <= 0x1F1FF) ||          // regional indicators (flags) — covered above too
        cp is 0x231A or 0x231B ||
        (cp >= 0x23E9 && cp <= 0x23EC) || cp is 0x23F0 or 0x23F3 ||
        (cp >= 0x25FD && cp <= 0x25FE) ||
        (cp >= 0x2614 && cp <= 0x2615) ||
        (cp >= 0x2648 && cp <= 0x2653) ||
        cp is 0x267F or 0x2693 or 0x26A1 ||
        (cp >= 0x26AA && cp <= 0x26AB) ||
        (cp >= 0x26BD && cp <= 0x26BE) ||
        (cp >= 0x26C4 && cp <= 0x26C5) ||
        cp is 0x26CE or 0x26D4 or 0x26EA ||
        (cp >= 0x26F2 && cp <= 0x26F3) || cp is 0x26F5 or 0x26FA or 0x26FD ||
        cp is 0x2705 ||
        (cp >= 0x270A && cp <= 0x270B) ||
        cp is 0x2728 or 0x274C or 0x274E ||
        (cp >= 0x2753 && cp <= 0x2755) || cp is 0x2757 ||
        (cp >= 0x2795 && cp <= 0x2797) ||
        cp is 0x27B0 or 0x27BF ||
        (cp >= 0x2B1B && cp <= 0x2B1C) || cp is 0x2B50 or 0x2B55;

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||            // Hangul Jamo
        cp == 0x2329 || cp == 0x232A ||
        (cp >= 0x2E80 && cp <= 0x303E) ||            // CJK radicals … Kangxi
        (cp >= 0x3041 && cp <= 0x33FF) ||            // Hiragana … CJK compatibility
        (cp >= 0x3400 && cp <= 0x4DBF) ||            // CJK Ext A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||            // CJK Unified
        (cp >= 0xA000 && cp <= 0xA4CF) ||            // Yi
        (cp >= 0xAC00 && cp <= 0xD7A3) ||            // Hangul Syllables
        (cp >= 0xF900 && cp <= 0xFAFF) ||            // CJK Compatibility Ideographs
        (cp >= 0xFE30 && cp <= 0xFE4F) ||            // CJK Compatibility Forms
        (cp >= 0xFF00 && cp <= 0xFF60) ||            // Fullwidth Forms
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||
        (cp >= 0x20000 && cp <= 0x3FFFD);            // CJK Ext B+ (supplementary ideographic planes)
}
