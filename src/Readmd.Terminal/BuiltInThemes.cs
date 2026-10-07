using TextMateSharp.Grammars;

namespace Readmd.Terminal;

/// <summary>
/// The themes that ship with readmd. Names are stable identifiers accepted by <c>--theme</c>,
/// <c>:theme</c>, the <c>theme</c> config key and <c>base</c> in a custom theme. Each carries the
/// TextMate theme that code blocks are highlighted with so code matches the surrounding palette.
/// </summary>
public static class BuiltInThemes
{
    private static readonly Dictionary<string, TerminalTheme> Table = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Order = [];

    static BuiltInThemes()
    {
        Add(TerminalTheme.Dark);
        Add(TerminalTheme.Light);
        Add(Nord);
        Add(Dracula);
        Add(GruvboxDark);
        Add(SolarizedDark);
        Add(SolarizedLight);
        Add(CatppuccinMocha);
        Add(OneDark);
        Add(TokyoNight);
        Add(Monokai);
    }

    private static void Add(TerminalTheme t) { Table[t.Name] = t; Order.Add(t.Name); }

    /// <summary>All built-in themes by name (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, TerminalTheme> All => Table;

    /// <summary>Built-in theme names in display order (dark, light, then the rest).</summary>
    public static IReadOnlyList<string> Names => Order;

    /// <summary>Looks a theme up by name; null when unknown.</summary>
    public static TerminalTheme? Find(string? name) =>
        name is not null && Table.TryGetValue(name.Trim(), out var t) ? t : null;

    private static Rgb C(string hex) => Rgb.FromHex(hex);

    public static TerminalTheme Nord { get; } = new()
    {
        Name = "nord", IsDark = true, CodeTheme = ThemeName.DarkPlus,
        Text = C("#d8dee9"), Muted = C("#7b88a1"), Heading = C("#88c0d0"), Link = C("#81a1c1"), Code = C("#d08770"),
        Quote = C("#8fbcbb"), Accent = C("#b48ead"), Rule = C("#4c566a"), SearchBg = C("#6b5d2e"), SearchActiveBg = C("#9c7f2a"),
        Background = C("#2e3440"), BackgroundElevated = C("#3b4252"), CodeBackground = C("#353b49"), CodeBorder = C("#4c566a"),
        H1 = C("#b48ead"), H2 = C("#88c0d0"), H3 = C("#a3be8c"), SelectionBackground = C("#5e81ac"), SelectionForeground = C("#eceff4"),
    };

    public static TerminalTheme Dracula { get; } = new()
    {
        Name = "dracula", IsDark = true, CodeTheme = ThemeName.Dracula,
        Text = C("#f8f8f2"), Muted = C("#6272a4"), Heading = C("#bd93f9"), Link = C("#8be9fd"), Code = C("#ffb86c"),
        Quote = C("#6272a4"), Accent = C("#ff79c6"), Rule = C("#44475a"), SearchBg = C("#6b6530"), SearchActiveBg = C("#9a9040"),
        Background = C("#282a36"), BackgroundElevated = C("#343746"), CodeBackground = C("#2f3240"), CodeBorder = C("#44475a"),
        H1 = C("#ff79c6"), H2 = C("#bd93f9"), H3 = C("#50fa7b"), SelectionBackground = C("#44475a"), SelectionForeground = C("#f8f8f2"),
    };

    public static TerminalTheme GruvboxDark { get; } = new()
    {
        Name = "gruvbox-dark", IsDark = true, CodeTheme = ThemeName.DarkPlus,
        Text = C("#ebdbb2"), Muted = C("#928374"), Heading = C("#fabd2f"), Link = C("#83a598"), Code = C("#fe8019"),
        Quote = C("#928374"), Accent = C("#d3869b"), Rule = C("#504945"), SearchBg = C("#6b5b14"), SearchActiveBg = C("#b57614"),
        Background = C("#282828"), BackgroundElevated = C("#3c3836"), CodeBackground = C("#32302f"), CodeBorder = C("#504945"),
        H1 = C("#fabd2f"), H2 = C("#83a598"), H3 = C("#b8bb26"), SelectionBackground = C("#458588"), SelectionForeground = C("#fbf1c7"),
    };

    public static TerminalTheme SolarizedDark { get; } = new()
    {
        Name = "solarized-dark", IsDark = true, CodeTheme = ThemeName.SolarizedDark,
        Text = C("#93a1a1"), Muted = C("#586e75"), Heading = C("#268bd2"), Link = C("#2aa198"), Code = C("#cb4b16"),
        Quote = C("#586e75"), Accent = C("#6c71c4"), Rule = C("#586e75"), SearchBg = C("#6b5200"), SearchActiveBg = C("#b58900"),
        Background = C("#002b36"), BackgroundElevated = C("#073642"), CodeBackground = C("#073642"), CodeBorder = C("#586e75"),
        H1 = C("#d33682"), H2 = C("#268bd2"), H3 = C("#859900"), SelectionBackground = C("#268bd2"), SelectionForeground = C("#fdf6e3"),
    };

    public static TerminalTheme SolarizedLight { get; } = new()
    {
        Name = "solarized-light", IsDark = false, CodeTheme = ThemeName.SolarizedLight,
        Text = C("#657b83"), Muted = C("#93a1a1"), Heading = C("#268bd2"), Link = C("#2aa198"), Code = C("#cb4b16"),
        Quote = C("#93a1a1"), Accent = C("#6c71c4"), Rule = C("#d6d0bd"), SearchBg = C("#f0dc8a"), SearchActiveBg = C("#e0c050"),
        Background = C("#fdf6e3"), BackgroundElevated = C("#eee8d5"), CodeBackground = C("#eee8d5"), CodeBorder = C("#d6d0bd"),
        H1 = C("#d33682"), H2 = C("#268bd2"), H3 = C("#859900"), SelectionBackground = C("#268bd2"), SelectionForeground = C("#fdf6e3"),
    };

    public static TerminalTheme CatppuccinMocha { get; } = new()
    {
        Name = "catppuccin-mocha", IsDark = true, CodeTheme = ThemeName.DarkPlus,
        Text = C("#cdd6f4"), Muted = C("#6c7086"), Heading = C("#89b4fa"), Link = C("#89dceb"), Code = C("#fab387"),
        Quote = C("#a6adc8"), Accent = C("#cba6f7"), Rule = C("#45475a"), SearchBg = C("#6a5d35"), SearchActiveBg = C("#9c8a4a"),
        Background = C("#1e1e2e"), BackgroundElevated = C("#313244"), CodeBackground = C("#181825"), CodeBorder = C("#45475a"),
        H1 = C("#cba6f7"), H2 = C("#89b4fa"), H3 = C("#a6e3a1"), SelectionBackground = C("#585b70"), SelectionForeground = C("#cdd6f4"),
    };

    public static TerminalTheme OneDark { get; } = new()
    {
        Name = "one-dark", IsDark = true, CodeTheme = ThemeName.OneDark,
        Text = C("#abb2bf"), Muted = C("#5c6370"), Heading = C("#61afef"), Link = C("#56b6c2"), Code = C("#d19a66"),
        Quote = C("#5c6370"), Accent = C("#c678dd"), Rule = C("#3e4451"), SearchBg = C("#6a5a2a"), SearchActiveBg = C("#9a8440"),
        Background = C("#282c34"), BackgroundElevated = C("#21252b"), CodeBackground = C("#21252b"), CodeBorder = C("#3e4451"),
        H1 = C("#c678dd"), H2 = C("#61afef"), H3 = C("#98c379"), SelectionBackground = C("#4b6eaf"), SelectionForeground = C("#ffffff"),
    };

    public static TerminalTheme TokyoNight { get; } = new()
    {
        Name = "tokyo-night", IsDark = true, CodeTheme = ThemeName.DarkPlus,
        Text = C("#c0caf5"), Muted = C("#565f89"), Heading = C("#7aa2f7"), Link = C("#7dcfff"), Code = C("#ff9e64"),
        Quote = C("#565f89"), Accent = C("#bb9af7"), Rule = C("#3b4261"), SearchBg = C("#5c4a26"), SearchActiveBg = C("#8a7038"),
        Background = C("#1a1b26"), BackgroundElevated = C("#24283b"), CodeBackground = C("#16161e"), CodeBorder = C("#3b4261"),
        H1 = C("#bb9af7"), H2 = C("#7aa2f7"), H3 = C("#9ece6a"), SelectionBackground = C("#33467c"), SelectionForeground = C("#c0caf5"),
    };

    public static TerminalTheme Monokai { get; } = new()
    {
        Name = "monokai", IsDark = true, CodeTheme = ThemeName.Monokai,
        Text = C("#f8f8f2"), Muted = C("#75715e"), Heading = C("#66d9ef"), Link = C("#ae81ff"), Code = C("#fd971f"),
        Quote = C("#75715e"), Accent = C("#f92672"), Rule = C("#49483e"), SearchBg = C("#66602a"), SearchActiveBg = C("#9a9040"),
        Background = C("#272822"), BackgroundElevated = C("#3e3d32"), CodeBackground = C("#1e1f1c"), CodeBorder = C("#49483e"),
        H1 = C("#f92672"), H2 = C("#66d9ef"), H3 = C("#a6e22e"), SelectionBackground = C("#57584f"), SelectionForeground = C("#f8f8f2"),
    };
}
