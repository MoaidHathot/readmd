using System.Net;
using System.Text;

namespace Readmd.Terminal;

/// <summary>
/// A minimal, forgiving HTML tokenizer for the subset of markup that commonly appears in README
/// files (<c>&lt;p align=center&gt;</c>, <c>&lt;img&gt;</c>, <c>&lt;picture&gt;</c>,
/// <c>&lt;details&gt;</c>, <c>&lt;table&gt;</c>, <c>&lt;kbd&gt;</c>, <c>&lt;br&gt;</c>, …). It
/// produces a flat stream of tag and text tokens; nesting is interpreted by the renderer. It never
/// throws on malformed input — unknown or broken constructs degrade to text.
/// </summary>
internal static class HtmlFragments
{
    public enum TokenKind { Text, Open, Close, Comment }

    public sealed record Token(TokenKind Kind, string Name, string Text, Dictionary<string, string>? Attributes, bool SelfClosing)
    {
        public string? Attr(string name) =>
            Attributes is not null && Attributes.TryGetValue(name, out var v) ? v : null;
        public bool HasAttr(string name) => Attributes is not null && Attributes.ContainsKey(name);
    }

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Elements whose content is raw text (not parsed for tags) until the matching close tag.</summary>
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea",
    };

    public static List<Token> Tokenize(string html)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(html)) return tokens;
        int i = 0, n = html.Length;
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length == 0) return;
            tokens.Add(new Token(TokenKind.Text, "", WebUtility.HtmlDecode(text.ToString()), null, false));
            text.Clear();
        }

        while (i < n)
        {
            char c = html[i];
            if (c != '<') { text.Append(c); i++; continue; }

            // Comment / doctype / CDATA
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                FlushText();
                int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0) end = n - 3;
                tokens.Add(new Token(TokenKind.Comment, "", html.Substring(i + 4, Math.Max(0, end - i - 4)), null, false));
                i = end + 3;
                continue;
            }
            if (i + 1 < n && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                FlushText();
                int end = html.IndexOf('>', i);
                i = end < 0 ? n : end + 1;
                continue;
            }

            // Tag: must be followed by a letter or '/'.
            if (i + 1 >= n || !(char.IsLetter(html[i + 1]) || html[i + 1] == '/'))
            {
                text.Append(c); i++; continue;
            }

            int close = FindTagEnd(html, i);
            if (close < 0) { text.Append(html, i, n - i); i = n; break; }

            FlushText();
            var body = html.Substring(i + 1, close - i - 1).Trim();
            i = close + 1;
            if (body.Length == 0) continue;

            bool closing = body[0] == '/';
            if (closing) body = body[1..].Trim();
            bool selfClosing = body.EndsWith('/');
            if (selfClosing) body = body[..^1].Trim();

            int nameEnd = 0;
            while (nameEnd < body.Length && !char.IsWhiteSpace(body[nameEnd])) nameEnd++;
            var name = body[..nameEnd].ToLowerInvariant();
            if (name.Length == 0) continue;

            if (closing)
            {
                tokens.Add(new Token(TokenKind.Close, name, "", null, false));
                continue;
            }

            var attrs = ParseAttributes(body.AsSpan(nameEnd));
            selfClosing |= VoidElements.Contains(name);
            tokens.Add(new Token(TokenKind.Open, name, "", attrs, selfClosing));

            // Raw-text elements: swallow everything up to the close tag as one text token.
            if (!selfClosing && RawTextElements.Contains(name))
            {
                int endTag = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (endTag < 0) endTag = n;
                tokens.Add(new Token(TokenKind.Text, "", html.Substring(i, endTag - i), null, false));
                int gt = endTag < n ? html.IndexOf('>', endTag) : -1;
                i = gt < 0 ? n : gt + 1;
                tokens.Add(new Token(TokenKind.Close, name, "", null, false));
            }
        }
        FlushText();
        return tokens;
    }

    // Finds the '>' that ends the tag starting at `start`, honouring quoted attribute values.
    private static int FindTagEnd(string html, int start)
    {
        char quote = '\0';
        for (int i = start + 1; i < html.Length; i++)
        {
            char c = html[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '>') return i;
            if (c == '<') return -1;   // a stray '<' before any '>' — not a tag
        }
        return -1;
    }

    private static Dictionary<string, string>? ParseAttributes(ReadOnlySpan<char> s)
    {
        Dictionary<string, string>? attrs = null;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            int nameStart = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '=') i++;
            var name = s[nameStart..i].ToString().ToLowerInvariant();
            if (name.Length == 0) { i++; continue; }
            string value = "";
            int j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j < s.Length && s[j] == '=')
            {
                j++;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j < s.Length && (s[j] == '"' || s[j] == '\''))
                {
                    char q = s[j];
                    int end = s[(j + 1)..].IndexOf(q);
                    if (end < 0) { value = s[(j + 1)..].ToString(); j = s.Length; }
                    else { value = s.Slice(j + 1, end).ToString(); j = j + 1 + end + 1; }
                }
                else
                {
                    int vs = j;
                    while (j < s.Length && !char.IsWhiteSpace(s[j])) j++;
                    value = s[vs..j].ToString();
                }
                i = j;
            }
            attrs ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            attrs[name] = WebUtility.HtmlDecode(value);
        }
        return attrs;
    }

    /// <summary>
    /// Collapses runs of whitespace (HTML semantics outside &lt;pre&gt;) to single spaces. Leading
    /// and trailing runs become one space each — paragraph edges are trimmed by the caller.
    /// </summary>
    public static string CollapseWhitespace(string text)
    {
        if (text.Length == 0) return text;
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        bool any = false;
        foreach (var ch in text)
        {
            if (ch is ' ' or '\t' or '\n' or '\r') { pendingSpace = true; continue; }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            any = true;
            sb.Append(ch);
        }
        if (pendingSpace && any) sb.Append(' ');
        if (!any) return " ";
        return sb.ToString();
    }
}
