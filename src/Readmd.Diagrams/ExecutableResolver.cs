using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Readmd.Diagrams;

/// <summary>
/// Resolves a CLI tool name (e.g. <c>mmdc</c>, <c>d2</c>, <c>dot</c>, <c>plantuml</c>) to a
/// launchable <see cref="ProcessStartInfo"/>. This matters on Windows, where npm and many
/// installers ship CLIs as <c>.cmd</c>/<c>.bat</c>/<c>.ps1</c> shims rather than <c>.exe</c> — and
/// those can't be started directly with <c>UseShellExecute=false</c>. Such shims are wrapped with
/// <c>cmd /c</c> (for <c>.cmd</c>/<c>.bat</c>) or <c>pwsh -File</c> (for <c>.ps1</c>).
/// </summary>
public static class ExecutableResolver
{
    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> that launches <paramref name="command"/> with the
    /// given <paramref name="arguments"/>, resolving Windows shims to a runnable launcher. Returns
    /// null if the command cannot be found on PATH (and isn't an existing absolute path).
    /// </summary>
    public static ProcessStartInfo? Resolve(string command, IEnumerable<string> arguments)
    {
        var resolved = Find(command);
        if (resolved is null) return null;

        var (fileName, prefixArgs) = LauncherFor(resolved);
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Returns the resolved launcher file path for a command, or null if not found.</summary>
    public static string? Find(string command)
    {
        // An explicit path (absolute or containing a directory separator) is used as-is when it
        // exists; otherwise we still try it verbatim so the OS can resolve it.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? command : command;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // If the command already has a known extension, look for it directly; otherwise probe
            // the usual Windows executable extensions in a sensible order.
            var exts = Path.HasExtension(command)
                ? new[] { "" }
                : new[] { ".exe", ".cmd", ".bat", ".ps1" };
            foreach (var dir in pathDirs)
            {
                foreach (var ext in exts)
                {
                    var candidate = Path.Combine(dir, command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        // Unix: find an executable file with that name on PATH.
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Maps a resolved launcher to the actual process to start plus any prefix args.
    private static (string FileName, string[] PrefixArgs) LauncherFor(string resolvedPath)
    {
        var ext = Path.GetExtension(resolvedPath).ToLowerInvariant();
        return ext switch
        {
            ".cmd" or ".bat" => ("cmd.exe", new[] { "/c", resolvedPath }),
            ".ps1" => ("pwsh", new[] { "-NoProfile", "-File", resolvedPath }),
            _ => (resolvedPath, Array.Empty<string>()),
        };
    }
}
