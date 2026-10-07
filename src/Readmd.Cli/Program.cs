using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Reflection;
using Readmd.Core;
using Readmd.Diagrams;
using Readmd.Terminal;
using Readmd.Web;

var fileArgument = new Argument<string[]>("file")
{
    Description = "Markdown file(s) to view: paths, globs (docs/**/*.md), a directory (opens a file picker), '-' for stdin, an https:// URL, or gh:owner/repo[@ref][/path].",
    Arity = ArgumentArity.ZeroOrMore,
};

var browserOption = new Option<bool>("--browser", ["-b"])
{
    Description = "Open in the browser instead of the terminal (full-fidelity mermaid/D2/math).",
};
var portOption = new Option<int>("--port", ["-p"])
{
    Description = "Port for browser mode (0 = pick a free port).",
    DefaultValueFactory = _ => 0,
};
var noOpenOption = new Option<bool>("--no-open")
{
    Description = "In browser mode, start the server but don't launch a browser.",
};
var bestEffortOption = new Option<bool>("--best-effort")
{
    Description = "Terminal mode: skip the headless-browser download; mermaid diagrams open in the browser instead.",
};
var themeOption = new Option<string?>("--theme")
{
    Description = "Color theme: dark, light, auto, or a custom theme name from your config (default: auto).",
};
var backgroundOption = new Option<string?>("--background", ["--bg"])
{
    Description = "Background fill: 'solid' paints a solid themed background (overrides terminal transparency); 'terminal' lets the terminal background show through.",
};
backgroundOption.AcceptOnlyFromAmong("solid", "terminal", "opaque", "on", "off");
var d2PathOption = new Option<string?>("--d2-path")
{
    Description = "Explicit path to the d2 executable (defaults to 'd2' on PATH).",
};
var exportOption = new Option<string?>("--export", ["-e", "-o"])
{
    Description = "Export to a self-contained file and exit. The format is chosen from the extension: .html (default) or .pdf.",
};
var printOption = new Option<bool>("--print")
{
    Description = "Render to stdout and exit (plain text, or ANSI when stdout is a terminal). Implied when stdout is redirected to a pipe or file.",
};
var rootOption = new Option<string?>("--root")
{
    Description = "Directory that local links and images may reach (default: the document's folder). Lets ../sibling.md links resolve inside a larger wiki.",
};
var colorOption = new Option<string?>("--color")
{
    Description = "Colour depth: auto (default: detect; honours NO_COLOR/FORCE_COLOR), always/truecolor, 256, 16, or never. For --print, 'always' forces ANSI even when piped (e.g. `| less -R`).",
};
colorOption.AcceptOnlyFromAmong("auto", "always", "never", "truecolor", "256", "16");
var graphicsOption = new Option<string?>("--graphics", ["-g"])
{
    Description = "Inline image protocol: auto (default: detect via terminal queries), sixel, kitty, iterm2, half-block, or none.",
};
graphicsOption.AcceptOnlyFromAmong("auto", "sixel", "kitty", "iterm2", "half-block", "none");
var asciiOption = new Option<bool>("--ascii")
{
    Description = "Use plain ASCII glyphs instead of box drawing and symbols (for fonts/terminals without them, or clean piped text).",
};
var offlineOption = new Option<bool>("--offline")
{
    Description = "Never touch the network: remote images are not fetched and no online diagram service is used.",
};
var slidesOption = new Option<bool>("--slides")
{
    Description = "Presentation mode: one slide per '---' / <!-- slide -->, with <!-- pause --> reveals and <!-- notes --> speaker notes (Ctrl+S toggles at runtime).",
};
var noWatchOption = new Option<bool>("--no-watch")
{
    Description = "Don't live-reload when the file changes (W toggles it at runtime).",
};

// `+N` / `+/pattern` (less/vim style) are handled before System.CommandLine sees the arguments.
int? startLine = null;
string? startSearch = null;
var filteredArgs = new List<string>(args.Length);
foreach (var a in args)
{
    if (a.Length > 1 && a[0] == '+' && !a.StartsWith("++"))
    {
        if (a[1] == '/') { startSearch = a[2..]; continue; }
        if (int.TryParse(a[1..], out var n)) { startLine = n; continue; }
    }
    filteredArgs.Add(a);
}
args = filteredArgs.ToArray();
var widthOption = new Option<int?>("--width", ["-w"])
{
    Description = "Maximum reading width in columns (content is centred when the terminal is wider); also the wrap width for --print. Default: full width (config: maxWidth).",
};

var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
              ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
              ?? "0.0.0";

var root = new RootCommand("readmd — a terminal-first Markdown viewer with live reload, search, [[_TOC_]], mermaid and D2 diagrams, and an optional browser mode.")
{
    fileArgument,
    browserOption,
    portOption,
    noOpenOption,
    bestEffortOption,
    themeOption,
    backgroundOption,
    d2PathOption,
    exportOption,
    printOption,
    rootOption,
    colorOption,
    widthOption,
    graphicsOption,
    asciiOption,
    offlineOption,
    noWatchOption,
    slidesOption,
};

// Replace the built-in --version (which prints the bare version) with a branded one that
// prints "readmd <version>" consistently, in any argument position.
for (var i = root.Options.Count - 1; i >= 0; i--)
{
    if (root.Options[i] is VersionOption)
        root.Options.RemoveAt(i);
}
root.Options.Add(new VersionOption("--version", ["-v"]) { Action = new PrintVersionAction(version) });

// `readmd completions <shell>` — print a shell completion script (bash/zsh/pwsh/fish).
var shellArgument = new Argument<string>("shell") { Description = "Shell to generate completions for: bash, zsh, pwsh, or fish." };
var completionsCommand = new Command("completions", "Print a shell completion script to stdout.") { shellArgument };
completionsCommand.SetAction(parse =>
{
    var shell = parse.GetValue(shellArgument)!;
    try { Console.Out.Write(ShellIntegration.ForShell(shell)); return 0; }
    catch (ArgumentException ex) { Console.Error.WriteLine($"readmd: {ex.Message}"); return 2; }
});
root.Subcommands.Add(completionsCommand);

// `readmd man` — print a troff man page to stdout (pipe to a file under man1/).
var manCommand = new Command("man", "Print a man page (troff) to stdout.");
manCommand.SetAction(_ => { Console.Out.Write(ShellIntegration.ManPage(version)); return 0; });
root.Subcommands.Add(manCommand);

// `readmd install-pdf` — provision the headless browser used for high-quality PDF export.
var installPdfCommand = new Command("install-pdf",
    "Download the headless browser used for high-quality PDF export (one-time, ~100 MB). Requires Node.js on PATH.");
installPdfCommand.SetAction(_ =>
{
    var result = PdfProvisioning.EnsureInstalled(msg => Console.Error.WriteLine("readmd: " + msg));
    if (result.IsReady)
    {
        Console.Error.WriteLine("readmd: PDF export is ready.");
        return 0;
    }
    Console.Error.WriteLine("readmd: " + result.Message);
    if (result.Readiness == PdfReadiness.NodeMissing)
        Console.Error.WriteLine("readmd: install Node.js from https://nodejs.org and re-run `readmd install-pdf`.");
    return 1;
});
root.Subcommands.Add(installPdfCommand);

// `readmd doctor` — report terminal capabilities, config, diagram backends, PDF readiness.
var noProbeOption = new Option<bool>("--no-probe") { Description = "Skip the live terminal interrogation (DA1/kitty/OSC 11 queries)." };
var doctorCommand = new Command("doctor", "Check this machine: terminal capabilities, colour depth, config files, diagram tools, PDF export, clipboard.") { noProbeOption, offlineOption, asciiOption };
doctorCommand.SetAction(async (parse, ct) =>
{
    Readmd.Terminal.Glyphs.Ascii = parse.GetValue(asciiOption) || Environment.GetEnvironmentVariable("READMD_ASCII") is "1" or "true";
    return await Doctor.RunAsync(version, parse.GetValue(offlineOption), parse.GetValue(noProbeOption), ct);
});
root.Subcommands.Add(doctorCommand);

// `readmd themes` — list built-in and configured themes with swatches.
var plainOption = new Option<bool>("--plain") { Description = "No colour swatches; print hex values instead." };
var sampleOption = new Option<string?>("--sample") { Description = "Heading text used in the sample line." };
var themesCommand = new Command("themes", "List the available colour themes (built-in and from your config) with swatches.") { plainOption, sampleOption };
themesCommand.SetAction(parse => ConfigCommands.Themes(parse.GetValue(plainOption), parse.GetValue(sampleOption)));
root.Subcommands.Add(themesCommand);

// `readmd config path|init|validate|schema`
var configCommand = new Command("config", "Inspect, create or validate readmd's configuration files.");
var quietOption = new Option<bool>("--quiet", ["-q"]) { Description = "Print only the user config path." };
var configPath = new Command("path", "Show where readmd reads configuration from (user file, project file, state).") { quietOption };
configPath.SetAction(parse => ConfigCommands.Path_(parse.GetValue(quietOption)));
var projectOption = new Option<bool>("--project") { Description = "Create a project-level .readmd.json in the current directory instead of the user config." };
var forceOption = new Option<bool>("--force") { Description = "Overwrite an existing file." };
var configInit = new Command("init", "Write a commented starter config file.") { projectOption, forceOption };
configInit.SetAction(parse => ConfigCommands.Init(parse.GetValue(projectOption), parse.GetValue(forceOption)));
var validateFile = new Argument<string?>("file") { Description = "Config file to check (default: the user config and any project .readmd.json).", Arity = ArgumentArity.ZeroOrOne };
var configValidate = new Command("validate", "Parse a config file and report unknown keys, bad values, invalid colours and key chords.") { validateFile };
configValidate.SetAction(parse => ConfigCommands.Validate(parse.GetValue(validateFile)));
var configSchema = new Command("schema", "Print the JSON Schema for the config file (for editor validation).");
configSchema.SetAction(_ => { Console.Out.Write(ConfigCommands.Schema()); return 0; });
configCommand.Subcommands.Add(configPath);
configCommand.Subcommands.Add(configInit);
configCommand.Subcommands.Add(configValidate);
configCommand.Subcommands.Add(configSchema);
root.Subcommands.Add(configCommand);

root.SetAction(async (parse, ct) =>
{
    Timing.Mark("args parsed");
    var fileArgs = parse.GetValue(fileArgument) ?? [];
    var browser = parse.GetValue(browserOption);
    var port = parse.GetValue(portOption);
    var noOpen = parse.GetValue(noOpenOption);
    var bestEffort = parse.GetValue(bestEffortOption);
    var themeArg = parse.GetValue(themeOption);
    var backgroundArg = parse.GetValue(backgroundOption);
    var d2PathArg = parse.GetValue(d2PathOption);
    var exportPath = parse.GetValue(exportOption);
    var print = parse.GetValue(printOption);
    var rootArg = parse.GetValue(rootOption);
    var colorArg = parse.GetValue(colorOption);
    var widthArg = parse.GetValue(widthOption);
    var graphicsArg = parse.GetValue(graphicsOption);
    var asciiArg = parse.GetValue(asciiOption);
    var offlineArg = parse.GetValue(offlineOption);
    var noWatch = parse.GetValue(noWatchOption);
    var slides = parse.GetValue(slidesOption);

    // Resolve what to open: stdin, remote URL(s), directories (file picker), globs, plain paths.
    // Offline is decided later (config) but remote fetching needs the network regardless, so a
    // remote argument with --offline is an error up front.
    if (fileArgs.Length == 0)
    {
        // No argument: a README in the current directory, else directory mode on ".".
        var cwd = Directory.GetCurrentDirectory();
        var readme = Directory.EnumerateFiles(cwd).FirstOrDefault(f => Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase));
        fileArgs = readme is not null ? [readme] : ["."];
    }
    var fromStdin = fileArgs.Length == 1 && fileArgs[0] == "-";
    string full;
    string? displayName = null;
    string? remoteRoot = null;
    List<string> fileSet = [];
    bool startWithPicker = false;
    if (fromStdin)
    {
        full = "-";
    }
    else
    {
        var remote = fileArgs.Where(RemoteSource.IsRemote).ToList();
        if (remote.Count > 0)
        {
            if (offlineArg) { Console.Error.WriteLine("readmd: cannot fetch a remote document with --offline."); return 2; }
            try
            {
                foreach (var r in remote)
                {
                    Console.Error.WriteLine($"readmd: fetching {r} …");
                    var fetched = await RemoteSource.FetchAsync(r, ct);
                    fileSet.Add(fetched.LocalPath);
                    displayName ??= fetched.DisplayName;
                    remoteRoot ??= Path.GetDirectoryName(fetched.LocalPath);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or TaskCanceledException)
            {
                Console.Error.WriteLine($"readmd: {ex.Message}");
                return 1;
            }
        }
        foreach (var a in fileArgs.Where(a => !RemoteSource.IsRemote(a)))
        {
            var expanded = FileArguments.Expand(a, out var isDirectory);
            if (expanded.Count == 0)
            {
                Console.Error.WriteLine(isDirectory ? $"readmd: no Markdown files under {a}" : $"readmd: file not found: {a}");
                return 2;
            }
            if (isDirectory && fileArgs.Length == 1) startWithPicker = true;
            fileSet.AddRange(expanded);
        }
        fileSet = fileSet.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        full = fileSet[0];
        if (startWithPicker)
        {
            // Directory mode: open the most recently modified file and show the picker over the rest.
            full = fileSet.OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } }).First();
            rootArg ??= Path.GetFullPath(fileArgs[0]);
        }
        else if (fileSet.Count > 1 && rootArg is null)
        {
            // Several files: the common parent is the sandbox so links between them resolve.
            rootArg = FileArguments.CommonDirectory(fileSet);
        }
    }

    string? rootDir = null;
    if (rootArg is not null)
    {
        rootDir = Path.GetFullPath(rootArg);
        if (!Directory.Exists(rootDir))
        {
            Console.Error.WriteLine($"readmd: --root directory not found: {rootArg}");
            return 2;
        }
        if (!fromStdin && !new LinkResolver(rootDir).IsInsideRoot(full))
        {
            Console.Error.WriteLine($"readmd: the document must be inside --root ({rootDir}).");
            return 2;
        }
    }
    // A remote document lives in the temp cache; its folder is its sandbox.
    rootDir ??= remoteRoot;

    // Config: user-level + project-level (.readmd.json) merged; CLI flags take precedence over it.
    // Remote documents only get the user-level config (their cache folder has no project file).
    var config = ConfigLoader.Load(fromStdin || remoteRoot is not null ? null : full);
    if (ConfigLoader.LastError is { } configError)
        Console.Error.WriteLine($"readmd: warning — config ignored: {configError}");
    Timing.Mark("config loaded");
    // Global rendering switches (glyph set, East Asian width policy) apply to every output path.
    Readmd.Terminal.Glyphs.Ascii = asciiArg || (config.Ascii ?? false) || Environment.GetEnvironmentVariable("READMD_ASCII") is "1" or "true";
    Readmd.Terminal.TextWidthSettings.AmbiguousWide = config.AmbiguousWide ?? false;
    var offline = offlineArg || config.Network == false || Environment.GetEnvironmentVariable("READMD_OFFLINE") is "1" or "true";
    var themeStr = themeArg ?? config.Theme ?? "auto";
    var backgroundStr = backgroundArg ?? config.Background ?? "terminal";
    var d2Path = d2PathArg ?? config.D2Path;

    if (port is < 0 or > 65535)
    {
        Console.Error.WriteLine($"readmd: --port must be between 0 and 65535 (got {port}).");
        return 2;
    }
    if (backgroundStr.Trim().ToLowerInvariant() is not ("solid" or "terminal" or "opaque" or "on" or "off"))
    {
        Console.Error.WriteLine($"readmd: --background must be 'solid' or 'terminal' (got '{backgroundStr}').");
        return 2;
    }
    if (browser && (bestEffort || backgroundStr is "solid" or "opaque" or "on"))
        Console.Error.WriteLine("readmd: note — --best-effort and --background only affect terminal mode and are ignored with --browser.");

    // Every theme the viewer can switch to with `:theme <name>` / `[`: built-ins, then config.
    var namedThemes = ThemeCatalog.Build(config);
    var themeCycle = ThemeCatalog.Cycle(config, namedThemes);

    // Resolve the theme: a named theme (built-in or from config), or dark/light/auto.
    Readmd.Terminal.TerminalTheme? customTheme = null;
    bool dark;
    if (themeStr is not ("dark" or "light" or "auto") && namedThemes.TryGetValue(themeStr, out var picked))
    {
        customTheme = picked;
        dark = picked.IsDark;
    }
    else
    {
        if (themeStr is not ("dark" or "light" or "auto"))
            Console.Error.WriteLine($"readmd: note — unknown theme '{themeStr}', using auto (see `readmd themes`).");
        dark = ResolveDark(themeStr);
    }
    var solidBackground = backgroundStr.Trim().ToLowerInvariant() is "solid" or "opaque" or "on";
    var diagramTheme = dark ? DiagramTheme.Dark : DiagramTheme.Light;
    var keyMap = Readmd.Terminal.KeyMap.FromConfig(config.Keys);
    var graphicsChoice = graphicsArg is null or "auto" ? config.Graphics : graphicsArg;
    var graphicsMode = Readmd.Terminal.TerminalCapabilities.Resolve(graphicsChoice);
    var graphicsAuto = Readmd.Terminal.TerminalCapabilities.IsAuto(graphicsChoice);
    var colorMode = Readmd.Terminal.TerminalCapabilities.ResolveColors(colorArg ?? config.Color);
    var layout = new LayoutSettings(
        MaxWidth: Math.Max(0, widthArg ?? config.MaxWidth ?? 0),
        LineNumbers: config.LineNumbers ?? true,
        TabSize: Math.Clamp(config.TabSize ?? 4, 1, 16),
        SmartPunctuation: config.SmartPunctuation ?? false);
    var viewerPrefs = new ViewerPrefs(
        ThemeAuto: themeStr.Equals("auto", StringComparison.OrdinalIgnoreCase) && customTheme is null,
        GraphicsAuto: graphicsAuto,
        Colors: colorMode,
        Hyperlinks: config.Hyperlinks ?? true,
        WheelLines: Math.Clamp(config.WheelLines ?? 3, 1, 20),
        ConfirmExternalLinks: config.ConfirmExternalLinks ?? true,
        Offline: offline,
        Watch: !noWatch && (config.Watch ?? true),
        RememberPosition: config.RememberPosition ?? true,
        Scrollbar: config.Scrollbar ?? true,
        ThemeName: customTheme is not null ? themeStr : null,
        NamedThemes: namedThemes,
        ThemeCycle: themeCycle,
        StartLine: startLine,
        StartSearch: startSearch,
        FileSet: fileSet,
        StartWithPicker: startWithPicker,
        DisplayName: displayName,
        Slides: slides);
    if (widthArg is < 20)
    {
        Console.Error.WriteLine("readmd: --width must be at least 20 columns.");
        return 2;
    }

    // The diagram renderer is created lazily, only for the paths that actually render diagrams
    // (terminal / browser / export). --print and stdin text output never touch it, so they skip
    // its construction (and its on-disk cache scan) entirely.
    DiagramRenderer? diagrams = null;
    DiagramRenderer GetDiagrams() => diagrams ??= new DiagramRenderer(new DiagramRendererOptions
    {
        BestEffort = bestEffort,
        D2Path = d2Path,
        MermaidCliPath = config.MermaidCliPath,
        GraphvizPath = config.GraphvizPath,
        PlantUmlPath = config.PlantUmlPath,
        KrokiUrl = config.KrokiUrl,
        KrokiFallback = config.KrokiFallback ?? true,
        AllowNetwork = !offline,
    });

    // Non-interactive modes: stdin always prints; a redirected stdout implies --print.
    var stdoutRedirected = Console.IsOutputRedirected;
    var shouldPrint = print || (fromStdin && exportPath is null) || (stdoutRedirected && exportPath is null && !browser);

    try
    {
        if (exportPath is not null)
        {
            return await RunExportAsync(full, fromStdin, exportPath, diagramTheme, GetDiagrams(), ct);
        }
        if (shouldPrint)
        {
            var printColors = colorMode == Readmd.Terminal.ColorMode.None ? Readmd.Terminal.ColorMode.TrueColor : colorMode;
            bool useColor = ResolveColor(colorArg ?? config.Color);
            if (fromStdin || fileSet.Count <= 1)
                return await RunPrintAsync(full, fromStdin, dark, useColor, layout, printColors, viewerPrefs.Hyperlinks, GetDiagrams, ct, slides);
            // Several files: print each in order, separated by a rule naming the file.
            var stdout = Console.OpenStandardOutput();
            foreach (var f in fileSet)
            {
                var header = (Readmd.Terminal.Glyphs.Ascii ? "==== " : "━━━━ ") + Path.GetRelativePath(Directory.GetCurrentDirectory(), f).Replace('\\', '/') + "\n";
                var hb = System.Text.Encoding.UTF8.GetBytes(header);
                await stdout.WriteAsync(hb, ct);
                var code = await RunPrintAsync(f, false, dark, useColor, layout, printColors, viewerPrefs.Hyperlinks, GetDiagrams, ct, slides);
                if (code != 0) return code;
            }
            return 0;
        }
        if (browser)
        {
            return await RunBrowserAsync(full, port, noOpen, diagramTheme, GetDiagrams(), ct);
        }
        return await RunTerminalAsync(full, dark, diagramTheme, GetDiagrams(), port, solidBackground, customTheme, keyMap, graphicsMode, rootDir, layout, viewerPrefs, ct);
    }
    catch (OperationCanceledException)
    {
        return 0;   // Ctrl+C / shutdown
    }
    catch (System.Net.Sockets.SocketException ex)
    {
        Console.Error.WriteLine($"readmd: could not start the browser server (port {port}): {ex.Message}");
        return 1;
    }
    catch (IOException ex) when (browser)
    {
        Console.Error.WriteLine($"readmd: server error: {ex.Message}");
        return 1;
    }
    catch (Exception ex)
    {
        // Last-resort handler: a concise message, not a stack trace.
        Console.Error.WriteLine($"readmd: {ex.Message}");
        return 1;
    }
    finally
    {
        if (diagrams is not null) await diagrams.DisposeAsync();
    }
});

return await root.Parse(args).InvokeAsync();

static bool ResolveDark(string theme) => theme.ToLowerInvariant() switch
{
    "light" => false,
    "dark" => true,
    _ => DetectDark(),   // auto
};

// Decides whether printed output carries ANSI colour: an explicit --color wins; otherwise colour
// only when stdout is a terminal, with the de-facto NO_COLOR / FORCE_COLOR / CLICOLOR_FORCE
// conventions honoured.
static bool ResolveColor(string? colorArg)
{
    switch (colorArg?.ToLowerInvariant())
    {
        case "always" or "truecolor" or "256" or "16": return true;
        case "never": return false;
    }
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
    var force = Environment.GetEnvironmentVariable("FORCE_COLOR") ?? Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
    if (!string.IsNullOrEmpty(force) && force != "0") return true;
    return !Console.IsOutputRedirected;
}

// Best-effort dark/light detection: honor COLORFGBG and common env hints; default to dark.
static bool DetectDark()
{
    // COLORFGBG="fg;bg" — a bg index < 8 (or the literal 0) indicates a dark background.
    var cfb = Environment.GetEnvironmentVariable("COLORFGBG");
    if (!string.IsNullOrEmpty(cfb))
    {
        var parts = cfb.Split(';');
        if (parts.Length >= 2 && int.TryParse(parts[^1], out var bg))
            return bg <= 6 || bg == 0;
    }
    // Apple Terminal/light hints are unreliable; default to dark (most terminals are dark).
    return true;
}

// Reads the document text from a file path or, when path is "-", from standard input.
static async Task<string> ReadInputAsync(string fullPathOrDash, bool fromStdin, CancellationToken ct)
{
    if (fromStdin)
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin);
        return await reader.ReadToEndAsync(ct);
    }
    return await File.ReadAllTextAsync(fullPathOrDash, ct);
}

// --export: write a self-contained .html or .pdf and exit.
static async Task<int> RunExportAsync(string full, bool fromStdin, string exportPath, DiagramTheme theme, IDiagramRenderer diagrams, CancellationToken ct)
{
    var outFull = Path.GetFullPath(exportPath);
    var isPdf = Path.GetExtension(outFull).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    // HtmlExporter works from a file on disk (for image sandboxing); stage stdin to a temp file.
    string sourcePath = full;
    string? temp = null;
    if (fromStdin)
    {
        var text = await ReadInputAsync(full, fromStdin: true, ct);
        temp = Path.Combine(Path.GetTempPath(), $"readmd-stdin-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(temp, text, ct);
        sourcePath = temp;
    }

    try
    {
        var html = await HtmlExporter.ExportAsync(sourcePath, diagrams, theme, ct);
        if (isPdf)
        {
            // Ensure the headless browser is available, downloading it on first use. A missing
            // Node.js runtime can't be auto-fixed, so report it clearly and exit.
            var prov = PdfProvisioning.EnsureInstalled(msg => Console.Error.WriteLine("readmd: " + msg));
            if (!prov.IsReady)
            {
                Console.Error.WriteLine("readmd: cannot export PDF. " + prov.Message);
                if (prov.Readiness == PdfReadiness.NodeMissing)
                    Console.Error.WriteLine("readmd: install Node.js from https://nodejs.org, then retry. " +
                        "Tip: `readmd install-pdf` downloads the browser once Node.js is available.");
                return 1;
            }
            var pdf = await PdfRenderer.RenderAsync(html, ct: ct);
            await File.WriteAllBytesAsync(outFull, pdf, ct);
        }
        else
        {
            await File.WriteAllTextAsync(outFull, html, ct);
        }
        Console.Error.WriteLine($"readmd: wrote {outFull}");
        return 0;
    }
    finally
    {
        if (temp is not null) { try { File.Delete(temp); } catch { /* ignore */ } }
    }
}

// --print / non-TTY: render to stdout (ANSI when stdout is a terminal, plain text otherwise).
static async Task<int> RunPrintAsync(string full, bool fromStdin, bool dark, bool color, LayoutSettings layout, Readmd.Terminal.ColorMode colors, bool hyperlinks, Func<DiagramRenderer> getDiagrams, CancellationToken ct, bool slides = false)
{
    var markdown = await ReadInputAsync(full, fromStdin, ct);
    if (slides)
    {
        // Presentation handout: every slide fully revealed, separated by a numbered rule, notes after.
        var deck = SlideDeck.Split(markdown);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < deck.Count; i++)
        {
            var slide = deck[i];
            string rule = Readmd.Terminal.Glyphs.Ascii ? "----" : "────";
            sb.Append(rule).Append($" Slide {i + 1}/{deck.Count}: {slide.Title} ").Append(rule).Append("\n\n");
            sb.Append(slide.Markdown.TrimEnd('\n')).Append("\n\n");
            if (slide.Notes is not null) sb.Append("> **Notes:** ").Append(slide.Notes.Replace("\n", "\n> ")).Append("\n\n");
        }
        markdown = sb.ToString();
    }
    Timing.Mark("file read");
    // Overlap the TextMate registry construction (~35 ms) with the Markdown parse when the document
    // has fenced code and colour is wanted; it's wasted work otherwise.
    if (color && (markdown.Contains("```") || markdown.Contains("~~~"))) _ = CodeHighlighter.WarmUpAsync(dark);
    var width = !Console.IsOutputRedirected && Console.WindowWidth > 0 ? Console.WindowWidth : 100;
    if (layout.MaxWidth > 0) width = Math.Min(width, layout.MaxWidth + 1);

    // Diagrams print as box art when d2/plantuml can produce it (rendered in parallel ahead of the
    // layout, bounded by a short timeout so a pipe never hangs on a slow tool), else as their source.
    // A cheap substring check keeps documents without such fences from paying for a second parse.
    Func<DiagramRequest, string?>? diagramText = null;
    bool mayHaveTextDiagrams = markdown.Contains("d2", StringComparison.OrdinalIgnoreCase) || markdown.Contains("uml", StringComparison.OrdinalIgnoreCase) || markdown.Contains("c4", StringComparison.OrdinalIgnoreCase);
    var requests = mayHaveTextDiagrams
        ? DiagramExtractor.Extract(Markdig.Markdown.Parse(FrontMatter.StripToml(markdown).Markdown))
            .Where(r => r.Kind is DiagramKind.D2 or DiagramKind.PlantUml or DiagramKind.C4PlantUml)
            .DistinctBy(r => r.Key).ToList()
        : [];
    if (requests.Count > 0)
    {
        var renderer = getDiagrams();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(60, 8 + 4 * requests.Count)));
        var texts = new Dictionary<string, string?>();
        try
        {
            var results = await Task.WhenAll(requests.Select(async r => (r.Key, Text: await renderer.RenderTextAsync(r, timeout.Token))));
            foreach (var (k, t) in results) texts[k] = t;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* fall back to source */ }
        diagramText = r => texts.TryGetValue(r.Key, out var t) ? t : null;
        Timing.Mark("diagram text");
    }

    var text = DocumentTextRenderer.Render(markdown, dark, width, color,
        new DocumentTextRenderer.Options(layout.LineNumbers, layout.TabSize, layout.SmartPunctuation, colors, hyperlinks) { DiagramText = diagramText });
    Timing.Mark("rendered");
    // Write UTF-8 bytes straight to the raw stdout stream so box-drawing/Unicode survives a
    // redirected pipe or file regardless of the console's default code page. The stream is a
    // shared handle; don't dispose it (multi-file printing reuses it).
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    var stdout = Console.OpenStandardOutput();
    await stdout.WriteAsync(bytes, ct);
    await stdout.FlushAsync(ct);
    return 0;
}

static async Task<int> RunBrowserAsync(string file, int port, bool noOpen, DiagramTheme theme, IDiagramRenderer diagrams, CancellationToken ct)
{
    await using var server = new WebViewerServer(new WebViewerOptions
    {
        FilePath = file,
        Port = port,
        Theme = theme,
    }, diagrams);

    await server.StartAsync(ct);
    var url = server.Url;
    Console.WriteLine($"readmd: serving {Path.GetFileName(file)} at {url}");
    Console.WriteLine("readmd: watching for changes — edit the file to live-reload. Press Ctrl+C to stop.");

    if (!noOpen) OpenBrowser(url);

    var tcs = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
    using var reg = ct.Register(() => tcs.TrySetResult());
    await tcs.Task;
    return 0;
}

static async Task<int> RunTerminalAsync(string file, bool dark, DiagramTheme theme, IDiagramRenderer diagrams, int port, bool solidBackground, Readmd.Terminal.TerminalTheme? customTheme, Readmd.Terminal.KeyMap keyMap, Readmd.Terminal.GraphicsMode graphicsMode, string? rootDir, LayoutSettings layout, ViewerPrefs prefs, CancellationToken ct)
{
    // One-off browser server spun up by the viewer's 'o' action; tracked so we can dispose it.
    WebViewerServer? sideServer = null;
    await using var viewer = new TerminalViewer(new TerminalViewerOptions
    {
        FilePath = file,
        Root = rootDir,
        DarkTerminal = dark,
        MaxWidth = layout.MaxWidth,
        LineNumbers = layout.LineNumbers,
        TabSize = layout.TabSize,
        SmartPunctuation = layout.SmartPunctuation,
        ThemeAuto = prefs.ThemeAuto,
        GraphicsAuto = prefs.GraphicsAuto,
        Colors = prefs.Colors,
        Hyperlinks = prefs.Hyperlinks,
        WheelLines = prefs.WheelLines,
        ConfirmExternalLinks = prefs.ConfirmExternalLinks,
        AllowNetwork = !prefs.Offline,
        Watch = prefs.Watch,
        RememberPosition = prefs.RememberPosition,
        Scrollbar = prefs.Scrollbar,
        ThemeName = prefs.ThemeName,
        NamedThemes = prefs.NamedThemes,
        ThemeCycle = prefs.ThemeCycle,
        StartLine = prefs.StartLine,
        StartSearch = prefs.StartSearch,
        FileSet = prefs.FileSet,
        StartWithPicker = prefs.StartWithPicker,
        StartInSlides = prefs.Slides,
        DisplayName = prefs.DisplayName,
        DiagramTheme = theme,
        SolidBackground = solidBackground,
        Theme = customTheme,
        KeyMap = keyMap,
        GraphicsMode = graphicsMode,
        OpenInBrowser = async path =>
        {
            // Reuse a single side server across 'o' presses instead of leaking one each time.
            sideServer ??= new WebViewerServer(new WebViewerOptions { FilePath = path, Port = 0, Theme = theme }, diagrams);
            await sideServer.StartAsync();
            OpenBrowser(sideServer.Url);
        },
        Export = async (source, target) =>
        {
            // Same exporters as --export: .pdf via headless Chromium, anything else as standalone HTML.
            try
            {
                var html = await HtmlExporter.ExportAsync(source, diagrams, theme);
                if (Path.GetExtension(target).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var prov = PdfProvisioning.EnsureInstalled(_ => { });
                    if (!prov.IsReady) return prov.Message + (prov.Readiness == PdfReadiness.NodeMissing ? " Install Node.js, then run `readmd install-pdf`." : "");
                    await File.WriteAllBytesAsync(target, await PdfRenderer.RenderAsync(html));
                }
                else await File.WriteAllTextAsync(target, html);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        },
    }, diagrams);

    try
    {
        await viewer.RunAsync();
        return 0;
    }
    finally
    {
        if (sideServer is not null) await sideServer.DisposeAsync();
    }
}

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
    catch
    {
        Console.WriteLine($"readmd: open {url} in your browser.");
    }
}

/// <summary>Prints "readmd &lt;version&gt;" for --version, regardless of argument position.</summary>
internal sealed class PrintVersionAction(string version) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        parseResult.InvocationConfiguration.Output.WriteLine($"readmd {version}");
        return 0;
    }
}

/// <summary>Layout settings resolved from flags + config, shared by the terminal and print paths.</summary>
internal sealed record LayoutSettings(int MaxWidth, bool LineNumbers, int TabSize, bool SmartPunctuation);

/// <summary>Interactive-viewer preferences resolved from flags + config.</summary>
internal sealed record ViewerPrefs(
    bool ThemeAuto, bool GraphicsAuto, Readmd.Terminal.ColorMode Colors, bool Hyperlinks, int WheelLines, bool ConfirmExternalLinks,
    bool Offline, bool Watch, bool RememberPosition, bool Scrollbar, string? ThemeName,
    IReadOnlyDictionary<string, Readmd.Terminal.TerminalTheme> NamedThemes, IReadOnlyList<string> ThemeCycle, int? StartLine, string? StartSearch,
    IReadOnlyList<string> FileSet, bool StartWithPicker, string? DisplayName, bool Slides);
