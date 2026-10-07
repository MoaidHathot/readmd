using Markdig.Syntax;

namespace Readmd.Core;

/// <summary>
/// Walks the parsed AST and collects fenced code blocks whose info string names a supported
/// diagram language, turning them into cacheable <see cref="DiagramRequest"/>s. Also owns the
/// fence-language → kind mapping (with the aliases people actually type).
/// </summary>
public static class DiagramExtractor
{
    /// <summary>
    /// Finds every diagram in the document: fenced code blocks whose info string names a diagram
    /// kind, and — when the original <paramref name="source"/> text is supplied — Azure DevOps style
    /// <c>::: mermaid</c> custom containers, whose body is sliced verbatim from the source (Markdig
    /// parses the container's content as Markdown, which would mangle the diagram text).
    /// </summary>
    public static IReadOnlyList<DiagramRequest> Extract(MarkdownObject document, string? source = null)
    {
        var result = new List<DiagramRequest>();
        foreach (var node in Descendants(document))
        {
            if (node is FencedCodeBlock fenced)
            {
                var kind = MapKind(fenced.Info);
                if (kind is null) continue;
                var text = fenced.Lines.ToString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                result.Add(DiagramRequest.Create(kind.Value, text));
            }
            else if (source is not null && node is Markdig.Extensions.CustomContainers.CustomContainer container)
            {
                var kind = MapKind(container.Info);
                if (kind is null) continue;
                var text = ContainerBody(source, container);
                if (string.IsNullOrWhiteSpace(text)) continue;
                result.Add(DiagramRequest.Create(kind.Value, text));
            }
        }
        return result;
    }

    /// <summary>
    /// The raw text between a container's <c>:::</c> fences, sliced from the original document
    /// (used for <c>::: mermaid</c> blocks, whose body must not be interpreted as Markdown).
    /// </summary>
    public static string ContainerBody(string source, ContainerBlock container)
    {
        var span = container.Span;
        if (span.Start < 0 || span.End < span.Start || span.Start >= source.Length) return "";
        int end = Math.Min(source.Length, span.End + 1);
        var text = source[span.Start..end];
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith(":::")) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith(":::")) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static readonly Dictionary<string, DiagramKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mermaid"] = DiagramKind.Mermaid, ["mmd"] = DiagramKind.Mermaid,
        ["d2"] = DiagramKind.D2, ["d2lang"] = DiagramKind.D2,
        ["graphviz"] = DiagramKind.Graphviz, ["dot"] = DiagramKind.Graphviz, ["gv"] = DiagramKind.Graphviz, ["dot-graph"] = DiagramKind.Graphviz,
        ["plantuml"] = DiagramKind.PlantUml, ["puml"] = DiagramKind.PlantUml, ["uml"] = DiagramKind.PlantUml, ["plant-uml"] = DiagramKind.PlantUml, ["pu"] = DiagramKind.PlantUml,
        ["ditaa"] = DiagramKind.Ditaa,
        ["svgbob"] = DiagramKind.Svgbob, ["bob"] = DiagramKind.Svgbob,
        ["nomnoml"] = DiagramKind.Nomnoml,
        ["pikchr"] = DiagramKind.Pikchr,
        ["wavedrom"] = DiagramKind.WaveDrom,
        ["vega"] = DiagramKind.Vega,
        ["vega-lite"] = DiagramKind.VegaLite, ["vegalite"] = DiagramKind.VegaLite,
        ["bpmn"] = DiagramKind.Bpmn,
        ["excalidraw"] = DiagramKind.Excalidraw,
        ["tikz"] = DiagramKind.TikZ,
        ["erd"] = DiagramKind.Erd,
        ["dbml"] = DiagramKind.Dbml,
        ["structurizr"] = DiagramKind.Structurizr,
        ["c4plantuml"] = DiagramKind.C4PlantUml, ["c4"] = DiagramKind.C4PlantUml, ["c4-plantuml"] = DiagramKind.C4PlantUml,
        ["blockdiag"] = DiagramKind.BlockDiag,
        ["seqdiag"] = DiagramKind.SeqDiag,
        ["actdiag"] = DiagramKind.ActDiag,
        ["nwdiag"] = DiagramKind.NwDiag,
        ["packetdiag"] = DiagramKind.PacketDiag,
        ["rackdiag"] = DiagramKind.RackDiag,
        ["bytefield"] = DiagramKind.ByteField,
        ["umlet"] = DiagramKind.Umlet,
        ["wireviz"] = DiagramKind.WireViz,
        ["symbolator"] = DiagramKind.Symbolator,
        ["diagramsnet"] = DiagramKind.DiagramsNet, ["drawio"] = DiagramKind.DiagramsNet,
    };

    /// <summary>Maps a fence info string (first word, case-insensitive) to a diagram kind.</summary>
    public static DiagramKind? MapKind(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return null;
        var word = info.Trim();
        int sp = word.IndexOfAny([' ', '\t', '{']);
        if (sp > 0) word = word[..sp];
        return Kinds.TryGetValue(word, out var kind) ? kind : null;
    }

    /// <summary>Kinds that have a local renderer (mmdc/Chromium, d2, dot, plantuml).</summary>
    public static bool HasLocalRenderer(DiagramKind kind) =>
        kind is DiagramKind.Mermaid or DiagramKind.D2 or DiagramKind.Graphviz or DiagramKind.PlantUml;

    /// <summary>Kroki's diagram-type path segment for a kind.</summary>
    public static string KrokiName(DiagramKind kind) => kind switch
    {
        DiagramKind.Mermaid => "mermaid",
        DiagramKind.D2 => "d2",
        DiagramKind.Graphviz => "graphviz",
        DiagramKind.PlantUml => "plantuml",
        DiagramKind.Ditaa => "ditaa",
        DiagramKind.Svgbob => "svgbob",
        DiagramKind.Nomnoml => "nomnoml",
        DiagramKind.Pikchr => "pikchr",
        DiagramKind.WaveDrom => "wavedrom",
        DiagramKind.Vega => "vega",
        DiagramKind.VegaLite => "vegalite",
        DiagramKind.Bpmn => "bpmn",
        DiagramKind.Excalidraw => "excalidraw",
        DiagramKind.TikZ => "tikz",
        DiagramKind.Erd => "erd",
        DiagramKind.Dbml => "dbml",
        DiagramKind.Structurizr => "structurizr",
        DiagramKind.C4PlantUml => "c4plantuml",
        DiagramKind.BlockDiag => "blockdiag",
        DiagramKind.SeqDiag => "seqdiag",
        DiagramKind.ActDiag => "actdiag",
        DiagramKind.NwDiag => "nwdiag",
        DiagramKind.PacketDiag => "packetdiag",
        DiagramKind.RackDiag => "rackdiag",
        DiagramKind.ByteField => "bytefield",
        DiagramKind.Umlet => "umlet",
        DiagramKind.WireViz => "wireviz",
        DiagramKind.Symbolator => "symbolator",
        DiagramKind.DiagramsNet => "diagramsnet",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Human-readable label for captions.</summary>
    public static string Label(DiagramKind kind) => kind switch
    {
        DiagramKind.Mermaid => "Mermaid",
        DiagramKind.D2 => "D2",
        DiagramKind.Graphviz => "Graphviz",
        DiagramKind.PlantUml => "PlantUML",
        DiagramKind.Ditaa => "ditaa",
        DiagramKind.Svgbob => "svgbob",
        DiagramKind.Nomnoml => "nomnoml",
        DiagramKind.Pikchr => "Pikchr",
        DiagramKind.WaveDrom => "WaveDrom",
        DiagramKind.Vega => "Vega",
        DiagramKind.VegaLite => "Vega-Lite",
        DiagramKind.Bpmn => "BPMN",
        DiagramKind.Excalidraw => "Excalidraw",
        DiagramKind.TikZ => "TikZ",
        DiagramKind.Erd => "ERD",
        DiagramKind.Dbml => "DBML",
        DiagramKind.Structurizr => "Structurizr",
        DiagramKind.C4PlantUml => "C4-PlantUML",
        DiagramKind.BlockDiag => "blockdiag",
        DiagramKind.SeqDiag => "seqdiag",
        DiagramKind.ActDiag => "actdiag",
        DiagramKind.NwDiag => "nwdiag",
        DiagramKind.PacketDiag => "packetdiag",
        DiagramKind.RackDiag => "rackdiag",
        DiagramKind.ByteField => "bytefield",
        DiagramKind.Umlet => "UMLet",
        DiagramKind.WireViz => "WireViz",
        DiagramKind.Symbolator => "Symbolator",
        DiagramKind.DiagramsNet => "diagrams.net",
        _ => kind.ToString(),
    };

    private static IEnumerable<MarkdownObject> Descendants(MarkdownObject root)
    {
        if (root is ContainerBlock container)
        {
            foreach (var child in container)
            {
                yield return child;
                foreach (var sub in Descendants(child)) yield return sub;
            }
        }
    }
}
