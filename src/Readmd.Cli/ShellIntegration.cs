using System.Text;

/// <summary>
/// Generates shell completion scripts and a man page for the <c>readmd</c> command. The option
/// and subcommand lists are kept here in one place so every output stays in sync with the CLI.
/// </summary>
internal static class ShellIntegration
{
    // (flags, value-placeholder-or-null, description). Long flag first.
    private static readonly (string Flags, string? Value, string Desc)[] Options =
    [
        ("--browser -b", null, "Open in the browser instead of the terminal"),
        ("--port -p", "PORT", "Port for browser mode (0 = pick a free port)"),
        ("--no-open", null, "In browser mode, start the server but don't launch a browser"),
        ("--best-effort", null, "Terminal mode: skip the headless-browser download"),
        ("--export -e -o", "PATH", "Export to a self-contained .html or .pdf and exit"),
        ("--print", null, "Render to stdout and exit (text/ANSI)"),
        ("--theme", "THEME", "Color theme: dark, light, auto, a built-in name (see readmd themes) or a custom name"),
        ("--background --bg", "MODE", "Background fill: solid or terminal"),
        ("--d2-path", "PATH", "Explicit path to the d2 executable"),
        ("--root", "DIR", "Directory local links and images may reach"),
        ("--color", "MODE", "Colour depth: auto, always, 256, 16, or never"),
        ("--width -w", "COLS", "Maximum reading width (content is centred when wider)"),
        ("--graphics -g", "MODE", "Inline images: auto, sixel, kitty, iterm2, half-block, or none"),
        ("--ascii", null, "Plain ASCII glyphs instead of box drawing and symbols"),
        ("--offline", null, "Never touch the network"),
        ("--no-watch", null, "Don't live-reload on change"),
        ("--version -v", null, "Show version information"),
        ("--help -h", null, "Show help and usage information"),
    ];

    // (name, description) — kept in sync with Program.cs.
    private static readonly (string Name, string Desc)[] Subcommands =
    [
        ("doctor", "Check terminal capabilities, config, diagram tools and PDF export"),
        ("themes", "List the available colour themes"),
        ("config", "Inspect, create or validate configuration (path, init, validate, schema)"),
        ("install-pdf", "Provision the headless browser for PDF export"),
        ("completions", "Print a shell completion script"),
        ("man", "Print a man page"),
    ];

    private static readonly string[] ThemeValues = ["auto", .. Readmd.Terminal.BuiltInThemes.Names];
    private static readonly string[] BackgroundValues = ["solid", "terminal"];
    private static readonly string[] ColorValues = ["auto", "always", "256", "16", "never"];
    private static readonly string[] GraphicsValues = ["auto", "sixel", "kitty", "iterm2", "half-block", "none"];

    public static string ForShell(string shell) => shell.Trim().ToLowerInvariant() switch
    {
        "bash" => Bash(),
        "zsh" => Zsh(),
        "pwsh" or "powershell" => Pwsh(),
        "fish" => Fish(),
        _ => throw new ArgumentException($"unknown shell '{shell}' (expected: bash, zsh, pwsh, fish)"),
    };

    private static IEnumerable<string> AllFlags() =>
        Options.SelectMany(o => o.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Bash()
    {
        var flags = string.Join(' ', AllFlags());
        var commands = string.Join(' ', Subcommands.Select(s => s.Name));
        return $$"""
            # readmd bash completion. Add to ~/.bashrc:  source <(readmd completions bash)
            _readmd() {
              local cur prev
              cur="${COMP_WORDS[COMP_CWORD]}"
              prev="${COMP_WORDS[COMP_CWORD-1]}"
              case "$prev" in
                --theme) COMPREPLY=( $(compgen -W "{{string.Join(' ', ThemeValues)}}" -- "$cur") ); return;;
                --background|--bg) COMPREPLY=( $(compgen -W "{{string.Join(' ', BackgroundValues)}}" -- "$cur") ); return;;
                --color) COMPREPLY=( $(compgen -W "{{string.Join(' ', ColorValues)}}" -- "$cur") ); return;;
                --graphics|-g) COMPREPLY=( $(compgen -W "{{string.Join(' ', GraphicsValues)}}" -- "$cur") ); return;;
                --export|-e|-o|--d2-path) COMPREPLY=( $(compgen -f -- "$cur") ); return;;
                --root) COMPREPLY=( $(compgen -d -- "$cur") ); return;;
                completions) COMPREPLY=( $(compgen -W "bash zsh fish pwsh" -- "$cur") ); return;;
                config) COMPREPLY=( $(compgen -W "path init validate schema" -- "$cur") ); return;;
              esac
              if [[ "$cur" == -* ]]; then
                COMPREPLY=( $(compgen -W "{{flags}}" -- "$cur") )
              elif [[ $COMP_CWORD -eq 1 ]]; then
                COMPREPLY=( $(compgen -W "{{commands}}" -- "$cur") $(compgen -f -- "$cur") )
              else
                COMPREPLY=( $(compgen -f -- "$cur") )
              fi
            }
            complete -F _readmd readmd

            """;
    }

    private static string Zsh()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#compdef readmd");
        sb.AppendLine("# readmd zsh completion. Add to your fpath, or:  source <(readmd completions zsh)");
        sb.AppendLine("_readmd() {");
        sb.AppendLine("  local state");
        sb.AppendLine("  _arguments \\");
        foreach (var (flags, value, desc) in Options)
        {
            var parts = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var spec = parts.Length > 1 ? "{" + string.Join(",", parts) + "}" : parts[0];
            var safeDesc = desc.Replace("'", "");
            string valueSpec = flags.StartsWith("--theme") ? $":theme:({string.Join(' ', ThemeValues)})"
                : flags.StartsWith("--graphics") ? $":mode:({string.Join(' ', GraphicsValues)})"
                : flags.StartsWith("--color") ? $":mode:({string.Join(' ', ColorValues)})"
                : flags.StartsWith("--background") ? $":mode:({string.Join(' ', BackgroundValues)})"
                : flags.StartsWith("--root") ? ":dir:_directories"
                : value is not null ? $":{value}:_files" : "";
            sb.AppendLine($"    {spec}'[{safeDesc}]{valueSpec}' \\");
        }
        sb.AppendLine("    '1:file or command:->first' \\");
        sb.AppendLine("    '*:file:_files'");
        sb.AppendLine("  case $state in");
        sb.AppendLine("    first)");
        sb.AppendLine("      local -a cmds");
        sb.AppendLine("      cmds=(" + string.Join(" ", Subcommands.Select(s => $"'{s.Name}:{s.Desc.Replace("'", "").Replace(":", "")}'")) + ")");
        sb.AppendLine("      _describe -t commands 'readmd command' cmds");
        sb.AppendLine("      _files");
        sb.AppendLine("      ;;");
        sb.AppendLine("  esac");
        sb.AppendLine("}");
        sb.AppendLine("_readmd \"$@\"");
        return sb.ToString();
    }

    private static string Fish()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# readmd fish completion. Save to ~/.config/fish/completions/readmd.fish");
        foreach (var (name, desc) in Subcommands)
            sb.AppendLine($"complete -c readmd -n '__fish_use_subcommand' -a {name} -d '{desc.Replace("'", "")}'");
        sb.AppendLine("complete -c readmd -n '__fish_seen_subcommand_from config' -a 'path init validate schema'");
        sb.AppendLine("complete -c readmd -n '__fish_seen_subcommand_from completions' -a 'bash zsh fish pwsh'");
        foreach (var (flags, value, desc) in Options)
        {
            var parts = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = new StringBuilder("complete -c readmd");
            foreach (var p in parts)
            {
                if (p.StartsWith("--")) line.Append($" -l {p[2..]}");
                else if (p.StartsWith('-')) line.Append($" -s {p[1..]}");
            }
            string[]? values = flags.StartsWith("--theme") ? ThemeValues : flags.StartsWith("--graphics") ? GraphicsValues
                : flags.StartsWith("--color") ? ColorValues : flags.StartsWith("--background") ? BackgroundValues : null;
            if (values is not null) line.Append($" -x -a '{string.Join(' ', values)}'");
            else if (value is null) line.Append(" -f");
            else line.Append(" -r");
            line.Append($" -d '{desc.Replace("'", "")}'");
            sb.AppendLine(line.ToString());
        }
        return sb.ToString();
    }

    private static string Pwsh()
    {
        var flagList = string.Join(", ", AllFlags().Select(f => $"'{f}'"));
        var commandList = string.Join(", ", Subcommands.Select(s => $"'{s.Name}'"));
        var themeList = string.Join(", ", ThemeValues.Select(t => $"'{t}'"));
        return $$"""
            # readmd PowerShell completion. Add to your $PROFILE:  readmd completions pwsh | Out-String | Invoke-Expression
            Register-ArgumentCompleter -Native -CommandName readmd -ScriptBlock {
                param($wordToComplete, $commandAst, $cursorPosition)
                $flags = @({{flagList}})
                $commands = @({{commandList}})
                $words = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() })
                $prev = if ($words.Count -ge 2) { $words[$words.Count - 2] } else { '' }
                $candidates = switch ($prev) {
                    '--theme' { @({{themeList}}) }
                    '--graphics' { @({{string.Join(", ", GraphicsValues.Select(v => $"'{v}'"))}}) }
                    '-g' { @({{string.Join(", ", GraphicsValues.Select(v => $"'{v}'"))}}) }
                    '--color' { @({{string.Join(", ", ColorValues.Select(v => $"'{v}'"))}}) }
                    'config' { @('path', 'init', 'validate', 'schema') }
                    'completions' { @('bash', 'zsh', 'fish', 'pwsh') }
                    default { if ($wordToComplete -like '-*') { $flags } elseif ($words.Count -le 1) { $commands + $flags } else { $flags } }
                }
                $candidates | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_)
                }
            }

            """;
    }

    /// <summary>Generates a troff/groff man page (section 1) for readmd.</summary>
    public static string ManPage(string version)
    {
        // Use just the semantic version for the header (drop any +buildmetadata).
        var shortVersion = version.Split('+')[0];
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine($".TH READMD 1 \"{date}\" \"readmd {shortVersion}\" \"User Commands\"");
        sb.AppendLine(".SH NAME");
        sb.AppendLine("readmd \\- terminal-first Markdown viewer");
        sb.AppendLine(".SH SYNOPSIS");
        sb.AppendLine(".B readmd");
        sb.AppendLine("[\\fIOPTIONS\\fR] [\\fIFILE\\fR|\\fIDIR\\fR|\\fIGLOB\\fR|\\fIURL\\fR ...]");
        sb.AppendLine(".br");
        sb.AppendLine(".B readmd -");
        sb.AppendLine("(read Markdown from standard input)");
        sb.AppendLine(".br");
        sb.AppendLine(".B readmd");
        sb.AppendLine("\\fIdoctor\\fR | \\fIthemes\\fR | \\fIconfig\\fR {path|init|validate|schema} | \\fIinstall-pdf\\fR | \\fIcompletions\\fR SHELL | \\fIman\\fR");
        sb.AppendLine(".SH DESCRIPTION");
        sb.AppendLine("readmd renders Markdown in the terminal \\(em including mermaid, D2, Graphviz and");
        sb.AppendLine("PlantUML diagrams (as images via Sixel, kitty or iTerm2 graphics, or half-block), syntax-highlighted code,");
        sb.AppendLine("tables, GitHub alerts, math, and an Azure DevOps [[_TOC_]] \\(em and live-reloads as");
        sb.AppendLine("you edit. It opens files, directories, globs, standard input and remote documents");
        sb.AppendLine("(https:// URLs, GitHub blob URLs, \\fBgh:owner/repo[@ref][/path]\\fR).");
        sb.AppendLine("With \\fB--browser\\fR the same document opens in your browser.");
        sb.AppendLine(".SH OPTIONS");
        foreach (var (flags, value, desc) in Options)
        {
            var rendered = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => value is not null ? $"\\fB{f}\\fR \\fI{value}\\fR" : $"\\fB{f}\\fR");
            sb.AppendLine(".TP");
            sb.AppendLine(string.Join(", ", rendered));
            sb.AppendLine(desc + ".");
        }
        sb.AppendLine(".TP");
        sb.AppendLine("\\fB+\\fIN\\fR, \\fB+/\\fIPATTERN\\fR");
        sb.AppendLine("Open at source line N, or at the first match for PATTERN.");
        sb.AppendLine(".SH COMMANDS");
        foreach (var (name, desc) in Subcommands)
        {
            sb.AppendLine(".TP");
            sb.AppendLine($"\\fB{name}\\fR");
            sb.AppendLine(desc + ".");
        }
        sb.AppendLine(".SH FILES");
        sb.AppendLine(".TP");
        sb.AppendLine("\\fI~/.config/readmd/config.json\\fR (\\fI%APPDATA%\\\\readmd\\\\config.json\\fR on Windows)");
        sb.AppendLine("User configuration (themes, key bindings, defaults). \\fBreadmd config init\\fR writes a starter file; \\fBreadmd config schema\\fR prints its JSON Schema.");
        sb.AppendLine(".TP");
        sb.AppendLine("\\fI.readmd.json\\fR");
        sb.AppendLine("Project configuration, found by walking up from the document.");
        sb.AppendLine(".TP");
        sb.AppendLine("\\fI~/.local/state/readmd/state.json\\fR (\\fI%LOCALAPPDATA%\\\\readmd\\\\state.json\\fR on Windows)");
        sb.AppendLine("Remembered reading positions, bookmarks, search history and recent files.");
        sb.AppendLine(".SH ENVIRONMENT");
        sb.AppendLine(".TP");
        sb.AppendLine("\\fBREADMD_GRAPHICS\\fR");
        sb.AppendLine("Inline image mode: sixel, kitty, iterm2, half-block, none, or auto.");
        sb.AppendLine(".TP");
        sb.AppendLine("\\fBREADMD_OFFLINE\\fR, \\fBREADMD_ASCII\\fR");
        sb.AppendLine("Set to 1 to disable network access / use plain ASCII glyphs.");
        sb.AppendLine(".TP");
        sb.AppendLine("\\fBREADMD_TIMING\\fR");
        sb.AppendLine("Set to 1 to print per-phase timings to stderr.");
        sb.AppendLine(".TP");
        sb.AppendLine("\\fBNO_COLOR\\fR, \\fBFORCE_COLOR\\fR, \\fBCOLORTERM\\fR, \\fBCOLORFGBG\\fR");
        sb.AppendLine("Honoured for colour depth and the automatic dark/light choice.");
        sb.AppendLine(".SH SEE ALSO");
        sb.AppendLine("Project home: https://github.com/MoaidHathot/readmd");
        return sb.ToString();
    }
}
