using Readmd.Core;
using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>Files & sources: glob/directory expansion, fuzzy quick-open, project search, remote URL
/// mapping and relative-link rewriting, the palette overlay, and multi-file stepping.</summary>
public class FilesAndSourcesTests : IDisposable
{
    private readonly string _root;

    public FilesAndSourcesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "readmd-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guide"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Project Readme\n\nSee [guide](docs/guide/intro.md).\n");
        File.WriteAllText(Path.Combine(_root, "docs", "guide", "intro.md"), "# Introduction\n\nhello searchable world\n");
        File.WriteAllText(Path.Combine(_root, "docs", "guide", "advanced.md"), "# Advanced Topics\n\nmore searchable text\n");
        File.WriteAllText(Path.Combine(_root, "docs", "notes.txt"), "not markdown searchable\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "README.md"), "# ignored\n");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Directory_argument_expands_to_markdown_files_skipping_noise()
    {
        var files = FileArguments.Expand(_root, out var isDir);
        Assert.True(isDir);
        Assert.Equal(3, files.Count);
        Assert.DoesNotContain(files, f => f.Contains("node_modules"));
        Assert.DoesNotContain(files, f => f.EndsWith(".txt"));
    }

    [Theory]
    [InlineData("docs/**/*.md", 2)]
    [InlineData("**/*.md", 3)]
    [InlineData("*.md", 1)]
    [InlineData("docs/guide/a*.md", 1)]
    public void Globs_expand_relative_to_their_literal_base(string pattern, int expected)
    {
        var old = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_root);
        try
        {
            var files = FileArguments.Expand(pattern, out var isDir);
            Assert.False(isDir);
            Assert.Equal(expected, files.Count);
        }
        finally { Directory.SetCurrentDirectory(old); }
    }

    [Theory]
    [InlineData("**/*.md", "a/b/c.md", true)]
    [InlineData("docs/*.md", "docs/x.md", true)]
    [InlineData("docs/*.md", "docs/sub/x.md", false)]
    [InlineData("*.md", "x.md", true)]
    [InlineData("*.md", "d/x.md", false)]
    [InlineData("docs/**", "docs/a/b.md", true)]
    public void Glob_matching_handles_double_star(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, FileArguments.GlobMatch(pattern, path));
    }

    [Fact]
    public void Noise_directories_are_skipped_only_in_the_wildcard_part()
    {
        var old = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_root);
        try
        {
            // Explicitly naming node_modules is honoured; sweeping over it with ** is not.
            Assert.Single(FileArguments.Expand("node_modules/pkg/*.md", out _));
            Assert.DoesNotContain(FileArguments.Expand("**/README.md", out _), f => f.Contains("node_modules"));
        }
        finally { Directory.SetCurrentDirectory(old); }
        // The filter is relative to the root, so a project that lives under a "dist" folder still lists.
        var distRoot = Path.Combine(_root, "dist");
        Directory.CreateDirectory(distRoot);
        File.WriteAllText(Path.Combine(distRoot, "inside.md"), "# Inside\n");
        Assert.Single(WikiIndex.ListFiles(distRoot));
        Assert.True(WikiIndex.IsNoisePath("node_modules/x/README.md"));
        Assert.False(WikiIndex.IsNoisePath("docs/binary/README.md"));
    }

    [Fact]
    public void Common_directory_of_several_files()
    {
        var common = FileArguments.CommonDirectory([Path.Combine(_root, "docs", "guide", "intro.md"), Path.Combine(_root, "README.md")]);
        Assert.Equal(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar), common.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Wiki_index_lists_titles_and_searches()
    {
        var files = WikiIndex.ListFiles(_root);
        Assert.Equal(3, files.Count);
        Assert.Contains(files, f => f.Title == "Introduction" && f.Relative == "docs/guide/intro.md");
        var hits = WikiIndex.Search(_root, "searchable");
        Assert.Equal(2, hits.Count);   // the .txt is not indexed
        Assert.All(hits, h => Assert.Contains("searchable", h.Snippet));
    }

    [Fact]
    public void Fuzzy_filter_prefers_segment_starts_and_file_names()
    {
        var files = WikiIndex.ListFiles(_root);
        var ranked = WikiIndex.FuzzyFilter(files, "adv");
        Assert.Equal("docs/guide/advanced.md", ranked[0].Relative);
        var intro = WikiIndex.FuzzyFilter(files, "dgi");   // d(ocs)/g(uide)/i(ntro)
        Assert.Equal("docs/guide/intro.md", intro[0].Relative);
        Assert.Empty(WikiIndex.FuzzyFilter(files, "zzz"));
        Assert.Equal(int.MinValue, WikiIndex.FuzzyScore("abc", "x"));
    }

    [Theory]
    [InlineData("gh:owner/repo", "https://raw.githubusercontent.com/owner/repo/HEAD/README.md")]
    [InlineData("gh:owner/repo@v1.2/docs/a.md", "https://raw.githubusercontent.com/owner/repo/v1.2/docs/a.md")]
    [InlineData("github:owner/repo/README.md", "https://raw.githubusercontent.com/owner/repo/HEAD/README.md")]
    [InlineData("https://github.com/owner/repo/blob/main/docs/a.md", "https://raw.githubusercontent.com/owner/repo/main/docs/a.md")]
    [InlineData("https://github.com/owner/repo", "https://raw.githubusercontent.com/owner/repo/HEAD/README.md")]
    [InlineData("https://gitlab.com/g/p/-/blob/main/README.md", "https://gitlab.com/g/p/-/raw/main/README.md")]
    [InlineData("https://example.com/x/doc.md", "https://example.com/x/doc.md")]
    public void Remote_arguments_map_to_raw_urls(string arg, string expected)
    {
        Assert.True(RemoteSource.IsRemote(arg));
        Assert.Equal(expected, RemoteSource.ToRawUrl(arg).AbsoluteUri);
    }

    [Fact]
    public void Relative_references_are_rewritten_against_the_base_outside_code_fences()
    {
        var md = "![a](img/a.png) [b](../b.md) [c](#frag) [d](https://x.y/z) <img src=\"pic.svg\">\n\n[ref]: docs/r.md\n\n```\n[not](rel.md)\n```\n";
        var outp = RemoteSource.RewriteRelativeReferences(md, new Uri("https://raw.githubusercontent.com/o/r/HEAD/docs/"));
        Assert.Contains("![a](https://raw.githubusercontent.com/o/r/HEAD/docs/img/a.png)", outp);
        Assert.Contains("[b](https://raw.githubusercontent.com/o/r/HEAD/b.md)", outp);
        Assert.Contains("[c](#frag)", outp);
        Assert.Contains("[d](https://x.y/z)", outp);
        Assert.Contains("src=\"https://raw.githubusercontent.com/o/r/HEAD/docs/pic.svg\"", outp);
        Assert.Contains("[ref]: https://raw.githubusercontent.com/o/r/HEAD/docs/docs/r.md", outp);
        Assert.Contains("[not](rel.md)", outp);   // inside the fence: untouched
    }

    private TerminalViewer Open(string file, int width = 90, int height = 24)
    {
        var options = new TerminalViewerOptions { FilePath = file, Root = _root, DarkTerminal = true, SynchronousHighlighting = true, RememberPosition = false };
        return TerminalViewer.CreateHeadless(File.ReadAllText(file), width, height, options: options, path: file);
    }

    [Fact]
    public void Quick_open_palette_lists_filters_and_opens()
    {
        var v = Open(Path.Combine(_root, "README.md"));
        v.Press("ctrl+p");
        var rows = v.FrameText();
        Assert.Contains(rows, r => r.Contains("Open file"));
        Assert.Contains(rows, r => r.Contains("docs/guide/intro.md"));
        v.Type("adv");
        rows = v.FrameText();
        Assert.Contains(rows, r => r.Contains("advanced.md"));
        Assert.DoesNotContain(rows, r => r.Contains("intro.md"));
        v.Press("enter");
        // Navigation is async (file read); wait for it.
        SpinWait.SpinUntil(() => v.CurrentPath.EndsWith("advanced.md", StringComparison.OrdinalIgnoreCase), 3000);
        Assert.EndsWith("advanced.md", v.CurrentPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_search_palette_finds_hits_across_files()
    {
        var v = Open(Path.Combine(_root, "README.md"));
        v.Press("ctrl+g");
        v.Type("searchable");
        // The search is debounced + async; give it a moment.
        SpinWait.SpinUntil(() => v.FrameText().Any(r => r.Contains("intro.md:3")), 3000);
        var rows = v.FrameText();
        Assert.Contains(rows, r => r.Contains("intro.md:3"));
        Assert.Contains(rows, r => r.Contains("advanced.md:3"));
        v.Press("esc");
        Assert.DoesNotContain(v.FrameText(), r => r.Contains("Search all files"));
    }

    [Fact]
    public void Next_file_steps_through_sibling_markdown_files()
    {
        var intro = Path.Combine(_root, "docs", "guide", "intro.md");
        var v = Open(intro, 80, 20);
        v.Press("]f");
        SpinWait.SpinUntil(() => !v.CurrentPath.EndsWith("intro.md"), 3000);
        Assert.EndsWith("advanced.md", v.CurrentPath, StringComparison.OrdinalIgnoreCase);
        v.Press("[f");
        SpinWait.SpinUntil(() => v.CurrentPath.EndsWith("intro.md"), 3000);
        Assert.EndsWith("intro.md", v.CurrentPath, StringComparison.OrdinalIgnoreCase);
    }
}
