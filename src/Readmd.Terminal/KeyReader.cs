using System.Runtime.InteropServices;
using System.Text;

namespace Readmd.Terminal;

/// <summary>
/// Reliable cross-platform input reader. On Windows it uses the Win32 console input API
/// (<c>ReadConsoleInputW</c>) directly — the dependable way to read individual key presses and
/// mouse wheel events on Windows Terminal. On other platforms it reads raw bytes from stdin (the
/// terminal is put in raw mode via termios) and decodes VT key sequences, SGR mouse reports,
/// bracketed paste, focus events and terminal replies (window/cell pixel size).
/// </summary>
internal static class KeyReader
{
    /// <summary>
    /// The terminal's text-area pixel size, captured from a CSI 14t report if one arrives. Null
    /// until reported. Dividing by the current rows/cols yields the per-cell pixel size.
    /// </summary>
    public static (int WidthPx, int HeightPx)? WindowPixelSize { get; private set; }

    /// <summary>The terminal's cell pixel size, captured from a CSI 16t report (CSI 6;h;w t). Null until reported.</summary>
    public static (int WidthPx, int HeightPx)? CellPixelSize { get; private set; }

    /// <summary>
    /// Raw terminal replies that aren't keys (DA1, XTVERSION, OSC colour replies, kitty graphics
    /// responses, …). <see cref="TerminalProbe"/> consumes these to detect capabilities; the viewer
    /// never sees them as keystrokes.
    /// </summary>
    public static event Action<string>? ReportReceived;

    /// <summary>Parses a swallowed terminal report (CSI/OSC/DCS/APC reply) and records what it tells us.</summary>
    internal static void TryParseReport(string seq)
    {
        // CSI 4 ; height ; width t  — window text-area size in pixels (reply to CSI 14 t).
        var m = System.Text.RegularExpressions.Regex.Match(seq, @"\[4;(\d+);(\d+)t");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out int h) &&
            int.TryParse(m.Groups[2].Value, out int w) &&
            w > 0 && h > 0)
        {
            WindowPixelSize = (w, h);
        }
        // CSI 6 ; height ; width t  — cell size in pixels (reply to CSI 16 t).
        var c = System.Text.RegularExpressions.Regex.Match(seq, @"\[6;(\d+);(\d+)t");
        if (c.Success &&
            int.TryParse(c.Groups[1].Value, out int ch) &&
            int.TryParse(c.Groups[2].Value, out int cw) &&
            cw > 0 && ch > 0)
        {
            CellPixelSize = (cw, ch);
        }
        ReportReceived?.Invoke(seq);
    }

    public static IEnumerable<KeyEvent> Read(Func<bool> isRunning)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ReadWindows(isRunning);
        return ReadPortable(isRunning);
    }

    /// <summary>
    /// Test seam: parses a complete byte sequence (as a Unix terminal would deliver it) into the
    /// events the portable reader would emit. Used to verify VT key and SGR mouse decoding.
    /// </summary>
    internal static List<KeyEvent> ParsePortableForTest(byte[] bytes)
    {
        var pending = new List<byte>(bytes);
        var result = new List<KeyEvent>();
        var state = new PortableState();
        int consumed;
        while (pending.Count > 0 && (consumed = TryParsePortable(pending, state, out var ev, final: true)) > 0)
        {
            pending.RemoveRange(0, consumed);
            if (ev is not null) result.Add(ev);
        }
        return result;
    }

    // ---------------- portable (non-Windows) ----------------
    // Reads raw bytes from stdin (terminal in raw mode via termios) and parses VT escape
    // sequences for keys and SGR mouse reports (CSI < b ; x ; y M/m). This brings wheel scroll,
    // click-to-follow-link and drag-select to macOS/Linux, matching the Windows console reader.
    private sealed class PortableState
    {
        public bool InPaste;
        public readonly StringBuilder Paste = new();
    }

    private static IEnumerable<KeyEvent> ReadPortable(Func<bool> isRunning)
    {
        var stdin = Console.OpenStandardInput();
        var buf = new byte[4096];
        var pending = new List<byte>(64);
        var state = new PortableState();

        while (isRunning())
        {
            int n;
            try { n = stdin.Read(buf, 0, buf.Length); }
            catch (IOException) { yield break; }
            catch (ObjectDisposedException) { yield break; }
            if (n <= 0) { if (!isRunning()) yield break; continue; }

            for (var i = 0; i < n; i++) pending.Add(buf[i]);

            // Drain complete tokens from the pending buffer. A lone ESC that ends a read is treated
            // as the Escape key (a real sequence arrives in one read, or at least its '[' does).
            int consumed;
            while ((consumed = TryParsePortable(pending, state, out var ev, final: false)) > 0)
            {
                pending.RemoveRange(0, consumed);
                if (ev is not null) yield return ev;
            }
            if (pending.Count == 1 && pending[0] == 0x1b)
            {
                pending.Clear();
                yield return new KeyEvent(KeyKind.Escape);
            }
        }
    }

    // Returns the number of bytes consumed (0 if it needs more input to decide), and the parsed
    // event (null when the bytes were a recognized-but-ignored sequence such as a CSI report).
    private static int TryParsePortable(List<byte> b, PortableState state, out KeyEvent? ev, bool final)
    {
        ev = null;
        if (b.Count == 0) return 0;

        // Inside a bracketed paste: accumulate everything until the ESC [ 201 ~ terminator.
        if (state.InPaste)
        {
            int end = IndexOf(b, PasteEnd);
            if (end < 0)
            {
                // Keep a possible partial terminator at the tail; consume the rest as paste text.
                int keep = Math.Min(b.Count, PasteEnd.Length - 1);
                int take = b.Count - keep;
                if (take <= 0) return 0;
                state.Paste.Append(Encoding.UTF8.GetString(b.ToArray(), 0, take));
                return take;
            }
            state.Paste.Append(Encoding.UTF8.GetString(b.ToArray(), 0, end));
            state.InPaste = false;
            ev = new KeyEvent(KeyKind.Paste, Text: state.Paste.ToString());
            state.Paste.Clear();
            return end + PasteEnd.Length;
        }

        byte c0 = b[0];
        if (c0 != 0x1b) // not ESC: a plain key / control char / UTF-8 text byte
            return ParsePortableByte(b, out ev);

        // ESC alone (so far): could be a real Escape or the start of a sequence. Need ≥2 bytes.
        if (b.Count == 1) { if (final) { ev = new KeyEvent(KeyKind.Escape); return 1; } return 0; }
        byte c1 = b[1];

        if (c1 == '[' || c1 == 'O')
        {
            // CSI ('[') or SS3 ('O') sequence. Find the final byte (0x40–0x7E).
            var end = -1;
            for (var i = 2; i < b.Count; i++)
            {
                if (b[i] >= 0x40 && b[i] <= 0x7e) { end = i; break; }
            }
            if (end < 0) return b.Count > 64 ? b.Count : 0; // wait for more, but cap runaway

            var seq = Encoding.ASCII.GetString(b.ToArray(), 1, end); // without the ESC
            if (seq == "[200~") { state.InPaste = true; state.Paste.Clear(); return end + 1; }
            ev = ParseCsi(seq);
            return end + 1;
        }

        if (c1 == ']' || c1 == 'P' || c1 == '_' || c1 == '^')
        {
            // OSC (]) / DCS (P) / APC (_) / PM (^) reply: terminated by ST (ESC \) or BEL (OSC only).
            for (var i = 2; i < b.Count; i++)
            {
                bool st = b[i] == 0x5c && b[i - 1] == 0x1b;
                bool bel = c1 == ']' && b[i] == 0x07;
                if (st || bel)
                {
                    int bodyLen = st ? i - 2 : i - 1;   // after ESC, before the terminator
                    TryParseReport(Encoding.UTF8.GetString(b.ToArray(), 1, Math.Max(0, bodyLen)));
                    return i + 1;
                }
            }
            return b.Count > 4096 ? b.Count : 0;
        }

        // ESC followed by a normal byte in the same read: xterm "meta" encoding of Alt+key.
        if (c1 >= 0x20 && c1 < 0x7f)
        {
            ev = new KeyEvent(KeyKind.Char, (char)c1, Alt: true);
            return 2;
        }
        if (c1 is >= 1 and <= 26)   // Alt+Ctrl+letter
        {
            ev = new KeyEvent(KeyKind.Char, (char)('a' + (c1 - 1)), Ctrl: true, Alt: true);
            return 2;
        }

        // ESC followed by something else: treat the ESC as a standalone Escape key, leave the rest.
        ev = new KeyEvent(KeyKind.Escape);
        return 1;
    }

    private static readonly byte[] PasteEnd = "\e[201~"u8.ToArray();

    private static int IndexOf(List<byte> haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static int ParsePortableByte(List<byte> b, out KeyEvent? ev)
    {
        ev = null;
        byte c = b[0];
        switch (c)
        {
            case 0x0d or 0x0a: ev = new KeyEvent(KeyKind.Enter); return 1;
            case 0x7f or 0x08: ev = new KeyEvent(KeyKind.Backspace); return 1;
            case 0x09: ev = new KeyEvent(KeyKind.Tab); return 1;
            case 0x03: ev = new KeyEvent(KeyKind.Char, 'c', Ctrl: true); return 1;  // Ctrl+C
            case 0x00: ev = new KeyEvent(KeyKind.Char, ' ', Ctrl: true); return 1;  // Ctrl+Space
        }
        if (c is >= 1 and <= 26) // Ctrl+A..Z (excluding the ones handled above)
        {
            ev = new KeyEvent(KeyKind.Char, (char)('a' + (c - 1)), Ctrl: true);
            return 1;
        }
        if (c < 0x80)
        {
            ev = new KeyEvent(KeyKind.Char, (char)c);
            return 1;
        }
        // Multi-byte UTF-8: gather the full code point.
        var len = c switch { >= 0xF0 => 4, >= 0xE0 => 3, _ => 2 };
        if (b.Count < len) return 0; // need more bytes
        var s = Encoding.UTF8.GetString(b.ToArray(), 0, len);
        // Non-BMP characters (emoji) occupy two UTF-16 chars; the viewer only needs the first for
        // key dispatch, and the full text is not meaningful as a command key anyway.
        ev = s.Length > 0 ? new KeyEvent(KeyKind.Char, s[0], Text: s.Length > 1 ? s : null) : null;
        return len;
    }

    // Parses a CSI/SS3 body (everything after the ESC), e.g. "[A", "[1;5C", "[<0;12;7M", "[4;500;900t".
    internal static KeyEvent? ParseCsi(string seq)
    {
        // SGR mouse: "[<b;x;yM" (press/move) or "[<b;x;ym" (release).
        if (seq.Length > 2 && seq[0] == '[' && seq[1] == '<')
        {
            var final = seq[^1];
            var body = seq[2..^1];
            var parts = body.Split(';');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var bcode) &&
                int.TryParse(parts[1], out var col) &&
                int.TryParse(parts[2], out var row))
            {
                return MouseFromSgr(bcode, col - 1, row - 1, release: final == 'm'); // 1-based -> 0-based
            }
            return null;
        }

        // Focus reporting (CSI I / CSI O).
        if (seq == "[I") return new KeyEvent(KeyKind.FocusIn);
        if (seq == "[O") return new KeyEvent(KeyKind.FocusOut);

        // Terminal replies (window/cell pixel size, DA1 "[?…c", cursor position "[r;cR", …):
        // record them and never surface them as keystrokes.
        char last = seq[^1];
        if (last == 't' || last == 'c' || last == 'R' || (seq.Length > 1 && seq[1] == '?') || last == 'y')
        {
            TryParseReport(seq);
            return null;
        }

        // Cursor/edit keys. Strip a leading '[' or 'O'.
        var s = seq.Length > 0 && (seq[0] == '[' || seq[0] == 'O') ? seq[1..] : seq;
        var key = s.Length > 0 ? s[^1] : '\0';
        // xterm modifiers: "1;<m>X" / "<n>;<m>~" where m-1 is a bit set: 1=shift 2=alt 4=ctrl.
        var (shift, alt, ctrl) = ParseModifier(s);
        return key switch
        {
            'A' => new KeyEvent(KeyKind.Up, Ctrl: ctrl, Alt: alt, Shift: shift),
            'B' => new KeyEvent(KeyKind.Down, Ctrl: ctrl, Alt: alt, Shift: shift),
            'C' => new KeyEvent(KeyKind.Right, Ctrl: ctrl, Alt: alt, Shift: shift),
            'D' => new KeyEvent(KeyKind.Left, Ctrl: ctrl, Alt: alt, Shift: shift),
            'H' => new KeyEvent(KeyKind.Home, Ctrl: ctrl, Alt: alt, Shift: shift),
            'F' => new KeyEvent(KeyKind.End, Ctrl: ctrl, Alt: alt, Shift: shift),
            'Z' => new KeyEvent(KeyKind.Tab, Shift: true),                 // CSI Z = back-tab
            'P' => new KeyEvent(KeyKind.Function, Number: 1, Ctrl: ctrl, Alt: alt, Shift: shift),
            'Q' => new KeyEvent(KeyKind.Function, Number: 2, Ctrl: ctrl, Alt: alt, Shift: shift),
            'S' => new KeyEvent(KeyKind.Function, Number: 4, Ctrl: ctrl, Alt: alt, Shift: shift),
            '~' => ParseTildeKey(s, shift, alt, ctrl),
            _ => null, // unrecognized — ignore
        };
    }

    private static (bool Shift, bool Alt, bool Ctrl) ParseModifier(string s)
    {
        int semi = s.IndexOf(';');
        if (semi < 0) return (false, false, false);
        var rest = s[(semi + 1)..].TrimEnd('~', 'A', 'B', 'C', 'D', 'H', 'F', 'P', 'Q', 'S', 'R');
        int colon = rest.IndexOf(':');   // kitty-style "1;5:1" event-type suffix
        if (colon >= 0) rest = rest[..colon];
        if (!int.TryParse(rest, out var m) || m < 2) return (false, false, false);
        int bits = m - 1;
        return ((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
    }

    private static KeyEvent? ParseTildeKey(string s, bool shift, bool alt, bool ctrl)
    {
        // s looks like "1~", "5~", "6~", possibly "5;3~". Take the leading number.
        var semi = s.IndexOf(';');
        var numStr = (semi >= 0 ? s[..semi] : s.TrimEnd('~'));
        if (!int.TryParse(numStr, out var code)) return null;
        return code switch
        {
            1 or 7 => new KeyEvent(KeyKind.Home, Ctrl: ctrl, Alt: alt, Shift: shift),
            2 => new KeyEvent(KeyKind.Insert, Ctrl: ctrl, Alt: alt, Shift: shift),
            3 => new KeyEvent(KeyKind.Delete, Ctrl: ctrl, Alt: alt, Shift: shift),
            4 or 8 => new KeyEvent(KeyKind.End, Ctrl: ctrl, Alt: alt, Shift: shift),
            5 => new KeyEvent(KeyKind.PageUp, Ctrl: ctrl, Alt: alt, Shift: shift),
            6 => new KeyEvent(KeyKind.PageDown, Ctrl: ctrl, Alt: alt, Shift: shift),
            11 or 12 or 13 or 14 or 15 => new KeyEvent(KeyKind.Function, Number: code - 10, Ctrl: ctrl, Alt: alt, Shift: shift),
            17 or 18 or 19 or 20 or 21 => new KeyEvent(KeyKind.Function, Number: code - 11, Ctrl: ctrl, Alt: alt, Shift: shift),
            23 or 24 => new KeyEvent(KeyKind.Function, Number: code - 12, Ctrl: ctrl, Alt: alt, Shift: shift),
            _ => null,
        };
    }

    // SGR mouse button code → our KeyEvent. Bit 0x40 marks wheel; 0x20 marks motion; low 2 bits
    // are the button (0=left, 1=middle, 2=right). Modifiers: 0x10 ctrl, 0x08 alt, 0x04 shift.
    private static KeyEvent? MouseFromSgr(int code, int col, int row, bool release)
    {
        bool ctrl = (code & 0x10) != 0;
        bool alt = (code & 0x08) != 0;
        bool shift = (code & 0x04) != 0;
        if ((code & 0x40) != 0)
        {
            // Wheel: button 0 = up, 1 = down.
            var up = (code & 0x03) == 0;
            return up ? new KeyEvent(KeyKind.MouseScrollUp, Ctrl: ctrl, Alt: alt, Shift: shift, MouseRow: row, MouseCol: col)
                      : new KeyEvent(KeyKind.MouseScrollDown, Ctrl: ctrl, Alt: alt, Shift: shift, MouseRow: row, MouseCol: col);
        }

        var button = code & 0x03;
        bool motion = (code & 0x20) != 0;
        if (motion)
        {
            // Drag with left button held (button bits read 0 during left-drag motion).
            if (button == 0) return new KeyEvent(KeyKind.MouseDrag, MouseRow: row, MouseCol: col);
            return null; // hover / other-button motion: ignore
        }

        if (release)
        {
            // Button up: treat a left-button release as the end of a drag.
            return new KeyEvent(KeyKind.MouseDragEnd, MouseRow: row, MouseCol: col);
        }

        // Button press edge.
        return button switch
        {
            2 => new KeyEvent(KeyKind.MouseRightClick, MouseRow: row, MouseCol: col),
            1 => new KeyEvent(KeyKind.MouseMiddleClick, MouseRow: row, MouseCol: col),
            0 => new KeyEvent(KeyKind.MouseClick, MouseRow: row, MouseCol: col, Ctrl: ctrl, Alt: alt, Shift: shift),
            _ => null,
        };
    }

    // ---------------- Windows (ReadConsoleInputW) ----------------
    private static IEnumerable<KeyEvent> ReadWindows(Func<bool> isRunning)
    {
        var handle = GetStdHandle(STD_INPUT_HANDLE);
        _inputHandle = handle;

        // Set a complete, explicit input mode: mouse + window events on, line/echo off,
        // quick-edit off (so the mouse isn't swallowed), processed-input on (so Ctrl+C still
        // works), and VT input OFF (so keys arrive as key records, not escape bytes).
        uint savedMode = 0;
        bool modeChanged = false;
        if (GetConsoleMode(handle, out savedMode))
        {
            _savedMode = savedMode;
            modeChanged = SetConsoleMode(handle, MouseCaptureMode);
        }

        var records = new INPUT_RECORD[64];
        bool inEscape = false;          // swallowing a terminal report like CSI ... <final>
        bool inPaste = false;           // between CSI 200~ and CSI 201~
        var escBuf = new StringBuilder();
        var pasteBuf = new StringBuilder();
        try
        {
            while (isRunning())
            {
                // Wait briefly so we can re-check isRunning() and exit promptly.
                uint wait = WaitForSingleObject(handle, 100);
                if (wait != 0) continue;

                if (!ReadConsoleInputW(handle, records, (uint)records.Length, out uint read) || read == 0)
                    continue;

                for (uint i = 0; i < read; i++)
                {
                    var rec = records[i];
                    if (rec.EventType == KEY_EVENT)
                    {
                        if (rec.KeyEvent.bKeyDown == 0) continue;
                        char ch = (char)rec.KeyEvent.UnicodeChar;

                        // Swallow terminal report sequences (e.g. responses to CSI 14t / cursor
                        // position) so they never leak in as keystrokes like 't' opening the TOC.
                        if (inEscape)
                        {
                            if (ch != '\0')
                            {
                                escBuf.Append(ch);
                                char kind = escBuf.Length > 1 ? escBuf[1] : '\0';
                                bool osc = kind is ']' or 'P' or '_' or '^';
                                bool done = osc
                                    ? (ch == '\a' || (ch == '\\' && escBuf.Length > 2 && escBuf[^2] == '\x1b'))
                                    : ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '~');
                                if (done)
                                {
                                    var seq = escBuf.ToString(1, escBuf.Length - 1);
                                    if (osc)
                                    {
                                        // Strip the terminator (BEL or ESC \) from the report body.
                                        if (seq.EndsWith('\a')) seq = seq[..^1];
                                        else if (seq.EndsWith("\x1b\\")) seq = seq[..^2];
                                    }
                                    inEscape = false;
                                    escBuf.Clear();
                                    if (seq == "[200~") { inPaste = true; pasteBuf.Clear(); continue; }
                                    if (seq == "[201~")
                                    {
                                        inPaste = false;
                                        yield return new KeyEvent(KeyKind.Paste, Text: pasteBuf.ToString());
                                        pasteBuf.Clear();
                                        continue;
                                    }
                                    if (!osc)
                                    {
                                        var parsed = ParseCsi(seq);
                                        if (parsed is not null && !inPaste) yield return parsed;
                                    }
                                    else TryParseReport(seq);
                                }
                                else if (escBuf.Length > 4096) { inEscape = false; escBuf.Clear(); }
                            }
                            continue;
                        }
                        if (ch == '\x1b')
                        {
                            // Could be a real Escape key or the start of a report sequence. If the
                            // very next char in this batch is '[' / 'O' / ']' / 'P' / '_', treat as a sequence.
                            bool nextIsSeq = i + 1 < read && records[i + 1].EventType == KEY_EVENT &&
                                             records[i + 1].KeyEvent.bKeyDown != 0 &&
                                             records[i + 1].KeyEvent.UnicodeChar is '[' or 'O' or ']' or 'P' or '_';
                            if (nextIsSeq) { inEscape = true; escBuf.Clear(); escBuf.Append(ch); continue; }
                            // else fall through and emit Escape via FromKeyEventRecord.
                        }

                        if (inPaste)
                        {
                            if (ch != '\0') pasteBuf.Append(ch == '\r' ? '\n' : ch);
                            continue;
                        }

                        var ev = FromKeyEventRecord(rec.KeyEvent);
                        if (ev is not null) yield return ev;
                    }
                    else if (rec.EventType == MOUSE_EVENT)
                    {
                        var ev = FromMouseEventRecord(rec.MouseEvent);
                        if (ev is not null) yield return ev;
                    }
                    else if (rec.EventType == FOCUS_EVENT)
                    {
                        yield return new KeyEvent(rec.FocusEvent.bSetFocus != 0 ? KeyKind.FocusIn : KeyKind.FocusOut);
                    }
                    // WINDOW_BUFFER_SIZE_EVENT: the render loop polls the console size each tick.
                }
            }
        }
        finally
        {
            if (modeChanged) SetConsoleMode(handle, savedMode);
        }
    }

    private static uint _prevButtonState;

    private static KeyEvent? FromMouseEventRecord(in MOUSE_EVENT_RECORD me)
    {
        bool ctrl = (me.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0;
        bool alt = (me.dwControlKeyState & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED)) != 0;
        bool shift = (me.dwControlKeyState & SHIFT_PRESSED) != 0;
        int row = me.dwMousePosition.Y, colPos = me.dwMousePosition.X;

        if (me.dwEventFlags == MOUSE_WHEELED)
        {
            // High word of dwButtonState is the wheel delta (signed). Positive = wheel up.
            short delta = (short)((me.dwButtonState >> 16) & 0xFFFF);
            return delta > 0
                ? new KeyEvent(KeyKind.MouseScrollUp, Ctrl: ctrl, Alt: alt, Shift: shift, MouseRow: row, MouseCol: colPos)
                : new KeyEvent(KeyKind.MouseScrollDown, Ctrl: ctrl, Alt: alt, Shift: shift, MouseRow: row, MouseCol: colPos);
        }

        bool leftNow = (me.dwButtonState & FROM_LEFT_1ST_BUTTON) != 0;
        bool leftBefore = (_prevButtonState & FROM_LEFT_1ST_BUTTON) != 0;
        bool rightNow = (me.dwButtonState & RIGHTMOST_BUTTON) != 0;
        bool rightBefore = (_prevButtonState & RIGHTMOST_BUTTON) != 0;
        bool middleNow = (me.dwButtonState & FROM_LEFT_2ND_BUTTON) != 0;
        bool middleBefore = (_prevButtonState & FROM_LEFT_2ND_BUTTON) != 0;
        _prevButtonState = me.dwButtonState;

        // Drag: mouse moved while the left button is held.
        if (me.dwEventFlags == MOUSE_MOVED)
        {
            if (leftNow) return new KeyEvent(KeyKind.MouseDrag, MouseRow: row, MouseCol: colPos);
            return null;
        }

        // Button press/release edges (dwEventFlags == 0) and double-clicks.
        if (me.dwEventFlags == 0 || me.dwEventFlags == DOUBLE_CLICK)
        {
            // Right-button press edge → right-click (used to copy a selection).
            if (rightNow && !rightBefore)
                return new KeyEvent(KeyKind.MouseRightClick, MouseRow: row, MouseCol: colPos);
            if (middleNow && !middleBefore)
                return new KeyEvent(KeyKind.MouseMiddleClick, MouseRow: row, MouseCol: colPos);

            // Left-button down edge → a click (follows links in normal mode; starts a drag
            // selection in mark mode — the viewer decides based on its current mode). Ctrl is
            // carried so Ctrl+click can open a link without the confirmation prompt.
            if (leftNow && !leftBefore)
                return new KeyEvent(KeyKind.MouseClick, MouseRow: row, MouseCol: colPos, Ctrl: ctrl, Alt: alt, Shift: shift);

            // Left-button up edge → end of a drag.
            if (!leftNow && leftBefore)
                return new KeyEvent(KeyKind.MouseDragEnd, MouseRow: row, MouseCol: colPos);
        }
        return null;
    }

    private static KeyEvent? FromKeyEventRecord(in KEY_EVENT_RECORD ke)
    {
        bool ctrl = (ke.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0;
        bool alt = (ke.dwControlKeyState & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED)) != 0;
        bool shift = (ke.dwControlKeyState & SHIFT_PRESSED) != 0;
        char ch = (char)ke.UnicodeChar;

        // AltGr (Ctrl+RightAlt on many layouts) produces ordinary printable characters such as '@'
        // or '{': those are plain text, not Ctrl/Alt chords.
        bool altGr = (ke.dwControlKeyState & RIGHT_ALT_PRESSED) != 0 && (ke.dwControlKeyState & LEFT_CTRL_PRESSED) != 0;
        if (altGr && ch != '\0' && !char.IsControl(ch)) { ctrl = false; alt = false; }

        switch (ke.wVirtualKeyCode)
        {
            case VK_UP: return new KeyEvent(KeyKind.Up, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_DOWN: return new KeyEvent(KeyKind.Down, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_LEFT: return new KeyEvent(KeyKind.Left, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_RIGHT: return new KeyEvent(KeyKind.Right, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_HOME: return new KeyEvent(KeyKind.Home, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_END: return new KeyEvent(KeyKind.End, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_PRIOR: return new KeyEvent(KeyKind.PageUp, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_NEXT: return new KeyEvent(KeyKind.PageDown, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_INSERT: return new KeyEvent(KeyKind.Insert, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_DELETE: return new KeyEvent(KeyKind.Delete, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_RETURN: return new KeyEvent(KeyKind.Enter, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_ESCAPE: return new KeyEvent(KeyKind.Escape);
            case VK_BACK: return new KeyEvent(KeyKind.Backspace, Ctrl: ctrl, Alt: alt);
            case VK_TAB: return new KeyEvent(KeyKind.Tab, Ctrl: ctrl, Alt: alt, Shift: shift);
            case >= VK_F1 and <= VK_F12:
                return new KeyEvent(KeyKind.Function, Number: ke.wVirtualKeyCode - VK_F1 + 1, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VK_SHIFT or VK_CONTROL or VK_MENU or VK_CAPITAL
                or VK_LWIN or VK_RWIN or VK_NUMLOCK or VK_SCROLL: return null; // modifier-only
        }

        if (ctrl && ch is >= (char)1 and <= (char)26)
        {
            // Ctrl+A..Z arrive as 1..26; normalize to the letter.
            return new KeyEvent(KeyKind.Char, (char)('a' + (ch - 1)), Ctrl: true, Alt: alt);
        }
        if (ctrl && ch == '\0' && ke.wVirtualKeyCode is >= 0x30 and <= 0x39)
        {
            // Ctrl+digit yields no character; synthesize it from the virtual key.
            return new KeyEvent(KeyKind.Char, (char)('0' + (ke.wVirtualKeyCode - 0x30)), Ctrl: true, Alt: alt);
        }
        if (alt && ch == '\0' && ke.wVirtualKeyCode is >= 0x41 and <= 0x5A)
        {
            // Alt+letter: the record carries no character on some consoles; synthesize it.
            return new KeyEvent(KeyKind.Char, (char)('a' + (ke.wVirtualKeyCode - 0x41)), Alt: true, Ctrl: ctrl);
        }
        if (ch != '\0' && !char.IsControl(ch))
            return new KeyEvent(KeyKind.Char, ch, ctrl, Alt: alt);
        return null;
    }

    // ---------------- Win32 interop ----------------
    private const int STD_INPUT_HANDLE = -10;
    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT = 0x0002;
    private const ushort FOCUS_EVENT = 0x0010;
    private const uint MOUSE_WHEELED = 0x0004;
    private const uint MOUSE_MOVED = 0x0001;
    private const uint DOUBLE_CLICK = 0x0002;
    private const uint FROM_LEFT_1ST_BUTTON = 0x0001;
    private const uint RIGHTMOST_BUTTON = 0x0002;
    private const uint FROM_LEFT_2ND_BUTTON = 0x0004;
    private const int LEFT_CTRL_PRESSED = 0x0008;
    private const int RIGHT_CTRL_PRESSED = 0x0004;
    private const int LEFT_ALT_PRESSED = 0x0002;
    private const int RIGHT_ALT_PRESSED = 0x0001;
    private const int SHIFT_PRESSED = 0x0010;

    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;

    // Mode while we capture the mouse (for wheel scrolling): mouse on, quick-edit off.
    private const uint MouseCaptureMode = ENABLE_PROCESSED_INPUT | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT | ENABLE_EXTENDED_FLAGS;
    // Mode without mouse reporting: quick-edit on so the terminal's native click-drag selection works.
    private const uint NoMouseMode = ENABLE_PROCESSED_INPUT | ENABLE_WINDOW_INPUT | ENABLE_EXTENDED_FLAGS | ENABLE_QUICK_EDIT_MODE;

    private static nint _inputHandle;
    private static uint _savedMode;

    /// <summary>
    /// Switches between mouse-capture mode (wheel scrolling, clicks) and no-mouse mode (the
    /// terminal's native click-drag text selection). Only affects Windows; no-op elsewhere.
    /// </summary>
    public static void SetMouseCapture(bool capture)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (_inputHandle == 0) return;
        SetConsoleMode(_inputHandle, capture ? MouseCaptureMode : NoMouseMode);
    }

    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10,
        VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B,
        VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_INSERT = 0x2D, VK_DELETE = 0x2E,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_F1 = 0x70, VK_F12 = 0x7B, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleInputW(nint hConsoleInput, [Out] INPUT_RECORD[] lpBuffer,
        uint nLength, out uint lpNumberOfEventsRead);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
        [FieldOffset(4)] public FOCUS_EVENT_RECORD FocusEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;          // Win32 BOOL (4 bytes); keep as int so the struct stays blittable
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort UnicodeChar;    // WCHAR
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FOCUS_EVENT_RECORD
    {
        public int bSetFocus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }
}
