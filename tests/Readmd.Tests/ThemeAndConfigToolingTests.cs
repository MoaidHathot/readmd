using System.Text.Json;
using Readmd.Core;
using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>Built-in themes, the theme catalog, config validation and the JSON Schema staying in sync with the model.</summary>
public class ThemeAndConfigToolingTests
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
    public void Built_in_themes_are_complete_and_distinct()
    {
        Assert.True(BuiltInThemes.Names.Count >= 10);
        Assert.Equal("dark", BuiltInThemes.Names[0]);
        Assert.Equal("light", BuiltInThemes.Names[1]);
        foreach (var name in BuiltInThemes.Names)
        {
            var t = BuiltInThemes.All[name];
            Assert.Equal(name, t.Name);
            // Legible: text must contrast with the background in the direction the theme claims.
            Assert.Equal(t.IsDark, t.Background.Luma < t.Text.Luma);
            Assert.True(Math.Abs(t.Background.Luma - t.Text.Luma) > 80, $"{name}: text/background contrast too low");
        }
        Assert.Same(BuiltInThemes.Nord, BuiltInThemes.Find("NORD"));
        Assert.Null(BuiltInThemes.Find("nope"));
        Assert.Equal(TextMateSharp.Grammars.ThemeName.Dracula, BuiltInThemes.Dracula.EffectiveCodeTheme);
        Assert.Equal(TextMateSharp.Grammars.ThemeName.LightPlus, TerminalTheme.Light.EffectiveCodeTheme);
    }

    [Fact]
    public void Custom_theme_inherits_from_a_built_in_base()
    {
        var ct = new ColorTheme { Base = "solarized-light", Link = "#ff0000", CodeTheme = "one-dark" };
        var t = TerminalTheme.FromColorTheme(ct, "mine");
        Assert.Equal("mine", t.Name);
        Assert.False(t.IsDark);                                           // follows the base when "dark" is unset
        Assert.Equal(BuiltInThemes.SolarizedLight.Background, t.Background);
        Assert.Equal("#ff0000", TerminalTheme.Hex(t.Link));
        Assert.Equal(TextMateSharp.Grammars.ThemeName.OneDark, t.CodeTheme);
        // Explicit dark flag wins over the base.
        var forced = TerminalTheme.FromColorTheme(new ColorTheme { Base = "solarized-light", Dark = true }, "x");
        Assert.True(forced.IsDark);
        // No base: dark/light default, bad hex falls back silently (validation reports it instead).
        var plain = TerminalTheme.FromColorTheme(new ColorTheme { Text = "not-a-colour" }, "p");
        Assert.True(plain.IsDark);
        Assert.Equal(TerminalTheme.Dark.Text, plain.Text);
    }

    [Theory]
    [InlineData("dracula", "Dracula")]
    [InlineData("one-dark", "OneDark")]
    [InlineData("Solarized_Light", "SolarizedLight")]
    [InlineData("dark-plus", "DarkPlus")]
    [InlineData("dark", "Dark")]
    [InlineData("nonsense", null)]
    public void Code_theme_names_parse_loosely(string input, string? expected)
    {
        var parsed = TerminalTheme.ParseCodeTheme(input);
        Assert.Equal(expected, parsed?.ToString());
    }

    [Fact]
    public void Theme_catalog_puts_built_ins_first_and_honours_the_cycle()
    {
        var config = ReadmdConfig.Parse("""{ "themes": { "mine": { "base": "nord" } }, "themeCycle": ["light", "mine", "bogus", "light"] }""");
        var table = ThemeCatalog.Build(config);
        Assert.Equal(BuiltInThemes.Names.Count + 1, table.Count);
        Assert.Equal("mine", table["mine"].Name);
        var cycle = ThemeCatalog.Cycle(config, table);
        Assert.Equal(["light", "mine"], cycle);
        Assert.Equal(table.Keys.ToList(), ThemeCatalog.Cycle(ReadmdConfig.Empty, table));
    }

    [Fact]
    public void Viewer_cycles_through_named_themes_and_sets_by_name()
    {
        var config = ReadmdConfig.Parse("""{ "themeCycle": ["dark", "nord", "light"] }""");
        var table = ThemeCatalog.Build(config);
        var options = new TerminalViewerOptions
        {
            FilePath = "t.md", DarkTerminal = true, SynchronousHighlighting = true, RememberPosition = false,
            NamedThemes = table, ThemeCycle = ThemeCatalog.Cycle(config, table),
        };
        var v = TerminalViewer.CreateHeadless("# Title\n\ntext\n", 60, 10, options: options);
        // "[" is also a prefix ("[[", "[c", …): it fires when the chord timeout expires.
        v.Press("["); v.FlushPendingChord();
        Assert.Equal("Theme: nord", v.StatusMessage);
        v.Press("["); v.FlushPendingChord();
        Assert.Equal("Theme: light", v.StatusMessage);
        v.Press("["); v.FlushPendingChord();
        Assert.Equal("Theme: dark", v.StatusMessage);    // wraps around
        v.RunCommand("theme dracula");                   // any named theme, not just the cycle
        Assert.Equal("Theme: dracula", v.StatusMessage);
        v.RunCommand("theme nope");
        Assert.StartsWith("Unknown theme: nope", v.StatusMessage);
    }

    [Fact]
    public void Validator_reports_unknown_keys_bad_values_and_suggestions()
    {
        var json = """
            {
              "theme": "nrod", "themeCycl": ["dark"], "graphics": "sixl", "tabSize": 40, "wheelLines": 0,
              "krokiUrl": "kroki.io", "color": "lots", "background": "blue",
              "themes": { "mine": { "base": "nordd", "link": "#12", "codeTheme": "foo", "lnk": "#fff" } },
              "keys": { "quitt": "x", "pageDown": "ctrl+shift+f", "yank": "hyper+x" }
            }
            """;
        var issues = ConfigValidator.ValidateJson(json);
        string All(ConfigValidator.Severity s) => string.Join("\n", issues.Where(i => i.Severity == s).Select(i => i.ToString()));
        var errors = All(ConfigValidator.Severity.Error);
        var warnings = All(ConfigValidator.Severity.Warning);
        Assert.Contains("themeCycl: unknown key (did you mean 'themeCycle'?)", warnings);
        Assert.Contains("themes.mine.lnk: unknown theme key (did you mean 'link'?)", warnings);
        Assert.Contains("themes.mine.codeTheme: unknown TextMate theme 'foo'", warnings);
        Assert.Contains("theme: unknown theme 'nrod' (did you mean 'nord'?)", errors);
        Assert.Contains("graphics: 'sixl'", errors);
        Assert.Contains("tabSize:", errors);
        Assert.Contains("wheelLines:", errors);
        Assert.Contains("krokiUrl:", errors);
        Assert.Contains("color: 'lots'", errors);
        Assert.Contains("background: 'blue'", errors);
        Assert.Contains("themes.mine.base: unknown built-in theme 'nordd' (did you mean 'nord'?)", errors);
        Assert.Contains("themes.mine.link: '#12'", errors);
        Assert.Contains("keys.quitt: unknown action (did you mean 'quit'?)", errors);
        Assert.Contains("keys.yank: 'hyper+x'", errors);
        Assert.DoesNotContain("pageDown", errors);   // ctrl+shift+f is a valid chord
    }

    [Fact]
    public void Validator_accepts_a_good_config_and_flags_invalid_json()
    {
        var good = """{ "$schema": "x", "theme": "nord", "themeCycle": ["dark", "light"], "maxWidth": 100, "themes": { "a": { "base": "dracula", "text": "#fff" } }, "keys": { "quit": ["q", "<C-c>"] } }""";
        Assert.Empty(ConfigValidator.ValidateJson(good));
        var broken = ConfigValidator.ValidateJson("{ \"theme\": ");
        Assert.Single(broken);
        Assert.Equal(ConfigValidator.Severity.Error, broken[0].Severity);
        var wrongType = ConfigValidator.ValidateJson("{ \"tabSize\": \"four\" }");
        Assert.Contains(wrongType, i => i.Severity == ConfigValidator.Severity.Error);
    }

    [Fact]
    public void Json_schema_matches_the_config_model()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("schema", "readmd.schema.json")));
        var root = doc.RootElement;
        var schemaKeys = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var modelKeys = ConfigValidator.KnownKeys.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(modelKeys.Except(schemaKeys));   // every config property is documented
        Assert.Empty(schemaKeys.Except(modelKeys));   // the schema documents nothing the model lacks
        var themeKeys = root.GetProperty("definitions").GetProperty("colorTheme").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ConfigValidator.KnownThemeKeys.ToHashSet(StringComparer.Ordinal), themeKeys);
        // Every key action the schema lists is a real action, and every action is listed.
        var schemaActions = root.GetProperty("properties").GetProperty("keys").GetProperty("propertyNames").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        foreach (var a in schemaActions) Assert.True(KeyMap.TryParseAction(a, out _), $"schema lists unknown action {a}");
        var actions = Enum.GetNames<EditorAction>().Where(n => n != "None").ToList();
        Assert.Equal(actions.Count, schemaActions.Count);
        // Built-in theme names mentioned in the description all exist.
        var desc = root.GetProperty("properties").GetProperty("theme").GetProperty("description").GetString()!;
        foreach (var name in BuiltInThemes.Names.Skip(2)) Assert.Contains(name, desc);
    }
}
