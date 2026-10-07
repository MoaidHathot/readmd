using System.Runtime.InteropServices;
using System.Text;

namespace Readmd.Terminal;

// Mouse text selection for "mark mode": readmd tracks a drag selection itself (rather than handing it
// to the terminal's quick-edit), renders a highlight, and copies the selected text to the clipboard
// on right-click. Works regardless of the terminal's own right-click behavior.
public sealed partial class TerminalViewer
{
    /// <summary>True if any cell on this document line falls within the current selection.</summary>
    private bool LineInSelection(int line)
    {
        if (_selAnchor is not { } a) return false;
        var (sLine, _, eLine, _) = NormalizedSelection(a, _selCursor);
        return line >= sLine && line <= eLine;
    }

    /// <summary>Renders a span char-by-char, flipping the background on selected cells.</summary>
    private void DrawSpanWithSelection(int lineIndex, StyledSpan span, string text, ref int col, int width)
    {
        var selBg = _theme.IsDark ? new Rgb(38, 79, 120) : new Rgb(173, 214, 255); // selection blue
        var baseBg = span.Background ?? (_solidBackground ? _theme.Background : (Rgb?)null);
        foreach (var ch in TextWidth.Graphemes(text))
        {
            int gw = TextWidth.ElementWidth(ch);
            if (col + gw > width) break;
            _screen.Reset();
            if (IsSelected(lineIndex, col))
            {
                _screen.SetBackground(selBg);
                _screen.SetForeground(_theme.IsDark ? new Rgb(245, 248, 252) : new Rgb(16, 18, 24));
            }
            else
            {
                if (baseBg is { } b) _screen.SetBackground(b);
                _screen.SetForeground(span.Color ?? _theme.Text);
                if (span.Style != CellStyle.None) _screen.SetStyle(span.Style);
            }
            _screen.Write(ch);
            col += gw;
        }
    }

    private void ClearSelection()
    {
        if (_selAnchor is not null) { _selAnchor = null; _dirty = true; }
    }

    /// <summary>Begins a selection at the clicked screen cell (mark mode).</summary>
    private void SelectionBegin(int screenRow, int screenCol)
    {
        if (screenRow >= ViewportHeight) return;
        int line = _scroll + screenRow;
        if (line < 0 || line >= _lines.Count) return;
        int col = Math.Max(0, screenCol - ContentOffset);
        _selAnchor = (line, col);
        _selCursor = (line, col);
        _dirty = true;
    }

    /// <summary>Extends the in-progress selection to the current screen cell.</summary>
    private void SelectionExtend(int screenRow, int screenCol)
    {
        if (_selAnchor is null) return;
        // Auto-scroll when dragging past the top/bottom edge.
        if (screenRow < 0) { ScrollBy(-1); screenRow = 0; }
        else if (screenRow >= ViewportHeight) { ScrollBy(1); screenRow = ViewportHeight - 1; }

        int line = Math.Clamp(_scroll + screenRow, 0, Math.Max(0, _lines.Count - 1));
        _selCursor = (line, Math.Max(0, screenCol - ContentOffset));
        _dirty = true;
    }

    /// <summary>True if (line, col) lies within the current selection range (inclusive of start, exclusive of end).</summary>
    private bool IsSelected(int line, int col)
    {
        if (_selAnchor is not { } a) return false;
        var (sLine, sCol, eLine, eCol) = NormalizedSelection(a, _selCursor);
        if (line < sLine || line > eLine) return false;
        if (sLine == eLine) return col >= sCol && col < eCol;
        if (line == sLine) return col >= sCol;
        if (line == eLine) return col < eCol;
        return true; // a fully-selected middle line
    }

    private static (int sLine, int sCol, int eLine, int eCol) NormalizedSelection((int Line, int Col) a, (int Line, int Col) b)
    {
        if (a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col))
            return (a.Line, a.Col, b.Line, b.Col);
        return (b.Line, b.Col, a.Line, a.Col);
    }

    /// <summary>Extracts the currently selected text across lines.</summary>
    private string SelectedText()
    {
        if (_selAnchor is not { } a) return "";
        var (sLine, sCol, eLine, eCol) = NormalizedSelection(a, _selCursor);
        var sb = new StringBuilder();
        for (int line = sLine; line <= eLine && line < _lines.Count; line++)
        {
            if (line < 0) continue;
            string text = _lines[line].PlainText;
            // Selection endpoints are DISPLAY columns (as drawn); map them back to char offsets so
            // lines containing wide/emoji glyphs copy the text that was actually highlighted.
            int from = line == sLine ? TextWidth.CharIndexAtColumn(text, sCol) : 0;
            int to = line == eLine ? TextWidth.CharIndexAtColumn(text, eCol) : text.Length;
            if (to > from) sb.Append(text, from, to - from);
            if (line != eLine) sb.Append('\n');
        }
        return sb.ToString();
    }

    private void CopySelection()
    {
        var text = SelectedText();
        if (string.IsNullOrEmpty(text)) { SetStatus("Nothing selected"); return; }
        Clipboard.Copy(text, _screen);
        int chars = text.Length;
        ClearSelection();
        SetStatus($"Copied {chars} character{(chars == 1 ? "" : "s")} to clipboard", 2);
    }

    // ---- dev/testing hook for the pure selection logic (no terminal I/O) ----
    /// <summary>Returns the text that would be selected between two (line, col) endpoints over the
    /// given document lines. Used by tests to verify selection extraction.</summary>
    internal static string SelectTextForTest(IReadOnlyList<string> lines, (int Line, int Col) a, (int Line, int Col) b)
    {
        var v = new TerminalViewer(dark: true, width: 80, height: 24, toc: []);
        v._lines = lines.Select(t =>
        {
            var d = new DisplayLine();
            if (t.Length > 0) d.Spans.Add(new StyledSpan(t));
            return d;
        }).ToList();
        v._selAnchor = a;
        v._selCursor = b;
        return v.SelectedText();
    }
}

/// <summary>
/// Writes text to the system clipboard. Uses the Win32 clipboard on Windows and also emits an
/// OSC 52 escape (honored by Windows Terminal, WezTerm, kitty, iTerm2, …) so it works elsewhere.
/// </summary>
internal static class Clipboard
{
    /// <summary>Test seam: when set, copies go here instead of the system clipboard.</summary>
    internal static Action<string>? TestSink;

    public static void Copy(string text, AnsiScreen screen)
    {
        if (TestSink is { } sink) { sink(text); return; }
        bool win32 = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { win32 = TrySetWindowsClipboard(text); } catch { win32 = false; }
        }
        // OSC 52 as a portable path / fallback (and for terminals that proxy the clipboard remotely).
        if (!win32)
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                screen.WriteControlNow($"\e]52;c;{b64}\a");
            }
            catch { /* best effort */ }
        }
    }

    // ---- Win32 clipboard ----
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint hMem);

    private static bool TrySetWindowsClipboard(string text)
    {
        if (!OpenClipboard(0)) return false;
        try
        {
            EmptyClipboard();
            // Null-terminated UTF-16.
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            nint hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
            if (hGlobal == 0) return false;
            nint target = GlobalLock(hGlobal);
            if (target == 0) return false;
            try { Marshal.Copy(bytes, 0, target, bytes.Length); }
            finally { GlobalUnlock(hGlobal); }
            // Ownership of hGlobal transfers to the clipboard on success.
            return SetClipboardData(CF_UNICODETEXT, hGlobal) != 0;
        }
        finally { CloseClipboard(); }
    }
}
