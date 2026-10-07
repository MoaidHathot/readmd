using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Readmd.Core;

namespace Readmd.Terminal;

/// <summary>
/// Checks a readmd config beyond "it parses": unknown keys (with a did-you-mean), enum-like
/// strings, numeric ranges, colour syntax, theme/base/codeTheme names and key bindings. The
/// loader itself is forgiving (unknown values fall back to defaults silently) so a typo would
/// otherwise go unnoticed; <c>readmd config validate</c> and <c>readmd doctor</c> surface them.
/// </summary>
public static class ConfigValidator
{
    public enum Severity { Error, Warning }

    /// <summary>One finding. <see cref="Path"/> is a dotted JSON path such as <c>themes.nord.link</c>.</summary>
    public sealed record Issue(Severity Severity, string Path, string Message)
    {
        public override string ToString() => $"{(Severity == Severity.Error ? "error" : "warning")}: {Path}: {Message}";
    }

    private static readonly Regex HexColor = new("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

    /// <summary>Top-level keys the config understands (from the model's JSON names).</summary>
    public static IReadOnlyList<string> KnownKeys { get; } = JsonNames(typeof(ReadmdConfig));

    /// <summary>Keys a custom theme understands.</summary>
    public static IReadOnlyList<string> KnownThemeKeys { get; } = JsonNames(typeof(ColorTheme));

    private static List<string> JsonNames(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
         .Where(n => n is not null).Select(n => n!).ToList();

    /// <summary>Parses and validates raw JSON (syntax errors, unknown keys, then the semantic checks).</summary>
    public static IReadOnlyList<Issue> ValidateJson(string json)
    {
        var issues = new List<Issue>();
        if (string.IsNullOrWhiteSpace(json)) return issues;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            issues.Add(new Issue(Severity.Error, "$", "invalid JSON: " + ex.Message));
            return issues;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new Issue(Severity.Error, "$", "the config must be a JSON object"));
                return issues;
            }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!KnownKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    issues.Add(new Issue(Severity.Warning, prop.Name, "unknown key" + Suggest(prop.Name, KnownKeys)));
            }
            if (doc.RootElement.TryGetProperty("themes", out var themes) && themes.ValueKind == JsonValueKind.Object)
            {
                foreach (var theme in themes.EnumerateObject())
                {
                    if (theme.Value.ValueKind != JsonValueKind.Object) { issues.Add(new Issue(Severity.Error, "themes." + theme.Name, "must be an object of colours")); continue; }
                    foreach (var prop in theme.Value.EnumerateObject())
                        if (!KnownThemeKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                            issues.Add(new Issue(Severity.Warning, $"themes.{theme.Name}.{prop.Name}", "unknown theme key" + Suggest(prop.Name, KnownThemeKeys)));
                }
            }
        }
        ReadmdConfig config;
        try { config = ReadmdConfig.Parse(json); }
        catch (JsonException ex)
        {
            issues.Add(new Issue(Severity.Error, ex.Path?.TrimStart('$', '.') ?? "$", ex.Message.Split(" Path:")[0].Trim()));
            return issues;
        }
        issues.AddRange(Validate(config));
        return issues;
    }

    /// <summary>Semantic checks on a parsed config.</summary>
    public static IReadOnlyList<Issue> Validate(ReadmdConfig c)
    {
        var issues = new List<Issue>();
        var themeNames = ThemeCatalog.Build(c).Keys.ToList();

        if (c.Theme is { } theme && theme is not ("dark" or "light" or "auto") && !themeNames.Contains(theme, StringComparer.OrdinalIgnoreCase))
            issues.Add(new Issue(Severity.Error, "theme", $"unknown theme '{theme}'" + Suggest(theme, themeNames.Concat(["auto"]).ToList())));
        if (c.ThemeCycle is { } cycle)
            foreach (var name in cycle)
                if (!themeNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    issues.Add(new Issue(Severity.Warning, "themeCycle", $"unknown theme '{name}' (dropped)" + Suggest(name, themeNames)));

        if (c.Background is { } bg && bg.Trim().ToLowerInvariant() is not ("solid" or "terminal" or "opaque" or "on" or "off" or "transparent"))
            issues.Add(new Issue(Severity.Error, "background", $"'{bg}' is not one of: solid, terminal"));
        if (c.Graphics is { } g && !g.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) && TerminalCapabilities.Parse(g) is null)
            issues.Add(new Issue(Severity.Error, "graphics", $"'{g}' is not one of: auto, sixel, kitty, iterm2, half-block, none"));
        if (c.Color is { } col && col.Trim().ToLowerInvariant() is not ("auto" or "always" or "truecolor" or "24bit" or "256" or "16" or "never" or "none" or "off"))
            issues.Add(new Issue(Severity.Error, "color", $"'{col}' is not one of: auto, always, truecolor, 256, 16, never"));

        if (c.MaxWidth is { } mw && mw != 0 && mw < 20) issues.Add(new Issue(Severity.Error, "maxWidth", "must be 0 (full width) or at least 20"));
        if (c.TabSize is { } ts && ts is < 1 or > 16) issues.Add(new Issue(Severity.Error, "tabSize", "must be between 1 and 16"));
        if (c.WheelLines is { } wl && wl is < 1 or > 20) issues.Add(new Issue(Severity.Error, "wheelLines", "must be between 1 and 20"));
        if (c.KrokiUrl is { } k && (!Uri.TryCreate(k, UriKind.Absolute, out var ku) || ku.Scheme is not ("http" or "https")))
            issues.Add(new Issue(Severity.Error, "krokiUrl", "must be an absolute http(s) URL"));

        foreach (var (key, path) in new[] { ("d2Path", c.D2Path), ("mermaidCliPath", c.MermaidCliPath), ("graphvizPath", c.GraphvizPath), ("plantUmlPath", c.PlantUmlPath) })
            if (path is not null && (path.Contains('/') || path.Contains('\\')) && !File.Exists(path))
                issues.Add(new Issue(Severity.Warning, key, $"file not found: {path}"));

        if (c.Themes is not null)
        {
            foreach (var (name, ct) in c.Themes)
            {
                if (ct.Base is { } b && BuiltInThemes.Find(b) is null)
                    issues.Add(new Issue(Severity.Error, $"themes.{name}.base", $"unknown built-in theme '{b}'" + Suggest(b, BuiltInThemes.Names)));
                if (ct.CodeTheme is { } code && TerminalTheme.ParseCodeTheme(code) is null)
                    issues.Add(new Issue(Severity.Warning, $"themes.{name}.codeTheme", $"unknown TextMate theme '{code}' (valid: {string.Join(", ", CodeThemeNames())})"));
                foreach (var (prop, value) in ct.Colors())
                    if (value is not null && !HexColor.IsMatch(value.Trim()))
                        issues.Add(new Issue(Severity.Error, $"themes.{name}.{prop}", $"'{value}' is not a #rgb or #rrggbb colour"));
            }
        }

        if (c.Keys is not null)
        {
            var actions = Enum.GetNames<EditorAction>().Where(n => n != nameof(EditorAction.None)).Select(n => char.ToLowerInvariant(n[0]) + n[1..]).ToList();
            foreach (var (action, chords) in c.Keys)
            {
                if (!KeyMap.TryParseAction(action, out _))
                    issues.Add(new Issue(Severity.Error, "keys." + action, "unknown action" + Suggest(action, actions)));
                foreach (var chord in chords)
                    if (KeyMap.TryNormalizeChord(chord) is null)
                        issues.Add(new Issue(Severity.Error, "keys." + action, $"'{chord}' is not a key chord (examples: \"x\", \"ctrl+q\", \"gg\", \"shift+tab\", \"<C-d>\")"));
            }
        }
        return issues;
    }

    /// <summary>The TextMate theme names a custom theme's <c>codeTheme</c> accepts.</summary>
    public static IReadOnlyList<string> CodeThemeNames() =>
        Enum.GetNames<TextMateSharp.Grammars.ThemeName>().Select(Kebab).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static string Kebab(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z0-9])(?=[A-Z])", "-").ToLowerInvariant();

    private static string Suggest(string given, IReadOnlyList<string> candidates)
    {
        string? best = null; int bestD = int.MaxValue;
        foreach (var cnd in candidates)
        {
            int d = Levenshtein(given.ToLowerInvariant(), cnd.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = cnd; }
        }
        return best is not null && bestD <= Math.Max(2, given.Length / 3) ? $" (did you mean '{best}'?)" : "";
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
