using System.Runtime.InteropServices;

namespace Readmd.Terminal;

/// <summary>How inline images/diagrams are drawn in the terminal.</summary>
public enum GraphicsMode
{
    /// <summary>Sixel graphics — for terminals that support it (Windows Terminal ≥ 1.22, xterm, foot, WezTerm, mintty, …).</summary>
    Sixel,
    /// <summary>Unicode half-block (▀) cells with truecolor — works on any truecolor terminal.</summary>
    HalfBlock,
    /// <summary>No inline images; diagrams/images show only their caption line (and diagram source).</summary>
    None,
    /// <summary>kitty graphics protocol (lossless PNG with alpha): kitty, Ghostty, WezTerm, Konsole, wayst.</summary>
    Kitty,
    /// <summary>iTerm2 inline images (OSC 1337): iTerm2, WezTerm, Hyper, mintty.</summary>
    ITerm2,
}

/// <summary>
/// Decides how to draw inline images/diagrams and how many colours to use. Detection combines the
/// well-known environment signals with live terminal queries (<see cref="TerminalProbe"/>): the
/// heuristic answer is used immediately and refined when the terminal replies (Device Attributes
/// for Sixel, the kitty graphics query, OSC 11 for the background colour). Everything can be
/// overridden explicitly (<c>READMD_GRAPHICS=sixel|kitty|iterm2|half-block|none|auto</c> or config
/// <c>graphics</c>; <c>--color</c> / <c>NO_COLOR</c> for colour depth).
/// </summary>
public static class TerminalCapabilities
{
    /// <summary>
    /// Resolves the graphics mode. <paramref name="configured"/> is the config value (if any);
    /// the <c>READMD_GRAPHICS</c> environment variable takes precedence over it; "auto"/null fall
    /// back to detection.
    /// </summary>
    public static GraphicsMode Resolve(string? configured)
    {
        var env = Environment.GetEnvironmentVariable("READMD_GRAPHICS");
        var choice = !string.IsNullOrWhiteSpace(env) ? env : configured;
        return Parse(choice) ?? Detect();
    }

    /// <summary>Parses an explicit mode name; null for auto/unknown.</summary>
    public static GraphicsMode? Parse(string? choice) => choice?.Trim().ToLowerInvariant() switch
    {
        "sixel" => GraphicsMode.Sixel,
        "kitty" or "kgp" => GraphicsMode.Kitty,
        "iterm" or "iterm2" or "osc1337" => GraphicsMode.ITerm2,
        "half-block" or "halfblock" or "blocks" or "block" => GraphicsMode.HalfBlock,
        "none" or "off" or "text" => GraphicsMode.None,
        _ => null,
    };

    /// <summary>True when the mode was chosen automatically (so probe replies may refine it).</summary>
    public static bool IsAuto(string? configured)
    {
        var env = Environment.GetEnvironmentVariable("READMD_GRAPHICS");
        var choice = !string.IsNullOrWhiteSpace(env) ? env : configured;
        return Parse(choice) is null;
    }

    /// <summary>Best-effort detection from environment markers; defaults to half-block when unsure.</summary>
    public static GraphicsMode Detect()
    {
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
        bool tmux = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX"));

        // kitty-protocol terminals (lossless, alpha). Ghostty and kitty set TERM; Konsole/WezTerm set TERM_PROGRAM.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID")) || term.Contains("kitty", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.Kitty;
        if (termProgram.Equals("ghostty", StringComparison.OrdinalIgnoreCase) || term.Contains("ghostty", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.Kitty;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KONSOLE_VERSION"))) return GraphicsMode.Kitty;
        // iTerm2 (and apps that announce themselves as it) use OSC 1337.
        if (termProgram.Equals("iTerm.app", StringComparison.OrdinalIgnoreCase) || (Environment.GetEnvironmentVariable("LC_TERMINAL") ?? "").Equals("iTerm2", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.ITerm2;
        if (termProgram.Contains("Hyper", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.ITerm2;

        // Known Sixel-capable terminals (by env marker). Under tmux, Sixel needs tmux ≥ 3.4 built
        // with --enable-sixel; be conservative and use half-block there unless told otherwise.
        if (!tmux)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))) return GraphicsMode.Sixel; // Windows Terminal ≥ 1.22
            if (term.Contains("sixel", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.Sixel;
            if (term.Contains("foot", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.Sixel;
            if (term.Contains("contour", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.Sixel;
            if (termProgram.Contains("WezTerm", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.Sixel;
            if (termProgram.Contains("mintty", StringComparison.OrdinalIgnoreCase)) return GraphicsMode.Sixel;
            if (term is "xterm-256color" && OperatingSystem.IsWindows()) return GraphicsMode.Sixel;
        }

        // Anything else gets the universally-supported half-block path.
        return GraphicsMode.HalfBlock;
    }

    /// <summary>
    /// Resolves colour depth: explicit <c>--color</c> (auto/always/never) first, then NO_COLOR,
    /// then COLORTERM / TERM / platform heuristics. "always" means truecolor.
    /// </summary>
    public static ColorMode ResolveColors(string? colorArg)
    {
        switch (colorArg?.Trim().ToLowerInvariant())
        {
            case "never" or "none" or "off": return ColorMode.None;
            case "always" or "truecolor" or "24bit": return ColorMode.TrueColor;
            case "256": return ColorMode.Ansi256;
            case "16": return ColorMode.Ansi16;
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return ColorMode.None;
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM") ?? "";
        if (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase) || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase)) return ColorMode.TrueColor;
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
        if (term == "dumb") return ColorMode.None;
        if (term == "linux") return ColorMode.Ansi16;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return ColorMode.TrueColor;   // conhost ≥ Win10 1703 and WT both do 24-bit
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID"))) return ColorMode.TrueColor;
        if (term.Contains("kitty") || term.Contains("ghostty") || term.Contains("wezterm") || term.Contains("alacritty") || term.Contains("foot") || term.Contains("contour") || term.Contains("direct")) return ColorMode.TrueColor;
        if (termProgram is "iTerm.app" or "WezTerm" or "vscode" or "Hyper" or "ghostty") return ColorMode.TrueColor;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VTE_VERSION"))) return ColorMode.TrueColor;   // GNOME Terminal, Tilix, …
        if (term.Contains("256color")) return ColorMode.Ansi256;
        if (term.StartsWith("xterm") || term.StartsWith("screen") || term.StartsWith("tmux") || term.StartsWith("rxvt")) return ColorMode.Ansi256;
        return ColorMode.Ansi256;
    }
}

/// <summary>
/// Live terminal interrogation. <see cref="Start"/> sends the queries; replies arrive through the
/// input reader (<see cref="KeyReader.ReportReceived"/>) and are parsed here, so the viewer can
/// upgrade its graphics mode, cell size and theme without guessing. Nothing here blocks.
/// </summary>
public static class TerminalProbe
{
    /// <summary>Sixel advertised in Device Attributes (DA1 parameter 4). Null until the reply arrives.</summary>
    public static bool? Sixel { get; private set; }
    /// <summary>The terminal answered the kitty graphics query with OK.</summary>
    public static bool? Kitty { get; private set; }
    /// <summary>Terminal background colour from OSC 11.</summary>
    public static Rgb? Background { get; private set; }
    /// <summary>Terminal name/version from XTVERSION (CSI > 0 q), when reported.</summary>
    public static string? TerminalVersion { get; private set; }
    /// <summary>Synchronized output (DEC 2026) supported, from DECRQM.</summary>
    public static bool? SynchronizedOutput { get; private set; }

    private static readonly object Gate = new();

    static TerminalProbe()
    {
        // Replies are routed here from whichever input reader is active (or the test parser).
        KeyReader.ReportReceived += OnReport;
    }

    /// <summary>Forces the static constructor to run (so replies are captured before Start is called).</summary>
    public static void EnsureHooked() { lock (Gate) { } }

    /// <summary>Sends the queries (the replies are asynchronous). Safe to call once per session.</summary>
    public static void Start(AnsiScreen screen)
    {
        // Order matters a little: DA1 is universally answered, so it doubles as a "queries done"
        // marker — anything the terminal didn't understand will have been ignored by the time it
        // replies to DA1 (terminals answer in order).
        var q = new System.Text.StringBuilder();
        q.Append("\e[14t");                                            // window size in pixels
        q.Append("\e[16t");                                            // cell size in pixels
        q.Append("\e]11;?\e\\");                                       // background colour
        q.Append("\e[?2026$p");                                        // synchronized output supported?
        q.Append(screen.Passthrough("\e_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\e\\")); // kitty graphics: 1x1 RGB query
        q.Append("\e[>0q");                                            // XTVERSION
        q.Append("\e[c");                                              // DA1
        screen.WriteControlNow(q.ToString());
    }

    internal static void OnReport(string seq)
    {
        // DA1: "[?64;1;2;4;6;…c" — parameter 4 = Sixel graphics.
        if (seq.StartsWith("[?") && seq.EndsWith('c'))
        {
            var ps = seq[2..^1].Split(';');
            Sixel = ps.Contains("4");
            return;
        }
        // kitty: "_Gi=31;OK" (we receive the body after ESC, without the ST).
        if (seq.StartsWith("_G"))
        {
            Kitty = seq.Contains("i=31") && seq.Contains(";OK");
            return;
        }
        // OSC 11 reply: "]11;rgb:1e1e/1e1e/1e1e" (16-bit per channel, sometimes 8-bit).
        if (seq.StartsWith("]11;"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(seq, @"rgb:([0-9a-fA-F]+)/([0-9a-fA-F]+)/([0-9a-fA-F]+)");
            if (m.Success)
            {
                static byte Chan(string hex) => hex.Length >= 2 ? Convert.ToByte(hex[..2], 16) : (byte)(Convert.ToByte(hex, 16) * 17);
                Background = new Rgb(Chan(m.Groups[1].Value), Chan(m.Groups[2].Value), Chan(m.Groups[3].Value));
            }
            return;
        }
        // XTVERSION: DCS > | name version ST  → we get "P>|WezTerm 2024…"
        if (seq.StartsWith("P>|"))
        {
            TerminalVersion = seq[3..].Trim();
            return;
        }
        // DECRPM for 2026: "[?2026;1$y" / ";2$y" = set/reset (supported), ";0$y" = unknown.
        if (seq.StartsWith("[?2026;") && seq.EndsWith("$y"))
        {
            SynchronizedOutput = !seq.Contains(";0$y");
            return;
        }
    }

    /// <summary>Chooses the best graphics mode given what the terminal has told us so far.</summary>
    public static GraphicsMode? Refine(GraphicsMode current)
    {
        bool tmux = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX"));
        if (Kitty == true) return GraphicsMode.Kitty;
        if (Sixel == true && !tmux) return GraphicsMode.Sixel;
        if (Sixel == false && current == GraphicsMode.Sixel && Kitty != true)
        {
            // We guessed Sixel from the environment but the terminal denies it. iTerm2-family
            // terminals are better served by OSC 1337; everyone else by half-block.
            var tp = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
            return tp.Contains("WezTerm", StringComparison.OrdinalIgnoreCase) || tp.Contains("iTerm", StringComparison.OrdinalIgnoreCase)
                ? GraphicsMode.ITerm2 : GraphicsMode.HalfBlock;
        }
        return null;
    }

    /// <summary>Resets probe state (tests).</summary>
    internal static void Reset()
    {
        Sixel = null; Kitty = null; Background = null; TerminalVersion = null; SynchronizedOutput = null;
    }
}
