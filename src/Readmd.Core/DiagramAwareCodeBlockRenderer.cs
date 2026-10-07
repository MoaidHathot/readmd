using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Readmd.Core;

/// <summary>
/// Replaces the default fenced-code rendering for <c>mermaid</c>/<c>d2</c> blocks with a
/// container the browser front-end can fill (mermaid client-side; D2 server-rendered SVG).
/// All other code blocks fall through to the standard renderer.
/// </summary>
public sealed class DiagramAwareCodeBlockRenderer(CodeBlockRenderer? fallback) : HtmlObjectRenderer<CodeBlock>
{
    private readonly CodeBlockRenderer _fallback = fallback ?? new CodeBlockRenderer();

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        if (obj is FencedCodeBlock fenced)
        {
            var kind = DiagramExtractor.MapKind(fenced.Info);
            if (kind is not null)
            {
                var source = fenced.Lines.ToString();
                var request = DiagramRequest.Create(kind.Value, source);
                WriteDiagram(renderer, kind.Value, request, source);
                return;
            }
        }

        _fallback.Write(renderer, obj);
    }

    /// <summary>Emits the diagram figure for a diagram found outside a fence (an Azure DevOps <c>::: mermaid</c> container).</summary>
    public static void WriteDiagramFigure(HtmlRenderer renderer, DiagramKind kind, string source) =>
        WriteDiagram(renderer, kind, DiagramRequest.Create(kind, source), source);

    private static void WriteDiagram(HtmlRenderer renderer, DiagramKind kind, DiagramRequest request, string source)
    {
        var kindClass = kind.ToString().ToLowerInvariant();
        renderer.Write("<figure class=\"readmd-diagram readmd-diagram-")
            .Write(kindClass)
            .Write("\" data-readmd-diagram=\"")
            .Write(kindClass)
            .Write("\" data-readmd-key=\"")
            .Write(request.Key)
            .Write("\">");

        if (kind == DiagramKind.Mermaid)
        {
            // Client-side mermaid renders the text content of .mermaid.
            renderer.Write("<pre class=\"mermaid\">");
            renderer.WriteEscape(source);
            renderer.Write("</pre>");
        }
        else
        {
            // D2 / Graphviz / PlantUML are rendered server-side; the placeholder is swapped for
            // inline SVG (the browser fetches /_readmd/diagram/{key}; export inlines it directly).
            var label = kind switch
            {
                DiagramKind.D2 => "D2",
                DiagramKind.Graphviz => "Graphviz",
                DiagramKind.PlantUml => "PlantUML",
                _ => "diagram",
            };
            renderer.Write("<div class=\"readmd-d2-slot\" data-readmd-key=\"")
                .Write(request.Key)
                .Write("\"><div class=\"readmd-diagram-placeholder\">Rendering ")
                .Write(label)
                .Write(" diagram…</div></div>");
        }

        renderer.Write("</figure>");
        renderer.EnsureLine();
    }
}

/// <summary>
/// Renders Azure DevOps <c>::: mermaid</c> (or any <c>::: &lt;diagram kind&gt;</c>) custom containers
/// as diagram figures, with the body taken verbatim from the document source — Markdig would
/// otherwise interpret the diagram text as Markdown (<c>[*]</c> → emphasis, <c>--></c> → a dash).
/// Other containers fall through to the standard <c>&lt;div class="…"&gt;</c> rendering.
/// </summary>
public sealed class DiagramContainerRenderer(string source, Markdig.Renderers.Html.HtmlObjectRenderer<Markdig.Extensions.CustomContainers.CustomContainer>? fallback)
    : HtmlObjectRenderer<Markdig.Extensions.CustomContainers.CustomContainer>
{
    protected override void Write(HtmlRenderer renderer, Markdig.Extensions.CustomContainers.CustomContainer obj)
    {
        var kind = DiagramExtractor.MapKind(obj.Info);
        if (kind is not null)
        {
            var body = DiagramExtractor.ContainerBody(source, obj);
            if (!string.IsNullOrWhiteSpace(body))
            {
                DiagramAwareCodeBlockRenderer.WriteDiagramFigure(renderer, kind.Value, body);
                return;
            }
        }
        if (fallback is not null) { fallback.Write(renderer, obj); return; }
        // No default renderer available: emit the children inside a plain div.
        renderer.Write("<div>");
        renderer.WriteChildren(obj);
        renderer.Write("</div>");
        renderer.EnsureLine();
    }
}