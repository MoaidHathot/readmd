using Microsoft.Playwright;
using Readmd.Core;

namespace Readmd.Diagrams;

/// <summary>
/// Renders mermaid diagrams to PNG using a headless Chromium driven by Playwright, with the
/// mermaid library bundled in-assembly (no network access required at render time). The browser
/// is launched lazily on first use and reused for the lifetime of the renderer.
/// </summary>
internal sealed class MermaidRenderer : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _provisioned;
    private string? _provisionError;

    public string? ProvisionError => _provisionError;

    public async Task<DiagramResult> RenderAsync(DiagramRequest request, DiagramTheme theme, CancellationToken ct, double renderScale = 1.0)
    {
        try
        {
            var browser = await EnsureBrowserAsync(ct);
            // The base render uses a 2× device scale; a higher renderScale produces a crisper PNG for
            // zoomed viewing (clamped so an extreme zoom can't ask for an enormous screenshot).
            double deviceScale = Math.Clamp(2.0 * renderScale, 2.0, 8.0);
            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1200, Height = 900 },
                DeviceScaleFactor = (float)deviceScale,
            });
            try
            {
                var png = await RenderOnPageAsync(page, request.Source, theme, ct);
                var (w, h) = ImageInfo.GetPngSize(png);
                return new DiagramResult(request.Key, DiagramStatus.Ready, png, null, w, h, null);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            // Provisioning failures (Chromium download) get an actionable hint; everything else
            // surfaces the underlying message.
            if (_provisionError is not null)
                return DiagramResult.Fail(request.Key,
                    _provisionError + " Install mermaid-cli (npm i -g @mermaid-js/mermaid-cli) to render " +
                    "mermaid without a headless browser, run readmd with --best-effort to open mermaid " +
                    "diagrams in your browser instead, or use --browser.");
            return DiagramResult.Fail(request.Key, "Mermaid render failed: " + ex.Message);
        }
    }

    private static async Task<byte[]> RenderOnPageAsync(IPage page, string source, DiagramTheme theme, CancellationToken ct)
    {
        // Set `color` on the body so any SVG element that uses `stroke="currentColor"` (notably gantt
        // grid tick lines, which ignore the gridColor theme variable) resolves to a VISIBLE color
        // instead of the default black. The browser front-end sets the page text color (var(--fg)) for
        // the same reason; we mirror those exact values so terminal and browser gantt grids match.
        // Without this, Playwright screenshots the SVG in isolation where `currentColor` defaults to
        // black and the grid lines vanish on a dark background.
        bool dark = theme == DiagramTheme.Dark;
        string currentColor = dark ? "#e6edf3" : "#1f2328";   // matches the web front-end's --fg
        string html = "<!doctype html><html><head><meta charset=\"utf-8\"></head>" +
                      $"<body style=\"margin:0;padding:8px;background:transparent;color:{currentColor}\">" +
                      "<div id=\"target\"></div></body></html>";
        await page.SetContentAsync(html);
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = BundledAssets.MermaidJs });

        var configJson = MermaidTheme.ConfigJson(theme == DiagramTheme.Dark);
        var renderScript = """
            async (def) => {
                mermaid.initialize(__CONFIG__);
                const { svg } = await mermaid.render('readmd-graph', def);
                const target = document.getElementById('target');
                target.innerHTML = svg;
                const el = target.querySelector('svg');
                const box = el.getBoundingClientRect();
                return { width: Math.ceil(box.width), height: Math.ceil(box.height) };
            }
            """.Replace("__CONFIG__", configJson);

        await page.EvaluateAsync(renderScript, source);
        await page.WaitForSelectorAsync("#target svg", new PageWaitForSelectorOptions { Timeout = 15000 });
        var element = await page.QuerySelectorAsync("#target svg")
            ?? throw new InvalidOperationException("mermaid did not produce an SVG element");

        return await element.ScreenshotAsync(new ElementHandleScreenshotOptions
        {
            OmitBackground = true,
            Type = ScreenshotType.Png,
        });
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is not null) return _browser;
        await _gate.WaitAsync(ct);
        try
        {
            if (_browser is not null) return _browser;

            EnsureChromiumInstalled();

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-gpu"],
            });
            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ensures Playwright can run (a Node.js runtime is available) and the Chromium build is
    /// installed, downloading it to the user cache on first run. Throws with an actionable message
    /// if provisioning fails.
    /// </summary>
    private void EnsureChromiumInstalled()
    {
        if (_provisioned) return;
        var result = PdfProvisioning.EnsureInstalled();
        if (!result.IsReady)
        {
            _provisionError = result.Message ?? "Failed to provision the headless browser used to render mermaid diagrams.";
            throw new InvalidOperationException(_provisionError);
        }
        _provisioned = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        _gate.Dispose();
    }
}
