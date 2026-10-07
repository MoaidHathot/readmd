using Readmd.Core;
using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>
/// The files under samples/ are documentation that executes: these tests keep them honest as
/// the renderer evolves (every diagram kind maps, the config example validates, the wiki links
/// resolve, the slide deck splits as described).
/// </summary>
public class SampleFilesTests
{
    private static string Repo(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    [Fact]
    public void Diagram_zoo_covers_many_kinds_and_every_fence_is_recognised()
    {
        var markdown = File.ReadAllText(Repo("samples", "diagrams.md"));
        var (body, toml) = FrontMatter.StripToml(markdown);
        Assert.NotNull(toml);                                             // the TOML front matter is recognised
        Assert.Equal("Diagram zoo", toml.Get("title"));
        var requests = DiagramExtractor.Extract(Markdig.Markdown.Parse(body, TerminalPipeline.For(false)), body);
        var kinds = requests.Select(r => r.Kind).Distinct().ToList();
        Assert.True(kinds.Count >= 16, $"only {kinds.Count} kinds: {string.Join(", ", kinds)}");
        foreach (var k in new[] { DiagramKind.Ditaa, DiagramKind.Svgbob, DiagramKind.Pikchr, DiagramKind.Nomnoml, DiagramKind.Erd, DiagramKind.Dbml,
                                  DiagramKind.WaveDrom, DiagramKind.ByteField, DiagramKind.VegaLite, DiagramKind.BlockDiag, DiagramKind.SeqDiag,
                                  DiagramKind.ActDiag, DiagramKind.PacketDiag, DiagramKind.Graphviz, DiagramKind.C4PlantUml, DiagramKind.Mermaid })
            Assert.Contains(k, kinds);
        // The ::: mermaid container counts as mermaid too (two mermaid diagrams: the container and the broken one).
        Assert.True(requests.Count(r => r.Kind == DiagramKind.Mermaid) >= 2);
        // Offline print shows every source, never a bare caption.
        var text = DocumentTextRenderer.Render(markdown, dark: true, width: 100, color: false);
        Assert.Contains("defattrs", text);
        Assert.Contains("Table users", text);
        Assert.Contains("stateDiagram-v2", text);
    }

    [Fact]
    public void Config_example_validates_and_round_trips_its_themes()
    {
        var json = File.ReadAllText(Repo("samples", "config.example.json"));
        var issues = ConfigValidator.ValidateJson(json);
        Assert.True(issues.Count == 0, string.Join("\n", issues));
        var config = ReadmdConfig.Parse(json);
        var table = ThemeCatalog.Build(config);
        Assert.True(table.ContainsKey("ink") && table.ContainsKey("paper"));
        Assert.Equal(BuiltInThemes.TokyoNight.Background, table["ink"].Background);   // inherited via base
        Assert.Equal(TextMateSharp.Grammars.ThemeName.OneDark, table["ink"].CodeTheme);
        Assert.False(table["paper"].IsDark);
        Assert.Equal(["ink", "dark", "light", "solarized-light"], ThemeCatalog.Cycle(config, table));
        var keys = KeyMap.FromConfig(config.Keys);
        Assert.Equal(EditorAction.Slides, keys.Resolve("f5"));
        Assert.Equal(EditorAction.QuickOpen, keys.Resolve("ctrl+o"));
        Assert.Equal(EditorAction.None, keys.Resolve("ctrl+s"));   // default binding replaced
    }

    [Fact]
    public void Wiki_sample_links_resolve_inside_the_root()
    {
        var root = Repo("samples", "wiki");
        var files = WikiIndex.ListFiles(root);
        Assert.Equal(5, files.Count);
        Assert.Contains(files, f => f.Title == "Getting started");
        Assert.Contains(files, f => f.Title == "Configuration reference");
        Assert.True(WikiIndex.Search(root, "TODO").Select(h => h.Relative).Distinct().Count() >= 2);

        // Every relative .md link in every page points at an existing page.
        var link = new System.Text.RegularExpressions.Regex(@"\]\((?<t>[^)#:]+\.md)(?:#[^)]*)?\)");
        foreach (var f in files)
        {
            foreach (System.Text.RegularExpressions.Match m in link.Matches(File.ReadAllText(f.Path)))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f.Path)!, m.Groups["t"].Value));
                Assert.True(File.Exists(target), $"{f.Relative} links to a missing page {m.Groups["t"].Value}");
                Assert.True(new LinkResolver(root).IsInsideRoot(target), $"{f.Relative} links outside the root");
            }
        }
        Assert.Equal("guide/getting-started.md", WikiIndex.FuzzyFilter(files, "gs")[0].Relative);
    }

    [Fact]
    public void Demo_renders_fence_options_math_fence_and_mermaid_container()
    {
        var markdown = File.ReadAllText(Repo("samples", "demo.md"));
        var text = DocumentTextRenderer.Render(markdown, dark: true, width: 100, color: false);
        Assert.Contains("Program.cs", text);                                   // title= header
        Assert.Contains("n(n+1)", text);                                       // ```math fence
        Assert.Contains("Where the time goes", text);                          // ::: mermaid container source
        Assert.Contains("gutter switched off", text);                          // nonums block
        var body = FrontMatter.StripToml(markdown).Markdown;
        var requests = DiagramExtractor.Extract(Markdig.Markdown.Parse(body, TerminalPipeline.For(false)), body);
        Assert.Contains(requests, r => r.Kind == DiagramKind.Mermaid && r.Source.Contains("pie title"));
    }
}
