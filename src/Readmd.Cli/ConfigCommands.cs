using System.Reflection;
using System.Text;
using Readmd.Core;
using Readmd.Terminal;

/// <summary>
/// <c>readmd config path|init|validate|schema</c> and <c>readmd themes</c>.
/// </summary>
internal static class ConfigCommands
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/MoaidHathot/readmd/main/schema/readmd.schema.json";

    /// <summary>The JSON Schema embedded in the executable (schema/readmd.schema.json).</summary>
    public static string Schema()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("readmd.schema.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "{}";
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>`config path`: where readmd looks for configuration (and which files exist).</summary>
    public static int Path_(bool quiet)
    {
        var user = ConfigLoader.UserConfigPath();
        var project = ConfigLoader.FindProjectConfig(Directory.GetCurrentDirectory());
        if (quiet) { Console.Out.WriteLine(user); return 0; }
        Console.Out.WriteLine($"user     {user}  {(File.Exists(user) ? "(exists)" : "(not found)")}");
        Console.Out.WriteLine($"project  {(project ?? ".readmd.json")}  {(project is not null ? "(exists)" : "(none found above the current directory)")}");
        Console.Out.WriteLine($"state    {StateStore.DefaultPath()}  {(File.Exists(StateStore.DefaultPath()) ? "(exists)" : "(not created yet)")}");
        return 0;
    }

    /// <summary>`config init`: writes a commented starter config (user-level, or `.readmd.json` in the cwd with --project).</summary>
    public static int Init(bool project, bool force)
    {
        var path = project ? Path.Combine(Directory.GetCurrentDirectory(), ".readmd.json") : ConfigLoader.UserConfigPath();
        if (File.Exists(path) && !force)
        {
            Console.Error.WriteLine($"readmd: {path} already exists (use --force to overwrite).");
            return 1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, StarterConfig(project), new UTF8Encoding(false));
        Console.Error.WriteLine($"readmd: wrote {path}");
        return 0;
    }

    /// <summary>`config validate [file]`: parses and checks a config file (default: the user file and any project file).</summary>
    public static int Validate(string? file)
    {
        var files = new List<string>();
        if (file is not null) files.Add(Path.GetFullPath(file));
        else
        {
            var user = ConfigLoader.UserConfigPath();
            if (File.Exists(user)) files.Add(user);
            var project = ConfigLoader.FindProjectConfig(Directory.GetCurrentDirectory());
            if (project is not null) files.Add(project);
            if (files.Count == 0) { Console.Out.WriteLine("no config files found (" + user + ")"); return 0; }
        }
        int errors = 0, warnings = 0;
        foreach (var f in files)
        {
            if (!File.Exists(f)) { Console.Error.WriteLine($"readmd: file not found: {f}"); return 2; }
            var issues = ConfigValidator.ValidateJson(File.ReadAllText(f));
            Console.Out.WriteLine($"{f}: {(issues.Count == 0 ? "ok" : $"{issues.Count(i => i.Severity == ConfigValidator.Severity.Error)} error(s), {issues.Count(i => i.Severity == ConfigValidator.Severity.Warning)} warning(s)")}");
            foreach (var issue in issues)
            {
                Utf8Out.Write("  " + issue + "\n");
                if (issue.Severity == ConfigValidator.Severity.Error) errors++; else warnings++;
            }
        }
        return errors > 0 ? 1 : 0;
    }

    /// <summary>`themes`: lists every theme with a swatch (coloured when stdout is a terminal) and marks the active one.</summary>
    public static int Themes(bool plain, string? sample)
    {
        var config = ConfigLoader.Load(Directory.GetCurrentDirectory());
        var catalog = ThemeCatalog.Build(config);
        var active = config.Theme ?? "auto";
        bool color = !plain && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var colors = TerminalCapabilities.ResolveColors(null);
        var sb = new StringBuilder();
        int nameWidth = Math.Max(8, catalog.Keys.Max(k => k.Length));
        foreach (var (name, theme) in catalog)
        {
            bool isActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase);
            string marker = isActive ? (Glyphs.Ascii ? "*" : "●") : " ";
            string kind = theme.IsDark ? "dark " : "light";
            string origin = BuiltInThemes.All.ContainsKey(name) && ReferenceEquals(BuiltInThemes.All[name], theme) ? "built-in" : "config";
            sb.Append(marker).Append(' ').Append(name.PadRight(nameWidth)).Append("  ").Append(kind).Append("  ");
            if (color)
            {
                // Swatch: background, then the main foreground roles as blocks, then a sample line in the theme.
                string Fg(Rgb c) => Sgr(c, false, colors);
                string Bg(Rgb c) => Sgr(c, true, colors);
                foreach (var c in new[] { theme.Background, theme.BackgroundElevated, theme.CodeBackground, theme.Rule, theme.Muted, theme.Text, theme.H1, theme.H2, theme.H3, theme.Link, theme.Code, theme.Accent })
                    sb.Append(Bg(c)).Append("  ");
                sb.Append("\e[0m  ");
                sb.Append(Bg(theme.Background)).Append(Fg(theme.H1)).Append("\e[1m# ").Append(sample ?? "Heading").Append("\e[22m ")
                  .Append(Fg(theme.Text)).Append("text ").Append(Fg(theme.Link)).Append("\e[4mlink\e[24m ").Append(Fg(theme.Code)).Append("`code` ")
                  .Append(Fg(theme.Muted)).Append("muted").Append("\e[0m");
            }
            else
            {
                sb.Append($"bg {TerminalTheme.Hex(theme.Background)}  text {TerminalTheme.Hex(theme.Text)}  h1 {TerminalTheme.Hex(theme.H1)}  link {TerminalTheme.Hex(theme.Link)}  code {TerminalTheme.Hex(theme.Code)}  accent {TerminalTheme.Hex(theme.Accent)}");
            }
            sb.Append("  ").Append(origin).Append("  code: ").Append(theme.EffectiveCodeTheme).AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine($"{catalog.Count} themes. Active: {active}{(active == "auto" ? " (dark or light, following the terminal)" : "")}. Use --theme <name>, \"theme\" in config, :theme <name> in the viewer, or [ to cycle.");
        sb.AppendLine("Define your own under \"themes\" in the config (inherit with \"base\"); see `readmd config init`.");
        Utf8Out.Write(sb.ToString());
        return 0;
    }

    private static string Sgr(Rgb c, bool background, ColorMode mode) => mode switch
    {
        ColorMode.TrueColor => $"\e[{(background ? 48 : 38)};2;{c.R};{c.G};{c.B}m",
        ColorMode.Ansi256 => $"\e[{(background ? 48 : 38)};5;{ColorQuantizer.ToAnsi256(c)}m",
        ColorMode.Ansi16 => $"\e[{ColorQuantizer.ToAnsi16Sgr(c, background)}m",
        _ => "",
    };

    private static string StarterConfig(bool project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"$schema\": \"{SchemaUrl}\",");
        sb.AppendLine();
        if (project)
        {
            sb.AppendLine("  // Project settings (.readmd.json) override the user config for documents under this folder.");
        }
        else
        {
            sb.AppendLine("  // readmd user configuration. Every key is optional; command-line flags override these.");
            sb.AppendLine("  // Validate with `readmd config validate`, list themes with `readmd themes`.");
        }
        sb.AppendLine();
        sb.AppendLine("  // Theme: \"auto\" follows the terminal background; or dark, light, nord, dracula, gruvbox-dark,");
        sb.AppendLine("  // solarized-dark, solarized-light, catppuccin-mocha, one-dark, tokyo-night, monokai, or a custom name below.");
        sb.AppendLine("  \"theme\": \"auto\",");
        sb.AppendLine("  // \"themeCycle\": [\"dark\", \"light\", \"nord\"],   // what the [ key cycles through");
        sb.AppendLine("  // \"background\": \"terminal\",                   // or \"solid\"");
        sb.AppendLine();
        sb.AppendLine("  // Rendering");
        sb.AppendLine("  // \"maxWidth\": 100,          // centre content at this width (0 = full width)");
        sb.AppendLine("  // \"lineNumbers\": true,");
        sb.AppendLine("  // \"tabSize\": 4,");
        sb.AppendLine("  // \"smartPunctuation\": false,");
        sb.AppendLine("  // \"graphics\": \"auto\",      // sixel | kitty | iterm2 | half-block | none");
        sb.AppendLine("  // \"color\": \"auto\",         // always | 256 | 16 | never");
        sb.AppendLine("  // \"hyperlinks\": true,");
        sb.AppendLine("  // \"ascii\": false,");
        sb.AppendLine("  // \"ambiguousWide\": false,");
        sb.AppendLine();
        sb.AppendLine("  // Behaviour");
        sb.AppendLine("  // \"wheelLines\": 3,");
        sb.AppendLine("  // \"confirmExternalLinks\": true,");
        sb.AppendLine("  // \"rememberPosition\": true,");
        sb.AppendLine("  // \"scrollbar\": true,");
        sb.AppendLine("  // \"watch\": true,");
        sb.AppendLine("  // \"network\": true,          // false = offline: no remote images, no Kroki, no remote documents");
        sb.AppendLine();
        sb.AppendLine("  // Diagram tools (default: found on PATH; Kroki renders what's missing)");
        sb.AppendLine("  // \"d2Path\": \"/usr/local/bin/d2\",");
        sb.AppendLine("  // \"mermaidCliPath\": \"mmdc\",");
        sb.AppendLine("  // \"graphvizPath\": \"dot\",");
        sb.AppendLine("  // \"plantUmlPath\": \"plantuml\",");
        sb.AppendLine("  // \"krokiUrl\": \"https://kroki.io\",");
        sb.AppendLine("  // \"krokiFallback\": true,");
        sb.AppendLine();
        sb.AppendLine("  // Custom themes: inherit from a built-in with \"base\" and override any colour (#rrggbb).");
        sb.AppendLine("  \"themes\": {");
        sb.AppendLine("    // \"mine\": { \"base\": \"nord\", \"link\": \"#8fbcbb\", \"codeTheme\": \"one-dark\" }");
        sb.AppendLine("  },");
        sb.AppendLine();
        sb.AppendLine("  // Key bindings: action -> chord or list of chords (replaces the defaults for that action).");
        sb.AppendLine("  \"keys\": {");
        sb.AppendLine("    // \"quit\": [\"q\", \"ctrl+c\"],");
        sb.AppendLine("    // \"pageDown\": \"<C-f>\",");
        sb.AppendLine("    // \"toggleTheme\": \"F2\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
