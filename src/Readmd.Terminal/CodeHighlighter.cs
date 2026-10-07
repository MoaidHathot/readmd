using System.Collections.Concurrent;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Readmd.Terminal;

/// <summary>
/// Syntax-highlights code blocks using TextMateSharp grammars. Falls back to a single code-color
/// run if the language is unknown or highlighting fails. Registry/grammars are cached per process,
/// and finished highlights are memoized by (language, theme, content) so a live reload, resize or
/// theme toggle never re-tokenizes an unchanged block.
/// </summary>
/// <remarks>
/// TextMateSharp's grammar/theme objects are not thread-safe. All tokenization is funnelled through
/// a single lock (<see cref="Sync"/>); the interactive viewer additionally runs it on one background
/// thread so the first paint never waits for grammar compilation (the expensive part — a grammar's
/// regexes are compiled lazily on first use and can cost 50–80 ms each).
/// </remarks>
public static class CodeHighlighter
{
    private static readonly object Sync = new();
    private static RegistryOptions? _options;
    private static Registry? _darkRegistry;
    private static Registry? _lightRegistry;
    private static readonly Dictionary<string, IGrammar?> GrammarCache = new(StringComparer.OrdinalIgnoreCase);

    // Memoized results. Bounded: when it grows past MaxCached entries it is simply reset (the
    // working set of a document is tiny; this only guards against unbounded growth over a long
    // session visiting many files).
    private const int MaxCached = 512;
    private static readonly ConcurrentDictionary<string, List<List<StyledSpan>>> ResultCache = new();

    /// <summary>Highlights <paramref name="code"/> synchronously (cached when possible).</summary>
    public static List<List<StyledSpan>> Highlight(string code, string language, TerminalTheme theme)
    {
        var key = CacheKey(code, language, theme);
        if (ResultCache.TryGetValue(key, out var hit)) return hit;
        var result = HighlightUncached(code, language, theme);
        if (ResultCache.Count >= MaxCached) ResultCache.Clear();
        ResultCache[key] = result;
        return result;
    }

    /// <summary>Returns a previously computed highlight for this exact block, or null (never tokenizes).</summary>
    public static List<List<StyledSpan>>? TryGetCached(string code, string language, TerminalTheme theme) =>
        ResultCache.TryGetValue(CacheKey(code, language, theme), out var hit) ? hit : null;

    /// <summary>Plain single-color rendering used before (or instead of) real highlighting.</summary>
    public static List<List<StyledSpan>> Plain(string code, TerminalTheme theme) =>
        Fallback(SplitLines(code), theme);

    /// <summary>True when a grammar exists for <paramref name="language"/> (so deferring is worthwhile).</summary>
    public static bool HasGrammar(string language) => !string.IsNullOrWhiteSpace(NormalizeLanguage(language));

    /// <summary>
    /// Builds the TextMate registry ahead of time on a background thread so the first highlight
    /// doesn't pay for it. Safe to call repeatedly; cheap once initialized.
    /// </summary>
    public static Task WarmUpAsync(bool dark) => Task.Run(() => { try { RegistryFor(dark); } catch { /* best effort */ } });

    private static string CacheKey(string code, string language, TerminalTheme theme) =>
        NormalizeLanguage(language) + "\0" + (theme.IsDark ? "d" : "l") + "\0" + theme.Code.GetHashCode() + "\0" + Readmd.Core.Hashing.Sha256Hex(code)[..24];

    private static string[] SplitLines(string code) => code.Replace("\r\n", "\n").Split('\n');

    private static List<List<StyledSpan>> HighlightUncached(string code, string language, TerminalTheme theme)
    {
        var lines = SplitLines(code);
        try
        {
            var grammar = GetGrammar(language);
            if (grammar is null) return Fallback(lines, theme);

            // Tokenization and theme matching share the (non-thread-safe) registry; serialize them.
            lock (Sync)
            {
                return Readmd.Core.Timing.Measure($"tokenize {language} ({lines.Length} lines)", () =>
                {
                    var result = new List<List<StyledSpan>>(lines.Length);
                    IStateStack? ruleStack = null;
                    // Scope lists repeat constantly within a block; memoize their resolved colour.
                    var colorMemo = new Dictionary<string, Rgb?>(StringComparer.Ordinal);
                    foreach (var line in lines)
                    {
                        var tokenizeResult = grammar.TokenizeLine(line, ruleStack, TimeSpan.FromMilliseconds(200));
                        ruleStack = tokenizeResult.RuleStack;
                        var spans = new List<StyledSpan>();
                        foreach (var token in tokenizeResult.Tokens)
                        {
                            int start = Math.Min(token.StartIndex, line.Length);
                            int end = Math.Min(token.EndIndex, line.Length);
                            if (end <= start) continue;
                            var text = line[start..end];
                            var scopeKey = string.Join('\u001f', token.Scopes);
                            if (!colorMemo.TryGetValue(scopeKey, out var color))
                            {
                                color = ResolveColor(token.Scopes, theme);
                                colorMemo[scopeKey] = color;
                            }
                            spans.Add(new StyledSpan(text, color ?? theme.Text));
                        }
                        if (spans.Count == 0) spans.Add(new StyledSpan(line.Length == 0 ? "" : line, theme.Text));
                        result.Add(spans);
                    }
                    return result;
                });
            }
        }
        catch
        {
            return Fallback(lines, theme);
        }
    }

    private static List<List<StyledSpan>> Fallback(string[] lines, TerminalTheme theme) =>
        lines.Select(l => new List<StyledSpan> { new(l, theme.Code) }).ToList();

    private static Rgb? ResolveColor(List<string> scopes, TerminalTheme theme)
    {
        // Use the registry whose theme matches the terminal background so code stays legible:
        // DarkPlus colors are designed for dark backgrounds and wash out on light, and vice versa.
        var registry = RegistryFor(theme.IsDark);
        if (registry is null) return null;

        var themeManager = registry.GetTheme();
        var rules = themeManager.Match(scopes);
        if (rules is null) return null;
        foreach (var rule in rules)
        {
            if (rule.foreground > 0)
            {
                var hex = themeManager.GetColor(rule.foreground);
                if (!string.IsNullOrEmpty(hex)) return Rgb.FromHex(hex);
            }
        }
        return null;
    }

    private static IGrammar? GetGrammar(string language)
    {
        var registry = RegistryFor(true) ?? RegistryFor(false); // grammars are theme-independent
        if (_options is null || registry is null) return null;
        language = NormalizeLanguage(language);
        if (string.IsNullOrEmpty(language)) return null;

        lock (Sync)
        {
            if (GrammarCache.TryGetValue(language, out var cached)) return cached;
            IGrammar? grammar = null;
            try
            {
                grammar = Readmd.Core.Timing.Measure("grammar load " + language, () =>
                {
                    var lang = _options.GetLanguageByExtension("." + language)
                               ?? _options.GetAvailableLanguages()
                                   .FirstOrDefault(l => l.Id.Equals(language, StringComparison.OrdinalIgnoreCase)
                                       || (l.Aliases?.Contains(language, StringComparer.OrdinalIgnoreCase) ?? false));
                    if (lang is null) return null;
                    var scope = _options.GetScopeByLanguageId(lang.Id);
                    return scope is not null ? registry.LoadGrammar(scope) : null;
                });
            }
            catch { grammar = null; }
            GrammarCache[language] = grammar;
            return grammar;
        }
    }

    private static string NormalizeLanguage(string language)
    {
        language = language.Trim().ToLowerInvariant();
        // Fence info strings may carry attributes ("js title=x", "python {1,3}"): the language is the
        // first word.
        int sp = language.IndexOfAny([' ', '\t', '{']);
        if (sp > 0) language = language[..sp];
        return language switch
        {
            "c#" or "csharp" => "cs",
            "js" or "javascript" or "mjs" or "cjs" => "js",
            "ts" or "typescript" => "ts",
            "py" or "python" or "python3" => "py",
            "sh" or "bash" or "shell" or "zsh" or "console" or "shell-session" => "sh",
            "yml" or "yaml" => "yaml",
            "ps" or "ps1" or "pwsh" or "powershell" => "powershell",
            "dockerfile" or "docker" => "dockerfile",
            "text" or "txt" or "plain" or "plaintext" or "" => "",
            _ => language,
        };
    }

    // Returns the registry for the requested theme, building it on first use. Only the theme actually
    // used is constructed — a single-theme run never pays to build the other registry; the other is
    // created lazily if the user toggles theme in the viewer.
    //
    // Grammars are theme-independent, so all grammar/scope lookups go through one shared RegistryOptions
    // (_options). Each Registry, however, must carry its own theme so GetTheme() resolves the right colors.
    private static Registry? RegistryFor(bool dark)
    {
        var existing = dark ? _darkRegistry : _lightRegistry;
        if (existing is not null) return existing;
        lock (Sync)
        {
            existing = dark ? _darkRegistry : _lightRegistry;
            if (existing is not null) return existing;
            return Readmd.Core.Timing.Measure("textmate registry init", () =>
            {
                // Shared options for grammar lookup; its theme is irrelevant (grammars don't vary by theme).
                _options ??= new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
                return dark
                    ? _darkRegistry ??= new Registry(new RegistryOptions(ThemeName.DarkPlus))
                    : _lightRegistry ??= new Registry(new RegistryOptions(ThemeName.LightPlus));
            });
        }
    }
}
