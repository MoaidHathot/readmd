using System.Text;

namespace Readmd.Terminal;

/// <summary>
/// Reconstructs the visible text of a terminal screen from an ANSI escape stream: honours cursor
/// positioning (CSI H), line/screen erases (CSI K / 2K / 2J) and treats every other control
/// sequence (SGR colours, mode toggles, OSC/DCS/APC payloads) as invisible. Used by the headless
/// viewer harness and tests to assert on what a user would actually see.
/// </summary>
public static class AnsiText
{
    public static List<string> ToRows(string ansi, int width, int height)
    {
        var grid = new char[height][];
        for (int r = 0; r < height; r++) { grid[r] = new char[width]; Array.Fill(grid[r], ' '); }
        int row = 0, col = 0;
        int i = 0;
        while (i < ansi.Length)
        {
            char c = ansi[i];
            if (c == '\e' && i + 1 < ansi.Length)
            {
                char kind = ansi[i + 1];
                if (kind == '[')
                {
                    int j = i + 2;
                    while (j < ansi.Length && !(ansi[j] >= 0x40 && ansi[j] <= 0x7e)) j++;
                    if (j >= ansi.Length) break;
                    var body = ansi[(i + 2)..j];
                    char final = ansi[j];
                    switch (final)
                    {
                        case 'H':
                        case 'f':
                        {
                            var parts = body.Split(';');
                            int r = parts.Length > 0 && int.TryParse(parts[0], out var pr) ? pr : 1;
                            int cc = parts.Length > 1 && int.TryParse(parts[1], out var pc) ? pc : 1;
                            row = Math.Clamp(r - 1, 0, height - 1);
                            col = Math.Clamp(cc - 1, 0, width);
                            break;
                        }
                        case 'K':
                            if (row < height)
                            {
                                if (body == "2") Array.Fill(grid[row], ' ');
                                else for (int x = col; x < width; x++) grid[row][x] = ' ';
                            }
                            break;
                        case 'J':
                            if (body == "2" || body == "3")
                                foreach (var gridRow in grid) Array.Fill(gridRow, ' ');
                            break;
                        case 'A': row = Math.Max(0, row - Math.Max(1, ParseInt(body))); break;
                        case 'B': row = Math.Min(height - 1, row + Math.Max(1, ParseInt(body))); break;
                        case 'C': col = Math.Min(width, col + Math.Max(1, ParseInt(body))); break;
                        case 'D': col = Math.Max(0, col - Math.Max(1, ParseInt(body))); break;
                        case 'G': col = Math.Clamp(Math.Max(1, ParseInt(body)) - 1, 0, width); break;
                    }
                    i = j + 1;
                    continue;
                }
                if (kind == ']' || kind == 'P' || kind == '_' || kind == '^')
                {
                    // OSC/DCS/APC: ends with ST (ESC \) or BEL.
                    int j = i + 2;
                    while (j < ansi.Length)
                    {
                        if (ansi[j] == '\a') { j++; break; }
                        if (ansi[j] == '\e' && j + 1 < ansi.Length && ansi[j + 1] == '\\') { j += 2; break; }
                        j++;
                    }
                    i = j;
                    continue;
                }
                // Two-char escape (e.g. ESC 7 / ESC 8 / ESC =): skip.
                i += 2;
                continue;
            }
            if (c == '\r') { col = 0; i++; continue; }
            if (c == '\n') { row = Math.Min(height - 1, row + 1); col = 0; i++; continue; }
            if (c == '\a' || char.IsControl(c)) { i++; continue; }

            // Grapheme-aware placement: wide glyphs occupy two cells.
            var e = System.Globalization.StringInfo.GetNextTextElementLength(ansi.AsSpan(i));
            var g = ansi.Substring(i, e);
            int w = TextWidth.ElementWidth(g);
            if (row < height && col < width)
            {
                grid[row][col] = g[0];
                // Store extra UTF-16 units / wide padding as placeholders that we drop on output.
                if (w == 2 && col + 1 < width) grid[row][col + 1] = '\0';
                if (g.Length > 1) _wideMap[(row, col)] = g;
            }
            col += Math.Max(1, w);
            i += e;
        }
        var rows = new List<string>(height);
        for (int r = 0; r < height; r++)
        {
            var sb = new StringBuilder(width);
            for (int x = 0; x < width; x++)
            {
                char ch = grid[r][x];
                if (ch == '\0') continue;
                if (_wideMap.TryGetValue((r, x), out var full)) sb.Append(full);
                else sb.Append(ch);
            }
            rows.Add(sb.ToString().TrimEnd());
        }
        _wideMap.Clear();
        return rows;
    }

    [ThreadStatic] private static Dictionary<(int, int), string>? _wideMapField;
    private static Dictionary<(int, int), string> _wideMap => _wideMapField ??= new();

    private static int ParseInt(string s) => int.TryParse(s, out var n) ? n : 0;

    /// <summary>Strips all ANSI escape sequences, leaving the raw character stream.</summary>
    public static string Strip(string ansi)
    {
        var sb = new StringBuilder(ansi.Length);
        int i = 0;
        while (i < ansi.Length)
        {
            char c = ansi[i];
            if (c == '\e' && i + 1 < ansi.Length)
            {
                char kind = ansi[i + 1];
                if (kind == '[')
                {
                    int j = i + 2;
                    while (j < ansi.Length && !(ansi[j] >= 0x40 && ansi[j] <= 0x7e)) j++;
                    i = Math.Min(ansi.Length, j + 1);
                    continue;
                }
                if (kind == ']' || kind == 'P' || kind == '_' || kind == '^')
                {
                    int j = i + 2;
                    while (j < ansi.Length)
                    {
                        if (ansi[j] == '\a') { j++; break; }
                        if (ansi[j] == '\e' && j + 1 < ansi.Length && ansi[j + 1] == '\\') { j += 2; break; }
                        j++;
                    }
                    i = j;
                    continue;
                }
                i += 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
