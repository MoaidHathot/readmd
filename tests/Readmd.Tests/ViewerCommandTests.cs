using Readmd.Core;
using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>Command line, bookmarks, sidebar, persisted state, offline mode, start position.</summary>
public class ViewerCommandTests
{
    private static string Doc()
    {
        var sb = new System.Text.StringBuilder("# Doc\n\n");
        for (int s = 1; s <= 5; s++)
        {
            sb.Append($"## Part {s}\n\n");
            for (int p = 1; p <= 6; p++) sb.Append($"Line {s}.{p} text here.\n\n");
        }
        return sb.ToString();
    }

    private static TerminalViewer View(string md, int width = 80, int height = 16) =>
        TerminalViewer.CreateHeadless(md, width, height);

    [Fact]
    public void Colon_number_goes_to_source_line()
    {
        var v = View(Doc());
        v.Press(":"); v.Type("20"); v.Press("enter");
        Assert.True(v.Scroll > 0);
        Assert.True(v.VisibleLines[v.Scroll].SourceLine >= 20 || v.VisibleLines[v.Scroll + 1].SourceLine >= 20);
    }

    [Fact]
    public void Colon_q_quits_and_unknown_commands_report()
    {
        var v = View(Doc());
        v.Press(":"); v.Type("frobnicate"); v.Press("enter");
        Assert.Contains("Unknown command", v.StatusMessage);
        Assert.True(v.IsRunning);
        v.Press(":"); v.Type("q"); v.Press("enter");
        Assert.False(v.IsRunning);
    }

    [Fact]
    public void Colon_set_width_relayouts_narrower()
    {
        var v = View(Doc(), width: 100);
        int before = v.VisibleLines.Max(l => l.PlainText.Length);
        v.RunCommand("set width=40");
        Assert.True(v.VisibleLines.All(l => l.PlainText.Length <= 40), "lines must fit the new width");
        Assert.True(before > 40);
    }

    [Fact]
    public void Colon_theme_switches_palette()
    {
        var v = View(Doc());
        v.RunCommand("theme light");
        Assert.Contains("light", v.StatusMessage);
        v.RunCommand("theme nope");
        Assert.Contains("Unknown theme", v.StatusMessage);
    }

    [Fact]
    public void Command_history_and_tab_completion()
    {
        var v = View(Doc());
        v.Press(":"); v.Type("fold"); v.Press("tab");
        var rows = v.FrameText();
        Assert.Contains(rows[^1], s => true);
        Assert.Contains(":fold", rows[^1]);
        v.Press("esc");
        v.Press(":"); v.Type("unfoldall"); v.Press("enter");
        v.Press(":"); v.Press("up");
        Assert.Contains(":unfoldall", v.FrameText()[^1]);
        v.Press("esc");
    }

    [Fact]
    public void Bookmarks_set_and_jump()
    {
        var v = View(Doc());
        v.Press("10gg");
        v.Press("M"); v.Press("a");
        Assert.Contains("Mark 'a' set", v.StatusMessage);
        v.Press("gg");
        Assert.Equal(0, v.Scroll);
        v.Press("'"); v.Press("a");
        Assert.Equal(9, v.Scroll);
        v.Press("'"); v.Press("'");   // '' returns to where we jumped from
        Assert.Equal(0, v.Scroll);
    }

    [Fact]
    public void Sidebar_toggles_and_lists_headings()
    {
        var v = View(Doc(), width: 100);
        v.Press("T");
        var rows = v.FrameText();
        Assert.Contains(rows, r => r.StartsWith(" ") && r.Contains("Part 1") && r.Contains("│"));
        // Content is offset to the right of the sidebar.
        Assert.Contains(rows, r => r.Contains("│") && r.Contains("Line 1.1"));
        v.Press("T");
        rows = v.FrameText();
        Assert.Contains(rows, r => r.StartsWith("Line 1.1"));
    }

    [Fact]
    public void Scrollbar_draws_thumb_when_document_overflows()
    {
        var v = View(Doc(), width: 60, height: 10);
        var frame = v.Frame();
        Assert.Contains("┃", frame);
        var small = View("short", width: 60, height: 10).Frame();
        Assert.DoesNotContain("┃", small);
    }

    [Fact]
    public void Start_line_and_start_search_position_the_viewport()
    {
        var byLine = TerminalViewer.CreateHeadless(Doc(), 80, 12, options: new TerminalViewerOptions { FilePath = "capture.md", SynchronousHighlighting = true, RememberPosition = false, StartLine = 30 });
        byLine.ApplyStartPosition();
        Assert.True(byLine.Scroll > 0);
        var bySearch = TerminalViewer.CreateHeadless(Doc(), 80, 12, options: new TerminalViewerOptions { FilePath = "capture.md", SynchronousHighlighting = true, RememberPosition = false, StartSearch = "Line 4.2" });
        bySearch.ApplyStartPosition();
        Assert.Equal(1, bySearch.SearchHitCount);
        Assert.Contains(bySearch.VisibleLines.Skip(bySearch.Scroll).Take(12), l => l.PlainText.Contains("Line 4.2"));
    }

    [Fact]
    public void State_store_round_trips_positions_marks_and_history()
    {
        var path = Path.Combine(Path.GetTempPath(), "readmd-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new StateStore(path);
            store.SetPosition(@"C:\docs\a.md", 42, "Some top line", 17);
            store.SetBookmark(@"C:\docs\a.md", "b", 99);
            store.AddSearch("alpha");
            store.AddSearch("beta");
            store.AddSearch("alpha");   // moves to the end, no duplicate
            store.Save();
            Assert.True(File.Exists(path));

            var again = new StateStore(path);
            var s = again.Get(@"C:\docs\a.md");
            Assert.NotNull(s);
            Assert.Equal(42, s!.Scroll);
            Assert.Equal("Some top line", s.TopText);
            Assert.Equal(17, s.SourceLine);
            Assert.Equal(99, s.Bookmarks!["b"]);
            Assert.Equal(["beta", "alpha"], again.SearchHistory);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Corrupt_state_file_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "readmd-state-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var store = new StateStore(path);
            Assert.Null(store.Get("x.md"));
            Assert.Empty(store.SearchHistory);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Offline_image_loader_refuses_remote_fetches()
    {
        await using var loader = new Readmd.Diagrams.ImageLoader(Directory.GetCurrentDirectory()) { AllowRemote = false };
        var r = await loader.LoadAsync("https://example.com/x.png", Path.Combine(Directory.GetCurrentDirectory(), "doc.md"));
        Assert.Equal(DiagramStatus.Failed, r.Status);
        Assert.Contains("offline", r.Error);
    }

    [Fact]
    public void Follow_and_watch_toggles_report_state()
    {
        var v = View(Doc());
        v.Press("W");
        Assert.Contains("paused", v.StatusMessage);
        v.ExpireStatus();
        Assert.Contains("[paused]", v.FrameText()[^1]);
        v.Press("W");
        Assert.Contains("on", v.StatusMessage);
    }

    [Fact]
    public void Yank_table_copies_tab_separated_text()
    {
        string? copied = null;
        Clipboard.TestSink = t => copied = t;
        try
        {
            var v = View("| a | b |\n|---|---|\n| 1 | 2 |\n\ntext");
            v.Press("y");
            Assert.NotNull(copied);
            Assert.Equal("a\tb\n1\t2", copied);
            Assert.DoesNotContain("┌", copied);
        }
        finally { Clipboard.TestSink = null; }
    }
}
