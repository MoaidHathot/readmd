using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Readmd.Core;
using Readmd.Diagrams;
using Readmd.Terminal;

/// <summary>
/// <c>readmd doctor</c>: a one-page report of everything that affects what readmd can do on this
/// machine — terminal capabilities (environment + a live probe), colour depth, config files and
/// their status, diagram backends and the tools behind them, PDF export readiness, clipboard
/// helpers, and where state is kept. Every line is actionable: when something is missing the fix
/// (a flag, a config key, an install) is named.
/// </summary>
internal static class Doctor
{
    public static async Task<int> RunAsync(string version, bool offline, bool noProbe, CancellationToken ct)
    {
        var o = new StringBuilder();
        bool ascii = Glyphs.Ascii;
        string Ok = ascii ? "[ok]" : "✓", No = ascii ? "[--]" : "✗", Warn = ascii ? "[!!]" : "!";
        string Dot = ascii ? " - " : " · ";

        // ---------------- header ----------------
        o.AppendLine($"readmd {version}{Dot}.NET {Environment.Version}{Dot}{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})");
        o.AppendLine();

        // ---------------- terminal ----------------
        o.AppendLine("Terminal");
        string Env(string n) => Environment.GetEnvironmentVariable(n) ?? "";
        bool tmux = Env("TMUX").Length > 0;
        var envBits = new List<string>();
        foreach (var n in new[] { "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "COLORTERM", "WT_SESSION", "KITTY_WINDOW_ID", "KONSOLE_VERSION", "VTE_VERSION", "LC_TERMINAL" })
            if (Env(n).Length > 0) envBits.Add($"{n}={(n == "WT_SESSION" ? "set" : Env(n))}");
        o.AppendLine($"  environment    {(envBits.Count > 0 ? string.Join("  ", envBits) : "(no terminal variables set)")}{(tmux ? "  (inside tmux)" : "")}");
        var colors = TerminalCapabilities.ResolveColors(null);
        var colorNote = Env("NO_COLOR").Length > 0 ? " (NO_COLOR is set)" : Env("FORCE_COLOR").Length > 0 ? " (FORCE_COLOR is set)" : "";
        o.AppendLine($"  colour depth   {ColorName(colors)}{colorNote}");
        var detected = TerminalCapabilities.Detect();
        var envGraphics = Env("READMD_GRAPHICS");
        o.AppendLine($"  graphics       {GraphicsName(detected)} (from the environment{(envGraphics.Length > 0 ? $", READMD_GRAPHICS={envGraphics}" : "")})");

        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        GraphicsMode effective = detected;
        bool? probedDark = null;
        if (noProbe) o.AppendLine("  live probe     skipped (--no-probe)");
        else if (!interactive) o.AppendLine("  live probe     skipped (stdin/stdout is not a terminal)");
        else
        {
            TerminalProbe.ProbeBlocking(TimeSpan.FromMilliseconds(1500));
            var parts = new List<string>
            {
                $"sixel {YesNo(TerminalProbe.Sixel)}",
                $"kitty graphics {YesNo(TerminalProbe.Kitty)}",
                $"synchronized output {YesNo(TerminalProbe.SynchronizedOutput)}",
            };
            if (TerminalProbe.Background is { } bg) { probedDark = bg.Luma < 128; parts.Add($"background {TerminalTheme.Hex(bg)} ({(probedDark.Value ? "dark" : "light")})"); }
            if (TerminalProbe.CellPixelSize is { } cell) parts.Add($"cell {cell.WidthPx}x{cell.HeightPx} px");
            if (!string.IsNullOrEmpty(TerminalProbe.TerminalVersion)) parts.Add(TerminalProbe.TerminalVersion);
            bool answered = TerminalProbe.Sixel is not null || TerminalProbe.Kitty is not null || TerminalProbe.Background is not null;
            o.AppendLine($"  live probe     {(answered ? string.Join(Dot, parts) : "no reply within 1.5 s (the terminal ignores DA1/OSC queries)")}");
            effective = TerminalProbe.Refine(detected) ?? detected;
        }
        var modeHint = effective switch
        {
            GraphicsMode.None => "  (diagram source/box art will be shown; try --graphics half-block)",
            GraphicsMode.HalfBlock => "  (coarse images; a Sixel/kitty-capable terminal renders real images)",
            GraphicsMode.Sixel when tmux => "  (tmux: needs `set -g allow-passthrough on`)",
            _ => "",
        };
        o.AppendLine($"  {Ok} readmd will use {GraphicsName(effective)} graphics, {ColorName(colors)} colour, {(probedDark ?? DetectDarkFromEnv()) switch { true => "dark", false => "light" }} theme by default{modeHint}");
        o.AppendLine();

        // ---------------- config ----------------
        o.AppendLine("Config");
        var userPath = ConfigLoader.UserConfigPath();
        o.AppendLine($"  user           {userPath}  {(File.Exists(userPath) ? Ok : "(not found; `readmd config init` creates one)")}");
        var projectPath = ConfigLoader.FindProjectConfig(Directory.GetCurrentDirectory());
        o.AppendLine($"  project        {(projectPath is not null ? projectPath + "  " + Ok : "(no .readmd.json above the current directory)")}");
        var config = ConfigLoader.Load(Directory.GetCurrentDirectory());
        if (ConfigLoader.LastError is { } err) o.AppendLine($"  {Warn} config ignored: {err}");
        else
        {
            var issues = ConfigValidator.Validate(config);
            o.AppendLine(issues.Count == 0 ? $"  {Ok} config parses and validates" : $"  {Warn} {issues.Count} config issue(s) — run `readmd config validate`");
        }
        var statePath = StateStore.DefaultPath();
        string stateInfo = File.Exists(statePath) ? $"{Ok} {SizeText(new FileInfo(statePath).Length)}" : "(not created yet)";
        o.AppendLine($"  state          {statePath}  {stateInfo}{(config.RememberPosition == false ? "  (rememberPosition: false)" : "")}");
        var activeTheme = config.Theme ?? "auto";
        var catalog = ThemeCatalog.Build(config);
        o.AppendLine($"  theme          {activeTheme}{(activeTheme is not ("auto" or "dark" or "light") && !catalog.ContainsKey(activeTheme) ? $"  {Warn} unknown (see `readmd themes`)" : "")}{Dot}{catalog.Count} themes available");
        o.AppendLine();

        // ---------------- diagrams ----------------
        o.AppendLine("Diagrams");
        bool network = !offline && config.Network != false && Environment.GetEnvironmentVariable("READMD_OFFLINE") is not ("1" or "true");
        await using (var diagrams = new DiagramRenderer(new DiagramRendererOptions
        {
            D2Path = config.D2Path, MermaidCliPath = config.MermaidCliPath, GraphvizPath = config.GraphvizPath, PlantUmlPath = config.PlantUmlPath,
            KrokiUrl = config.KrokiUrl, KrokiFallback = config.KrokiFallback ?? true, AllowNetwork = network,
        }))
        {
            var backends = diagrams.DescribeBackends();
            foreach (var (kindLabel, backend) in backends)
            {
                // "others (ditaa, svgbob, …)" → a short row label with the list in the detail column.
                var kind = kindLabel.Split(' ')[0];
                string detail = kindLabel.Length > kind.Length ? "  " + kindLabel[kind.Length..].Trim() : "";
                string tool = kind switch
                {
                    "mermaid" => config.MermaidCliPath ?? "mmdc",
                    "d2" => config.D2Path ?? "d2",
                    "graphviz" => config.GraphvizPath ?? "dot",
                    "plantuml" => config.PlantUmlPath ?? "plantuml",
                    _ => "",
                };
                if (tool.Length > 0)
                {
                    var found = ExecutableResolver.Find(tool);
                    if (found is not null && backend.Contains("(local)"))
                    {
                        var ver = ToolVersion(tool, kind switch { "graphviz" => "-V", "plantuml" => "-version", _ => "--version" });
                        detail = $"  {found}{(ver is not null ? $" (v{ver})" : "")}";
                        if (kind == "d2" && ver is not null && !VersionAtLeast(ver, 0, 7, 1)) detail += "  (ASCII rendering needs d2 >= 0.7.1)";
                    }
                    else if (found is null && backend.StartsWith("kroki")) detail = $"  ({tool} not on PATH; rendered remotely)";
                    else if (found is null && backend.StartsWith("unavailable")) detail = $"  (install {tool}, or allow the network for Kroki)";
                }
                o.AppendLine($"  {kind,-14} {(backend.StartsWith("unavailable") ? No : Ok)} {backend}{detail}");
            }
            o.AppendLine($"  network        {(network ? $"on{Dot}Kroki {(config.KrokiFallback == false ? "fallback disabled (krokiFallback: false)" : "at " + (config.KrokiUrl ?? "https://kroki.io"))}" : "off (--offline / READMD_OFFLINE / \"network\": false)")}");
        }
        var cacheDir = Path.Combine(Path.GetTempPath(), "readmd", "diagram-cache");
        o.AppendLine($"  cache          {cacheDir}  {DirSummary(cacheDir)}");
        o.AppendLine();

        // ---------------- PDF ----------------
        o.AppendLine("PDF export");
        var node = PdfProvisioning.FindNode();
        o.AppendLine($"  node.js        {(node is not null ? $"{Ok} {node}{VersionSuffix(ToolVersion(node, "--version"))}" : $"{No} not found (needed for --export file.pdf; https://nodejs.org)")}");
        var pdf = PdfProvisioning.CheckReadiness();
        o.AppendLine($"  chromium       {pdf.Readiness switch
        {
            PdfReadiness.Ready => $"{Ok} installed — `--export out.pdf` is ready",
            PdfReadiness.BrowserMissing => $"{No} not installed — run `readmd install-pdf` (~100 MB, one time)",
            PdfReadiness.DriverMissing => $"{No} Playwright driver missing from this install",
            _ => $"{No} {pdf.Message}",
        }}");
        o.AppendLine();

        // ---------------- clipboard ----------------
        o.AppendLine("Clipboard");
        var clipTools = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) clipTools.Add("clip");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) clipTools.Add("pbcopy");
        else { clipTools.AddRange(["wl-copy", "xclip", "xsel"]); if (Env("WSL_DISTRO_NAME").Length > 0 || Env("WSL_INTEROP").Length > 0) clipTools.Add("clip.exe"); }
        var foundTools = clipTools.Where(t => ExecutableResolver.Find(t) is not null).ToList();
        o.AppendLine($"  tools          {(foundTools.Count > 0 ? $"{Ok} {string.Join(", ", foundTools)}" : $"{Warn} none of {string.Join(", ", clipTools)} found")}{Dot}OSC 52 is always attempted (works in most modern terminals, and through SSH/tmux)");
        o.AppendLine();

        // ---------------- paths ----------------
        o.AppendLine("Install");
        var location = typeof(Doctor).Assembly.Location;
        if (string.IsNullOrEmpty(location)) location = Environment.ProcessPath ?? AppContext.BaseDirectory;   // single-file publish
        o.AppendLine($"  executable     {location}");
        o.AppendLine($"  shell help     `readmd completions <bash|zsh|fish|pwsh>`{Dot}`readmd man`{Dot}`readmd themes`{Dot}`readmd config path`");

        Utf8Out.Write(o.ToString());
        return 0;
    }

    private static string YesNo(bool? v) => v switch { true => "yes", false => "no", null => "?" };

    private static bool VersionAtLeast(string version, int major, int minor, int patch)
    {
        var parts = version.Split('.');
        int a = parts.Length > 0 && int.TryParse(parts[0], out var x) ? x : 0;
        int b = parts.Length > 1 && int.TryParse(parts[1], out var y) ? y : 0;
        int c = parts.Length > 2 && int.TryParse(parts[2], out var z) ? z : 0;
        return (a, b, c).CompareTo((major, minor, patch)) >= 0;
    }
    private static string VersionSuffix(string? v) => v is null ? "" : $" (v{v})";

    private static string ColorName(ColorMode m) => m switch
    {
        ColorMode.TrueColor => "truecolor", ColorMode.Ansi256 => "256-colour", ColorMode.Ansi16 => "16-colour", _ => "no colour",
    };

    private static string GraphicsName(GraphicsMode m) => m switch
    {
        GraphicsMode.Sixel => "sixel", GraphicsMode.Kitty => "kitty", GraphicsMode.ITerm2 => "iterm2", GraphicsMode.HalfBlock => "half-block", _ => "none",
    };

    private static bool DetectDarkFromEnv()
    {
        var fgbg = Environment.GetEnvironmentVariable("COLORFGBG");
        if (!string.IsNullOrEmpty(fgbg))
        {
            var parts = fgbg.Split(';');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var bg)) return bg <= 6 || bg == 8;
        }
        return true;
    }

    private static string SizeText(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    private static string DirSummary(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "(empty)";
            var files = new DirectoryInfo(dir).EnumerateFiles().ToList();
            return files.Count == 0 ? "(empty)" : $"({files.Count} files, {SizeText(files.Sum(f => f.Length))})";
        }
        catch { return ""; }
    }

    /// <summary>Runs <c>tool &lt;arg&gt;</c> briefly and extracts the first x.y[.z] version it prints.</summary>
    private static string? ToolVersion(string tool, string arg)
    {
        try
        {
            var psi = ExecutableResolver.Resolve(tool, [arg]);
            if (psi is null) return null;
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return null; }
            var text = outTask.Result + "\n" + errTask.Result;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?");
            return m.Success ? m.Value : null;
        }
        catch { return null; }
    }
}
