namespace Readmd.Tests;

/// <summary>Regression tests for rendering bugs fixed in the 0.9 line (entities, breaks, list
/// gutters, headings, tables, list numbering, abbreviations, media links, tabs).</summary>
public class RenderingRegressionTests
{
    [Fact]
    public void Html_entities_are_decoded_not_dropped()
    {
        var text = Render.Text("Copyright &copy; 2024 &amp; co &#169; x&nbsp;y");
        Assert.Contains("Copyright © 2024 & co © x\u00a0y", text);
    }

    [Fact]
    public void Entities_survive_in_headings_and_toc_titles()
    {
        var lines = Render.Lines("# Tom &amp; Jerry\n\nbody");
        Assert.Contains(lines, l => l.Contains("Tom & Jerry"));
    }

    [Fact]
    public void Inline_br_tag_forces_a_line_break()
    {
        var lines = Render.Lines("Line one<br>Line two<br/>Line three<BR />Line four");
        Assert.Contains(lines, l => l.Trim() == "Line one");
        Assert.Contains(lines, l => l.Trim() == "Line two");
        Assert.Contains(lines, l => l.Trim() == "Line three");
        Assert.Contains(lines, l => l.Trim() == "Line four");
    }

    [Fact]
    public void Wrapped_list_item_does_not_repeat_its_bullet()
    {
        var longItem = "- " + string.Join(" ", Enumerable.Repeat("word", 30));
        var lines = Render.Lines(longItem, width: 40).Where(l => l.Contains("word")).ToList();
        Assert.True(lines.Count >= 3, "expected the item to wrap across several lines");
        Assert.StartsWith("• ", lines[0]);
        // Continuation lines are indented under the text, with NO bullet.
        foreach (var cont in lines.Skip(1))
        {
            Assert.DoesNotContain("•", cont);
            Assert.StartsWith("  word", cont);
        }
    }

    [Fact]
    public void Wrapped_ordered_item_does_not_repeat_its_number()
    {
        var longItem = "1. " + string.Join(" ", Enumerable.Repeat("word", 30));
        var lines = Render.Lines(longItem, width: 40).Where(l => l.Contains("word")).ToList();
        Assert.StartsWith("1. ", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.DoesNotContain("1.", l));
        Assert.All(lines.Skip(1), l => Assert.StartsWith("   word", l));
    }

    [Fact]
    public void Quote_bar_repeats_on_wrapped_lines()
    {
        var md = "> " + string.Join(" ", Enumerable.Repeat("word", 30));
        var lines = Render.Lines(md, width: 40).Where(l => l.Contains("word")).ToList();
        Assert.True(lines.Count >= 2);
        Assert.All(lines, l => Assert.StartsWith("▌", l));
    }

    [Fact]
    public void H1_keeps_inline_code_and_registers_links_and_wraps()
    {
        var md = "# Install `readmd` from [GitHub](https://example.com) " + string.Join(" ", Enumerable.Repeat("word", 20));
        var lines = Render.Lines(md, width: 40);
        var h1Lines = lines.TakeWhile(l => !l.StartsWith("━")).ToList();
        Assert.True(h1Lines.Count >= 2, "a long H1 must wrap, not clip");
        Assert.All(h1Lines, l => Assert.True(l.Length <= 40, $"H1 line exceeds width: '{l}'"));
        Assert.Contains(h1Lines, l => l.Contains("readmd"));
        Assert.Contains(h1Lines, l => l.Contains("GitHub"));
        // The link inside the H1 is registered for navigation (superscript marker ¹ follows it).
        Assert.Contains(h1Lines, l => l.Contains("GitHub¹"));
    }

    [Fact]
    public void H5_and_H6_are_distinguishable()
    {
        var l5 = Render.Lines("##### five").First(l => l.Contains("five"));
        var l6 = Render.Lines("###### six").First(l => l.Contains("six"));
        Assert.NotEqual(l5[0], l6[0]);
    }

    [Fact]
    public void Alpha_and_roman_lists_keep_their_numbering_style()
    {
        var alpha = Render.Lines("a. first\nb. second\nc. third");
        Assert.Contains(alpha, l => l.StartsWith("a. first"));
        Assert.Contains(alpha, l => l.StartsWith("c. third"));
        var roman = Render.Lines("i. first\nii. second\niii. third");
        Assert.Contains(roman, l => l.StartsWith("i. first"));
        Assert.Contains(roman, l => l.StartsWith("iii. third"));
    }

    [Fact]
    public void Paren_delimiter_ordered_list_is_preserved()
    {
        var lines = Render.Lines("1) one\n2) two");
        Assert.Contains(lines, l => l.StartsWith("1) one"));
        Assert.Contains(lines, l => l.StartsWith("2) two"));
    }

    [Fact]
    public void Abbreviation_expansion_is_shown_once()
    {
        var md = "HTML is great. HTML again.\n\n*[HTML]: HyperText Markup Language";
        var text = Render.Text(md);
        Assert.Contains("HTML (HyperText Markup Language)", text);
        // Only the first occurrence carries the expansion.
        Assert.Equal(1, CountOf(text, "(HyperText Markup Language)"));
    }

    [Fact]
    public void Media_image_urls_render_as_links_not_images()
    {
        var text = Render.Text("![intro](https://www.youtube.com/watch?v=abc)\n\n![clip](./demo.mp4)");
        Assert.Contains("▶ intro", text);
        Assert.Contains("▶ clip", text);
        Assert.DoesNotContain("▣", text);   // no image anchors were emitted
    }

    [Fact]
    public void Tabs_in_code_are_expanded_to_spaces()
    {
        var lines = Render.Lines("```go\nfunc main() {\n\tfmt.Println(1)\n}\n```");
        var body = lines.First(l => l.Contains("Println"));
        Assert.DoesNotContain("\t", body);
        Assert.Contains("    fmt.Println", body);
    }

    [Fact]
    public void Table_rows_have_no_trailing_space_and_match_border_width()
    {
        var lines = Render.Lines("| a | b |\n|---|---|\n| 1 | 2 |");
        var border = lines.First(l => l.StartsWith("┌"));
        var row = lines.First(l => l.StartsWith("│") && l.Contains("1"));
        Assert.False(row.EndsWith(' '));
        Assert.Equal(border.Length, row.Length);
    }

    [Fact]
    public void Grid_table_cell_with_list_keeps_its_items()
    {
        var md = string.Join("\n",
            "+-------+-----------+",
            "| Col A | Col B     |",
            "+=======+===========+",
            "| x     | - one     |",
            "|       | - two     |",
            "+-------+-----------+");
        var text = Render.Text(md);
        Assert.Contains("one", text);
        Assert.Contains("two", text);
    }

    [Fact]
    public void Footnote_continuation_lines_hang_under_the_text()
    {
        var md = "Claim.[^1]\n\n[^1]: " + string.Join(" ", Enumerable.Repeat("note", 30));
        var lines = Render.Lines(md, width: 40).Where(l => l.Contains("note") && !l.Contains("Footnotes")).ToList();
        Assert.True(lines.Count >= 2);
        Assert.StartsWith("  1. note", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("     note", l));
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
