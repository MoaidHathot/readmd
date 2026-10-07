namespace Readmd.Terminal;

/// <summary>A foreground color expressed as a 24-bit truecolor value.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        // Exactly six hex digits: "#12345" or "#1234567" must not silently become some other colour.
        if (hex.Length != 6) throw new FormatException($"'#{hex}' is not a #rgb or #rrggbb colour");
        var value = Convert.ToInt32(hex, 16);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>Parses "#rgb" / "#rrggbb"; null when the text isn't a colour.</summary>
    public static Rgb? TryFromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return FromHex(hex); } catch { return null; }
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

/// <summary>The kind of source block a display line was produced from.</summary>
public enum BlockKind
{
    None,
    Paragraph,
    Heading,
    Code,
    Table,
    ListItem,
    Quote,
    Alert,
    Image,
    Diagram,
    Math,
    Rule,
    Html,
    Details,
    Footnote,
    Toc,
    FrontMatter,
}

/// <summary>
/// Per-block metadata recorded while rendering, keyed by <see cref="DisplayLine.BlockId"/>: what
/// the block is, where it came from, and the copyable content (code text, diagram source, …) that
/// block-level actions (yank, focus, toggle source) operate on.
/// </summary>
public sealed record BlockInfo(int Id, BlockKind Kind, int SourceLine, string? Text = null, string? DiagramKey = null, int HeadingLevel = 0, string? Language = null);

/// <summary>
/// A foldable region of the layout: the header line (a heading or a &lt;details&gt; summary) and the
/// inclusive raw-line range it hides when collapsed. Line indices refer to the renderer's output
/// (before the viewer inserts image rows).
/// </summary>
public sealed record FoldRegion(int Id, int HeaderLine, int StartLine, int EndLine, bool DefaultOpen, bool IsHeading);

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
    /// Identifies the source block this line belongs to (0 = none/separator). Lines of one block
    /// share an id so the viewer can treat the block as a unit (active-block actions, in-place
    /// re-highlighting of code).
    /// </summary>
    public int BlockId { get; set; }

    /// <summary>The kind of block this line belongs to.</summary>
    public BlockKind Kind { get; set; }

    /// <summary>Heading level when this line starts a heading (0 otherwise).</summary>
    public int HeadingLevel { get; set; }

    /// <summary>Id of the <see cref="FoldRegion"/> this line is the header of (0 = none).</summary>
    public int FoldHeader { get; set; }

    /// <summary>Set on the synthetic "… N lines folded" row standing in for a collapsed region.</summary>
    public int FoldPlaceholder { get; set; }

    /// <summary>Set on provisional rows reserved under an image/diagram that is still rendering.</summary>
    public bool IsPlaceholder { get; set; }

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
