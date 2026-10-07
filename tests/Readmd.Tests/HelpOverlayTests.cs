using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>The ? overlay must document every bound action and must never silently cut content off.</summary>
public class HelpOverlayTests
{
    private static TerminalViewer Open(int w, int h)
    {
        var v = TerminalViewer.CreateHeadless("# Title\n\nSome text.\n\n## Section\n\nmore\n", w, h);
        v.Press("?");
        return v;
    }

    [Fact]
    public void Every_default_bound_action_is_documented()
    {
        var v = Open(120, 40);
        var text = string.Join("\n", v.HelpEntries());
        var keyMap = KeyMap.Default;
        var missing = new List<string>();
        foreach (var action in Enum.GetValues<EditorAction>())
        {
            if (action == EditorAction.None) continue;
            var chords = keyMap.ChordsFor(action);
            if (chords.Count == 0) continue;
            // The overlay shows the primary binding's display label (as KeyMap.Label does), or a compressed
            // pair such as "Ctrl-d/u"; accept either form.
            var label = KeyMap.Display(chords[0]);
            string compressed = label.Length >= 2 && label[^2] == '-' ? label[..^2] + "-" : label;
            bool shown = text.Contains(label, StringComparison.Ordinal)
                      || (label.StartsWith("Ctrl-") && text.Contains(compressed + label[^1] + "/", StringComparison.Ordinal))
                      || (label.StartsWith("Ctrl-") && System.Text.RegularExpressions.Regex.IsMatch(text, $@"Ctrl-\w/{System.Text.RegularExpressions.Regex.Escape(label[^1].ToString())}\b"));
            if (!shown) missing.Add($"{action} ({label})");
        }
        Assert.True(missing.Count == 0, "not in the help overlay: " + string.Join(", ", missing));
    }

    [Fact]
    public void Mark_mode_and_quit_are_reachable_in_a_small_terminal()
    {
        var v = Open(80, 24);
        var first = v.FrameText();
        Assert.Contains(first, r => r.Contains("Keybindings"));
        Assert.Contains(first, r => r.Contains("▾"));                 // more below is announced
        Assert.DoesNotContain(first, r => r.Contains("mark mode"));   // too short to show it all at once…
        v.Press("G");                                                  // …but scrolling reaches it
        var last = v.FrameText();
        Assert.Contains(last, r => r.Contains("mark mode"));
        Assert.Contains(last, r => r.Contains("this help / quit"));
        Assert.Contains(last, r => r.Contains("▴"));
        Assert.True(v.InHelpMode);                                     // scrolling keys don't dismiss
        v.Press("gg");
        Assert.Contains(v.FrameText(), r => r.Contains("line down / up"));
        v.Press("esc");
        Assert.False(v.InHelpMode);
    }

    [Theory]
    [InlineData(100, 30, 2)]
    [InlineData(120, 40, 2)]
    [InlineData(160, 50, 3)]
    public void Columns_adapt_to_the_width(int w, int h, int expectedColumns)
    {
        var v = Open(w, h);
        var rows = v.FrameText();
        // Count section headings on the row that holds "M O V E": one per column.
        var moveRow = rows.First(r => r.Contains("M O V E"));
        int headings = System.Text.RegularExpressions.Regex.Matches(moveRow, @"[A-Z](?: [A-Z])+(?:   [A-Z](?: [A-Z])+)*").Count;
        Assert.True(headings >= expectedColumns, $"expected {expectedColumns} columns at {w}x{h}, heading row: {moveRow.Trim()}");
        // Nothing is truncated when it all fits; otherwise the footer says so.
        bool fits = rows.Any(r => r.Contains("esc  or  any key to close"));
        if (!fits) Assert.Contains(rows, r => r.Contains("▾"));
        else Assert.Contains(rows, r => r.Contains("this help / quit"));
    }

    [Fact]
    public void Any_non_scroll_key_still_closes_and_slides_variant_differs()
    {
        var v = Open(100, 30);
        v.Press("t");
        Assert.False(v.InHelpMode);
        v.RunCommand("slides"); v.ExpireStatus();
        v.Press("?");
        Assert.Contains(v.HelpEntries(), e => e.Contains("leave slides"));
    }

    [Fact]
    public void Status_bar_hint_mentions_mark_mode_on_wide_terminals()
    {
        var v = TerminalViewer.CreateHeadless("# T\n\ntext\n", 160, 20);
        var bar = v.FrameText()[^1];
        Assert.Contains("m:mark", bar);
        Assert.Contains("Ctrl-p:files", bar);
        Assert.Contains("?:help", bar);
    }

    [Fact]
    public void Key_labels_capitalise_named_keys_after_modifiers()
    {
        Assert.Equal("Shift-Tab", KeyMap.Display("shift+tab"));
        Assert.Equal("Ctrl-PgDn", KeyMap.Display("ctrl+pagedown"));
        Assert.Equal("Ctrl-d", KeyMap.Display("ctrl+d"));
        Assert.Equal("Alt-←", KeyMap.Display("alt+left"));
        Assert.Equal("gg", KeyMap.Display("g g"));
        Assert.Equal("Ctrl-+", KeyMap.Display("ctrl++"));
    }
}