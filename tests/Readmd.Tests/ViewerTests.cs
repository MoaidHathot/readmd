using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>
/// Drives the interactive viewer headlessly (no terminal): key dispatch with counts and chords,
/// scrolling, search, folding, link hints, block navigation, yank, TOC filtering, reload anchoring.
/// </summary>
public class ViewerTests
{
    private static string LongDoc(int sections = 6, int parasPerSection = 4)
    {
        var sb = new System.Text.StringBuilder("# Title\n\nIntro paragraph with a [link](https://example.com/a) and [another](./other.md).\n\n");
        for (int s = 1; s <= sections; s++)
        {
            sb.Append($"## Section {s}\n\n");
            for (int p = 1; p <= parasPerSection; p++)
                sb.Append($"Paragraph {s}.{p} lorem ipsum dolor sit amet, consectetur adipiscing elit.\n\n");
            if (s == 2) sb.Append("```python\nprint('hello')\nx = 1\n```\n\n");
            if (s == 3) sb.Append("<details>\n<summary>Hidden details</summary>\n\nSecret body text inside details.\n\n</details>\n\n");
        }
        return sb.ToString();
    }

    private static TerminalViewer View(string md, int width = 80, int height = 20) =>
        TerminalViewer.CreateHeadless(md, width, height);

    [Fact]
    public void Scrolls_with_j_k_and_honours_count_prefix()
    {
        var v = View(LongDoc());
        v.Press("j"); Assert.Equal(1, v.Scroll);
        v.Press("5j"); Assert.Equal(6, v.Scroll);
        v.Press("2k"); Assert.Equal(4, v.Scroll);
        v.Press("G"); Assert.True(v.Scroll > 10);
        v.Press("gg"); Assert.Equal(0, v.Scroll);
        v.Press("12gg"); Assert.Equal(11, v.Scroll);
    }

    [Fact]
    public void Chord_prefix_waits_and_single_key_still_works()
    {
        var v = View(LongDoc());
        v.Press("z");           // pending prefix (za / zc / zo)
        Assert.Equal(0, v.Scroll);
        v.Press("j");           // not a z-chord: falls back to plain j
        Assert.Equal(1, v.Scroll);
    }

    [Fact]
    public void Quit_sets_not_running()
    {
        var v = View("hi");
        Assert.True(v.IsRunning);
        v.Press("q");
        Assert.False(v.IsRunning);
    }

    [Fact]
    public void Search_finds_hits_jumps_and_counts()
    {
        var v = View(LongDoc());
        v.Press("/"); v.Type("Paragraph 4.2"); v.Press("enter");
        Assert.Equal(1, v.SearchHitCount);
        var rows = v.FrameText();
        Assert.Contains(rows, r => r.Contains("Paragraph 4.2"));
        // Status bar shows the persistent counter after Enter.
        Assert.Contains("1/1", rows[^1]);
    }

    [Fact]
    public void Search_is_smart_case_and_supports_regex_toggle()
    {
        var v = View("alpha Alpha ALPHA");
        v.Press("/"); v.Type("alpha"); v.Press("enter");
        Assert.Equal(3, v.SearchHitCount);
        v.Press("/"); v.Type("Alpha"); v.Press("enter");
        Assert.Equal(1, v.SearchHitCount);
        v.Press("/"); v.Type("^al[a-z]+"); v.Press("ctrl+r"); v.Press("enter");   // regex on, smart-case off
        Assert.Equal(1, v.SearchHitCount);     // anchored regex: only the first word
    }

    [Fact]
    public void Details_are_folded_by_default_and_search_unfolds_them()
    {
        var v = View(LongDoc());
        var rows = v.FrameText();
        Assert.DoesNotContain(v.VisibleLines, l => l.PlainText.Contains("Secret body"));
        Assert.Contains(v.VisibleLines, l => l.FoldPlaceholder != 0);
        v.Press("/"); v.Type("Secret"); v.Press("enter");
        Assert.Contains(v.VisibleLines, l => l.PlainText.Contains("Secret body"));
    }

    [Fact]
    public void Heading_section_folds_with_za_and_survives_reload()
    {
        var md = LongDoc();
        var v = View(md);
        // Scroll so "## Section 1" is the active (top) block.
        int idx = v.VisibleLines.ToList().FindIndex(l => l.PlainText.Contains("Section 1") && l.HeadingLevel == 2);
        v.Press($"{idx + 1}gg");
        Assert.Equal(idx, v.Scroll);
        int before = v.VisibleLines.Count;
        v.Press("za");
        Assert.True(v.VisibleLines.Count < before, "folding must hide the section body");
        Assert.Contains(v.VisibleLines, l => l.FoldPlaceholder != 0 && l.PlainText.Contains("folded"));
        // A live reload with an extra paragraph above keeps the fold.
        v.LoadMarkdown(v.CurrentPath, "New top paragraph.\n\n" + md, pushHistory: false);
        Assert.Contains(v.VisibleLines, l => l.FoldPlaceholder != 0);
        v.Press("zR");
        Assert.DoesNotContain(v.VisibleLines, l => l.FoldPlaceholder != 0 && l.PlainText.Contains("Section"));
    }

    [Fact]
    public void Reload_keeps_the_viewport_anchored_to_the_same_content()
    {
        var md = LongDoc();
        var v = View(md);
        v.Press("/"); v.Type("Section 4"); v.Press("enter");
        var topText = v.VisibleLines[v.Scroll].PlainText;
        v.LoadMarkdown(v.CurrentPath, "# Inserted\n\nA whole new paragraph above everything.\n\n" + md, pushHistory: false);
        Assert.Equal(topText, v.VisibleLines[v.Scroll].PlainText);
    }

    [Fact]
    public void Link_hints_label_visible_links_and_follow_on_selection()
    {
        var v = View(LongDoc());
        v.Press("f");
        Assert.True(v.InHintMode);
        Assert.Equal(2, v.Hints.Count);
        var frame = v.FrameText();
        Assert.Contains(frame, r => r.Contains("A") && r.Contains("link"));
        v.Press("esc");
        Assert.False(v.InHintMode);
    }

    [Fact]
    public void Hint_labels_are_unique_and_short()
    {
        var one = TerminalViewer.HintLabels(5);
        Assert.Equal(["a", "s", "d", "f", "g"], one);
        var many = TerminalViewer.HintLabels(60);
        Assert.Equal(60, many.Distinct().Count());
        Assert.All(many, l => Assert.Equal(2, l.Length));
    }

    [Fact]
    public void Tab_cycles_visible_links_and_status_shows_url()
    {
        var v = View(LongDoc());
        v.Press("tab");
        Assert.Contains("example.com", v.StatusMessage);
        v.Press("tab");
        Assert.Contains("other.md", v.StatusMessage);
    }

    [Fact]
    public void Block_motions_jump_between_headings_and_code()
    {
        var v = View(LongDoc());
        v.Press("]]");
        Assert.True(v.VisibleLines[v.Scroll].HeadingLevel == 2 && v.VisibleLines[v.Scroll].PlainText.Contains("Section 1"));
        v.Press("]]");
        Assert.Contains("Section 2", v.VisibleLines[v.Scroll].PlainText);
        v.Press("[[");
        Assert.Contains("Section 1", v.VisibleLines[v.Scroll].PlainText);
        v.Press("]c");
        Assert.Equal(BlockKind.Code, v.VisibleLines[v.Scroll].Kind);
    }

    [Fact]
    public void Yank_copies_code_without_frame_chrome()
    {
        string? copied = null;
        Clipboard.TestSink = t => copied = t;
        try
        {
            var v = View(LongDoc());
            v.Press("]c");
            v.Press("y");
            Assert.Equal("print('hello')\nx = 1", copied);
        }
        finally { Clipboard.TestSink = null; }
    }

    [Fact]
    public void Toc_overlay_filters_as_you_type_and_jumps()
    {
        var v = View(LongDoc());
        v.Press("t");
        var frame = v.FrameText();
        Assert.Contains(frame, r => r.Contains("Contents"));
        v.Type("Section 5");           // type-to-filter
        frame = v.FrameText();
        Assert.Contains(frame, r => r.Contains("Section 5"));
        Assert.DoesNotContain(frame, r => r.Contains("Section 4"));
        v.Press("enter");
        Assert.Contains("Section 5", v.VisibleLines[v.Scroll].PlainText);
    }

    [Fact]
    public void Breadcrumb_shows_current_section()
    {
        var v = View(LongDoc());
        v.Press("]]"); v.Press("]]"); v.Press("]]");   // "## Section 3" at the top
        var status = v.FrameText()[^1];
        Assert.Contains("Section 3", status);
    }

    [Fact]
    public void Help_overlay_opens_and_any_key_closes_it()
    {
        var v = View(LongDoc());
        v.Press("?");
        Assert.Contains(v.FrameText(), r => r.Contains("Keybindings"));
        v.Press("x");
        Assert.DoesNotContain(v.FrameText(), r => r.Contains("Keybindings"));
    }

    [Fact]
    public void Paste_in_normal_mode_is_ignored_but_fills_the_search_prompt()
    {
        var v = View(LongDoc());
        v.Press(new KeyEvent(KeyKind.Paste, Text: "q"));
        Assert.True(v.IsRunning);
        v.Press("/");
        v.Press(new KeyEvent(KeyKind.Paste, Text: "Paragraph 2.1\nignored"));
        v.Press("enter");
        Assert.Equal(1, v.SearchHitCount);
    }

    [Fact]
    public void Frame_text_renders_document_and_status_bar()
    {
        var v = View("# Hello\n\nWorld", 40, 6);
        var rows = v.FrameText();
        Assert.Equal(6, rows.Count);
        Assert.Contains("Hello", rows[0]);
        Assert.Contains("World", string.Join("\n", rows));
        Assert.Contains("capture.md", rows[^1]);
    }
}
