namespace Readmd.Core;

/// <summary>
/// The kind of diagram embedded in a fenced code block that readmd renders to an image. The first
/// four have local renderers; the rest are rendered through a Kroki server (self-hostable) unless
/// the network is disabled.
/// </summary>
public enum DiagramKind
{
    Mermaid,
    D2,
    Graphviz,
    PlantUml,
    // Kroki-only kinds (https://kroki.io):
    Ditaa,
    Svgbob,
    Nomnoml,
    Pikchr,
    WaveDrom,
    Vega,
    VegaLite,
    Bpmn,
    Excalidraw,
    TikZ,
    Erd,
    Dbml,
    Structurizr,
    C4PlantUml,
    BlockDiag,
    SeqDiag,
    ActDiag,
    NwDiag,
    PacketDiag,
    RackDiag,
    ByteField,
    Umlet,
    WireViz,
    Symbolator,
    DiagramsNet,
}

/// <summary>
/// A diagram extracted from the document. <see cref="Key"/> is a stable content hash used
/// for caching rendered output so unchanged diagrams are never re-rendered.
/// </summary>
public sealed record DiagramRequest(DiagramKind Kind, string Source, string Key)
{
    public static DiagramRequest Create(DiagramKind kind, string source)
    {
        var normalized = source.Replace("\r\n", "\n").Trim();
        var key = kind.ToString().ToLowerInvariant() + "-" + Hashing.Sha256Hex(normalized)[..16];
        return new DiagramRequest(kind, normalized, key);
    }
}

/// <summary>One entry in the document's table of contents.</summary>
public sealed record TocEntry(int Level, string Title, string Id, int Line);

/// <summary>
/// Document metadata extracted from a parsed AST without rendering HTML: title, table of contents,
/// referenced diagrams, and front matter. Used by the terminal/print path, which renders directly
/// from the AST and never needs the HTML body.
/// </summary>
public sealed record DocumentMetadata(
    string Title,
    IReadOnlyList<TocEntry> Toc,
    IReadOnlyList<DiagramRequest> Diagrams,
    FrontMatter FrontMatter);

/// <summary>
/// The result of parsing a markdown file: rendered HTML body (for browser mode),
/// the table of contents, the set of diagrams referenced, and the source line count.
/// </summary>
public sealed record MarkdownDocument(
    string SourcePath,
    string Title,
    string Html,
    IReadOnlyList<TocEntry> Toc,
    IReadOnlyList<DiagramRequest> Diagrams,
    string RawMarkdown)
{
    /// <summary>Parsed YAML front matter (empty when the document has none).</summary>
    public FrontMatter FrontMatter { get; init; } = FrontMatter.Empty;
}
