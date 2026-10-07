using System.Runtime.InteropServices;
using System.Text;

namespace Readmd.Terminal;

/// <summary>How many colours the output may use.</summary>
public enum ColorMode
{
    /// <summary>24-bit SGR (38;2;r;g;b).</summary>
    TrueColor,
    /// <summary>xterm 256-colour palette (38;5;n).</summary>
    Ansi256,
    /// <summary>The 16 basic ANSI colours.</summary>
    Ansi16,
    /// <summary>No colour at all; bold/underline/reverse only.</summary>
    None,
}

/// <summary>
/// Low-level terminal control: enables VT processing (Windows), switches to the alternate
/// screen buffer, hides the cursor, and provides SGR helpers that downgrade truecolor to the
/// terminal's palette when needed. Output is buffered per row and flushed explicitly: each frame is
/// wrapped in synchronized-output markers (DEC 2026) and only rows whose content changed since the
/// previous frame are written, so scrolling and status updates never tear or flicker.
/// </summary>
public sealed class AnsiScreen : IDisposable
{
    private readonly Stream _stdout;
    private bool _restored;

    // --- frame composition: one buffer per screen row + a preamble for row-independent sequences ---
    private readonly StringBuilder _preamble = new(1024);
    private readonly List<StringBuilder> _rows = [];
    private readonly List<string> _lastRows = [];
    private StringBuilder _current;
    private bool _invalidateAll = true;
    private int _frameWidth, _frameHeight;

    // --- capture mode (dev/testing) ---
    // When set, the screen renders to an in-memory buffer with a fixed size and performs no terminal
    // I/O (no alt-screen, no Win32 calls). Used by the SixelView dev tool and the test harness.
    private readonly bool _capture;
    private int _capW;
    private int _capH;
    private readonly StringBuilder _captureBuffer = new(64 * 1024);

    /// <summary>Colour depth used for SGR output.</summary>
    public ColorMode Colors { get; set; } = ColorMode.TrueColor;

    /// <summary>Emit OSC 8 hyperlinks for links (terminals that don't support them ignore the escapes).</summary>
    public bool Hyperlinks { get; set; } = true;

    /// <summary>Wrap graphics/OSC sequences for tmux passthrough (requires `allow-passthrough on`).</summary>
    public bool TmuxPassthrough { get; set; }

    /// <summary>Creates an in-memory capture screen of a fixed size (no terminal side effects).</summary>
    public static AnsiScreen CreateCapture(int width, int height) => new(width, height);

    /// <summary>Capture mode: simulates a terminal resize (tests).</summary>
    internal void SetCaptureSize(int width, int height)
    {
        if (!_capture) return;
        _capW = width; _capH = height;
        _frameWidth = width; _frameHeight = height;
    }

    private AnsiScreen(int width, int height)
    {
        _capture = true;
        _capW = width;
        _capH = height;
        _stdout = Stream.Null;
        _current = _preamble;
        _frameWidth = width; _frameHeight = height;
    }

    /// <summary>The accumulated escape/text stream (capture mode only).</summary>
    public string CaptureBuffer
    {
        get
        {
            // In capture mode BeginFrame resets; the buffer contains the current frame fully.
            var sb = new StringBuilder(_captureBuffer.ToString());
            sb.Append(_preamble);
            foreach (var r in _rows) sb.Append(r);
            return sb.ToString();
        }
    }

    public AnsiScreen()
    {
        EnableVirtualTerminal();
        EnterRawModeUnix();
        Console.OutputEncoding = Encoding.UTF8;
        _stdout = Console.OpenStandardOutput();
        _current = _preamble;
        TmuxPassthrough = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX"));
        // Enter alt screen, hide cursor, clear. Enable bracketed paste (so pasted text can't be
        // misread as keystrokes — a pasted 'q' must not quit) and focus reporting. Push the window
        // title so we can restore it on exit. On non-Windows also enable SGR mouse reporting (button
        // + drag) so KeyReader can parse wheel/click/drag; Windows uses the console API for the mouse.
        WriteRaw("\e[?1049h\e[?25l\e[2J\e[H\e[?2004h\e[?1004h\e[22;2t");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            WriteRaw("\e[?1000h\e[?1002h\e[?1006h");
        RefreshSize();

        // Safety net: if the process exits without Dispose (e.g. an unhandled exception or a
        // SIGTERM), still restore the main screen, cursor, and input mode so the terminal isn't
        // left in a broken state.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
        RestoreInputMode();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Best-effort restore; the viewer's own handler decides whether to cancel termination.
        Dispose();
        RestoreInputMode();
    }

    /// <summary>Terminal width in columns (cached per frame; see <see cref="RefreshSize"/>).</summary>
    public int Width => _frameWidth;
    /// <summary>Terminal height in rows (cached per frame).</summary>
    public int Height => _frameHeight;

    /// <summary>
    /// Re-reads the console size (a syscall on every platform — do this once per loop tick, not per
    /// cell). Returns true when the size changed.
    /// </summary>
    public bool RefreshSize()
    {
        if (_capture) return false;
        int w, h;
        try { w = Math.Max(20, Console.WindowWidth); h = Math.Max(6, Console.WindowHeight); }
        catch { w = 80; h = 24; }
        bool changed = w != _frameWidth || h != _frameHeight;
        _frameWidth = w; _frameHeight = h;
        if (changed) _invalidateAll = true;
        return changed;
    }

    public void BeginFrame()
    {
        _preamble.Clear();
        foreach (var r in _rows) r.Clear();
        // Track the terminal height both ways: after a shrink, rows past the new bottom must not be
        // flushed (they'd land on the last line or scroll the screen).
        if (_rows.Count > _frameHeight) { _rows.RemoveRange(_frameHeight, _rows.Count - _frameHeight); _invalidateAll = true; }
        while (_rows.Count < _frameHeight) _rows.Add(new StringBuilder(256));
        _current = _preamble;
        if (_capture) _captureBuffer.Clear();
    }

    /// <summary>Forces the next flush to write every row (after a hard clear, resize, overlay change…).</summary>
    public void InvalidateAll() => _invalidateAll = true;

    public AnsiScreen MoveTo(int row, int col)
    {
        if (row >= 0 && row < _rows.Count)
        {
            var target = _rows[row];
            if (!ReferenceEquals(target, _current))
            {
                _current = target;
                // Each row starts from a clean SGR state so rows can be emitted independently.
                if (_current.Length == 0) _current.Append("\e[0m");
            }
        }
        _current.Append("\e[").Append(row + 1).Append(';').Append(col + 1).Append('H');
        return this;
    }

    public AnsiScreen ClearScreen() { _preamble.Append("\e[2J\e[H"); _invalidateAll = true; return this; }
    public AnsiScreen ClearLine() { _current.Append("\e[2K"); return this; }
    /// <summary>Clears from the cursor to the end of the line, filling with the current background.</summary>
    public AnsiScreen ClearLineToEnd() { _current.Append("\e[K"); return this; }
    public AnsiScreen Reset() { _current.Append("\e[0m"); return this; }

    /// <summary>
    /// Clears the entire screen including any Sixel/graphics pixels. Per-line text clears do not
    /// erase Sixel images, so this is used when diagrams are on screen to avoid leftover artifacts.
    /// Sends a kitty graphics-delete plus an erase-display, which together cover the common
    /// graphics-capable terminals (Windows Terminal, WezTerm, kitty, foot, …).
    /// </summary>
    public AnsiScreen HardClear()
    {
        _preamble.Append(Passthrough("\e_Ga=d,d=A,q=2\e\\"));  // kitty: delete all images and free them
        _preamble.Append("\e[2J\e[H");    // erase display, cursor home
        _invalidateAll = true;
        return this;
    }

    /// <summary>
    /// Fills the entire screen with a solid background color (overriding terminal transparency).
    /// Sets the background, then erases every row so each cell carries the color.
    /// </summary>
    public AnsiScreen FillBackground(Rgb background, int rows, int columns)
    {
        for (int r = 0; r < rows; r++)
        {
            MoveTo(r, 0);
            SetBackground(background);
            _current.Append("\e[K");
        }
        MoveTo(0, 0);
        return this;
    }

    public AnsiScreen SetForeground(Rgb c)
    {
        switch (Colors)
        {
            case ColorMode.TrueColor:
                _current.Append("\e[38;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
                break;
            case ColorMode.Ansi256:
                _current.Append("\e[38;5;").Append(ColorQuantizer.ToAnsi256(c)).Append('m');
                break;
            case ColorMode.Ansi16:
                _current.Append("\e[").Append(ColorQuantizer.ToAnsi16Sgr(c, background: false)).Append('m');
                break;
        }
        return this;
    }

    public AnsiScreen SetBackground(Rgb c)
    {
        switch (Colors)
        {
            case ColorMode.TrueColor:
                _current.Append("\e[48;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
                break;
            case ColorMode.Ansi256:
                _current.Append("\e[48;5;").Append(ColorQuantizer.ToAnsi256(c)).Append('m');
                break;
            case ColorMode.Ansi16:
                _current.Append("\e[").Append(ColorQuantizer.ToAnsi16Sgr(c, background: true)).Append('m');
                break;
        }
        return this;
    }

    public AnsiScreen SetStyle(CellStyle style)
    {
        if (style.HasFlag(CellStyle.Bold)) _current.Append("\e[1m");
        if (style.HasFlag(CellStyle.Dim)) _current.Append("\e[2m");
        if (style.HasFlag(CellStyle.Italic)) _current.Append("\e[3m");
        if (style.HasFlag(CellStyle.Underline)) _current.Append("\e[4m");
        if (style.HasFlag(CellStyle.Reverse)) _current.Append("\e[7m");
        if (style.HasFlag(CellStyle.Strikethrough)) _current.Append("\e[9m");
        return this;
    }

    public AnsiScreen Write(string text) { _current.Append(text); return this; }
    public AnsiScreen Write(ReadOnlySpan<char> text) { _current.Append(text); return this; }

    /// <summary>Starts an OSC 8 hyperlink (no-op when disabled). Pair with <see cref="EndHyperlink"/>.</summary>
    public AnsiScreen BeginHyperlink(string url, string? id = null)
    {
        if (!Hyperlinks || string.IsNullOrEmpty(url)) return this;
        // Control characters would break the sequence; links never legitimately contain them.
        if (url.Any(ch => ch < 0x20 || ch == 0x7f)) return this;
        _current.Append("\e]8;").Append(id is null ? "" : "id=" + id).Append(';').Append(url).Append("\e\\");
        return this;
    }

    public AnsiScreen EndHyperlink()
    {
        if (!Hyperlinks) return this;
        _current.Append("\e]8;;\e\\");
        return this;
    }

    /// <summary>Writes pre-formed escape data (e.g. a Sixel sequence) verbatim, tmux-wrapped if needed.</summary>
    public AnsiScreen WriteEscape(string sequence) { _current.Append(Passthrough(sequence)); return this; }

    /// <summary>Writes an escape that must not be tmux-wrapped (plain CSI queries).</summary>
    public AnsiScreen WriteControl(string sequence) { _current.Append(sequence); return this; }

    /// <summary>Sets the terminal window/tab title (OSC 2).</summary>
    public void SetTitle(string title)
    {
        if (_capture) return;
        var clean = new string(title.Where(ch => ch >= 0x20 && ch != 0x7f).ToArray());
        WriteControlNow("\e]2;" + clean + "\a");
    }

    /// <summary>
    /// Wraps a graphics/OSC sequence for tmux passthrough: DCS tmux; with every ESC doubled.
    /// </summary>
    public string Passthrough(string sequence)
    {
        if (!TmuxPassthrough) return sequence;
        return "\ePtmux;" + sequence.Replace("\e", "\e\e") + "\e\\";
    }

    /// <summary>
    /// Writes a control sequence to the terminal immediately, bypassing the frame buffer (used for
    /// out-of-band sequences like the OSC 52 clipboard write that must not interleave with a frame).
    /// </summary>
    public void WriteControlNow(string sequence)
    {
        if (_capture) return;
        var bytes = Encoding.UTF8.GetBytes(sequence);
        _stdout.Write(bytes, 0, bytes.Length);
        _stdout.Flush();
    }

    public void Flush()
    {
        if (_capture)
        {
            // Keep everything for inspection (CaptureBuffer composes it on demand).
            return;
        }
        var sb = new StringBuilder(64 * 1024);
        sb.Append("\e[?2026h");           // begin synchronized update (ignored where unsupported)
        sb.Append(_preamble);
        bool all = _invalidateAll || _lastRows.Count != _rows.Count || _preamble.Length > 0;
        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r].ToString();
            if (all || r >= _lastRows.Count || !string.Equals(row, _lastRows[r], StringComparison.Ordinal))
                sb.Append(row);
        }
        sb.Append("\e[0m\e[?2026l");      // end synchronized update
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        _stdout.Write(bytes, 0, bytes.Length);
        _stdout.Flush();

        _lastRows.Clear();
        foreach (var r in _rows) _lastRows.Add(r.ToString());
        _invalidateAll = false;
        _preamble.Clear();
        foreach (var r in _rows) r.Clear();
        _current = _preamble;
    }

    private void WriteRaw(string s)
    {
        if (_capture) return;
        var bytes = Encoding.UTF8.GetBytes(s);
        var os = Console.OpenStandardOutput();
        os.Write(bytes, 0, bytes.Length);
        os.Flush();
    }

    public void Dispose()
    {
        if (_capture || _restored) return;
        _restored = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Console.CancelKeyPress -= OnCancelKeyPress;
        // Disable SGR mouse reporting (non-Windows), paste/focus reporting, restore the title, show
        // cursor, leave alt screen, reset.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            WriteRaw("\e[?1006l\e[?1002l\e[?1000l");
        WriteRaw("\e[?2004l\e[?1004l\e[23;2t\e[0m\e[?25h\e[?1049l");
        RestoreRawModeUnix();
    }

    // ---------- Windows VT enablement ----------
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    private static uint _savedInputMode;
    private static nint _inputHandle;

    private static void EnableVirtualTerminal()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var stdout = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(stdout, out var outMode))
            SetConsoleMode(stdout, outMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);

        _inputHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (GetConsoleMode(_inputHandle, out _savedInputMode))
        {
            // Disable line buffering + echo so keystrokes reach the reader immediately and don't
            // print. Deliberately KEEP ENABLE_PROCESSED_INPUT so Ctrl+C still raises a signal (a
            // guaranteed escape hatch), and do NOT enable ENABLE_VIRTUAL_TERMINAL_INPUT — keys arrive
            // as key records, which the Win32 reader decodes reliably.
            var inMode = _savedInputMode;
            inMode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT);
            inMode |= ENABLE_PROCESSED_INPUT;
            SetConsoleMode(_inputHandle, inMode);
        }
    }

    public static void RestoreInputMode()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (_inputHandle != 0 && _savedInputMode != 0)
            SetConsoleMode(_inputHandle, _savedInputMode);
    }

    /// <summary>
    /// Puts stdin into raw (unbuffered, no-echo) mode and enables VT processing on stdout without
    /// touching the screen — for one-shot terminal interrogation outside the viewer
    /// (<see cref="TerminalProbe.ProbeBlocking"/>). Pair with <see cref="RestoreRawInput"/>.
    /// </summary>
    internal static void EnterRawInput()
    {
        EnableVirtualTerminal();
        EnterRawModeUnix();
    }

    /// <summary>Undoes <see cref="EnterRawInput"/> on every platform.</summary>
    internal static void RestoreRawInput()
    {
        RestoreInputMode();
        RestoreRawModeUnix();
    }

    /// <summary>Wraps a sequence for tmux passthrough when running under tmux (static form of <see cref="Passthrough"/>).</summary>
    internal static string PassthroughIfTmux(string sequence) =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX")) ? sequence : "\ePtmux;" + sequence.Replace("\e", "\e\e") + "\e\\";

    // ---------- Unix raw mode (termios) ----------
    // Putting the terminal in raw mode lets KeyReader read individual bytes (and mouse escape
    // sequences) instead of line-buffered input. ISIG is kept so Ctrl+C still raises SIGINT.
    private const int STDIN_FILENO = 0;
    private const int TCSANOW = 0;

    // c_lflag bits
    private const uint ICANON = 0x0002, ECHO_ = 0x0008, IEXTEN = 0x8000;
    // c_iflag bits
    private const uint IXON = 0x0400, ICRNL = 0x0100, BRKINT = 0x0002, INPCK = 0x0010, ISTRIP = 0x0020;

    private static byte[]? _savedTermios;
    private static bool _rawModeSet;

    private static void EnterRawModeUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (Console.IsInputRedirected) return;
        try
        {
            var termios = new byte[256];
            if (tcgetattr(STDIN_FILENO, termios) != 0) return;
            _savedTermios = (byte[])termios.Clone();

            // termios layout (Linux & macOS): c_iflag, c_oflag, c_cflag, c_lflag are the first four
            // 4-byte fields. Clear the canonical/echo/signal-interfering bits but keep ISIG.
            ref uint iflag = ref AsUInt(termios, 0);
            ref uint lflag = ref AsUInt(termios, 12);
            iflag &= ~(IXON | ICRNL | BRKINT | INPCK | ISTRIP);
            lflag &= ~(ICANON | ECHO_ | IEXTEN);   // NOTE: ISIG intentionally left on (Ctrl+C works)

            if (tcsetattr(STDIN_FILENO, TCSANOW, termios) == 0)
                _rawModeSet = true;
        }
        catch (DllNotFoundException) { /* libc not present (unexpected on Unix) */ }
        catch (EntryPointNotFoundException) { /* fall back to cooked mode */ }
    }

    private static void RestoreRawModeUnix()
    {
        if (!_rawModeSet || _savedTermios is null) return;
        try { tcsetattr(STDIN_FILENO, TCSANOW, _savedTermios); } catch { /* best effort */ }
        _rawModeSet = false;
    }

    private static ref uint AsUInt(byte[] buffer, int offset) =>
        ref System.Runtime.InteropServices.MemoryMarshal.AsRef<uint>(buffer.AsSpan(offset, 4));

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, [Out] byte[] termios_p);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optional_actions, [In] byte[] termios_p);
}

/// <summary>Maps truecolor values onto the xterm-256 and 16-colour palettes.</summary>
public static class ColorQuantizer
{
    private static readonly (int R, int G, int B)[] Ansi16 =
    {
        (0, 0, 0), (205, 49, 49), (13, 188, 121), (229, 229, 16), (36, 114, 200), (188, 63, 188), (17, 168, 205), (229, 229, 229),
        (102, 102, 102), (241, 76, 76), (35, 209, 139), (245, 245, 67), (59, 142, 234), (214, 112, 214), (41, 184, 219), (255, 255, 255),
    };

    /// <summary>Nearest xterm-256 index (colour cube or grey ramp).</summary>
    public static int ToAnsi256(Rgb c)
    {
        // Grey ramp candidate (indices 232..255 cover 8..238 in steps of 10).
        int grey = (int)Math.Round((c.R * 0.299 + c.G * 0.587 + c.B * 0.114));
        int greyIdx = Math.Clamp((grey - 8) / 10, 0, 23);
        int greyVal = 8 + greyIdx * 10;
        // Cube candidate (0,95,135,175,215,255).
        static int Level(int v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        static int Value(int l) => l == 0 ? 0 : 55 + l * 40;
        int rl = Level(c.R), gl = Level(c.G), bl = Level(c.B);
        int cubeIdx = 16 + 36 * rl + 6 * gl + bl;
        int dCube = Dist(c, (Value(rl), Value(gl), Value(bl)));
        int dGrey = Dist(c, (greyVal, greyVal, greyVal));
        return dGrey < dCube ? 232 + greyIdx : cubeIdx;
    }

    /// <summary>SGR parameter for the nearest basic colour (30–37/90–97, or 40–47/100–107).</summary>
    public static int ToAnsi16Sgr(Rgb c, bool background)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < Ansi16.Length; i++)
        {
            int d = Dist(c, Ansi16[i]);
            if (d < bestD) { bestD = d; best = i; }
        }
        int baseCode = best < 8 ? (background ? 40 : 30) + best : (background ? 100 : 90) + (best - 8);
        return baseCode;
    }

    private static int Dist(Rgb c, (int R, int G, int B) p)
    {
        int dr = c.R - p.R, dg = c.G - p.G, db = c.B - p.B;
        // Weighted for perceived difference.
        return 3 * dr * dr + 4 * dg * dg + 2 * db * db;
    }
}
