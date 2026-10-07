using System.Runtime.InteropServices;
using System.Text;

namespace Readmd.Terminal;

/// <summary>
/// Low-level terminal control: enables VT processing (Windows), switches to the alternate
/// screen buffer, hides the cursor, and provides truecolor SGR helpers. Output is buffered and
/// flushed explicitly so each frame is written in a single syscall (reduces flicker).
/// </summary>
public sealed class AnsiScreen : IDisposable
{
    private readonly StringBuilder _buffer = new(64 * 1024);
    private readonly Stream _stdout;
    private bool _restored;

    // --- capture mode (dev/testing) ---
    // When set, the screen renders to an in-memory buffer with a fixed size and performs no terminal
    // I/O (no alt-screen, no Win32 calls). Used by the SixelView dev tool to rasterise overlays.
    private readonly bool _capture;
    private readonly int _capW;
    private readonly int _capH;

    /// <summary>Creates an in-memory capture screen of a fixed size (no terminal side effects).</summary>
    public static AnsiScreen CreateCapture(int width, int height) => new(width, height);

    private AnsiScreen(int width, int height)
    {
        _capture = true;
        _capW = width;
        _capH = height;
        _stdout = Stream.Null;
    }

    /// <summary>The accumulated escape/text stream (capture mode only).</summary>
    public string CaptureBuffer => _buffer.ToString();

    public AnsiScreen()
    {
        EnableVirtualTerminal();
        EnterRawModeUnix();
        Console.OutputEncoding = Encoding.UTF8;
        _stdout = Console.OpenStandardOutput();
        // Enter alt screen, hide cursor, clear. Enable bracketed paste (so pasted text can't be
        // misread as keystrokes — a pasted 'q' must not quit) and focus reporting. On non-Windows
        // also enable SGR mouse reporting (button + drag) so KeyReader can parse wheel/click/drag;
        // Windows uses the console API for the mouse.
        WriteRaw("\e[?1049h\e[?25l\e[2J\e[H\e[?2004h\e[?1004h");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            WriteRaw("\e[?1000h\e[?1002h\e[?1006h");
        Flush();

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

    public int Width => _capture ? _capW : Math.Max(20, Console.WindowWidth);
    public int Height => _capture ? _capH : Math.Max(6, Console.WindowHeight);

    public void BeginFrame() => _buffer.Clear();

    public AnsiScreen MoveTo(int row, int col)
    {
        _buffer.Append("\e[").Append(row + 1).Append(';').Append(col + 1).Append('H');
        return this;
    }

    public AnsiScreen ClearScreen() { _buffer.Append("\e[2J\e[H"); return this; }
    public AnsiScreen ClearLine() { _buffer.Append("\e[2K"); return this; }
    /// <summary>Clears from the cursor to the end of the line, filling with the current background.</summary>
    public AnsiScreen ClearLineToEnd() { _buffer.Append("\e[K"); return this; }
    public AnsiScreen Reset() { _buffer.Append("\e[0m"); return this; }

    /// <summary>
    /// Clears the entire screen including any Sixel/graphics pixels. Per-line text clears do not
    /// erase Sixel images, so this is used when diagrams are on screen to avoid leftover artifacts.
    /// Sends a kitty graphics-delete plus an erase-display, which together cover the common
    /// graphics-capable terminals (Windows Terminal, WezTerm, kitty, foot, …).
    /// </summary>
    public AnsiScreen HardClear()
    {
        _buffer.Append("\e_Ga=d\e\\");  // kitty: delete all images (ignored by non-kitty terminals)
        _buffer.Append("\e[2J\e[H");    // erase display, cursor home
        return this;
    }

    /// <summary>
    /// Fills the entire screen with a solid background color (overriding terminal transparency).
    /// Sets the background, then erases every row so each cell carries the color.
    /// </summary>
    public AnsiScreen FillBackground(Rgb background, int rows, int columns)
    {
        SetBackground(background);
        for (int r = 0; r < rows; r++)
        {
            MoveTo(r, 0);
            _buffer.Append("\e[K");
        }
        MoveTo(0, 0);
        return this;
    }

    public AnsiScreen SetForeground(Rgb c)
    {
        _buffer.Append("\e[38;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
        return this;
    }

    public AnsiScreen SetBackground(Rgb c)
    {
        _buffer.Append("\e[48;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
        return this;
    }

    public AnsiScreen SetStyle(CellStyle style)
    {
        if (style.HasFlag(CellStyle.Bold)) _buffer.Append("\e[1m");
        if (style.HasFlag(CellStyle.Dim)) _buffer.Append("\e[2m");
        if (style.HasFlag(CellStyle.Italic)) _buffer.Append("\e[3m");
        if (style.HasFlag(CellStyle.Underline)) _buffer.Append("\e[4m");
        if (style.HasFlag(CellStyle.Reverse)) _buffer.Append("\e[7m");
        if (style.HasFlag(CellStyle.Strikethrough)) _buffer.Append("\e[9m");
        return this;
    }

    public AnsiScreen Write(string text) { _buffer.Append(text); return this; }
    public AnsiScreen Write(ReadOnlySpan<char> text) { _buffer.Append(text); return this; }

    /// <summary>Writes pre-formed escape data (e.g. a Sixel sequence) verbatim.</summary>
    public AnsiScreen WriteEscape(string sequence) { _buffer.Append(sequence); return this; }

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
        if (_capture) return;   // keep the buffer; CaptureBuffer exposes it
        var bytes = Encoding.UTF8.GetBytes(_buffer.ToString());
        _stdout.Write(bytes, 0, bytes.Length);
        _stdout.Flush();
        _buffer.Clear();
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
        // Disable SGR mouse reporting (non-Windows), paste/focus reporting, show cursor, leave alt
        // screen, reset.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            WriteRaw("\e[?1006l\e[?1002l\e[?1000l");
        WriteRaw("\e[?2004l\e[?1004l\e[0m\e[?25h\e[?1049l");
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
            // Disable line buffering + echo so keystrokes reach Console.ReadKey immediately and
            // don't print. Deliberately KEEP ENABLE_PROCESSED_INPUT so Ctrl+C still raises a
            // signal (a guaranteed escape hatch), and do NOT enable ENABLE_VIRTUAL_TERMINAL_INPUT
            // — that turns keys into raw escape bytes that Console.ReadKey cannot parse, which
            // made the viewer go deaf to 'q' on Windows Terminal.
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
