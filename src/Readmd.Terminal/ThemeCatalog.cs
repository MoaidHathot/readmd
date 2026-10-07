using Readmd.Core;

namespace Readmd.Terminal;

/// <summary>
/// Assembles the theme table a session can switch between: every built-in theme followed by the
/// custom themes from config (a custom theme may shadow a built-in name). Shared by the CLI
/// (<c>--theme</c>, <c>readmd themes</c>) and the viewer (<c>:theme</c>, <c>[</c>).
/// </summary>
public static class ThemeCatalog
{
    /// <summary>Built-ins first (in display order), then config themes in their declared order.</summary>
    public static Dictionary<string, TerminalTheme> Build(ReadmdConfig? config)
    {
        var table = new Dictionary<string, TerminalTheme>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in BuiltInThemes.Names) table[name] = BuiltInThemes.All[name];
        if (config?.Themes is not null)
            foreach (var (name, ct) in config.Themes)
                table[name] = TerminalTheme.FromColorTheme(ct, name);
        return table;
    }

    /// <summary>
    /// The order <c>[</c> cycles through: <c>themeCycle</c> from config when present (unknown names
    /// dropped), else every theme in <paramref name="table"/>'s order.
    /// </summary>
    public static IReadOnlyList<string> Cycle(ReadmdConfig? config, IReadOnlyDictionary<string, TerminalTheme> table)
    {
        if (config?.ThemeCycle is { Count: > 0 } configured)
        {
            var list = configured.Where(table.ContainsKey).Select(n => table[n].Name.Length > 0 ? table[n].Name : n).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count > 0) return list;
        }
        return table.Keys.ToList();
    }
}
