namespace Readmd.Terminal;

/// <summary>
/// The decorative glyphs used by the terminal renderer, with an ASCII fallback set
/// (<c>--ascii</c> / config <c>ascii: true</c>) for terminals or fonts that lack box drawing,
/// block elements or symbols — or for plain-text piping where Unicode decoration is unwanted.
/// </summary>
public static class Glyphs
{
    /// <summary>Use the 7-bit ASCII glyph set.</summary>
    public static bool Ascii { get; set; }

    // Lists & tasks
    public static string Bullet1 => Ascii ? "* " : "• ";
    public static string Bullet2 => Ascii ? "- " : "◦ ";
    public static string Bullet3 => Ascii ? "+ " : "▪ ";
    public static string Checked => Ascii ? "[x] " : "☑ ";
    public static string Unchecked => Ascii ? "[ ] " : "☐ ";

    // Headings
    public static string H2Marker => Ascii ? "## " : "▌ ";
    public static string H3Marker => Ascii ? "### " : "● ";
    public static string H4Marker => Ascii ? "#### " : "◗ ";
    public static string H5Marker => Ascii ? "##### " : "▸ ";
    public static string H6Marker => Ascii ? "###### " : "· ";
    public static char H1Rule => Ascii ? '=' : '━';
    public static char Rule => Ascii ? '-' : '─';

    // Code frame
    public static char FrameH => Ascii ? '-' : '─';
    public static char FrameV => Ascii ? '|' : '│';
    public static char FrameTop => Ascii ? '+' : '┬';
    public static char FrameMid => Ascii ? '+' : '┼';
    public static char FrameBottom => Ascii ? '+' : '┴';
    public static string Continuation => Ascii ? ">" : "↳";

    // Tables
    public static char BoxH => Ascii ? '-' : '─';
    public static char BoxV => Ascii ? '|' : '│';
    public static char BoxTL => Ascii ? '+' : '┌';
    public static char BoxTM => Ascii ? '+' : '┬';
    public static char BoxTR => Ascii ? '+' : '┐';
    public static char BoxML => Ascii ? '+' : '├';
    public static char BoxMM => Ascii ? '+' : '┼';
    public static char BoxMR => Ascii ? '+' : '┤';
    public static char BoxBL => Ascii ? '+' : '└';
    public static char BoxBM => Ascii ? '+' : '┴';
    public static char BoxBR => Ascii ? '+' : '┘';

    // Blocks
    public static string QuoteBar => Ascii ? "| " : "▌ ";
    public static string Diagram => Ascii ? "# " : "◆ ";
    public static string Image => Ascii ? "[img] " : "▣ ";
    public static string ImagePlaceholder => Ascii ? "[img]" : "🖼";
    public static string Media => Ascii ? "> " : "▶ ";
    public static string DetailsOpen => Ascii ? "v " : "▾ ";
    public static string DetailsClosed => Ascii ? "> " : "▸ ";
    public static string Fold => Ascii ? "... " : "⋯ ";
    public static string Error => Ascii ? "X " : "✖ ";
    public static string Rendering => Ascii ? "~ rendering..." : "⟳ rendering…";
    public static string FooterBar => Ascii ? "|| " : "‖ ";
    public static string Caption => Ascii ? "-- " : "— ";
    public static string ColorChip => Ascii ? "#" : "■";
    public static string Ellipsis => Ascii ? "..." : "…";

    // Alerts
    public static string AlertNote => Ascii ? "i" : "ℹ";
    public static string AlertTip => Ascii ? "v" : "✔";
    public static string AlertImportant => Ascii ? "*" : "◆";
    public static string AlertWarning => Ascii ? "!" : "⚠";
    public static string AlertCaution => Ascii ? "x" : "⛔";

    // Overlays / status
    public static string CornerTL => Ascii ? "+" : "╭";
    public static string CornerTR => Ascii ? "+" : "╮";
    public static string CornerBL => Ascii ? "+" : "╰";
    public static string CornerBR => Ascii ? "+" : "╯";
    public static char Shadow => Ascii ? '#' : '░';
    public static string Selected => Ascii ? ">" : "▌";
    public static string Current => Ascii ? "-" : "›";
    public static string Crumb => Ascii ? " > " : " › ";
    public static string Cursor => Ascii ? "_" : "▏";
    public static string Dot => Ascii ? " - " : "  ·  ";
    public static string ScrollTrack => Ascii ? "|" : "│";
    public static string ScrollThumb => Ascii ? "#" : "┃";

    /// <summary>Superscript digits for footnote references (ASCII: "[n]").</summary>
    public static string Superscript(int n)
    {
        if (Ascii || n < 0) return "[" + n + "]";
        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        var sb = new System.Text.StringBuilder();
        foreach (var ch in n.ToString()) sb.Append(digits[ch - '0']);
        return sb.ToString();
    }
}
