using System.Text;
using SkiaSharp;

namespace Readmd.Terminal;

/// <summary>
/// Encodes a raster image as a Sixel escape sequence for display in capable terminals
/// (Windows Terminal ≥ 1.22, WezTerm, xterm, mintty, foot, etc.). Builds an adaptive palette:
/// images with at most 256 distinct colours (virtually every diagram) are reproduced exactly;
/// richer images are median-cut quantized to 256 colours with Floyd–Steinberg dithering so
/// gradients and photos stay smooth.
/// </summary>
public static class SixelEncoder
{
    private const int MaxColors = 256;

    public static string Encode(SKBitmap bmp, Rgb? background = null)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w <= 0 || h <= 0) return "";
        var bg = background ?? new Rgb(0, 0, 0);

        // 1) Flatten to opaque RGB (bulk pixel read: one marshal, not one P/Invoke per pixel).
        var src = bmp.Pixels;
        var rgb = new int[w * h];
        for (int i = 0; i < rgb.Length; i++)
        {
            var c = src[i];
            int R = c.Red, G = c.Green, B = c.Blue;
            if (c.Alpha < 255)
            {
                double a = c.Alpha / 255.0;
                R = (int)(R * a + bg.R * (1 - a));
                G = (int)(G * a + bg.G * (1 - a));
                B = (int)(B * a + bg.B * (1 - a));
            }
            rgb[i] = (R << 16) | (G << 8) | B;
        }

        // 2) Palette: exact when small enough, else median cut.
        var palette = BuildPalette(rgb, out bool exact);
        var idx = new byte[w * h];
        if (exact)
        {
            var map = new Dictionary<int, byte>(palette.Count);
            for (int i = 0; i < palette.Count; i++) map[palette[i]] = (byte)i;
            for (int i = 0; i < rgb.Length; i++) idx[i] = map[rgb[i]];
        }
        else
        {
            Dither(rgb, w, h, palette, idx);
        }

        // 3) Emit.
        var sb = new StringBuilder(w * h / 3 + 4096);
        sb.Append("\eP0;1;0q");          // DCS sixel, 1:1 aspect
        sb.Append("\"1;1;").Append(w).Append(';').Append(h);
        for (int i = 0; i < palette.Count; i++)
        {
            int p = palette[i];
            sb.Append('#').Append(i).Append(";2;")
              .Append(((p >> 16) & 0xFF) * 100 / 255).Append(';')
              .Append(((p >> 8) & 0xFF) * 100 / 255).Append(';')
              .Append((p & 0xFF) * 100 / 255);
        }

        int bands = (h + 5) / 6;
        var present = new bool[palette.Count];
        var colorsInBand = new List<int>(palette.Count);
        for (int band = 0; band < bands; band++)
        {
            int y0 = band * 6;
            int yMax = Math.Min(y0 + 6, h);

            Array.Clear(present);
            colorsInBand.Clear();
            for (int y = y0; y < yMax; y++)
            {
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int ci = idx[rowBase + x];
                    if (!present[ci]) { present[ci] = true; colorsInBand.Add(ci); }
                }
            }

            bool firstColor = true;
            foreach (var color in colorsInBand)
            {
                if (!firstColor) sb.Append('$'); // return to start of band for the next color overlay
                firstColor = false;
                sb.Append('#').Append(color);

                int runStart = 0; char runChar = '\0'; bool have = false;
                for (int x = 0; x < w; x++)
                {
                    int bits = 0;
                    for (int dy = 0; dy < 6; dy++)
                    {
                        int y = y0 + dy;
                        if (y < h && idx[y * w + x] == color) bits |= 1 << dy;
                    }
                    char ch = (char)(0x3F + bits);
                    if (!have) { runChar = ch; runStart = x; have = true; }
                    else if (ch != runChar)
                    {
                        EmitRun(sb, runChar, x - runStart);
                        runChar = ch; runStart = x;
                    }
                }
                if (have) EmitRun(sb, runChar, w - runStart);
            }
            sb.Append('-'); // graphics newline
        }

        sb.Append("\e\\"); // ST
        return sb.ToString();
    }

    private static void EmitRun(StringBuilder sb, char ch, int count)
    {
        if (count >= 4)
        {
            sb.Append('!').Append(count).Append(ch); // RLE
        }
        else
        {
            for (int i = 0; i < count; i++) sb.Append(ch);
        }
    }

    // ---------------- palette ----------------
    /// <summary>Distinct colours if ≤ 256 (exact), otherwise a 256-entry median-cut palette.</summary>
    internal static List<int> BuildPalette(int[] rgb, out bool exact)
    {
        var distinct = new HashSet<int>();
        foreach (var p in rgb)
        {
            distinct.Add(p);
            if (distinct.Count > MaxColors) break;
        }
        if (distinct.Count <= MaxColors)
        {
            exact = true;
            return distinct.ToList();
        }
        exact = false;
        return MedianCut(rgb, MaxColors);
    }

    private static List<int> MedianCut(int[] rgb, int count)
    {
        // Work on a histogram of 5-bit-per-channel buckets to keep this fast on large images.
        var hist = new Dictionary<int, int>();
        foreach (var p in rgb)
        {
            int q = ((p >> 19) << 10) | (((p >> 11) & 0x1F) << 5) | ((p >> 3) & 0x1F);
            hist[q] = hist.TryGetValue(q, out var n) ? n + 1 : 1;
        }
        var boxes = new List<List<(int Q, int N)>> { hist.Select(kv => (kv.Key, kv.Value)).ToList() };
        while (boxes.Count < count)
        {
            // Split the box with the largest colour range along its widest channel.
            int bi = -1, bestRange = -1, bestChannel = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.Count < 2) continue;
                int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
                foreach (var (q, _) in box)
                {
                    int r = q >> 10, g = (q >> 5) & 0x1F, b = q & 0x1F;
                    if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                    if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                    if (b < bMin) bMin = b; if (b > bMax) bMax = b;
                }
                int rr = rMax - rMin, gr = gMax - gMin, br = bMax - bMin;
                int range = Math.Max(rr, Math.Max(gr, br));
                if (range > bestRange) { bestRange = range; bi = i; bestChannel = range == gr ? 1 : range == rr ? 0 : 2; }
            }
            if (bi < 0 || bestRange == 0) break;
            var target = boxes[bi];
            target.Sort((a, b) => Channel(a.Q, bestChannel).CompareTo(Channel(b.Q, bestChannel)));
            // Split at the median by pixel count.
            long total = target.Sum(e => (long)e.N), acc = 0;
            int split = 0;
            for (; split < target.Count - 1; split++) { acc += target[split].N; if (acc >= total / 2) { split++; break; } }
            split = Math.Clamp(split, 1, target.Count - 1);
            boxes[bi] = target.GetRange(0, split);
            boxes.Add(target.GetRange(split, target.Count - split));
        }
        var palette = new List<int>(boxes.Count);
        foreach (var box in boxes)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (var (q, cnt) in box)
            {
                r += (long)((q >> 10) << 3) * cnt; g += (long)(((q >> 5) & 0x1F) << 3) * cnt; b += (long)((q & 0x1F) << 3) * cnt; n += cnt;
            }
            if (n == 0) continue;
            palette.Add(((int)Math.Min(255, r / n + 4) << 16) | ((int)Math.Min(255, g / n + 4) << 8) | (int)Math.Min(255, b / n + 4));
        }
        return palette;
    }

    private static int Channel(int q, int ch) => ch == 0 ? q >> 10 : ch == 1 ? (q >> 5) & 0x1F : q & 0x1F;

    /// <summary>Floyd–Steinberg error diffusion onto the palette, with a nearest-colour cache.</summary>
    private static void Dither(int[] rgb, int w, int h, List<int> palette, byte[] idx)
    {
        var nearest = new Dictionary<int, byte>(4096);
        var err = new float[(w + 2) * 3 * 2];   // two rows of error terms (current, next)
        int cur = 0, next = (w + 2) * 3;
        for (int y = 0; y < h; y++)
        {
            Array.Clear(err, next, (w + 2) * 3);
            for (int x = 0; x < w; x++)
            {
                int p = rgb[y * w + x];
                int e = cur + (x + 1) * 3;
                int r = Math.Clamp((int)Math.Round(((p >> 16) & 0xFF) + err[e]), 0, 255);
                int g = Math.Clamp((int)Math.Round(((p >> 8) & 0xFF) + err[e + 1]), 0, 255);
                int b = Math.Clamp((int)Math.Round((p & 0xFF) + err[e + 2]), 0, 255);
                int key = (r << 16) | (g << 8) | b;
                if (!nearest.TryGetValue(key, out var pi))
                {
                    pi = Nearest(palette, r, g, b);
                    if (nearest.Count < 65536) nearest[key] = pi;
                }
                idx[y * w + x] = pi;
                int q = palette[pi];
                float dr = r - ((q >> 16) & 0xFF), dg = g - ((q >> 8) & 0xFF), db = b - (q & 0xFF);
                // Distribute: right 7/16, below-left 3/16, below 5/16, below-right 1/16.
                int er = cur + (x + 2) * 3;
                err[er] += dr * 7 / 16; err[er + 1] += dg * 7 / 16; err[er + 2] += db * 7 / 16;
                int bl = next + x * 3, bb = next + (x + 1) * 3, br = next + (x + 2) * 3;
                err[bl] += dr * 3 / 16; err[bl + 1] += dg * 3 / 16; err[bl + 2] += db * 3 / 16;
                err[bb] += dr * 5 / 16; err[bb + 1] += dg * 5 / 16; err[bb + 2] += db * 5 / 16;
                err[br] += dr / 16; err[br + 1] += dg / 16; err[br + 2] += db / 16;
            }
            (cur, next) = (next, cur);
        }
    }

    private static byte Nearest(List<int> palette, int r, int g, int b)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            int q = palette[i];
            int dr = r - ((q >> 16) & 0xFF), dg = g - ((q >> 8) & 0xFF), db = b - (q & 0xFF);
            int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (byte)best;
    }
}
