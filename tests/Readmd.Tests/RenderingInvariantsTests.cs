using System.Text.RegularExpressions;
using Readmd.Terminal;
using Xunit.Abstractions;

namespace Readmd.Tests;

/// <summary>
/// Layout invariants over every real document in the repo (samples + README), at several widths,
/// plain and coloured: no line is wider than the layout width, and no source-level markup leaks
/// into the output (fence attributes, slide/pause/notes comments, container fences, raw footnote
/// labels, unescaped entities).
/// </summary>
public class RenderingInvariantsTests(ITestOutputHelper output)
{
    private static readonly Regex Ansi = new(@"\e\[[0-9;?]*[ -/]*[@-~]|\e\][^\a\e]*(?:\a|\e\\)", RegexOptions.Compiled);

    private static IEnumerable<string> Documents()
    {
        var dir = AppContext.BaseDirectory;
        string? root = null;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Readmd.slnx"))) { root = dir; break; }
            dir = Path.GetDirectoryName(dir);
        }
        if (root is null) throw new FileNotFoundException("repo root");
        yield return Path.Combine(root, "README.md");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "samples"), "*.md", SearchOption.AllDirectories).OrderBy(f => f))
            yield return f;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var doc in Documents())
            foreach (var width in new[] { 40, 60, 80, 100, 132 })
                yield return [doc, width];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Lines_fit_the_width(string path, int width)
    {
        var markdown = File.ReadAllText(path);
        foreach (var color in new[] { false, true })
        {
            var text = DocumentTextRenderer.Render(markdown, dark: true, width: width, color: color,
                new DocumentTextRenderer.Options(Colors: ColorMode.TrueColor, Hyperlinks: true));
            var plain = color ? Ansi.Replace(text, "") : text;
            var offenders = new List<string>();
            int n = 0;
            foreach (var line in plain.Split('\n'))
            {
                n++;
                int w = TextWidth.Of(line.TrimEnd());
                if (w > width) offenders.Add($"  L{n} width {w} > {width}: {line.TrimEnd()}");
            }
            if (offenders.Count > 0)
            {
                output.WriteLine($"{Path.GetFileName(path)} @ {width} ({(color ? "color" : "plain")}):");
                foreach (var o in offenders.Take(12)) output.WriteLine(o);
            }
            Assert.True(offenders.Count == 0, $"{Path.GetFileName(path)} @ {width} ({(color ? "color" : "plain")}): {offenders.Count} overflowing line(s) — see test output");
        }
    }

    /// <summary>Structural markup (not prose that mentions it) must never reach the output.</summary>
    [Fact]
    public void Structural_markup_does_not_leak()
    {
        var md = """
            ---
            title: Leak check
            ---

            # Heading

            <!-- slide -->

            Paragraph one.

            <!-- pause -->

            <!-- notes: speaker only -->

            <!-- notes
            multi
            line
            -->

            ```csharp title="Program.cs" {1,3-5} nonums
            var a = 1;
            ```

            ```text hl_lines="2" showLineNumbers
            x
            y
            ```

            ::: mermaid
            graph TD
              A --- B
            :::

            Tom &amp; Jerry &copy; 5 &lt; 6 a&nbsp;b.

            Footnote[^x] here.

            [^x]: The note.

            *[HTML]: HyperText Markup Language

            HTML is an abbreviation.
            """;
        var text = DocumentTextRenderer.Render(md, dark: true, width: 80, color: false);
        foreach (var leak in new[] { "<!--", "-->", "speaker only", "multi", ":::", "title=\"", "{1,3-5}", "nonums", "hl_lines", "showLineNumbers", "&amp;", "&copy;", "&lt;", "&nbsp;", "[^x]", "*[HTML]:" })
            Assert.DoesNotContain(leak, text);
        Assert.Contains("Program.cs", text);        // the title is shown as a header…
        Assert.Contains("Tom & Jerry © 5 < 6", text);
        Assert.Contains("graph TD", text);          // the container's diagram source (no renderer in print)
        Assert.Contains("The note.", text);
        Assert.Contains("HyperText Markup Language", text);
    }
}