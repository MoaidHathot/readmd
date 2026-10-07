using System.Runtime.CompilerServices;

namespace Readmd.Tests;

/// <summary>Process-wide test setup.</summary>
internal static class TestSetup
{
    /// <summary>
    /// Tool probes (d2/mmdc --version) must not read or write the developer's real on-disk memo
    /// during a test run: a stale negative entry could change which backend a test sees, and the
    /// run shouldn't leave artefacts behind. ToolProbeCacheTests re-enables it against a temp path.
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => Readmd.Diagrams.ToolProbeCache.Disabled = true;
}