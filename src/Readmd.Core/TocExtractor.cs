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
            case Markdig.Extensions.SmartyPants.SmartyPant pant:
                sb.Append(SmartyPantText(pant));
                break;
            case LineBreakInline:
                sb.Append(' ');
                break;
            case ContainerInline container:
                foreach (var child in container) AppendInline(child, sb);
                break;
        }
    }

    /// <summary>The typographic character a SmartyPants replacement stands for.</summary>
    public static string SmartyPantText(Markdig.Extensions.SmartyPants.SmartyPant pant) => pant.Type switch
    {
        Markdig.Extensions.SmartyPants.SmartyPantType.LeftQuote => "‘",
        Markdig.Extensions.SmartyPants.SmartyPantType.RightQuote => "’",
        Markdig.Extensions.SmartyPants.SmartyPantType.LeftDoubleQuote => "“",
        Markdig.Extensions.SmartyPants.SmartyPantType.RightDoubleQuote => "”",
        Markdig.Extensions.SmartyPants.SmartyPantType.LeftAngleQuote => "«",
        Markdig.Extensions.SmartyPants.SmartyPantType.RightAngleQuote => "»",
        Markdig.Extensions.SmartyPants.SmartyPantType.Ellipsis => "…",
        Markdig.Extensions.SmartyPants.SmartyPantType.Dash2 => "–",
        Markdig.Extensions.SmartyPants.SmartyPantType.Dash3 => "—",
        Markdig.Extensions.SmartyPants.SmartyPantType.Quote => "'",
        Markdig.Extensions.SmartyPants.SmartyPantType.DoubleQuote => "\"",
        _ => pant.ToString() ?? "",
    };

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
