namespace Readmd.Terminal;

/// <summary>A foreground color expressed as a 24-bit truecolor value.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        var value = Convert.ToInt32(hex, 16);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>Linearly interpolates toward <paramref name="other"/> by <paramref name="t"/> (0..1).</summary>
    public Rgb Mix(Rgb other, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(
            (byte)Math.Round(R + (other.R - R) * t),
            (byte)Math.Round(G + (other.G - G) * t),
            (byte)Math.Round(B + (other.B - B) * t));
    }

    /// <summary>Darkens toward black by <paramref name="amount"/> (0..1).</summary>
    public Rgb Darken(double amount) => Mix(new Rgb(0, 0, 0), amount);

    /// <summary>Lightens toward white by <paramref name="amount"/> (0..1).</summary>
    public Rgb Lighten(double amount) => Mix(new Rgb(255, 255, 255), amount);

    /// <summary>Perceived brightness (0..255), Rec. 601 luma.</summary>
    public double Luma => 0.299 * R + 0.587 * G + 0.114 * B;
}

[Flags]
public enum CellStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Dim = 8,
    Strikethrough = 16,
    Reverse = 32,
}

/// <summary>A run of text sharing one style/color within a display line.</summary>
public sealed record StyledSpan(string Text, Rgb? Color = null, CellStyle Style = CellStyle.None, int? LinkId = null, Rgb? Background = null);

/// <summary>
/// One visual line in the rendered document. May carry a diagram anchor, meaning a rendered
/// image should be drawn starting at this line and the following <see cref="DiagramRows"/> rows
/// are reserved blank for it.
/// </summary>
public sealed class DisplayLine
{
    public List<StyledSpan> Spans { get; } = [];
    public string? HeadingId { get; init; }
    public int SourceLine { get; init; }

    /// <summary>
    /// Identifies the source block this line belongs to (0 = none). Lines of one code block share an
    /// id so a background highlighter can find and restyle them in place after the first paint.
    /// </summary>
    public int BlockId { get; set; }

    /// <summary>
    /// If set, the whole line is filled with this background color out to the terminal width
    /// (used to give code blocks a distinct backdrop).
    /// </summary>
    public Rgb? LineBackground { get; set; }

    /// <summary>If set, the key of a diagram whose image is drawn at this line.</summary>
    public string? DiagramKey { get; set; }
    public int DiagramRows { get; set; }
    public int DiagramCols { get; set; }

    /// <summary>Column at which the image/diagram for this anchor is drawn (its block indentation).</summary>
    public int DiagramIndent { get; set; }

    /// <summary>
    /// If this is a clickable image anchor (image wrapped in a link), the link id to follow when
    /// the anchor or its image area is clicked.
    /// </summary>
    public int? ImageLinkId { get; set; }

    public int VisibleLength
    {
        get { int n = 0; foreach (var s in Spans) n += s.Text.Length; return n; }
    }

    public string PlainText
    {
        get
        {
            if (Spans.Count == 0) return "";
            if (Spans.Count == 1) return Spans[0].Text;
            var sb = new System.Text.StringBuilder();
            foreach (var s in Spans) sb.Append(s.Text);
            return sb.ToString();
        }
    }

    /// <summary>Replaces this line's spans in place (used by the background highlighter).</summary>
    public void ReplaceSpans(IEnumerable<StyledSpan> spans)
    {
        Spans.Clear();
        Spans.AddRange(spans);
    }

    public static DisplayLine Empty() => new();
}
