using System.Text;

namespace Readmd.Terminal;

/// <summary>Every remappable action in the terminal viewer.</summary>
public enum EditorAction
{
    None,
    Quit,
    ScrollDown, ScrollUp,
    HalfPageDown, HalfPageUp,
    PageDown, PageUp,
    GoTop, GoBottom,
    Search, SearchNext, SearchPrev, ClearSearch,
    Toc, TocSidebar,
    Back, Forward,
    FollowLink, LinkHints, NextLink, PrevLink, CopyLinkUrl,
    OpenInBrowser, ViewImage, ToggleSelectionMode, Rerender,
    ToggleTheme, ToggleBackground, Help,
    NextHeading, PrevHeading, NextBlock, PrevBlock, NextCode, PrevCode, NextImage, PrevImage,
    Yank, YankAll,
    FoldToggle, FoldClose, FoldOpen, FoldAll, UnfoldAll,
    ToggleDiagramSource,
    Command,
    Export,
    QuickOpen, ProjectSearch,
    ZoomIn, ZoomOut, ZoomReset,
    NextFile, PrevFile,
    Bookmark, GoBookmark,
    ToggleWatch, Follow,
    Slides,
}

/// <summary>
/// Maps key chords to <see cref="EditorAction"/>s. A chord is a space-separated sequence of key
/// tokens; a token is a single character (<c>j</c>, <c>?</c>), a named key (<c>enter</c>,
/// <c>tab</c>, <c>left</c>, <c>pageup</c>, <c>f1</c>, <c>space</c>), or modifiers plus a key
/// (<c>ctrl+d</c>, <c>shift+tab</c>, <c>alt+left</c>). A bare multi-character token with no
/// modifier such as <c>gg</c>, <c>]]</c> or <c>za</c> is a sequence of single-character keys.
/// Several chords may map to one action. Config <c>keys</c> entries (action name → chord, or a
/// list of chords) replace that action's defaults.
/// </summary>
public sealed class KeyMap
{
    private readonly Dictionary<string, EditorAction> _bindings;   // normalized chord -> action
    private readonly HashSet<string> _prefixes;                     // every proper prefix of a multi-key chord

    private KeyMap(Dictionary<string, EditorAction> bindings)
    {
        _bindings = bindings;
        _prefixes = [];
        foreach (var chord in bindings.Keys)
        {
            var keys = chord.Split(' ');
            for (int i = 1; i < keys.Length; i++)
                _prefixes.Add(string.Join(' ', keys.Take(i)));
        }
    }

    /// <summary>The built-in bindings.</summary>
    public static KeyMap Default => _default ??= new(DefaultBindings());
    private static KeyMap? _default;

    /// <summary>Looks up the action for a complete chord (None if unbound).</summary>
    public EditorAction Resolve(string chord) => _bindings.TryGetValue(chord, out var a) ? a : EditorAction.None;

    /// <summary>Compatibility: resolves a single plain key.</summary>
    public EditorAction Resolve(char ch) => Resolve(ch == ' ' ? "space" : ch.ToString());

    /// <summary>True when <paramref name="chord"/> begins at least one longer binding (so the viewer should wait for more keys).</summary>
    public bool IsPrefix(string chord) => _prefixes.Contains(chord);

    /// <summary>All chords bound to <paramref name="action"/>, in a stable order (defaults first).</summary>
    public IReadOnlyList<string> ChordsFor(EditorAction action) =>
        _bindings.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList();

    /// <summary>The primary key bound to <paramref name="action"/> for display in help/status text, or null.</summary>
    public string? KeyFor(EditorAction action)
    {
        var chords = ChordsFor(action);
        return chords.Count == 0 ? null : Display(chords[0]);
    }

    /// <summary>Like <see cref="KeyFor"/> but falls back to <paramref name="fallback"/> when unbound.</summary>
    public string Label(EditorAction action, string fallback) => KeyFor(action) ?? fallback;

    /// <summary>All bindings (for help generation and `readmd keys`).</summary>
    public IEnumerable<(string Chord, EditorAction Action)> All => _bindings.Select(kv => (kv.Key, kv.Value));

    /// <summary>Human-readable form of a normalized chord ("g g" → "gg", "ctrl+d" → "Ctrl-d", "space" → "Space").</summary>
    public static string Display(string chord)
    {
        var parts = chord.Split(' ');
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            string s = p;
            s = s.Replace("ctrl+", "Ctrl-").Replace("alt+", "Alt-").Replace("shift+", "Shift-");
            s = s switch
            {
                "space" => "Space", "enter" => "Enter", "esc" => "Esc", "tab" => "Tab", "backspace" => "Bksp",
                "up" => "↑", "down" => "↓", "left" => "←", "right" => "→", "home" => "Home", "end" => "End",
                "pageup" => "PgUp", "pagedown" => "PgDn", "delete" => "Del", "insert" => "Ins",
                _ => s,
            };
            if (sb.Length > 0 && parts.Length > 1 && (p.Length > 1 || sb.Length > 1 && sb[^1] > 0x7f)) sb.Append(' ');
            sb.Append(s);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a key map from config overrides (action name → chord or list of chords). Unknown action
    /// names or unparsable chords are ignored. An override replaces every default binding of that
    /// action and steals the chord from any other action that had it.
    /// </summary>
    public static KeyMap FromConfig(IReadOnlyDictionary<string, string>? keys) =>
        FromConfig(keys?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[kv.Value]));

    public static KeyMap FromConfig(IReadOnlyDictionary<string, IReadOnlyList<string>>? keys)
    {
        if (keys is null || keys.Count == 0) return Default;
        var map = DefaultBindings();
        foreach (var (actionName, chordSpecs) in keys)
        {
            if (!TryParseAction(actionName, out var action)) continue;
            var normalized = chordSpecs.Select(TryNormalizeChord).Where(c => c is not null).Select(c => c!).ToList();
            if (normalized.Count == 0) continue;
            foreach (var existing in map.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList())
                map.Remove(existing);
            foreach (var chord in normalized) map[chord] = action;
        }
        return new KeyMap(map);
    }

    private static Dictionary<string, EditorAction> DefaultBindings()
    {
        var m = new Dictionary<string, EditorAction>(StringComparer.Ordinal);
        void Bind(EditorAction a, params string[] chords) { foreach (var c in chords) m[TryNormalizeChord(c)!] = a; }
        Bind(EditorAction.Quit, "q", "ctrl+c");
        Bind(EditorAction.ScrollDown, "j", "down", "ctrl+e");
        Bind(EditorAction.ScrollUp, "k", "up", "ctrl+y");
        Bind(EditorAction.HalfPageDown, "ctrl+d");
        Bind(EditorAction.HalfPageUp, "ctrl+u");
        Bind(EditorAction.PageDown, "ctrl+f", "space", "pagedown");
        Bind(EditorAction.PageUp, "ctrl+b", "b", "pageup");
        Bind(EditorAction.GoTop, "gg", "home");
        Bind(EditorAction.GoBottom, "G", "end");
        Bind(EditorAction.Search, "/");
        Bind(EditorAction.SearchNext, "n");
        Bind(EditorAction.SearchPrev, "N");
        Bind(EditorAction.ClearSearch, "esc");
        Bind(EditorAction.Toc, "t");
        Bind(EditorAction.TocSidebar, "T");
        Bind(EditorAction.Back, "left", "backspace", "alt+left", "H");
        Bind(EditorAction.Forward, "right", "alt+right", "L");
        Bind(EditorAction.FollowLink, "enter");
        Bind(EditorAction.LinkHints, "f");
        Bind(EditorAction.NextLink, "tab");
        Bind(EditorAction.PrevLink, "shift+tab");
        Bind(EditorAction.CopyLinkUrl, "F");
        Bind(EditorAction.OpenInBrowser, "o");
        Bind(EditorAction.ViewImage, "v");
        Bind(EditorAction.ToggleSelectionMode, "m");
        Bind(EditorAction.Rerender, "r", "ctrl+l");
        Bind(EditorAction.ToggleTheme, "[");
        Bind(EditorAction.ToggleBackground, "]");
        Bind(EditorAction.Help, "?", "f1");
        Bind(EditorAction.NextHeading, "]]");
        Bind(EditorAction.PrevHeading, "[[");
        Bind(EditorAction.NextBlock, "}");
        Bind(EditorAction.PrevBlock, "{");
        Bind(EditorAction.NextCode, "]c");
        Bind(EditorAction.PrevCode, "[c");
        Bind(EditorAction.NextImage, "]i");
        Bind(EditorAction.PrevImage, "[i");
        Bind(EditorAction.Yank, "y");
        Bind(EditorAction.YankAll, "Y");
        Bind(EditorAction.FoldToggle, "za");
        Bind(EditorAction.FoldClose, "zc");
        Bind(EditorAction.FoldOpen, "zo");
        Bind(EditorAction.FoldAll, "zM");
        Bind(EditorAction.UnfoldAll, "zR");
        Bind(EditorAction.ToggleDiagramSource, "s");
        Bind(EditorAction.Command, ":");
        Bind(EditorAction.Export, "e");
        Bind(EditorAction.QuickOpen, "ctrl+p");
        Bind(EditorAction.ProjectSearch, "ctrl+g");
        Bind(EditorAction.ZoomIn, "+", "=");
        Bind(EditorAction.ZoomOut, "-");
        Bind(EditorAction.ZoomReset, "ctrl+0");
        Bind(EditorAction.NextFile, "]f");
        Bind(EditorAction.PrevFile, "[f");
        Bind(EditorAction.Bookmark, "M");
        Bind(EditorAction.GoBookmark, "'");
        Bind(EditorAction.ToggleWatch, "W");
        Bind(EditorAction.Follow, "alt+f");   // not ctrl+shift+f: that normalizes to ctrl+f and would shadow PageDown
        Bind(EditorAction.Slides, "ctrl+s");
        return m;
    }

    /// <summary>Parses an action name from config (case-insensitive, with a few aliases).</summary>
    public static bool TryParseAction(string name, out EditorAction action)
    {
        var n = name.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
        action = n switch
        {
            "quit" or "exit" => EditorAction.Quit,
            "scrolldown" or "down" => EditorAction.ScrollDown,
            "scrollup" or "up" => EditorAction.ScrollUp,
            "halfpagedown" => EditorAction.HalfPageDown,
            "halfpageup" => EditorAction.HalfPageUp,
            "pagedown" => EditorAction.PageDown,
            "pageup" => EditorAction.PageUp,
            "gotop" or "top" => EditorAction.GoTop,
            "gobottom" or "bottom" => EditorAction.GoBottom,
            "search" or "find" => EditorAction.Search,
            "searchnext" or "next" => EditorAction.SearchNext,
            "searchprev" or "prev" => EditorAction.SearchPrev,
            "clearsearch" => EditorAction.ClearSearch,
            "toc" or "contents" => EditorAction.Toc,
            "tocsidebar" or "sidebar" => EditorAction.TocSidebar,
            "back" => EditorAction.Back,
            "forward" => EditorAction.Forward,
            "followlink" or "follow" or "open" => EditorAction.FollowLink,
            "linkhints" or "hints" => EditorAction.LinkHints,
            "nextlink" => EditorAction.NextLink,
            "prevlink" => EditorAction.PrevLink,
            "copylinkurl" or "copylink" => EditorAction.CopyLinkUrl,
            "openinbrowser" or "browser" => EditorAction.OpenInBrowser,
            "viewimage" or "view" or "focus" => EditorAction.ViewImage,
            "selectionmode" or "mark" => EditorAction.ToggleSelectionMode,
            "rerender" or "refresh" or "redraw" => EditorAction.Rerender,
            "toggletheme" or "theme" => EditorAction.ToggleTheme,
            "togglebackground" or "background" => EditorAction.ToggleBackground,
            "help" => EditorAction.Help,
            "nextheading" => EditorAction.NextHeading,
            "prevheading" => EditorAction.PrevHeading,
            "nextblock" => EditorAction.NextBlock,
            "prevblock" => EditorAction.PrevBlock,
            "nextcode" => EditorAction.NextCode,
            "prevcode" => EditorAction.PrevCode,
            "nextimage" => EditorAction.NextImage,
            "previmage" => EditorAction.PrevImage,
            "yank" or "copy" => EditorAction.Yank,
            "yankall" or "copyall" => EditorAction.YankAll,
            "foldtoggle" or "fold" => EditorAction.FoldToggle,
            "foldclose" => EditorAction.FoldClose,
            "foldopen" => EditorAction.FoldOpen,
            "foldall" => EditorAction.FoldAll,
            "unfoldall" => EditorAction.UnfoldAll,
            "togglediagramsource" or "diagramsource" or "source" => EditorAction.ToggleDiagramSource,
            "command" or "cmdline" => EditorAction.Command,
            "export" => EditorAction.Export,
            "quickopen" => EditorAction.QuickOpen,
            "projectsearch" => EditorAction.ProjectSearch,
            "zoomin" => EditorAction.ZoomIn,
            "zoomout" => EditorAction.ZoomOut,
            "zoomreset" => EditorAction.ZoomReset,
            "nextfile" => EditorAction.NextFile,
            "prevfile" => EditorAction.PrevFile,
            "bookmark" => EditorAction.Bookmark,
            "gobookmark" => EditorAction.GoBookmark,
            "togglewatch" or "watch" => EditorAction.ToggleWatch,
            "followtail" or "tail" => EditorAction.Follow,
            "slides" or "presentation" => EditorAction.Slides,
            _ => EditorAction.None,
        };
        return action != EditorAction.None;
    }

    private static readonly HashSet<string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "space", "enter", "return", "esc", "escape", "tab", "backspace", "bksp", "up", "down", "left", "right",
        "home", "end", "pageup", "pgup", "pagedown", "pgdn", "delete", "del", "insert", "ins",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
    };

    /// <summary>Normalizes a chord spec; null when it can't be parsed.</summary>
    public static string? TryNormalizeChord(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var tokens = new List<string>();
        foreach (var rawToken in spec.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken;
            // Vim-style <C-d>, <S-Tab>, <CR>, <Space>.
            if (token.StartsWith('<') && token.EndsWith('>') && token.Length > 2)
                token = token[1..^1].Replace("C-", "ctrl+").Replace("S-", "shift+").Replace("A-", "alt+").Replace("M-", "alt+")
                    .Replace("CR", "enter", StringComparison.OrdinalIgnoreCase);

            bool ctrl = false, alt = false, shift = false;
            string key = token;
            if (token.Length > 1 && token.Contains('+') && !token.EndsWith("++"))
            {
                var parts = token.Split('+');
                key = parts[^1];
                if (key.Length == 0) key = "+";   // "ctrl++" → plus key
                foreach (var mod in parts[..^1])
                {
                    switch (mod.ToLowerInvariant())
                    {
                        case "ctrl" or "control" or "c": ctrl = true; break;
                        case "alt" or "meta" or "opt" or "option" or "a" or "m": alt = true; break;
                        case "shift" or "s": shift = true; break;
                        default: return null;
                    }
                }
            }

            if (NamedKeys.Contains(key))
            {
                key = key.ToLowerInvariant() switch
                {
                    "return" => "enter", "escape" => "esc", "bksp" => "backspace", "pgup" => "pageup",
                    "pgdn" => "pagedown", "del" => "delete", "ins" => "insert", var k => k,
                };
                tokens.Add(Mods(ctrl, alt, shift) + key);
                continue;
            }
            if (key.Length == 1)
            {
                if (key == " ") key = "space";
                // Shift on a letter is just the uppercase letter; shift on others is kept as a modifier.
                if (shift && char.IsLetter(key[0])) { key = key.ToUpperInvariant(); shift = false; }
                if (ctrl) key = key.ToLowerInvariant();
                tokens.Add(Mods(ctrl, alt, shift) + key);
                continue;
            }
            if (!ctrl && !alt && !shift && key.All(c => !char.IsWhiteSpace(c)))
            {
                // A bare run of characters: a key sequence (gg, ]], za).
                foreach (var ch in key) tokens.Add(ch == ' ' ? "space" : ch.ToString());
                continue;
            }
            return null;
        }
        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    private static string Mods(bool ctrl, bool alt, bool shift) =>
        (ctrl ? "ctrl+" : "") + (alt ? "alt+" : "") + (shift ? "shift+" : "");

    /// <summary>Converts an input event to its chord token (null for mouse/paste/focus events).</summary>
    public static string? TokenFor(KeyEvent key)
    {
        string mods = Mods(key.Ctrl, key.Alt, key.Shift && key.Kind != KeyKind.Char);
        switch (key.Kind)
        {
            case KeyKind.Char:
                if (key.Char == ' ') return mods + "space";
                if (char.IsControl(key.Char)) return null;
                return mods + (key.Ctrl ? char.ToLowerInvariant(key.Char).ToString() : key.Char.ToString());
            case KeyKind.Enter: return mods + "enter";
            case KeyKind.Escape: return "esc";
            case KeyKind.Tab: return mods + "tab";
            case KeyKind.Backspace: return mods + "backspace";
            case KeyKind.Up: return mods + "up";
            case KeyKind.Down: return mods + "down";
            case KeyKind.Left: return mods + "left";
            case KeyKind.Right: return mods + "right";
            case KeyKind.Home: return mods + "home";
            case KeyKind.End: return mods + "end";
            case KeyKind.PageUp: return mods + "pageup";
            case KeyKind.PageDown: return mods + "pagedown";
            case KeyKind.Delete: return mods + "delete";
            case KeyKind.Insert: return mods + "insert";
            case KeyKind.Function: return mods + "f" + key.Number;
            default: return null;
        }
    }
}
