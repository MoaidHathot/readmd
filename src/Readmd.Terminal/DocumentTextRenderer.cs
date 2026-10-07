using System.Text;
using Markdig;
using Readmd.Core;

namespace Readmd.Terminal;

/// <summary>
/// Renders a Markdown document to a flat string for non-interactive output (pipes, files,
/// <c>--print</c>). Produces either plain text (styles stripped) or ANSI-colored text suitable for
/// a pager such as <c>less -R</c>. Diagrams and images appear as their caption line (e.g.
/// "◆ Mermaid diagram") since there is no terminal to draw Sixel into.
/// </summary>
public static class DocumentTextRenderer
{
    /// <summary>Layout options for printed output.</summary>
    public sealed record Options(bool LineNumbers = true, int TabSize = 4, bool SmartPunctuation = false, ColorMode Colors = ColorMode.TrueColor, bool Hyperlinks = true)
    {
        public static readonly Options Default = new();

        /// <summary>
        /// Optional text renderer for diagrams (box art from d2/plantuml). When it returns null for a
        /// diagram, the diagram's source is printed as a code block instead of a bare caption.
        /// </summary>
        public Func<DiagramRequest, string?>? DiagramText { get; init; }

        /// <summary>Print diagram source as a code block when no text rendering is available (default true).</summary>
        public bool DiagramSourceFallback { get; init; } = true;
    }

    public static string Render(string markdown, bool dark, int width, bool color, Options? options = null) =>
        Compose(Layout(markdown, dark, width, color, options), options?.DiagramText);

    /// <summary>A laid-out document awaiting composition: the lines plus the diagrams found in them.</summary>
    public sealed class Laid
    {
        internal Laid(MarkdownTerminalRenderer renderer, MarkdownTerminalRenderer.RenderResult result, TerminalTheme theme, int width, bool color, Options options)
        { Renderer = renderer; Result = result; Theme = theme; Width = width; Color = color; Options = options; }
        internal MarkdownTerminalRenderer Renderer { get; }
        internal MarkdownTerminalRenderer.RenderResult Result { get; }
        internal TerminalTheme Theme { get; }
        internal int Width { get; }
        internal bool Color { get; }
        internal Options Options { get; }

        /// <summary>The diagrams the document contains, by key — so callers can pre-render text for them.</summary>
        public IReadOnlyDictionary<string, DiagramRequest> Diagrams => Renderer.PendingDiagrams;
    }

    /// <summary>
    /// Parses and lays out the document (one parse; TOC/front matter come from the same AST). Call
    /// <see cref="Compose"/> to produce the text, optionally after rendering diagram box art for
    /// <see cref="Laid.Diagrams"/>.
    /// </summary>
    public static Laid Layout(string markdown, bool dark, int width, bool color, Options? options = null)
    {
        options ??= Options.Default;
        var theme = TerminalTheme.For(dark);
        var (body, toml) = FrontMatter.StripToml(markdown);
        var ast = Timing.Measure("parse", () => Markdown.Parse(body, TerminalPipeline.For(options.SmartPunctuation)));
        var meta = Timing.Measure("metadata", () => MarkdownRenderer.ExtractMetadata(ast, "stdin.md", toml));
        // Plain output carries no colour, so syntax highlighting (the slowest part of layout) would be
        // pure waste: defer it and simply never run the deferred work.
        var renderer = new MarkdownTerminalRenderer(theme, Math.Max(20, width) - 1)
        {
            DeferHighlighting = !color,
            LineNumbers = options.LineNumbers,
            TabSize = options.TabSize,
        };
        var result = Timing.Measure("layout", () => renderer.Render(ast, meta.Toc, meta.FrontMatter, body));
        return new Laid(renderer, result, theme, width, color, options);
    }

    /// <summary>Turns a laid-out document into plain or ANSI text; diagrams become box art (when <paramref name="diagramText"/> supplies it) or their source.</summary>
    public static string Compose(Laid laid, Func<DiagramRequest, string?>? diagramText)
    {
        var options = laid.Options; var result = laid.Result; var renderer = laid.Renderer; bool color = laid.Color;
        diagramText ??= options.DiagramText;
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            AppendLine(sb, line, color, options, result.Links);
            // Diagrams: box art when a backend can produce it, else the source — never a bare caption.
            if (line.DiagramKey is { } key && !key.StartsWith("reserve:") && renderer.PendingDiagrams.TryGetValue(key, out var req))
            {
                var art = diagramText?.Invoke(req);
                if (art is not null)
                {
                    foreach (var row in art.Replace("\r\n", "\n").Split('\n'))
                        sb.Append(new string(' ', line.DiagramIndent + 2)).Append(row.TrimEnd()).Append('\n');
                }
                else if (options.DiagramSourceFallback)
                {
                    var scratch = new MarkdownTerminalRenderer(laid.Theme, Math.Max(20, laid.Width) - 1) { LineNumbers = options.LineNumbers, TabSize = options.TabSize, DeferHighlighting = !color };
                    foreach (var row in scratch.RenderCodeLines(req.Source, req.Kind.ToString().ToLowerInvariant(), line.DiagramIndent))
                        AppendLine(sb, row, color, options, result.Links);
                }
            }
        }
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, DisplayLine line, bool color, Options options, IReadOnlyList<TerminalLink> links)
    {
        if (!color)
        {
            sb.Append(line.PlainText.TrimEnd()).Append('\n');
            return;
        }

        foreach (var span in line.Spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;
            var open = new StringBuilder();
            if (span.Color is { } c) AppendColor(open, c, foreground: true, options.Colors);
            if (span.Background is { } b) AppendColor(open, b, foreground: false, options.Colors);
            if (span.Style.HasFlag(CellStyle.Bold)) open.Append("\e[1m");
            if (span.Style.HasFlag(CellStyle.Italic)) open.Append("\e[3m");
            if (span.Style.HasFlag(CellStyle.Underline)) open.Append("\e[4m");
            if (span.Style.HasFlag(CellStyle.Dim)) open.Append("\e[2m");
            if (span.Style.HasFlag(CellStyle.Strikethrough)) open.Append("\e[9m");
            if (span.Style.HasFlag(CellStyle.Reverse)) open.Append("\e[7m");
            // OSC 8 hyperlinks for absolute targets (pagers like less -R pass them through).
            string? href = options.Hyperlinks && span.LinkId is { } id && id < links.Count && LinkResolver.IsExternal(links[id].Url) ? links[id].Url : null;
            if (href is not null) sb.Append("\e]8;;").Append(href).Append("\e\\");
            sb.Append(open).Append(span.Text);
            if (open.Length > 0) sb.Append("\e[0m");
            if (href is not null) sb.Append("\e]8;;\e\\");
        }
        sb.Append('\n');
    }

    private static void AppendColor(StringBuilder sb, Rgb c, bool foreground, ColorMode mode)
    {
        switch (mode)
        {
            case ColorMode.TrueColor:
                sb.Append(foreground ? "\e[38;2;" : "\e[48;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
                break;
            case ColorMode.Ansi256:
                sb.Append(foreground ? "\e[38;5;" : "\e[48;5;").Append(ColorQuantizer.ToAnsi256(c)).Append('m');
                break;
            case ColorMode.Ansi16:
                sb.Append("\e[").Append(ColorQuantizer.ToAnsi16Sgr(c, !foreground)).Append('m');
                break;
        }
    }
}
