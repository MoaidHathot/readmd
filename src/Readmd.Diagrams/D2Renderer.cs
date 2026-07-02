using System.Diagnostics;
using Readmd.Core;
using SkiaSharp;
using Svg.Skia;

namespace Readmd.Diagrams;

/// <summary>
/// Renders D2 diagrams by invoking the <c>d2</c> binary to produce SVG, then rasterizing the
/// SVG to PNG with SkiaSharp/Svg.Skia entirely in-process (no headless browser required).
/// </summary>
internal sealed class D2Renderer
{
    private readonly string _d2Path;

    public D2Renderer(string? d2Path = null)
    {
        _d2Path = d2Path ?? "d2";
    }

    public bool IsAvailable()
    {
        try
        {
            var psi = ExecutableResolver.Resolve(_d2Path, ["--version"]);
            if (psi is null) return false;
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double renderScale = 1.0)
    {
        try
        {
            var svg = await RunD2Async(request.Source, theme, ct);
            if (string.IsNullOrWhiteSpace(svg))
                return DiagramResult.Fail(request.Key, "d2 produced no output");

            var png = Rasterizer.SvgToPng(svg, out var width, out var height, 1.5f * (float)renderScale);
            return new DiagramResult(request.Key, DiagramStatus.Ready, png, svg, width, height, null);
        }
        catch (D2NotFoundException)
        {
            return DiagramResult.Fail(request.Key,
                $"d2 executable not found ('{_d2Path}'). Install D2 (https://d2lang.com) and ensure " +
                "it is on your PATH, or pass --d2-path <path-to-d2>.");
        }
        catch (OperationCanceledException)
        {
            return DiagramResult.Fail(request.Key, "D2 render timed out.");
        }
        catch (Exception ex)
        {
            return DiagramResult.Fail(request.Key, "D2 render failed: " + ex.Message);
        }
    }

    private async Task<string> RunD2Async(string source, DiagramTheme theme, CancellationToken ct)
    {
        // d2 theme ids: 0 = neutral default (light), 200 = dark mauve.
        var themeId = theme == DiagramTheme.Dark ? "200" : "0";
        var psi = ExecutableResolver.Resolve(_d2Path, [$"--theme={themeId}", "-", "-"])
            ?? throw new D2NotFoundException();

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // d2 binary not found / not executable.
            throw new D2NotFoundException();
        }

        // Bound the render so a wedged d2 process can't hang the viewer indefinitely.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        var linked = timeoutCts.Token;

        try
        {
            await proc.StandardInput.WriteAsync(source.AsMemory(), linked);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked);
            var stderrTask = proc.StandardError.ReadToEndAsync(linked);
            await proc.WaitForExitAsync(linked);

            var stdout = await stdoutTask;
            if (proc.ExitCode != 0)
            {
                var err = await stderrTask;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "d2 exited with an error." : err.Trim());
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

/// <summary>Signals that the configured d2 executable could not be started (not found on PATH).</summary>
internal sealed class D2NotFoundException : Exception;

internal static class Rasterizer
{
    public static byte[] SvgToPng(string svg, out int width, out int height, float scale = 1.5f)
    {
        using var skSvg = new SKSvg();
        skSvg.FromSvg(svg);
        var picture = skSvg.Picture
            ?? throw new InvalidOperationException("SVG could not be parsed for rasterization");

        var rect = picture.CullRect;
        width = (int)Math.Ceiling(rect.Width * scale);
        height = (int)Math.Ceiling(rect.Height * scale);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("SVG had zero size");

        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
