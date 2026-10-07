namespace Readmd.Terminal;

/// <summary>Remappable single-key actions in the terminal viewer.</summary>
public enum EditorAction
{
    None,
    Quit,
    ScrollDown,
    ScrollUp,
    PageDown,
    PageUp,
    GoBottom,
    Search,
    SearchNext,
    SearchPrev,
    Toc,
    OpenInBrowser,
    ViewImage,
    ToggleSelectionMode,
    Rerender,
    ToggleTheme,
    ToggleBackground,
    Help,
}

/// <summary>
/// Maps key characters to <see cref="EditorAction"/>s for the terminal viewer. Defaults to the
/// built-in vim-style bindings; a config <c>keys</c> map (action name -&gt; key) overrides them.
/// Only plain (unmodified, lowercase) character keys are remappable here — arrows, mouse, Ctrl
/// combinations, digits (link-following), and the 'g' prefix keep their built-in behavior.
/// </summary>
public sealed class KeyMap
{
    private readonly Dictionary<char, EditorAction> _map;

    private KeyMap(Dictionary<char, EditorAction> map) => _map = map;

    /// <summary>The built-in bindings (j/k scroll, / search, t toc, q quit, …).</summary>
    public static KeyMap Default { get; } = new(DefaultMap());

    /// <summary>Looks up the action bound to <paramref name="ch"/> (None if unbound).</summary>
    public EditorAction Resolve(char ch) => _map.TryGetValue(ch, out var a) ? a : EditorAction.None;

    /// <summary>The key currently bound to <paramref name="action"/>, for display in help/status text
    /// (e.g. "space" for ' '); null when the action has no plain-key binding.</summary>
    public string? KeyFor(EditorAction action)
    {
        foreach (var (key, a) in _map)
            if (a == action) return key == ' ' ? "space" : key.ToString();
        return null;
    }

    /// <summary>Like <see cref="KeyFor"/> but falls back to <paramref name="fallback"/> when unbound.</summary>
    public string Label(EditorAction action, string fallback) => KeyFor(action) ?? fallback;

    /// <summary>
    /// Builds a key map from config overrides ("action name" -&gt; key string). Unknown action
    /// names or keys are ignored. An override that rebinds an action moves it off its default key.
    /// </summary>
    public static KeyMap FromConfig(IReadOnlyDictionary<string, string>? keys)
    {
        if (keys is null || keys.Count == 0) return Default;

        var map = new Dictionary<char, EditorAction>(DefaultMap());
        foreach (var (actionName, keyStr) in keys)
        {
            if (!TryParseAction(actionName, out var action)) continue;
            var ch = ParseKey(keyStr);
            if (ch == '\0') continue;

            // Remove any existing default binding for this action, then bind the new key.
            foreach (var existing in map.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList())
                map.Remove(existing);
            map[ch] = action;
        }
        return new KeyMap(map);
    }

    private static Dictionary<char, EditorAction> DefaultMap() =>
        new()
        {
            ['q'] = EditorAction.Quit,
            ['j'] = EditorAction.ScrollDown,
            ['k'] = EditorAction.ScrollUp,
            [' '] = EditorAction.PageDown,
            ['b'] = EditorAction.PageUp,
            ['/'] = EditorAction.Search,
            ['n'] = EditorAction.SearchNext,
            ['t'] = EditorAction.Toc,
            ['o'] = EditorAction.OpenInBrowser,
            ['v'] = EditorAction.ViewImage,
            ['m'] = EditorAction.ToggleSelectionMode,
            ['r'] = EditorAction.Rerender,
            ['['] = EditorAction.ToggleTheme,
            [']'] = EditorAction.ToggleBackground,
            ['?'] = EditorAction.Help,
        };

    private static bool TryParseAction(string name, out EditorAction action)
    {
        action = name.Trim().ToLowerInvariant() switch
        {
            "quit" => EditorAction.Quit,
            "scrolldown" or "down" => EditorAction.ScrollDown,
            "scrollup" or "up" => EditorAction.ScrollUp,
            "pagedown" => EditorAction.PageDown,
            "pageup" => EditorAction.PageUp,
            "gobottom" or "bottom" => EditorAction.GoBottom,
            "search" => EditorAction.Search,
            "searchnext" or "next" => EditorAction.SearchNext,
            "searchprev" or "prev" => EditorAction.SearchPrev,
            "toc" => EditorAction.Toc,
            "openinbrowser" or "browser" => EditorAction.OpenInBrowser,
            "viewimage" or "view" => EditorAction.ViewImage,
            "selectionmode" or "mark" => EditorAction.ToggleSelectionMode,
            "rerender" => EditorAction.Rerender,
            "toggletheme" or "theme" => EditorAction.ToggleTheme,
            "togglebackground" or "background" => EditorAction.ToggleBackground,
            "help" => EditorAction.Help,
            _ => EditorAction.None,
        };
        return action != EditorAction.None;
    }

    // Accepts a single character ("x"), or the word "space".
    private static char ParseKey(string keyStr)
    {
        keyStr = keyStr.Trim();
        if (keyStr.Length == 1) return char.ToLowerInvariant(keyStr[0]);
        if (keyStr.Equals("space", StringComparison.OrdinalIgnoreCase)) return ' ';
        return '\0';
    }
}
