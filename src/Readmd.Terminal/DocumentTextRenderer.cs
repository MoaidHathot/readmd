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
    public static string Render(string markdown, bool dark, int width, bool color)
    {
        var theme = TerminalTheme.For(dark);
        // Parse once with the terminal pipeline, then derive TOC/front-matter from that same AST -
        // no second parse and no HTML render (which the text output never displays).
        var ast = Timing.Measure("parse", () => Markdown.Parse(markdown, TerminalPipeline.Instance));
        var meta = Timing.Measure("metadata", () => MarkdownRenderer.ExtractMetadata(ast, "stdin.md"));
        // Plain output carries no colour, so syntax highlighting (the slowest part of layout) would be
        // pure waste: defer it and simply never run the deferred work.
        var renderer = new MarkdownTerminalRenderer(theme, Math.Max(20, width) - 1) { DeferHighlighting = !color };
        var result = Timing.Measure("layout", () => renderer.Render(ast, meta.Toc, meta.FrontMatter));

        var sb = new StringBuilder();
        foreach (var line in result.Lines)
            AppendLine(sb, line, color);
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, DisplayLine line, bool color)
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
            if (span.Color is { } c) open.Append("\e[38;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
            if (span.Style.HasFlag(CellStyle.Bold)) open.Append("\e[1m");
            if (span.Style.HasFlag(CellStyle.Italic)) open.Append("\e[3m");
            if (span.Style.HasFlag(CellStyle.Underline)) open.Append("\e[4m");
            if (span.Style.HasFlag(CellStyle.Dim)) open.Append("\e[2m");
            if (span.Style.HasFlag(CellStyle.Strikethrough)) open.Append("\e[9m");
            if (span.Style.HasFlag(CellStyle.Reverse)) open.Append("\e[7m");
            sb.Append(open).Append(span.Text);
            if (open.Length > 0) sb.Append("\e[0m");
        }
        sb.Append('\n');
    }
}
