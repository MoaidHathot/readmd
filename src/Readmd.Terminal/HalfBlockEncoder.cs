using System.Text;
using SkiaSharp;

namespace Readmd.Terminal;

/// <summary>
/// Renders a raster image as Unicode half-block (▀) text: each character cell stacks two vertical
/// pixels, using the upper-half glyph with the top pixel as the foreground color and the bottom
/// pixel as the background color. Works on any truecolor terminal, so it's the fallback where Sixel
/// (or another graphics protocol) isn't available.
/// </summary>
public static class HalfBlockEncoder
{
    private const char UpperHalf = '\u2580'; // ▀

    /// <summary>
    /// Encodes a bitmap to one ANSI string per <em>text row</em> (each row covers two pixel rows).
    /// Transparent pixels are flattened onto <paramref name="background"/>. Colours are emitted at
    /// the requested depth (truecolor, or quantized to the 256/16-colour palettes).
    /// </summary>
    public static IReadOnlyList<string> Encode(SKBitmap bmp, Rgb background, ColorMode colors = ColorMode.TrueColor)
    {
        int w = bmp.Width, h = bmp.Height;
        int rows = (h + 1) / 2;
        var lines = new List<string>(rows);
        var px = bmp.Pixels;   // one bulk read instead of a P/Invoke per pixel

        for (int row = 0; row < rows; row++)
        {
            int yTop = row * 2;
            int yBot = yTop + 1;
            var sb = new StringBuilder(w * 24);

            Rgb? lastFg = null;
            Rgb? lastBg = null;
            for (int x = 0; x < w; x++)
            {
                var top = Sample(px[yTop * w + x], background);
                var bot = yBot < h ? Sample(px[yBot * w + x], background) : background;

                if (lastFg != top) { AppendColor(sb, top, foreground: true, colors); lastFg = top; }
                if (lastBg != bot) { AppendColor(sb, bot, foreground: false, colors); lastBg = bot; }
                sb.Append(colors == ColorMode.None ? '▒' : UpperHalf);
            }
            sb.Append("\e[0m");
            lines.Add(sb.ToString());
        }
        return lines;
    }

    private static void AppendColor(StringBuilder sb, Rgb c, bool foreground, ColorMode colors)
    {
        switch (colors)
        {
            case ColorMode.TrueColor:
                sb.Append(foreground ? "\e[38;2;" : "\e[48;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
                break;
            case ColorMode.Ansi256:
                sb.Append(foreground ? "\e[38;5;" : "\e[48;5;").Append(ColorQuantizer.ToAnsi256(c)).Append('m');
                break;
            case ColorMode.Ansi16:
                sb.Append("\e[").Append(ColorQuantizer.ToAnsi16Sgr(c, !foreground)).Append('m');
                break;
        }
    }

    private static Rgb Sample(SKColor c, Rgb bg)
    {
        if (c.Alpha == 0) return bg;
        if (c.Alpha == 255) return new Rgb(c.Red, c.Green, c.Blue);
        double a = c.Alpha / 255.0;
        return new Rgb(
            (byte)(c.Red * a + bg.R * (1 - a)),
            (byte)(c.Green * a + bg.G * (1 - a)),
            (byte)(c.Blue * a + bg.B * (1 - a)));
    }
}
