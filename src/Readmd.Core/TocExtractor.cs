using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Readmd.Core;

/// <summary>Walks the parsed AST and collects heading entries for the table of contents.</summary>
public static class TocExtractor
{
    public static IReadOnlyList<TocEntry> Extract(MarkdownObject document)
    {
        var entries = new List<TocEntry>();
        if (document is not ContainerBlock) return entries;

        foreach (var node in Descendants(document))
        {
            if (node is HeadingBlock heading)
            {
                var title = GetPlainText(heading);
                if (string.IsNullOrWhiteSpace(title)) continue;
                var id = heading.GetAttributes().Id ?? Slug(title);
                entries.Add(new TocEntry(heading.Level, title, id, heading.Line + 1));
            }
        }

        return entries;
    }

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

    public static string GetPlainText(LeafBlock leaf)
    {
        if (leaf.Inline is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var inline in leaf.Inline)
        {
            AppendInline(inline, sb);
        }
        return sb.ToString().Trim();
    }

    private static void AppendInline(Inline inline, System.Text.StringBuilder sb)
    {
        switch (inline)
        {
            case LiteralInline literal:
                sb.Append(literal.Content.AsSpan());
                break;
            case CodeInline code:
                sb.Append(code.Content);
                break;
            case HtmlEntityInline entity:
                sb.Append(entity.Transcoded.AsSpan());
                break;
            case LineBreakInline:
                sb.Append(' ');
                break;
            case ContainerInline container:
                foreach (var child in container) AppendInline(child, sb);
                break;
        }
    }

    private static string Slug(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
