using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>The : command catalog is the single source for dispatch, Tab completion and :help commands.</summary>
public class CommandCatalogTests
{
    private static TerminalViewer V() => TerminalViewer.CreateHeadless("# T\n\nsome text\n\n```js\nlet x = 1;\n```\n\n## S\n\nmore\n", 100, 30);

    [Fact]
    public void Every_catalog_entry_and_alias_is_accepted_by_the_dispatcher()
    {
        var unknown = new List<string>();
        foreach (var cmd in TerminalViewer.CommandCatalog)
        {
            foreach (var name in cmd.AllNames)
            {
                if (cmd.Name == "browser") continue;                 // would launch a real browser
                var v = V();
                var saved = Glyphs.Ascii;
                try
                {
                    v.RunCommand(name);
                    if (v.StatusMessage?.StartsWith("Unknown command") == true) unknown.Add(name);
                }
                finally { Glyphs.Ascii = saved; }                    // :ascii flips a process-wide switch
            }
        }
        Assert.True(unknown.Count == 0, "catalog entries the dispatcher rejects: " + string.Join(", ", unknown));
        var w = V();
        w.RunCommand("frobnicate");
        Assert.StartsWith("Unknown command: frobnicate", w.StatusMessage);
        Assert.Contains(":help commands", w.StatusMessage);
    }

    [Fact]
    public void Catalog_is_well_formed()
    {
        var all = TerminalViewer.CommandCatalog.SelectMany(c => c.AllNames).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // no duplicate names/aliases
        Assert.All(TerminalViewer.CommandCatalog, c => Assert.False(string.IsNullOrWhiteSpace(c.Desc)));
        Assert.All(TerminalViewer.CommandCatalog, c => Assert.False(string.IsNullOrWhiteSpace(c.Group)));
        Assert.Equal("edit", TerminalViewer.ResolveCommand("E"));
        Assert.Equal("quit", TerminalViewer.ResolveCommand("exit"));
        Assert.Null(TerminalViewer.ResolveCommand("wrap"));          // a stale completion entry that was never a command
    }

    [Theory]
    [InlineData("exp", "export ")]        // unique prefix
    [InlineData("zo", "unfold ")]         // an exact alias expands to the canonical command
    [InlineData("zoo", "zoom ")]
    [InlineData("e", "edit ")]
    [InlineData("the", "theme ")]
    [InlineData("fold", "fold ")]         // exact name even though foldall also matches
    [InlineData("s", "source ")]          // alias
    [InlineData("x", "x")]                // no match
    [InlineData("theme n", "theme n")]    // arguments are never completed
    public void Tab_completion(string typed, string expected)
    {
        Assert.Equal(expected, TerminalViewer.CompleteCommand(typed));
    }

    [Fact]
    public void Help_commands_page_lists_every_command_and_tab_switches_pages()
    {
        var v = V();
        v.RunCommand("help commands");
        Assert.True(v.InHelpMode);
        Assert.Equal(1, v.HelpPage);
        var text = string.Join("\n", Scrolled(v));
        foreach (var cmd in TerminalViewer.CommandCatalog) Assert.Contains(":" + cmd.Name, text);
        Assert.Contains(":123", text);
        Assert.Contains("(:e :open :o)", text);
        v.Press("tab");
        Assert.Equal(0, v.HelpPage);
        Assert.Contains(v.FrameText(), r => r.Contains("Keybindings"));
        v.Press("tab");
        Assert.Equal(1, v.HelpPage);
        v.Press("esc");
        Assert.False(v.InHelpMode);
        // :commands is the shortcut; ? always opens on keys.
        v.RunCommand("commands"); Assert.Equal(1, v.HelpPage); v.Press("esc");
        v.Press("?"); Assert.Equal(0, v.HelpPage);
    }

    /// <summary>Every frame of the overlay from top to bottom, so content below the fold counts too.</summary>
    private static List<string> Scrolled(TerminalViewer v)
    {
        var rows = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            rows.AddRange(v.FrameText());
            var before = string.Join("", v.FrameText());
            v.Press("space");
            if (string.Join("", v.FrameText()) == before) break;
        }
        return rows;
    }
}