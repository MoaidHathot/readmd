using System.Diagnostics;
using Readmd.Core;

namespace Readmd.Diagrams;

/// <summary>
/// Renders Graphviz (<c>dot</c>) diagrams by invoking the <c>dot</c> binary to produce SVG, then
/// rasterizing the SVG to PNG in-process (no headless browser). Requires Graphviz on PATH.
/// </summary>
internal sealed class GraphvizRenderer
{
    private readonly string _dotPath;

    public GraphvizRenderer(string? dotPath = null) => _dotPath = dotPath ?? "dot";

    public async Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double renderScale = 1.0)
    {
        try
        {
            var svg = await RunAsync(request.Source, theme, ct);
            if (string.IsNullOrWhiteSpace(svg))
                return DiagramResult.Fail(request.Key, "dot produced no output.");
            var png = Rasterizer.SvgToPng(svg, out var width, out var height, 1.5f * (float)renderScale);
            return new DiagramResult(request.Key, DiagramStatus.Ready, png, svg, width, height, null);
        }
        catch (ToolNotFoundException)
        {
            return DiagramResult.Fail(request.Key,
                $"Graphviz 'dot' not found ('{_dotPath}'). Install Graphviz (https://graphviz.org) and ensure it is on your PATH.");
        }
        catch (OperationCanceledException)
        {
            return DiagramResult.Fail(request.Key, "Graphviz render timed out.");
        }
        catch (Exception ex)
        {
            return DiagramResult.Fail(request.Key, "Graphviz render failed: " + ex.Message);
        }
    }

    private async Task<string> RunAsync(string source, DiagramTheme theme, CancellationToken ct)
    {
        // On dark themes, give nodes/edges/text light defaults (only applied when the graph doesn't
        // set its own colors) and keep the page background transparent.
        var args = new List<string> { "-Tsvg", "-Gbgcolor=transparent" };
        if (theme == DiagramTheme.Dark)
        {
            args.Add("-Ncolor=#7c8cf8");
            args.Add("-Nfontcolor=#e6edf3");
            args.Add("-Ecolor=#8b9bf4");
            args.Add("-Efontcolor=#e6edf3");
        }

        var psi = ExecutableResolver.Resolve(_dotPath, args) ?? throw new ToolNotFoundException();
        return await ProcessPipe.RunAsync(psi, source, TimeSpan.FromSeconds(20), ct);
    }
}

/// <summary>
/// Renders PlantUML diagrams by invoking <c>plantuml</c> in pipe mode to produce SVG, then
/// rasterizing the SVG to PNG in-process. Requires PlantUML on PATH (and Java).
/// </summary>
internal sealed class PlantUmlRenderer
{
    private readonly string _plantUmlPath;

    public PlantUmlRenderer(string? plantUmlPath = null) => _plantUmlPath = plantUmlPath ?? "plantuml";

    public async Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double renderScale = 1.0)
    {
        try
        {
            var source = WithTheme(request.Source, theme);
            var svg = await RunAsync(source, ct);
            if (string.IsNullOrWhiteSpace(svg))
                return DiagramResult.Fail(request.Key, "plantuml produced no output.");
            var png = Rasterizer.SvgToPng(svg, out var width, out var height, 1.5f * (float)renderScale);
            return new DiagramResult(request.Key, DiagramStatus.Ready, png, svg, width, height, null);
        }
        catch (ToolNotFoundException)
        {
            return DiagramResult.Fail(request.Key,
                $"PlantUML not found ('{_plantUmlPath}'). Install PlantUML (https://plantuml.com) and ensure 'plantuml' (and Java) are on your PATH.");
        }
        catch (OperationCanceledException)
        {
            return DiagramResult.Fail(request.Key, "PlantUML render timed out.");
        }
        catch (Exception ex)
        {
            return DiagramResult.Fail(request.Key, "PlantUML render failed: " + ex.Message);
        }
    }

    // Inject a dark theme directive when the diagram doesn't already declare one.
    private static string WithTheme(string source, DiagramTheme theme)
    {
        if (theme != DiagramTheme.Dark) return source;
        if (source.Contains("!theme", StringComparison.OrdinalIgnoreCase)) return source;
        // Insert "!theme" after the leading @startXXX line if present, else prepend.
        var nl = source.IndexOf('\n');
        if (nl >= 0 && source.TrimStart().StartsWith("@start", StringComparison.OrdinalIgnoreCase))
            return source[..(nl + 1)] + "!theme cyborg\n" + source[(nl + 1)..];
        return "!theme cyborg\n" + source;
    }

    private async Task<string> RunAsync(string source, CancellationToken ct)
    {
        var psi = ExecutableResolver.Resolve(_plantUmlPath, ["-tsvg", "-pipe"])
            ?? throw new ToolNotFoundException();
        return await ProcessPipe.RunAsync(psi, source, TimeSpan.FromSeconds(30), ct);
    }
}

/// <summary>Signals that a configured external diagram tool could not be started (not on PATH).</summary>
internal sealed class ToolNotFoundException : Exception;

/// <summary>Helper that pipes text to a process's stdin and returns its stdout, with a timeout.</summary>
internal static class ProcessPipe
{
    public static async Task<string> RunAsync(ProcessStartInfo psi, string stdinText, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new ToolNotFoundException(); }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var linked = timeoutCts.Token;
        try
        {
            await proc.StandardInput.WriteAsync(stdinText.AsMemory(), linked);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked);
            var stderrTask = proc.StandardError.ReadToEndAsync(linked);
            await proc.WaitForExitAsync(linked);

            var stdout = await stdoutTask;
            if (proc.ExitCode != 0)
            {
                var err = await stderrTask;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "the tool exited with an error." : err.Trim());
            }
            return stdout;
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
    }
}
