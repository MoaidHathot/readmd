using System.Text;
using SkiaSharp;

namespace Readmd.Terminal;

/// <summary>
/// Encodes a bitmap for the kitty graphics protocol: PNG payload (<c>f=100</c>), transmitted and
/// placed in one go (<c>a=T</c>) at the cursor position, scaled to a cell box of
/// <paramref name="cols"/> × <paramref name="rows"/>, chunked in 4 KiB base64 pieces. Lossless,
/// keeps alpha (so diagrams can sit on a transparent terminal background), and the terminal does the
/// scaling, so it stays crisp on HiDPI.
/// </summary>
public static class KittyEncoder
{
    public static string Encode(SKBitmap bitmap, int cols, int rows, uint imageId)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return EncodePng(data.ToArray(), cols, rows, imageId);
    }

    public static string EncodePng(byte[] png, int cols, int rows, uint imageId)
    {
        var b64 = Convert.ToBase64String(png);
        var sb = new StringBuilder(b64.Length + 256);
        const int chunk = 4096;
        int pos = 0;
        bool first = true;
        while (pos < b64.Length)
        {
            int len = Math.Min(chunk, b64.Length - pos);
            bool last = pos + len >= b64.Length;
            sb.Append("\e_G");
            if (first)
            {
                // a=T transmit+display, f=100 PNG, q=2 quiet (no replies), C=1 keep the cursor where it is.
                sb.Append("a=T,f=100,q=2,C=1,i=").Append(imageId);
                if (cols > 0) sb.Append(",c=").Append(cols);
                if (rows > 0) sb.Append(",r=").Append(rows);
                sb.Append(",m=").Append(last ? '0' : '1');
                first = false;
            }
            else sb.Append("m=").Append(last ? '0' : '1');
            sb.Append(';').Append(b64, pos, len).Append("\e\\");
            pos += len;
        }
        return sb.ToString();
    }

    /// <summary>Deletes every visible placement (and frees the image data).</summary>
    public const string DeleteAll = "\e_Ga=d,d=A,q=2\e\\";

    /// <summary>A stable 24-bit image id derived from a cache key.</summary>
    public static uint IdFor(string key)
    {
        uint h = 2166136261;
        foreach (var ch in key) { h ^= ch; h *= 16777619; }
        return (h & 0x00FFFFFF) | 0x00000001;   // non-zero, within the id range
    }
}

/// <summary>
/// Encodes a bitmap for the iTerm2 inline-image protocol (OSC 1337 <c>File=</c>), also honoured by
/// WezTerm, mintty and Hyper. The terminal scales the PNG into a <paramref name="cols"/> ×
/// <paramref name="rows"/> cell box.
/// </summary>
public static class ITerm2Encoder
{
    public static string Encode(SKBitmap bitmap, int cols, int rows)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return EncodePng(data.ToArray(), cols, rows);
    }

    public static string EncodePng(byte[] png, int cols, int rows)
    {
        var b64 = Convert.ToBase64String(png);
        var sb = new StringBuilder(b64.Length + 128);
        sb.Append("\e]1337;File=inline=1;size=").Append(png.Length);
        if (cols > 0) sb.Append(";width=").Append(cols);
        if (rows > 0) sb.Append(";height=").Append(rows);
        sb.Append(";preserveAspectRatio=0;doNotMoveCursor=1:").Append(b64).Append('\a');
        return sb.ToString();
    }
}
