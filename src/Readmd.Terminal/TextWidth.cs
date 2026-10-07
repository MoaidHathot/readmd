using System.Globalization;

namespace Readmd.Terminal;

/// <summary>Public knobs for <see cref="TextWidth"/> (which is internal to the terminal assembly).</summary>
public static class TextWidthSettings
{
    /// <summary>Treat East Asian Ambiguous characters as wide (CJK-locale terminals).</summary>
    public static bool AmbiguousWide { get => TextWidth.AmbiguousWide; set => TextWidth.AmbiguousWide = value; }
}

/// <summary>
/// Computes the terminal display width of text, accounting for East-Asian wide/fullwidth glyphs
/// (counted as 2 columns), zero-width combining marks/variation selectors (0), and grapheme
/// clusters such as emoji ZWJ sequences and flags (counted once at the base character's width).
/// Shared by the body word-wrapper and the table layout so widths stay consistent. Ranges follow
/// Unicode 15.1 EastAsianWidth.txt (W/F) and UTS #51 Emoji_Presentation.
/// </summary>
internal static class TextWidth
{
    /// <summary>
    /// Treat East Asian Ambiguous characters (box drawing, ①, °, ±, Greek/Cyrillic …) as wide, as
    /// CJK-locale terminals do. Off by default (Western terminals render them narrow).
    /// </summary>
    public static bool AmbiguousWide { get; set; }

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
        if (cp < 0x20 || cp == 0x7F) return cp == '\t' ? 1 : 0;   // controls take no cell (tabs are expanded upstream)
        if (IsZeroWidth(cp)) return 0;
        // An emoji variation selector (U+FE0F) forces emoji presentation → wide, even for a base
        // symbol that is narrow by default (e.g. "✔️" vs the plain "✔"). A text selector (U+FE0E)
        // forces narrow.
        if (element.Contains('\uFE0E')) return 1;
        if (element.Contains('\uFE0F')) return 2;
        // Emoji presentation (incl. ZWJ sequences, flags, skin tones) render as 2 cells.
        if (IsEmoji(cp)) return 2;
        // A keycap sequence (digit + U+20E3) is wide.
        if (element.Contains('\u20E3')) return 2;
        if (IsWide(cp)) return 2;
        if (AmbiguousWide && IsAmbiguous(cp)) return 2;
        return 1;
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
        cp == 0x200B || cp == 0x200C || cp == 0x200D ||   // zero-width space / non-joiner / joiner
        cp == 0x2060 || cp == 0xFEFF ||                   // word joiner, BOM
        (cp >= 0x200E && cp <= 0x200F) ||                 // LRM/RLM
        (cp >= 0x202A && cp <= 0x202E) ||                 // bidi embedding controls
        (cp >= 0x2066 && cp <= 0x2069) ||                 // bidi isolates
        (cp >= 0x0300 && cp <= 0x036F) ||                 // combining diacritical marks
        (cp >= 0x0483 && cp <= 0x0489) ||                 // Cyrillic combining
        (cp >= 0x0591 && cp <= 0x05BD) || cp == 0x05BF || (cp >= 0x05C1 && cp <= 0x05C7 && cp != 0x05C3 && cp != 0x05C6) || // Hebrew points
        (cp >= 0x0610 && cp <= 0x061A) || (cp >= 0x064B && cp <= 0x065F) || cp == 0x0670 || (cp >= 0x06D6 && cp <= 0x06DC) || (cp >= 0x06DF && cp <= 0x06E4) || (cp >= 0x06E7 && cp <= 0x06E8) || (cp >= 0x06EA && cp <= 0x06ED) || // Arabic marks
        (cp >= 0x0900 && cp <= 0x0902) || cp == 0x093C || (cp >= 0x0941 && cp <= 0x0948) || cp == 0x094D || (cp >= 0x0951 && cp <= 0x0957) || (cp >= 0x0962 && cp <= 0x0963) || // Devanagari non-spacing
        (cp >= 0x0E31 && cp <= 0x0E31) || (cp >= 0x0E34 && cp <= 0x0E3A) || (cp >= 0x0E47 && cp <= 0x0E4E) || // Thai marks
        (cp >= 0x1AB0 && cp <= 0x1AFF) ||
        (cp >= 0x1DC0 && cp <= 0x1DFF) ||
        (cp >= 0x20D0 && cp <= 0x20F0) ||                 // combining marks for symbols
        (cp >= 0xFE00 && cp <= 0xFE0F) ||                 // variation selectors
        (cp >= 0xFE20 && cp <= 0xFE2F) ||                 // combining half marks
        (cp >= 0xE0100 && cp <= 0xE01EF) ||               // variation selectors supplement
        (cp >= 0xE0020 && cp <= 0xE007F) ||               // tags (flag sequences)
        (cp >= 0x1F3FB && cp <= 0x1F3FF);                 // skin tone modifiers (handled within clusters)

    // Only code points that are wide (2 cells) by DEFAULT. Most of the Miscellaneous Symbols and
    // Dingbats blocks (0x2600–0x27BF) are Narrow/Neutral text symbols rendered in one cell (e.g. ✓
    // U+2713, ✗, arrows). Treating the whole block as wide mis-measured tables. These are the
    // specific points/ranges with Unicode Emoji_Presentation=Yes (default-wide), per UTS #51.
    private static bool IsEmoji(int cp) =>
        (cp >= 0x1F300 && cp <= 0x1F64F) ||          // misc symbols & pictographs, emoticons
        (cp >= 0x1F680 && cp <= 0x1F6FF) ||          // transport & map
        (cp >= 0x1F900 && cp <= 0x1F9FF) ||          // supplemental symbols & pictographs
        (cp >= 0x1FA70 && cp <= 0x1FAFF) ||          // symbols & pictographs extended-A
        (cp >= 0x1F1E6 && cp <= 0x1F1FF) ||          // regional indicators (flags)
        (cp >= 0x1F004 && cp <= 0x1F004) || cp == 0x1F0CF || cp == 0x1F18E || (cp >= 0x1F191 && cp <= 0x1F19A) ||
        (cp >= 0x1F200 && cp <= 0x1F251) ||          // enclosed ideographic supplement
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
        (cp >= 0x2E80 && cp <= 0x303E) ||            // CJK radicals … Kangxi … CJK symbols
        (cp >= 0x3041 && cp <= 0x33FF) ||            // Hiragana … CJK compatibility
        (cp >= 0x3400 && cp <= 0x4DBF) ||            // CJK Ext A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||            // CJK Unified
        (cp >= 0xA000 && cp <= 0xA4CF) ||            // Yi
        (cp >= 0xA960 && cp <= 0xA97F) ||            // Hangul Jamo Extended-A
        (cp >= 0xAC00 && cp <= 0xD7A3) ||            // Hangul Syllables
        (cp >= 0xF900 && cp <= 0xFAFF) ||            // CJK Compatibility Ideographs
        (cp >= 0xFE10 && cp <= 0xFE19) ||            // vertical forms
        (cp >= 0xFE30 && cp <= 0xFE6F) ||            // CJK Compatibility Forms, small form variants
        (cp >= 0xFF00 && cp <= 0xFF60) ||            // Fullwidth Forms
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||
        (cp >= 0x16FE0 && cp <= 0x16FE4) ||          // Tangut/Nushu marks
        (cp >= 0x17000 && cp <= 0x18AFF) ||          // Tangut, Khitan
        (cp >= 0x1B000 && cp <= 0x1B2FF) ||          // Kana supplement/extended, Nushu
        (cp >= 0x20000 && cp <= 0x3FFFD);            // CJK Ext B+ (supplementary ideographic planes)

    // The commonly-encountered East Asian Ambiguous characters (full list is long; these are the
    // ones that visibly misalign on CJK terminals): box drawing, block elements, geometric shapes,
    // circled numbers, some punctuation/symbols, Greek and Cyrillic letters.
    private static bool IsAmbiguous(int cp) =>
        cp is 0x00A1 or 0x00A4 or 0x00A7 or 0x00A8 or 0x00AA or 0x00AD or 0x00AE or 0x00B0 or 0x00B1 or 0x00B2 or 0x00B3 or 0x00B4 or 0x00B6 or 0x00B7 or 0x00B8 or 0x00B9 or 0x00BA or 0x00BC or 0x00BD or 0x00BE or 0x00BF or 0x00C6 or 0x00D0 or 0x00D7 or 0x00D8 or 0x00DE or 0x00DF or 0x00E0 or 0x00E1 or 0x00E6 or 0x00E8 or 0x00E9 or 0x00EA or 0x00EC or 0x00ED or 0x00F0 or 0x00F2 or 0x00F3 or 0x00F7 or 0x00F8 or 0x00F9 or 0x00FA or 0x00FC or 0x00FE ||
        (cp >= 0x0391 && cp <= 0x03A9) || (cp >= 0x03B1 && cp <= 0x03C9) ||      // Greek
        cp == 0x0401 || (cp >= 0x0410 && cp <= 0x044F) || cp == 0x0451 ||        // Cyrillic
        cp is 0x2010 or 0x2013 or 0x2014 or 0x2015 or 0x2016 or 0x2018 or 0x2019 or 0x201C or 0x201D or 0x2020 or 0x2021 or 0x2022 or 0x2024 or 0x2025 or 0x2026 or 0x2027 or 0x2030 or 0x2032 or 0x2033 or 0x2035 or 0x203B or 0x203E or 0x2074 or 0x207F or 0x2081 or 0x2082 or 0x2083 or 0x2084 or 0x20AC or 0x2103 or 0x2105 or 0x2109 or 0x2113 or 0x2116 or 0x2121 or 0x2122 or 0x2126 or 0x212B or 0x2153 or 0x2154 ||
        (cp >= 0x215B && cp <= 0x215E) || (cp >= 0x2160 && cp <= 0x216B) || (cp >= 0x2170 && cp <= 0x2179) ||
        (cp >= 0x2190 && cp <= 0x2199) || cp is 0x21B8 or 0x21B9 or 0x21D2 or 0x21D4 or 0x21E7 ||
        cp is 0x2200 or 0x2202 or 0x2203 or 0x2207 or 0x2208 or 0x220B or 0x220F or 0x2211 or 0x2215 or 0x221A or 0x221D or 0x221E or 0x221F or 0x2220 or 0x2223 or 0x2225 or 0x2227 or 0x2228 or 0x2229 or 0x222A or 0x222B or 0x222C or 0x222E or 0x2234 or 0x2235 or 0x2236 or 0x2237 or 0x223C or 0x223D or 0x2248 or 0x224C or 0x2252 or 0x2260 or 0x2261 or 0x2264 or 0x2265 or 0x2266 or 0x2267 or 0x226A or 0x226B or 0x226E or 0x226F or 0x2282 or 0x2283 or 0x2286 or 0x2287 or 0x2295 or 0x2299 or 0x22A5 or 0x22BF or 0x2312 ||
        (cp >= 0x2460 && cp <= 0x24E9) || (cp >= 0x24EB && cp <= 0x254B) ||      // enclosed alphanumerics, box drawing
        (cp >= 0x2550 && cp <= 0x2573) || (cp >= 0x2580 && cp <= 0x258F) ||      // box drawing double, block elements
        (cp >= 0x2592 && cp <= 0x2595) || (cp >= 0x25A0 && cp <= 0x25A1) || (cp >= 0x25A3 && cp <= 0x25A9) || (cp >= 0x25B2 && cp <= 0x25B3) || (cp >= 0x25B6 && cp <= 0x25B7) || (cp >= 0x25BC && cp <= 0x25BD) || (cp >= 0x25C0 && cp <= 0x25C1) || (cp >= 0x25C6 && cp <= 0x25C8) || cp == 0x25CB || (cp >= 0x25CE && cp <= 0x25D1) || (cp >= 0x25E2 && cp <= 0x25E5) || cp == 0x25EF ||
        (cp >= 0x2605 && cp <= 0x2606) || cp is 0x2609 or 0x260E or 0x260F or 0x261C or 0x261E or 0x2640 or 0x2642 or 0x2660 or 0x2661 or 0x2663 or 0x2664 or 0x2665 or 0x2667 or 0x2668 or 0x2669 or 0x266A or 0x266C or 0x266D or 0x266F or 0x269E or 0x269F or 0x26BF ||
        (cp >= 0x26C6 && cp <= 0x26CD) || (cp >= 0x26CF && cp <= 0x26D3) || (cp >= 0x26D5 && cp <= 0x26E1) || cp == 0x26E3 || (cp >= 0x26E8 && cp <= 0x26E9) || (cp >= 0x26EB && cp <= 0x26F1) || cp is 0x26F4 or 0x26F6 or 0x26F7 or 0x26F8 or 0x26F9 or 0x26FA or 0x26FB or 0x26FC or 0x26FE or 0x26FF or 0x273D ||
        (cp >= 0x2776 && cp <= 0x277F) || cp == 0x2B56 || cp == 0x2B57 || cp == 0x2B58 || cp == 0x2B59 || cp == 0x3248 || (cp >= 0xE000 && cp <= 0xF8FF) || cp == 0xFFFD;
}
