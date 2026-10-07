using System.Text;
using System.Text.RegularExpressions;

namespace Readmd.Core;

/// <summary>
/// Splits a Markdown document into presentation slides. Slide boundaries are thematic breaks
/// (<c>---</c>, <c>***</c>, <c>___</c>) preceded by a blank line (so a setext H2 underline is not
/// a boundary) or an explicit <c>&lt;!-- slide --&gt;</c> comment; neither is honoured inside a
/// fenced code block. Within a slide, <c>&lt;!-- pause --&gt;</c> marks a progressive-reveal step,
/// and speaker notes come from <c>&lt;!-- notes … --&gt;</c> comments (single- or multi-line) or a
/// remark-style <c>???</c> line that starts the notes for the rest of the slide. Front matter stays
/// with the first slide so its title/author/date header becomes the title slide.
/// </summary>
public sealed class SlideDeck
{
    /// <summary>One slide: full Markdown, its reveal fragments (cumulative when joined), notes, a title and where it starts in the source.</summary>
    public sealed record Slide(string Markdown, IReadOnlyList<string> Fragments, string? Notes, string Title, int SourceLine)
    {
        /// <summary>Markdown visible after <paramref name="step"/> pauses (0 = first fragment only).</summary>
        public string MarkdownUpTo(int step)
        {
            step = Math.Clamp(step, 0, Fragments.Count - 1);
            if (step == Fragments.Count - 1) return Markdown;
            var sb = new StringBuilder();
            for (int i = 0; i <= step; i++) { if (i > 0) sb.Append('\n'); sb.Append(Fragments[i]); }
            return sb.ToString();
        }
    }

    public IReadOnlyList<Slide> Slides { get; }
    public int Count => Slides.Count;
    public Slide this[int index] => Slides[index];

    private SlideDeck(List<Slide> slides) { Slides = slides; }

    private static readonly Regex ThematicBreak = new(@"^ {0,3}(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
    private static readonly Regex SlideMarker = new(@"^\s*<!--\s*slide\s*-->\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PauseMarker = new(@"^\s*<!--\s*pause\s*-->\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NotesOpen = new(@"^\s*<!--\s*notes?\b:?\s*(?<text>.*?)\s*(?<close>-->)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FenceOpen = new(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^ {0,3}#{1,6}\s+(?<t>.+?)\s*#*\s*$", RegexOptions.Compiled);

    /// <summary>Splits <paramref name="markdown"/> into slides (always at least one).</summary>
    public static SlideDeck Split(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var slides = new List<Slide>();
        var current = new List<string>();
        int currentStart = 1;

        int i = 0;
        // Front matter (--- … --- or +++ … +++) belongs to the first slide verbatim. A leading "---"
        // followed by a blank line is a slide separator, not front matter: the opening delimiter
        // must be followed directly by a "key:" / "key =" line (or a comment).
        if (lines.Length > 2 && lines[0].Trim() is "---" or "+++" && LooksLikeFrontMatterLine(lines[1], lines[0].Trim() == "+++"))
        {
            var delim = lines[0].Trim();
            int close = Array.FindIndex(lines, 1, l => l.Trim() == delim || (delim == "---" && l.Trim() == "..."));
            if (close > 0)
            {
                for (; i <= close; i++) current.Add(lines[i]);
            }
        }

        string? fence = null;
        bool prevBlank = true;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (fence is null)
            {
                var m = FenceOpen.Match(line);
                if (m.Success) fence = m.Groups[1].Value;
            }
            else
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith(fence) && trimmed.Trim().All(c => c == fence[0]) && trimmed.Trim().Length >= fence.Length) fence = null;
                current.Add(line);
                prevBlank = false;
                continue;
            }

            bool isBreak = fence is null && ((prevBlank && ThematicBreak.IsMatch(line)) || SlideMarker.IsMatch(line));
            if (isBreak)
            {
                // A separator with nothing before it (document starting with ---) doesn't make an empty slide.
                if (current.Any(l => l.Trim().Length > 0)) slides.Add(Build(current, currentStart, slides.Count + 1));
                current = [];
                currentStart = i + 2;   // 1-based line after the separator
                prevBlank = true;
                continue;
            }
            current.Add(line);
            prevBlank = line.Trim().Length == 0;
        }
        if (current.Any(l => l.Trim().Length > 0) || slides.Count == 0) slides.Add(Build(current, currentStart, slides.Count + 1));
        return new SlideDeck(slides);
    }

    private static readonly Regex YamlKey = new(@"^\s*[\w\-\.""']+\s*:", RegexOptions.Compiled);
    private static readonly Regex TomlKey = new(@"^\s*(?:[\w\-\.""']+\s*=|\[)", RegexOptions.Compiled);

    private static bool LooksLikeFrontMatterLine(string line, bool toml)
    {
        var t = line.Trim();
        if (t.Length == 0) return false;
        if (t.StartsWith('#')) return true;
        return toml ? TomlKey.IsMatch(t) : YamlKey.IsMatch(t);
    }

    private static Slide Build(List<string> raw, int sourceLine, int ordinal)
    {
        var fragments = new List<string>();
        var notes = new StringBuilder();
        var body = new StringBuilder();
        var fragment = new StringBuilder();
        string? fence = null;
        bool inNotes = false, remarkNotes = false;

        void EndFragment()
        {
            fragments.Add(fragment.ToString().Trim('\n'));
            fragment.Clear();
        }

        foreach (var line in raw)
        {
            if (remarkNotes) { notes.Append(line).Append('\n'); continue; }
            if (inNotes)
            {
                int close = line.IndexOf("-->", StringComparison.Ordinal);
                if (close >= 0) { notes.Append(line[..close].TrimEnd()).Append('\n'); inNotes = false; }
                else notes.Append(line).Append('\n');
                continue;
            }
            if (fence is null)
            {
                var m = FenceOpen.Match(line);
                if (m.Success) fence = m.Groups[1].Value;
                else
                {
                    if (PauseMarker.IsMatch(line)) { EndFragment(); continue; }
                    if (line.Trim() == "???") { remarkNotes = true; continue; }
                    var n = NotesOpen.Match(line);
                    if (n.Success)
                    {
                        if (n.Groups["text"].Value.Length > 0) notes.Append(n.Groups["text"].Value).Append('\n');
                        if (!n.Groups["close"].Success) inNotes = true;
                        continue;
                    }
                }
            }
            else
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith(fence) && trimmed.Trim().All(c => c == fence[0])) fence = null;
            }
            fragment.Append(line).Append('\n');
            body.Append(line).Append('\n');
        }
        EndFragment();
        if (fragments.Count > 1 && fragments[^1].Length == 0) fragments.RemoveAt(fragments.Count - 1);

        var markdown = string.Join("\n", fragments);
        string title = "";
        foreach (var line in markdown.Split('\n'))
        {
            var h = Heading.Match(line);
            if (h.Success) { title = h.Groups["t"].Value.Trim(); break; }
        }
        if (title.Length == 0)
        {
            var first = markdown.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("---") && !l.StartsWith("+++") && !l.StartsWith("<!--"));
            title = first is null ? $"Slide {ordinal}" : (first.Length > 48 ? first[..47] + "…" : first);
        }
        var notesText = notes.ToString().Trim();
        return new Slide(markdown, fragments, notesText.Length > 0 ? notesText : null, title, sourceLine);
    }

    /// <summary>Index of the slide that contains 1-based source line <paramref name="line"/>.</summary>
    public int IndexForSourceLine(int line)
    {
        int idx = 0;
        for (int i = 0; i < Slides.Count; i++)
            if (Slides[i].SourceLine <= line) idx = i;
        return idx;
    }
}
