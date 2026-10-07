using TextMateSharp.Grammars;

namespace Readmd.Terminal;

/// <summary>Color palette for terminal rendering, in light and dark variants.</summary>
public sealed class TerminalTheme
{
    /// <summary>Display name ("dark", "nord", a config theme name, …).</summary>
    public string Name { get; init; } = "";

    /// <summary>TextMate theme used for code highlighting; null = the default for the background (Dark+ / Light+).</summary>
    public ThemeName? CodeTheme { get; init; }

    /// <summary>The code theme that will actually be used.</summary>
    public ThemeName EffectiveCodeTheme => CodeTheme ?? (IsDark ? ThemeName.DarkPlus : ThemeName.LightPlus);

    public required Rgb Text { get; init; }
    public required Rgb Muted { get; init; }
    public required Rgb Heading { get; init; }
    public required Rgb Link { get; init; }
    public required Rgb Code { get; init; }
    public required Rgb Quote { get; init; }
    public required Rgb Accent { get; init; }
    public required Rgb Rule { get; init; }
    public required Rgb SearchBg { get; init; }
    public required Rgb SearchActiveBg { get; init; }

    /// <summary>The document background. Used as a solid fill when <c>SolidBackground</c> is on.</summary>
    public required Rgb Background { get; init; }

    /// <summary>Slightly elevated background for panels/status/overlays.</summary>
    public required Rgb BackgroundElevated { get; init; }

    /// <summary>Background fill for fenced/indented code blocks.</summary>
    public required Rgb CodeBackground { get; init; }

    /// <summary>Left gutter bar color for code blocks.</summary>
    public required Rgb CodeBorder { get; init; }

    /// <summary>Distinct accent for H1 banners.</summary>
    public required Rgb H1 { get; init; }
    /// <summary>Distinct accent for H2 headings.</summary>
    public required Rgb H2 { get; init; }
    /// <summary>Distinct accent for H3 headings.</summary>
    public required Rgb H3 { get; init; }

    /// <summary>Background/foreground of selected rows (overlays) and mark-mode selections.</summary>
    public Rgb SelectionBackground { get; init; }
    public Rgb SelectionForeground { get; init; }

    public bool IsDark { get; init; }

    public static TerminalTheme Dark { get; } = new()
    {
        Name = "dark",
        IsDark = true,
        Text = Rgb.FromHex("#e6edf3"),
        Muted = Rgb.FromHex("#9da7b3"),
        Heading = Rgb.FromHex("#58a6ff"),
        Link = Rgb.FromHex("#6cb6ff"),
        Code = Rgb.FromHex("#ffa657"),
        Quote = Rgb.FromHex("#8b949e"),
        Accent = Rgb.FromHex("#a371f7"),
        Rule = Rgb.FromHex("#30363d"),
        SearchBg = Rgb.FromHex("#9e6a03"),
        SearchActiveBg = Rgb.FromHex("#bb8009"),
        Background = Rgb.FromHex("#0d1117"),
        BackgroundElevated = Rgb.FromHex("#161b22"),
        CodeBackground = Rgb.FromHex("#1b2230"),
        CodeBorder = Rgb.FromHex("#3d4757"),
        H1 = Rgb.FromHex("#d2a8ff"),
        H2 = Rgb.FromHex("#79c0ff"),
        H3 = Rgb.FromHex("#56d364"),
        SelectionBackground = Rgb.FromHex("#1f6feb"),
        SelectionForeground = Rgb.FromHex("#ffffff"),
    };

    public static TerminalTheme Light { get; } = new()
    {
        Name = "light",
        IsDark = false,
        Text = Rgb.FromHex("#1f2328"),
        Muted = Rgb.FromHex("#636c76"),
        Heading = Rgb.FromHex("#0969da"),
        Link = Rgb.FromHex("#0969da"),
        Code = Rgb.FromHex("#953800"),
        Quote = Rgb.FromHex("#636c76"),
        Accent = Rgb.FromHex("#8250df"),
        Rule = Rgb.FromHex("#d0d7de"),
        SearchBg = Rgb.FromHex("#fae17d"),
        SearchActiveBg = Rgb.FromHex("#fbca04"),
        Background = Rgb.FromHex("#ffffff"),
        BackgroundElevated = Rgb.FromHex("#f6f8fa"),
        CodeBackground = Rgb.FromHex("#f0f2f5"),
        CodeBorder = Rgb.FromHex("#d0d7de"),
        H1 = Rgb.FromHex("#8250df"),
        H2 = Rgb.FromHex("#0550ae"),
        H3 = Rgb.FromHex("#1a7f37"),
        SelectionBackground = Rgb.FromHex("#0969da"),
        SelectionForeground = Rgb.FromHex("#ffffff"),
    };

    public static TerminalTheme For(bool dark) => dark ? Dark : Light;

    /// <summary>Formats a colour as "#rrggbb".</summary>
    public static string Hex(Rgb c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Returns a copy with a different display name.</summary>
    public TerminalTheme WithName(string name) => new()
    {
        Name = name, CodeTheme = CodeTheme, IsDark = IsDark,
        Text = Text, Muted = Muted, Heading = Heading, Link = Link, Code = Code, Quote = Quote, Accent = Accent, Rule = Rule,
        SearchBg = SearchBg, SearchActiveBg = SearchActiveBg, Background = Background, BackgroundElevated = BackgroundElevated,
        CodeBackground = CodeBackground, CodeBorder = CodeBorder, H1 = H1, H2 = H2, H3 = H3,
        SelectionBackground = SelectionBackground, SelectionForeground = SelectionForeground,
    };

    /// <summary>
    /// Parses a TextMate theme name as written in config: "dracula", "one-dark", "OneDark",
    /// "solarized_light" … (case-insensitive, separators ignored). Null when unknown.
    /// </summary>
    public static ThemeName? ParseCodeTheme(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = new string(name.Where(char.IsLetterOrDigit).ToArray());
        foreach (var value in Enum.GetValues<ThemeName>())
            if (value.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)) return value;
        return key.ToLowerInvariant() switch
        {
            "dark" => ThemeName.DarkPlus, "light" => ThemeName.LightPlus,
            "vsdark" or "visualstudiodark" => ThemeName.VisualStudioDark, "vslight" => ThemeName.VisualStudioLight,
            _ => null,
        };
    }

    /// <summary>
    /// Builds a theme from a config <see cref="Readmd.Core.ColorTheme"/>, filling any unset color
    /// from the base theme (<c>base</c> if it names a built-in, else dark/light by <c>dark</c>).
    /// </summary>
    public static TerminalTheme FromColorTheme(Readmd.Core.ColorTheme ct, string name = "custom")
    {
        TerminalTheme? b = null;
        if (!string.IsNullOrWhiteSpace(ct.Base)) BuiltInThemes.All.TryGetValue(ct.Base, out b);
        // A custom theme named after a built-in ("nord": { "text": … }) tweaks that built-in rather
        // than starting from the plain dark/light palette — unless it asks for another base.
        if (b is null && ct.Base is null && BuiltInThemes.All.TryGetValue(name, out var same) && name is not ("dark" or "light")) b = same;
        b ??= (ct.Dark ?? true) ? Dark : Light;
        bool dark = ct.Dark ?? b.IsDark;
        Rgb Pick(string? hex, Rgb fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try { return Rgb.FromHex(hex); } catch { return fallback; }
        }
        var searchBg = Pick(ct.SearchBackground, b.SearchBg);
        return new TerminalTheme
        {
            Name = name,
            IsDark = dark,
            CodeTheme = ParseCodeTheme(ct.CodeTheme) ?? b.CodeTheme,
            Text = Pick(ct.Text, b.Text),
            Muted = Pick(ct.Muted, b.Muted),
            Heading = Pick(ct.Heading, b.Heading),
            Link = Pick(ct.Link, b.Link),
            Code = Pick(ct.Code, b.Code),
            Quote = Pick(ct.Quote, Pick(ct.Muted, b.Quote)),
            Accent = Pick(ct.Accent, b.Accent),
            Rule = Pick(ct.Rule, b.Rule),
            SearchBg = searchBg,
            SearchActiveBg = ct.SearchBackground is null ? b.SearchActiveBg : (dark ? searchBg.Lighten(0.2) : searchBg.Darken(0.15)),
            Background = Pick(ct.Background, b.Background),
            BackgroundElevated = Pick(ct.BackgroundElevated, b.BackgroundElevated),
            CodeBackground = Pick(ct.CodeBackground, b.CodeBackground),
            CodeBorder = Pick(ct.CodeBorder, b.CodeBorder),
            H1 = Pick(ct.H1, b.H1),
            H2 = Pick(ct.H2, b.H2),
            H3 = Pick(ct.H3, b.H3),
            SelectionBackground = Pick(ct.SelectionBackground, Pick(ct.Accent, b.SelectionBackground)),
            SelectionForeground = b.SelectionForeground,
        };
    }
}