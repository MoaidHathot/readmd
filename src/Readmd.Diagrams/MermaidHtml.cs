using System.Text.Json;

namespace Readmd.Diagrams;

/// <summary>
/// Builds a self-contained HTML page that renders a mermaid diagram client-side (as vector SVG via
/// the bundled mermaid.js), so it can be opened in a browser and zoomed to any size without the blur
/// of a fixed-resolution raster. Uses the shared theme config so it matches readmd's other output.
/// </summary>
public static class MermaidHtml
{
    /// <summary>Returns a complete HTML document that renders <paramref name="source"/> with mermaid.</summary>
    public static string BuildStandalonePage(string source, bool dark)
    {
        var config = MermaidTheme.ConfigJson(dark);           // already has startOnLoad:false
        var srcLiteral = JsonSerializer.Serialize(source);    // safe JS string literal
        var bg = dark ? "#0d1117" : "#ffffff";
        var fg = dark ? "#e6edf3" : "#1f2328";
        return
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>Mermaid diagram</title>" +
            "<style>html,body{margin:0;height:100%}" +
            $"body{{display:flex;align-items:center;justify-content:center;background:{bg};color:{fg};" +
            "font-family:Segoe UI,system-ui,sans-serif}#d{max-width:100vw;padding:12px}" +
            "#d svg{max-width:100vw;height:auto}</style></head>" +
            "<body><div id=\"d\">Rendering…</div>" +
            "<script>" + BundledAssets.MermaidJs + "</script>" +
            "<script>(function(){try{mermaid.initialize(" + config + ");" +
            "mermaid.render('readmd-graph'," + srcLiteral + ").then(function(r){" +
            "document.getElementById('d').innerHTML=r.svg;}).catch(function(e){" +
            "document.getElementById('d').textContent='Mermaid error: '+e;});}" +
            "catch(e){document.getElementById('d').textContent='Mermaid error: '+e;}})();</script>" +
            "</body></html>";
    }
}
