using System.Text.Json;
using System.Text.Json.Serialization;

namespace Readmd.Core;

/// <summary>
/// User configuration for readmd, loaded from JSON. Discovered from a user-level file
/// (<c>~/.config/readmd/config.json</c> or <c>%APPDATA%\readmd\config.json</c>) and an optional
/// project-level <c>.readmd.json</c> found by walking up from the document; project settings
/// override user settings. All members are optional — an absent file yields defaults.
/// </summary>
public sealed class ReadmdConfig
{
    /// <summary>Default theme when <c>--theme</c> isn't passed: "dark", "light", "auto", or a custom theme name.</summary>
    [JsonPropertyName("theme")] public string? Theme { get; set; }

    /// <summary>Default background when <c>--background</c> isn't passed: "solid"/"terminal".</summary>
    [JsonPropertyName("background")] public string? Background { get; set; }

    /// <summary>Default path to the <c>d2</c> executable when <c>--d2-path</c> isn't passed.</summary>
    [JsonPropertyName("d2Path")] public string? D2Path { get; set; }

    /// <summary>Path to a local <c>mmdc</c> (mermaid-cli) to render mermaid without Chromium.</summary>
    [JsonPropertyName("mermaidCliPath")] public string? MermaidCliPath { get; set; }

    /// <summary>Path to the Graphviz <c>dot</c> executable (for <c>graphviz</c>/<c>dot</c> code blocks).</summary>
    [JsonPropertyName("graphvizPath")] public string? GraphvizPath { get; set; }

    /// <summary>Path to the <c>plantuml</c> launcher (for <c>plantuml</c>/<c>puml</c> code blocks).</summary>
    [JsonPropertyName("plantUmlPath")] public string? PlantUmlPath { get; set; }

    /// <summary>Inline-graphics mode for the terminal: "sixel", "half-block", "none", or "auto".</summary>
    [JsonPropertyName("graphics")] public string? Graphics { get; set; }

    /// <summary>Maximum reading width in columns for the terminal (0/absent = full width). Narrower content is centred.</summary>
    [JsonPropertyName("maxWidth")] public int? MaxWidth { get; set; }

    /// <summary>Show line numbers in code blocks (default true).</summary>
    [JsonPropertyName("lineNumbers")] public bool? LineNumbers { get; set; }

    /// <summary>Tab stop used when expanding tabs inside code blocks (default 4).</summary>
    [JsonPropertyName("tabSize")] public int? TabSize { get; set; }

    /// <summary>Typographic replacements: smart quotes, dashes and ellipses (default false).</summary>
    [JsonPropertyName("smartPunctuation")] public bool? SmartPunctuation { get; set; }

    /// <summary>Colour depth: "auto", "always"/"truecolor", "256", "16", or "never".</summary>
    [JsonPropertyName("color")] public string? Color { get; set; }

    /// <summary>Emit OSC 8 hyperlinks for links (default true).</summary>
    [JsonPropertyName("hyperlinks")] public bool? Hyperlinks { get; set; }

    /// <summary>Lines scrolled per mouse-wheel notch (default 3).</summary>
    [JsonPropertyName("wheelLines")] public int? WheelLines { get; set; }

    /// <summary>Ask before opening external links (default true; Ctrl+click always skips the prompt).</summary>
    [JsonPropertyName("confirmExternalLinks")] public bool? ConfirmExternalLinks { get; set; }

    /// <summary>Use plain ASCII glyphs instead of box drawing / symbols (default false).</summary>
    [JsonPropertyName("ascii")] public bool? Ascii { get; set; }

    /// <summary>Treat East Asian Ambiguous characters as wide, as CJK-locale terminals do (default false).</summary>
    [JsonPropertyName("ambiguousWide")] public bool? AmbiguousWide { get; set; }

    /// <summary>Allow network access (remote images, online diagram rendering). false = offline (default true).</summary>
    [JsonPropertyName("network")] public bool? Network { get; set; }

    /// <summary>Remember the reading position per file and search history across sessions (default true).</summary>
    [JsonPropertyName("rememberPosition")] public bool? RememberPosition { get; set; }

    /// <summary>Show a scrollbar at the right edge (default true).</summary>
    [JsonPropertyName("scrollbar")] public bool? Scrollbar { get; set; }

    /// <summary>Live-reload on file change (default true).</summary>
    [JsonPropertyName("watch")] public bool? Watch { get; set; }

    /// <summary>Named custom color themes that can be selected via <see cref="Theme"/>.</summary>
    [JsonPropertyName("themes")] public Dictionary<string, ColorTheme>? Themes { get; set; }

    /// <summary>Keybinding overrides: action name -> chord ("x", "ctrl+q", "gg") or a list of chords.</summary>
    [JsonPropertyName("keys")]
    [JsonConverter(typeof(KeyBindingsConverter))]
    public Dictionary<string, IReadOnlyList<string>>? Keys { get; set; }

    public static ReadmdConfig Empty { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses config JSON, returning <see cref="Empty"/> on null/blank input.</summary>
    public static ReadmdConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        return JsonSerializer.Deserialize<ReadmdConfig>(json, JsonOptions) ?? Empty;
    }

    /// <summary>Returns a copy of this config with non-null values from <paramref name="overrides"/> applied.</summary>
    public ReadmdConfig MergedWith(ReadmdConfig overrides)
    {
        var merged = new ReadmdConfig
        {
            Theme = overrides.Theme ?? Theme,
            Background = overrides.Background ?? Background,
            D2Path = overrides.D2Path ?? D2Path,
            MermaidCliPath = overrides.MermaidCliPath ?? MermaidCliPath,
            GraphvizPath = overrides.GraphvizPath ?? GraphvizPath,
            PlantUmlPath = overrides.PlantUmlPath ?? PlantUmlPath,
            Graphics = overrides.Graphics ?? Graphics,
            MaxWidth = overrides.MaxWidth ?? MaxWidth,
            LineNumbers = overrides.LineNumbers ?? LineNumbers,
            TabSize = overrides.TabSize ?? TabSize,
            SmartPunctuation = overrides.SmartPunctuation ?? SmartPunctuation,
            Color = overrides.Color ?? Color,
            Hyperlinks = overrides.Hyperlinks ?? Hyperlinks,
            WheelLines = overrides.WheelLines ?? WheelLines,
            ConfirmExternalLinks = overrides.ConfirmExternalLinks ?? ConfirmExternalLinks,
            Ascii = overrides.Ascii ?? Ascii,
            AmbiguousWide = overrides.AmbiguousWide ?? AmbiguousWide,
            Network = overrides.Network ?? Network,
            RememberPosition = overrides.RememberPosition ?? RememberPosition,
            Scrollbar = overrides.Scrollbar ?? Scrollbar,
            Watch = overrides.Watch ?? Watch,
            Themes = MergeDict(Themes, overrides.Themes),
            Keys = MergeDict(Keys, overrides.Keys),
        };
        return merged;
    }

    private static Dictionary<string, T>? MergeDict<T>(Dictionary<string, T>? baseDict, Dictionary<string, T>? over)
    {
        if (baseDict is null) return over;
        if (over is null) return baseDict;
        var result = new Dictionary<string, T>(baseDict, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in over) result[k] = v;
        return result;
    }
}

/// <summary>
/// Accepts each key binding as either a single string (<c>"quit": "x"</c>) or an array of strings
/// (<c>"quit": ["x", "ctrl+q"]</c>).
/// </summary>
public sealed class KeyBindingsConverter : JsonConverter<Dictionary<string, IReadOnlyList<string>>>
{
    public override Dictionary<string, IReadOnlyList<string>>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("keys must be an object");
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == JsonTokenType.String) result[name] = [reader.GetString() ?? ""];
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    if (reader.TokenType == JsonTokenType.String) list.Add(reader.GetString() ?? "");
                result[name] = list;
            }
            else if (reader.TokenType == JsonTokenType.Null) result[name] = [];
            else throw new JsonException($"keys.{name} must be a string or an array of strings");
        }
        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, IReadOnlyList<string>> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value)
        {
            writer.WritePropertyName(k);
            if (v.Count == 1) writer.WriteStringValue(v[0]);
            else { writer.WriteStartArray(); foreach (var s in v) writer.WriteStringValue(s); writer.WriteEndArray(); }
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// A custom color palette defined in config. Any field left null falls back to the built-in
/// dark/light value (selected by <see cref="Dark"/>), so a theme can override just a few colors.
/// Colors are hex strings like "#rrggbb".
/// </summary>
public sealed class ColorTheme
{
    /// <summary>Whether this theme is a dark theme (selects the base palette to fill gaps).</summary>
    [JsonPropertyName("dark")] public bool Dark { get; set; } = true;

    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("muted")] public string? Muted { get; set; }
    [JsonPropertyName("heading")] public string? Heading { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("accent")] public string? Accent { get; set; }
    [JsonPropertyName("rule")] public string? Rule { get; set; }
    [JsonPropertyName("background")] public string? Background { get; set; }
    [JsonPropertyName("backgroundElevated")] public string? BackgroundElevated { get; set; }
    [JsonPropertyName("h1")] public string? H1 { get; set; }
    [JsonPropertyName("h2")] public string? H2 { get; set; }
    [JsonPropertyName("h3")] public string? H3 { get; set; }
}
