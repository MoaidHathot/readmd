using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Reflection;
using Readmd.Core;
using Readmd.Diagrams;
using Readmd.Terminal;
using Readmd.Web;

var fileArgument = new Argument<string>("file")
{
    Description = "Path to the Markdown file to view.",
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

root.SetAction(async (parse, ct) =>
{
    var file = parse.GetValue(fileArgument)!;
    var browser = parse.GetValue(browserOption);
    var port = parse.GetValue(portOption);
    var noOpen = parse.GetValue(noOpenOption);
    var bestEffort = parse.GetValue(bestEffortOption);
    var themeArg = parse.GetValue(themeOption);
    var backgroundArg = parse.GetValue(backgroundOption);
    var d2PathArg = parse.GetValue(d2PathOption);
    var exportPath = parse.GetValue(exportOption);
    var print = parse.GetValue(printOption);

    var fromStdin = file == "-";
    string full;
    if (fromStdin)
    {
        full = "-";
    }
    else
    {
        full = Path.GetFullPath(file);
        if (!File.Exists(full))
        {
            Console.Error.WriteLine($"readmd: file not found: {file}");
            return 2;
        }
    }

    // Config: user-level + project-level (.readmd.json) merged; CLI flags take precedence over it.
    var config = ConfigLoader.Load(fromStdin ? null : full);
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

    // Resolve the theme: a custom config theme name, or built-in dark/light/auto.
    Readmd.Terminal.TerminalTheme? customTheme = null;
    bool dark;
    if (config.Themes is not null && config.Themes.TryGetValue(themeStr, out var colorTheme))
    {
        customTheme = Readmd.Terminal.TerminalTheme.FromColorTheme(colorTheme);
        dark = colorTheme.Dark;
    }
    else
    {
        if (themeStr is not ("dark" or "light" or "auto") && themeArg is not null)
            Console.Error.WriteLine($"readmd: note — unknown theme '{themeStr}', using auto.");
        dark = ResolveDark(themeStr);
    }
    var solidBackground = backgroundStr.Trim().ToLowerInvariant() is "solid" or "opaque" or "on";
    var diagramTheme = dark ? DiagramTheme.Dark : DiagramTheme.Light;
    var keyMap = Readmd.Terminal.KeyMap.FromConfig(config.Keys);
    var graphicsMode = Readmd.Terminal.TerminalCapabilities.Resolve(config.Graphics);

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
            return await RunPrintAsync(full, fromStdin, dark, ct);
        }
        if (browser)
        {
            return await RunBrowserAsync(full, port, noOpen, diagramTheme, GetDiagrams(), ct);
        }
        return await RunTerminalAsync(full, dark, diagramTheme, GetDiagrams(), port, solidBackground, customTheme, keyMap, graphicsMode, ct);
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
static async Task<int> RunPrintAsync(string full, bool fromStdin, bool dark, CancellationToken ct)
{
    var markdown = await ReadInputAsync(full, fromStdin, ct);
    var color = !Console.IsOutputRedirected;
    var width = color && Console.WindowWidth > 0 ? Console.WindowWidth : 100;
    var text = DocumentTextRenderer.Render(markdown, dark, width, color);
    // Write UTF-8 bytes straight to the raw stdout stream so box-drawing/Unicode survives a
    // redirected pipe or file regardless of the console's default code page.
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    await using var stdout = Console.OpenStandardOutput();
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

static async Task<int> RunTerminalAsync(string file, bool dark, DiagramTheme theme, IDiagramRenderer diagrams, int port, bool solidBackground, Readmd.Terminal.TerminalTheme? customTheme, Readmd.Terminal.KeyMap keyMap, Readmd.Terminal.GraphicsMode graphicsMode, CancellationToken ct)
{
    // One-off browser server spun up by the viewer's 'o' action; tracked so we can dispose it.
    WebViewerServer? sideServer = null;
    await using var viewer = new TerminalViewer(new TerminalViewerOptions
    {
        FilePath = file,
        DarkTerminal = dark,
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
