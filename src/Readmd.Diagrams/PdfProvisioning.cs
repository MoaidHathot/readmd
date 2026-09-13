using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Readmd.Diagrams;

/// <summary>
/// The outcome of checking/ensuring the tooling that <see cref="PdfRenderer"/> needs (a Node.js
/// runtime to drive Playwright, plus a downloaded Chromium build).
/// </summary>
public enum PdfReadiness
{
    /// <summary>Node.js and a Chromium build are both available; PDF export can run.</summary>
    Ready,

    /// <summary>The Playwright JS driver is present but no Node.js runtime was found on PATH.</summary>
    NodeMissing,

    /// <summary>Node.js is available but the Chromium browser build has not been installed yet.</summary>
    BrowserMissing,

    /// <summary>The bundled Playwright JS driver itself is missing (unexpected packaging state).</summary>
    DriverMissing,
}

/// <summary>Result of a readiness check or a provisioning attempt for PDF export.</summary>
public readonly record struct PdfProvisionResult(PdfReadiness Readiness, string? NodePath, string? Message)
{
    public bool IsReady => Readiness == PdfReadiness.Ready;
}

/// <summary>
/// Discovers and provisions the runtime PDF export depends on. The published readmd tool ships the
/// small (~12 MB) Playwright JS driver but not the ~88 MB bundled Node.js binary, so we point
/// Playwright at a Node.js already on the user's machine (via PLAYWRIGHT_NODEJS_PATH) and download
/// the Chromium build on demand. Keeping this separate from <see cref="PdfRenderer"/> lets the CLI
/// and web server query readiness and trigger installation without launching a browser.
/// </summary>
public static class PdfProvisioning
{
    /// <summary>Marker Playwright writes into a browser directory once its download has fully unpacked.</summary>
    private const string InstallationCompleteMarker = "INSTALLATION_COMPLETE";

    /// <summary>
    /// Locates a Node.js executable to drive the Playwright JS driver: an explicit
    /// PLAYWRIGHT_NODEJS_PATH wins, otherwise 'node' on PATH. Returns null if none is found.
    /// </summary>
    public static string? FindNode()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        return ExecutableResolver.Find("node");
    }

    /// <summary>
    /// The bundled Playwright JS driver package (laid out next to the app as .playwright/package,
    /// with cli.js and browsers.json at its root), or null if it was stripped from this build.
    /// </summary>
    private static string? DriverDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, ".playwright", "package");
        return File.Exists(Path.Combine(dir, "cli.js")) ? dir : null;
    }

    /// <summary>
    /// Ensures Playwright can find a Node.js runtime by setting PLAYWRIGHT_NODEJS_PATH for this
    /// process when it isn't already set. Returns the node path used, or null if none was found.
    /// </summary>
    public static string? EnsureNodeEnvironment()
    {
        var existing = Environment.GetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH");
        if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing))
            return existing;

        var node = ExecutableResolver.Find("node");
        if (node is not null)
            Environment.SetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH", node);
        return node;
    }

    /// <summary>
    /// Checks whether PDF export can run right now, without downloading anything. Reports the
    /// specific missing piece so callers can prompt the user appropriately.
    /// </summary>
    public static PdfProvisionResult CheckReadiness()
    {
        if (DriverDirectory() is null)
            return new PdfProvisionResult(PdfReadiness.DriverMissing,
                null, "The Playwright driver is missing from this build.");

        var node = EnsureNodeEnvironment();
        if (node is null)
            return new PdfProvisionResult(PdfReadiness.NodeMissing,
                null, "Node.js was not found on PATH. Install Node.js (https://nodejs.org) to enable high-quality PDF export, or use Print to PDF.");

        return IsChromiumInstalled()
            ? new PdfProvisionResult(PdfReadiness.Ready, node, null)
            : new PdfProvisionResult(PdfReadiness.BrowserMissing, node,
                "The headless browser for PDF export is not installed yet.");
    }

    /// <summary>
    /// Ensures Chromium is installed for PDF export, downloading it on first use (~100 MB to the
    /// user cache). Returns Ready on success, or a specific reason it could not complete. Safe to
    /// call repeatedly; it is a no-op once the browser is present.
    /// </summary>
    public static PdfProvisionResult EnsureInstalled(Action<string>? log = null)
    {
        var ready = CheckReadiness();
        if (ready.Readiness is PdfReadiness.DriverMissing or PdfReadiness.NodeMissing)
            return ready;
        if (ready.IsReady) return ready;

        // BrowserMissing -> run Playwright's installer through the JS driver + system Node. We only
        // ever launch headless without a channel, so install exactly the build that launch uses (the
        // headless shell) rather than the full Chromium as well; the manifest tells us its name.
        var target = RequiredChromiumBuild()?.Name ?? "chromium";
        log?.Invoke("Downloading the headless browser for PDF export (one-time, ~100 MB)…");
        try
        {
            var exit = Microsoft.Playwright.Program.Main(["install", target]);
            if (exit != 0)
                return new PdfProvisionResult(PdfReadiness.BrowserMissing, ready.NodePath,
                    "Failed to download the headless browser. Check your network connection and try again.");
        }
        catch (Exception ex)
        {
            return new PdfProvisionResult(PdfReadiness.BrowserMissing, ready.NodePath,
                "Failed to download the headless browser: " + ex.Message);
        }

        // A zero exit code is Playwright's own guarantee that the requested build is present, so
        // trust it over our cache heuristic (which could lag behind a future cache layout change).
        return new PdfProvisionResult(PdfReadiness.Ready, ready.NodePath, null);
    }

    /// <summary>
    /// The Chromium build this driver version launches for headless work, read from the manifest
    /// (browsers.json) shipped with the driver. Null if the driver or its manifest can't be read.
    /// </summary>
    private static ChromiumBuild? RequiredChromiumBuild()
    {
        var driverDir = DriverDirectory();
        if (driverDir is null) return null;
        try
        {
            return ParseChromiumBuild(File.ReadAllText(Path.Combine(driverDir, "browsers.json")));
        }
        catch
        {
            return null; // unreadable/malformed manifest -> callers fall back to lenient detection
        }
    }

    /// <summary>
    /// Picks, from a Playwright browsers.json manifest, the Chromium build a headless launch without
    /// a channel uses — Playwright 1.49+ runs the separate "chromium-headless-shell" build, older
    /// drivers only had "chromium" — and computes the cache directory names Playwright's registry
    /// installs it to: the name with '-' replaced by '_', then "-&lt;revision&gt;". Platform-specific
    /// pins in "revisionOverrides" land in "&lt;name&gt;_&lt;platform&gt;_special-&lt;revision&gt;"
    /// instead; since the host platform isn't known here, all of them are accepted. Returns null if
    /// the manifest has no usable Chromium entry.
    /// </summary>
    internal static ChromiumBuild? ParseChromiumBuild(string browsersJson)
    {
        using var doc = JsonDocument.Parse(browsersJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("browsers", out var browsers) ||
            browsers.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? headlessShell = null, fullChromium = null;
        foreach (var entry in browsers.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("name", out var nameProp) ||
                nameProp.ValueKind != JsonValueKind.String)
                continue;
            switch (nameProp.GetString())
            {
                case "chromium-headless-shell": headlessShell = entry; break;
                case "chromium": fullChromium = entry; break;
            }
        }

        if ((headlessShell ?? fullChromium) is not { } build) return null;

        var name = build.GetProperty("name").GetString()!;
        var dirs = new List<string>();
        if (build.TryGetProperty("revision", out var revision) && RevisionText(revision) is { } rev)
            dirs.Add(name.Replace('-', '_') + "-" + rev);
        if (build.TryGetProperty("revisionOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Object)
        {
            foreach (var pin in overrides.EnumerateObject())
            {
                if (RevisionText(pin.Value) is { } pinned)
                    dirs.Add((name + "_" + pin.Name + "_special").Replace('-', '_') + "-" + pinned);
            }
        }

        return dirs.Count == 0 ? null : new ChromiumBuild(name, dirs);
    }

    private static string? RevisionText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// Detects whether the Chromium build this driver launches is fully installed in the browser
    /// cache, so we can avoid spawning the installer on the happy path. Mirrors Playwright's cache
    /// location (PLAYWRIGHT_BROWSERS_PATH override, else the per-OS default) and requires the exact
    /// revision the bundled driver pins: a cache holding only builds from other Playwright versions
    /// (e.g. left behind by other projects) would otherwise look "installed", and the launch would
    /// then fail with "Executable doesn't exist at …".
    /// </summary>
    private static bool IsChromiumInstalled()
    {
        var expected = RequiredChromiumBuild()?.DirectoryNames;
        return BrowserCacheDirs().Any(dir => IsChromiumInstalledIn(dir, expected));
    }

    /// <summary>
    /// Checks one cache directory for a completed install of any of <paramref name="expectedDirectories"/>.
    /// Playwright writes an INSTALLATION_COMPLETE marker once a download has fully unpacked, so a
    /// half-extracted build doesn't count. With a null list (the driver manifest couldn't be read)
    /// this falls back to accepting any Chromium build.
    /// </summary>
    internal static bool IsChromiumInstalledIn(string cacheDir, IReadOnlyList<string>? expectedDirectories)
    {
        if (!Directory.Exists(cacheDir)) return false;
        try
        {
            if (expectedDirectories is null)
            {
                return Directory.EnumerateDirectories(cacheDir, "chromium-*").Any() ||
                       Directory.EnumerateDirectories(cacheDir, "chromium_headless_shell-*").Any();
            }

            return expectedDirectories.Any(name =>
                File.Exists(Path.Combine(cacheDir, name, InstallationCompleteMarker)));
        }
        catch
        {
            return false; // unreadable cache dir -> treat as not installed
        }
    }

    private static IEnumerable<string> BrowserCacheDirs()
    {
        var overridePath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (overridePath == "0")
            {
                // "0" means a hermetic install: browsers live inside the driver package itself.
                var driverDir = DriverDirectory();
                if (driverDir is not null) yield return Path.Combine(driverDir, ".local-browsers");
            }
            else
            {
                yield return overridePath;
            }
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "ms-playwright");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Library", "Caches", "ms-playwright");
        }
        else
        {
            // Playwright honours XDG_CACHE_HOME on Linux before falling back to ~/.cache.
            var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(cache))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                cache = string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".cache");
            }
            if (cache is not null) yield return Path.Combine(cache, "ms-playwright");
        }
    }
}

/// <summary>
/// The Chromium build a Playwright driver launches for headless work: its manifest name (which is
/// also the <c>playwright install</c> target) and the browser-cache directory names it may be
/// installed under.
/// </summary>
internal readonly record struct ChromiumBuild(string Name, IReadOnlyList<string> DirectoryNames);
