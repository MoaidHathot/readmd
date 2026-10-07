using Readmd.Core;
using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>Presentation mode: SlideDeck splitting rules and the viewer's slide navigation.</summary>
public class SlideTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    [Fact]
    public void Splits_on_thematic_breaks_and_slide_comments_but_not_setext_or_fences()
    {
        var md = "# One\n\ntext\n\n---\n\n## Two\n\nA setext heading\n---\n\n```\n---\n<!-- slide -->\n```\n\n<!-- slide -->\n\n## Three\n";
        var deck = SlideDeck.Split(md);
        Assert.Equal(3, deck.Count);
        Assert.Equal("One", deck[0].Title);
        Assert.Equal("Two", deck[1].Title);
        Assert.Contains("A setext heading\n---", deck[1].Markdown);      // setext underline kept
        Assert.Contains("```\n---\n<!-- slide -->\n```", deck[1].Markdown); // fence content kept
        Assert.Equal("Three", deck[2].Title);
        Assert.Equal(1, deck[0].SourceLine);
        Assert.Equal(6, deck[1].SourceLine);   // the line after the separator
    }

    [Fact]
    public void Front_matter_stays_with_the_first_slide_and_a_leading_separator_makes_no_empty_slide()
    {
        var md = "---\ntitle: Deck\n---\n\n# Intro\n\n---\n\n# Next\n";
        var deck = SlideDeck.Split(md);
        Assert.Equal(2, deck.Count);
        Assert.StartsWith("---\ntitle: Deck\n---", deck[0].Markdown);
        Assert.Equal("Intro", deck[0].Title);
        var leading = SlideDeck.Split("---\n\n# A\n\n---\n\n# B\n");
        Assert.Equal(2, leading.Count);
        Assert.Equal("A", leading[0].Title);
    }

    [Fact]
    public void Pauses_make_cumulative_fragments_and_notes_are_extracted()
    {
        var md = "# T\n\n- one\n\n<!-- pause -->\n\n- two\n\n<!-- pause -->\n\n- three\n\n<!-- notes: say hi -->\n<!-- notes\nsecond line\nthird\n-->\n";
        var deck = SlideDeck.Split(md);
        var s = deck[0];
        Assert.Equal(3, s.Fragments.Count);
        Assert.DoesNotContain("pause", s.Markdown);
        Assert.DoesNotContain("notes", s.Markdown);
        Assert.Contains("- one", s.MarkdownUpTo(0));
        Assert.DoesNotContain("- two", s.MarkdownUpTo(0));
        Assert.Contains("- two", s.MarkdownUpTo(1));
        Assert.DoesNotContain("- three", s.MarkdownUpTo(1));
        Assert.Equal(s.Markdown, s.MarkdownUpTo(2));
        Assert.Equal("say hi\nsecond line\nthird", s.Notes);
    }

    [Fact]
    public void Remark_style_notes_and_title_fallbacks()
    {
        var deck = SlideDeck.Split("Just a paragraph of text.\n\n???\n\nspeaker only\n\n---\n\n\n\n---\n\n![img](x.png)\n");
        Assert.Equal(2, deck.Count);   // the blank slide between separators is dropped
        Assert.Equal("Just a paragraph of text.", deck[0].Title);
        Assert.Equal("speaker only", deck[0].Notes);
        Assert.DoesNotContain("???", deck[0].Markdown);
        Assert.Equal("![img](x.png)", deck[1].Title);
        Assert.Single(SlideDeck.Split("").Slides);   // never zero slides
        Assert.Equal(1, SlideDeck.Split("# A\n\n---\n\n# B\n").IndexForSourceLine(6));
    }

    [Fact]
    public void Sample_deck_splits_as_documented()
    {
        var deck = SlideDeck.Split(File.ReadAllText(RepoFile("samples", "slides.md")));
        Assert.Equal(5, deck.Count);
        Assert.Equal(3, deck[1].Fragments.Count);
        Assert.NotNull(deck[0].Notes);
        Assert.NotNull(deck[2].Notes);
        Assert.Contains("Remark-style", deck[3].Notes);
        Assert.Contains("A setext heading\n---", deck[3].Markdown);
    }

    private static TerminalViewer Deck(bool start = true)
    {
        var md = "# One\n\nfirst\n\n---\n\n## Two\n\n- a\n\n<!-- pause -->\n\n- b\n\n<!-- notes: remember b -->\n\n---\n\n## Three\n\nlast\n";
        var options = new TerminalViewerOptions { FilePath = "deck.md", DarkTerminal = true, SynchronousHighlighting = true, RememberPosition = false };
        var v = TerminalViewer.CreateHeadless(md, 60, 16, options: options);
        if (start) { v.RunCommand("slides"); v.ExpireStatus(); }
        return v;
    }

    [Fact]
    public void Viewer_enters_slides_and_centres_one_slide_at_a_time()
    {
        var v = Deck();
        Assert.True(v.InSlideMode);
        Assert.Equal(3, v.SlideCount);
        var rows = v.FrameText();
        Assert.Contains(rows, r => r.Contains("One"));
        Assert.DoesNotContain(rows, r => r.Contains("Two"));
        // Vertically centred: the title is not on the first row.
        Assert.False(rows[0].Contains("One"));
        Assert.Contains("Slide 1/3", rows[^1]);
    }

    [Fact]
    public void Navigation_steps_through_fragments_then_slides()
    {
        var v = Deck();
        v.Press("n");
        v.ExpireStatus();
        Assert.Equal(1, v.SlideIndex);
        Assert.Equal(0, v.SlideStep);
        Assert.Contains(v.FrameText(), r => r.Contains("a"));
        Assert.DoesNotContain(v.FrameText(), r => r.Trim().EndsWith("b"));
        Assert.Contains("step 1/2", v.FrameText()[^1]);
        v.Press("space");                                  // PageDown action → next step
        Assert.Equal(1, v.SlideStep);
        Assert.Contains(v.FrameText(), r => r.Trim().EndsWith("b"));
        v.Press("right");                                  // Forward action → next slide
        Assert.Equal(2, v.SlideIndex);
        v.Press("n");
        Assert.Equal("End of slides", v.StatusMessage);
        v.Press("p");                                      // back lands on the previous slide fully revealed
        Assert.Equal(1, v.SlideIndex);
        Assert.Equal(1, v.SlideStep);
        v.Press("gg");
        Assert.Equal(0, v.SlideIndex);
        v.Press("3gg");
        Assert.Equal(2, v.SlideIndex);
        v.RunCommand("2");
        Assert.Equal(1, v.SlideIndex);
        Assert.Equal(0, v.SlideStep);
    }

    [Fact]
    public void Notes_overlay_toggles_and_exit_returns_to_the_document()
    {
        var v = Deck();
        v.Press("n");
        v.Press("N");
        Assert.True(v.NotesShown);
        Assert.Contains(v.FrameText(), r => r.Contains("remember b"));
        v.Press("esc");
        Assert.False(v.NotesShown);
        v.RunCommand("slides");
        Assert.False(v.InSlideMode);
        var rows = v.FrameText();
        // Back in the document, positioned at slide two's heading, with the whole document laid out.
        Assert.Contains(rows, r => r.Contains("Two"));
        Assert.True(v.AllLines.Count > 10);
    }

    [Fact]
    public void Start_in_slides_option_and_reload_keep_the_slide()
    {
        var md = "# One\n\n---\n\n# Two\n\n---\n\n# Three\n";
        var options = new TerminalViewerOptions { FilePath = "deck.md", DarkTerminal = true, SynchronousHighlighting = true, RememberPosition = false, StartInSlides = true };
        var v = TerminalViewer.CreateHeadless(md, 60, 12, options: options);
        v.RunCommand("slides on");
        v.Press("n");
        Assert.Equal(1, v.SlideIndex);
        // A reload that adds a slide keeps us on slide 2.
        v.LoadMarkdown(v.CurrentPath, md + "\n---\n\n# Four\n", pushHistory: false);
        Assert.True(v.InSlideMode);
        Assert.Equal(4, v.SlideCount);
        Assert.Equal(1, v.SlideIndex);
        Assert.Contains(v.FrameText(), r => r.Contains("Two"));
    }
}
