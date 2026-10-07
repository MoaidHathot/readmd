using Readmd.Core;
using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>Markdown-fidelity features: the HTML subset, images in nested contexts, fence
/// attributes, math fences, Azure DevOps ::: mermaid containers, LaTeX coverage, wide-table
/// stacking, TOML front matter, and smart punctuation.</summary>
public class MarkdownFidelityTests
{
    // ---------------- HTML subset ----------------

    [Fact]
    public void Centered_html_header_with_image_and_bold_is_rendered()
    {
        var md = "<p align=\"center\">\n  <img src=\"./logo.png\" alt=\"Logo\" width=\"120\">\n  <br>\n  <b>Readmd</b> &mdash; <i>terminal markdown</i>\n</p>";
        var lines = Render.Lines(md, width: 80);
        var text = lines.First(l => l.Contains("Readmd"));
        Assert.Contains("Readmd — terminal markdown", text);
        Assert.True(text.StartsWith("   "), "centred text has a left margin");
        Assert.Contains(lines, l => l.Contains("▣") && l.Contains("Logo"));
    }

    [Fact]
    public void Picture_element_picks_the_source_matching_the_theme()
    {
        var md = "<picture>\n  <source media=\"(prefers-color-scheme: dark)\" srcset=\"dark.png\">\n  <source media=\"(prefers-color-scheme: light)\" srcset=\"light.png\">\n  <img alt=\"Themed\" src=\"light.png\">\n</picture>";
        var dark = Render.Full(md, dark: true).Renderer.PendingImages.Values.ToList();
        var light = Render.Full(md, dark: false).Renderer.PendingImages.Values.ToList();
        Assert.Equal(["dark.png"], dark);
        Assert.Equal(["light.png"], light);
    }

    [Fact]
    public void Inline_html_formatting_tags_apply_styles()
    {
        var (lines, links, _) = Render.Full("Press <kbd>Ctrl</kbd>, <sup>2</sup>, <sub>2</sub>, <mark>m</mark>, <s>x</s>, <span style=\"color:#ff0000\">red</span>, <a href=\"https://example.com\">lnk</a>.");
        var spans = lines[0].Spans;
        Assert.Contains(spans, s => s.Text == "Ctrl" && s.Style.HasFlag(CellStyle.Bold) && s.Background is not null);
        Assert.Contains(spans, s => s.Text == "²");
        Assert.Contains(spans, s => s.Text == "₂");
        Assert.Contains(spans, s => s.Text == "m" && s.Style.HasFlag(CellStyle.Reverse));
        Assert.Contains(spans, s => s.Text == "x" && s.Style.HasFlag(CellStyle.Strikethrough));
        Assert.Contains(spans, s => s.Text == "red" && s.Color == Rgb.FromHex("#ff0000"));
        Assert.Contains(spans, s => s.Text == "lnk" && s.LinkId is not null);
        Assert.Contains(links, l => l.Url == "https://example.com");
    }

    [Fact]
    public void Unclosed_inline_html_does_not_leak_into_the_next_paragraph()
    {
        var (lines, _, _) = Render.Full("<b>bold start\n\nnext paragraph");
        var next = lines.First(l => l.PlainText.Contains("next paragraph"));
        Assert.All(next.Spans, s => Assert.False(s.Style.HasFlag(CellStyle.Bold)));
    }

    [Fact]
    public void Details_summary_renders_header_and_indents_body()
    {
        var md = "<details>\n<summary>Click to expand</summary>\n\nHidden **body** text.\n\n</details>\n\nAfter.";
        var lines = Render.Lines(md);
        Assert.Contains(lines, l => l.StartsWith("▾ Click to expand"));
        Assert.Contains(lines, l => l.StartsWith("  Hidden body text."));
        Assert.Contains(lines, l => l.StartsWith("After."));   // indentation ends with </details>
    }

    [Fact]
    public void Html_table_becomes_a_grid_with_header_and_alignment()
    {
        var md = "<table>\n<tr><th>Name</th><th align=\"right\">Size</th></tr>\n<tr><td>alpha</td><td align=\"right\">10</td></tr>\n</table>";
        var lines = Render.Lines(md, width: 60);
        Assert.Contains(lines, l => l.StartsWith("┌"));
        var header = lines.First(l => l.Contains("Name"));
        Assert.Contains("Size", header);
        var row = lines.First(l => l.Contains("alpha"));
        Assert.Contains("  10 │", row);   // right-aligned under "Size"
        Assert.Contains(lines, l => l.StartsWith("├"));
    }

    [Fact]
    public void Html_lists_quotes_pre_and_hr_are_rendered()
    {
        var md = "<ul><li>one</li><li>two <code>c</code></li></ul>\n<blockquote>quoted</blockquote>\n<pre><code class=\"language-python\">x = 1</code></pre>\n<hr>";
        var lines = Render.Lines(md);
        Assert.Contains(lines, l => l.StartsWith("• one"));
        Assert.Contains(lines, l => l.StartsWith("• two c"));
        Assert.Contains(lines, l => l.StartsWith("▌ quoted"));
        Assert.Contains(lines, l => l.Contains("│ python"));
        Assert.Contains(lines, l => l.Contains("x = 1"));
        Assert.Contains(lines, l => l.StartsWith("────────"));
    }

    [Fact]
    public void Html_comments_and_scripts_are_invisible()
    {
        var text = Render.Text("<!-- hidden -->\n<script>alert(1)</script>\n<style>p{}</style>\n\nvisible");
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("p{}", text);
        Assert.Contains("visible", text);
    }

    [Fact]
    public void Html_heading_tags_render_as_headings()
    {
        var lines = Render.Lines("<h2>Section</h2>\n\n<h3>Sub</h3>");
        Assert.Contains(lines, l => l.StartsWith("▌ Section"));
        Assert.Contains(lines, l => l.StartsWith("● Sub"));
    }

    [Fact]
    public void Html_img_tag_renders_an_image_anchor_inside_markdown_paragraph()
    {
        var (lines, _, r) = Render.Full("Logo: <img src=\"a.png\" alt=\"A\"> done.");
        Assert.Contains(lines, l => l.PlainText.Contains("Logo:") && l.PlainText.Contains("done."));
        Assert.Contains(lines, l => l.DiagramKey is not null && l.PlainText.Contains("A"));
        Assert.Contains("a.png", r.PendingImages.Values);
    }

    [Fact]
    public void Right_aligned_div_is_pushed_right()
    {
        var lines = Render.Lines("<div align=\"right\">right</div>", width: 40);
        var l = lines.First(x => x.Contains("right"));
        Assert.True(l.Length >= 38, $"expected right alignment, got '{l}'");
    }

    // ---------------- images everywhere ----------------

    [Fact]
    public void Images_in_list_items_quotes_and_alerts_render_as_anchors()
    {
        var md = "- ![logo](./a.png)\n\n> ![q](./b.png)\n\n> [!NOTE]\n> ![n](./c.png)";
        var (lines, _, r) = Render.Full(md);
        var anchors = lines.Where(l => l.DiagramKey is not null).ToList();
        Assert.Equal(3, anchors.Count);
        Assert.StartsWith("• ▣ logo", anchors[0].PlainText);
        Assert.StartsWith("▌ ▣ q", anchors[1].PlainText);
        Assert.Equal(3, r.PendingImages.Count);
        Assert.Equal(2, anchors[0].DiagramIndent);
    }

    [Fact]
    public void Image_mixed_with_text_in_list_item_keeps_text_and_anchor()
    {
        var (lines, _, _) = Render.Full("- See ![logo](./a.png) here");
        Assert.Contains(lines, l => l.PlainText.StartsWith("• See") && l.PlainText.Contains("here"));
        Assert.Contains(lines, l => l.DiagramKey is not null);
        Assert.DoesNotContain(lines, l => l.PlainText.Contains("🖼"));
    }

    // ---------------- code fences ----------------

    [Fact]
    public void Fence_title_attribute_shows_in_the_header()
    {
        var lines = Render.Lines("```js title=\"app.js\"\nconst a = 1;\n```");
        Assert.Contains(lines, l => l.Contains("js") && l.Contains("app.js"));
    }

    [Fact]
    public void Fence_highlight_ranges_tint_the_lines()
    {
        var (lines, _, _) = Render.Full("```js {2,3}\na\nb\nc\nd\n```");
        DisplayLine Row(string t) => lines.First(l => l.PlainText.TrimEnd().EndsWith("│ " + t));
        Assert.DoesNotContain(Row("a").Spans, s => s.Text == "a" && s.Background is not null);
        Assert.Contains(Row("b").Spans, s => s.Text == "b" && s.Background is not null);
        Assert.Contains(Row("c").Spans, s => s.Text == "c" && s.Background is not null);
        Assert.DoesNotContain(Row("d").Spans, s => s.Text == "d" && s.Background is not null);
    }

    [Fact]
    public void Fence_nonums_hides_line_numbers()
    {
        var lines = Render.Lines("```python nonums\nprint(1)\n```");
        Assert.DoesNotContain(lines, l => l.Contains(" 1 │"));
        Assert.Contains(lines, l => l.Contains("│ print(1)"));
    }

    [Fact]
    public void Math_fence_renders_as_display_math()
    {
        var lines = Render.Lines("```math\n\\frac{a}{b}\n```");
        Assert.Contains(lines, l => l.Trim() == "(a)/(b)");
        Assert.DoesNotContain(lines, l => l.Contains("│ math"));
    }

    [Fact]
    public void Azure_devops_mermaid_container_becomes_a_diagram()
    {
        var md = "Before\n\n::: mermaid\ngraph LR\n  A --> B\n:::\n\nAfter";
        var (lines, _, r) = Render.Full(md);
        Assert.Contains(lines, l => l.PlainText.Contains("Mermaid diagram"));
        var req = Assert.Single(r.PendingDiagrams.Values);
        Assert.Equal(DiagramKind.Mermaid, req.Kind);
        Assert.Equal("graph LR\n  A --> B", req.Source);
    }

    // ---------------- math ----------------

    [Theory]
    [InlineData("\\lim_{x \\to 0} f(x)", "lim")]
    [InlineData("\\hat{x} + \\vec{v}", "x̂")]
    [InlineData("\\binom{n}{k}", "(n choose k)")]
    [InlineData("\\mathcal{L}", "ℒ")]
    [InlineData("\\mathbb{R}^n", "ℝⁿ")]
    [InlineData("\\overline{z}", "z̅")]
    [InlineData("\\sin x \\cos y", "sin x cos y")]
    [InlineData("\\left( a \\right)", "( a )")]
    [InlineData("\\unknowncmd x", "unknowncmd x")]
    public void Latex_subset_converts_to_unicode(string latex, string expected)
    {
        var s = MathRenderer.ToUnicode(latex);
        Assert.Contains(expected, s);
        Assert.DoesNotContain("\\", s.Replace("\\\\", ""));
    }

    // ---------------- tables ----------------

    [Fact]
    public void Table_too_wide_for_grid_stacks_into_cards()
    {
        var header = "| " + string.Join(" | ", Enumerable.Range(1, 12).Select(i => $"Column {i}")) + " |";
        var sep = "|" + string.Concat(Enumerable.Repeat("---|", 12));
        var row = "| " + string.Join(" | ", Enumerable.Range(1, 12).Select(i => $"value{i}")) + " |";
        var lines = Render.Lines($"{header}\n{sep}\n{row}", width: 40);
        Assert.All(lines, l => Assert.True(l.Length <= 40, $"line exceeds width: '{l}'"));
        Assert.DoesNotContain(lines, l => l.StartsWith("┌"));
        Assert.Contains(lines, l => l.Contains("Column 1") && l.Contains("│") && l.Contains("value1"));
        Assert.Contains(lines, l => l.Contains("Column 12") && l.Contains("value12"));
    }

    // ---------------- front matter / pipeline ----------------

    [Fact]
    public void Toml_front_matter_is_stripped_and_parsed()
    {
        var md = "+++\ntitle = \"Hugo Post\"\ntags = [\"a\", \"b\"]\n+++\n\n# Body";
        var (body, fm) = FrontMatter.StripToml(md);
        Assert.NotNull(fm);
        Assert.Equal("Hugo Post", fm!.Get("title"));
        Assert.Equal(["a", "b"], fm.GetList("tags"));
        Assert.StartsWith("\n# Body", body);
        Assert.DoesNotContain("+++", DocumentTextRenderer.Render(md, dark: true, width: 80, color: false));
    }

    [Fact]
    public void Smart_punctuation_is_opt_in()
    {
        var plain = DocumentTextRenderer.Render("\"quoted\" -- dash...", dark: true, width: 80, color: false);
        Assert.Contains("\"quoted\" -- dash...", plain);
        var smart = DocumentTextRenderer.Render("\"quoted\" -- dash...", dark: true, width: 80, color: false, new DocumentTextRenderer.Options(SmartPunctuation: true));
        Assert.Contains("“quoted” – dash…", smart);
    }

    [Fact]
    public void Color_chips_follow_css_color_literals_in_inline_code()
    {
        var (lines, _, _) = Render.Full("Use `#ff8800` or `rgb(0, 128, 255)` but not `#nothex`.");
        var spans = lines[0].Spans;
        Assert.Equal(2, spans.Count(s => s.Text == "■"));
        Assert.Contains(spans, s => s.Text == "■" && s.Color == Rgb.FromHex("#ff8800"));
    }
}
