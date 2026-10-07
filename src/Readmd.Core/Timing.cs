using System.Diagnostics;

namespace Readmd.Core;

/// <summary>
/// Opt-in startup/phase timing diagnostics. Set <c>READMD_TIMING=1</c> to have readmd print how
/// long each phase (config load, parse, layout, highlight, first paint, …) took to stderr. Zero cost
/// when disabled beyond a single environment-variable read.
/// </summary>
public static class Timing
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("READMD_TIMING") is { Length: > 0 } v && v != "0";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _last;

    /// <summary>Records a named phase boundary (time since process start and since the previous mark).</summary>
    public static void Mark(string phase)
    {
        if (!Enabled) return;
        long now = Clock.ElapsedMilliseconds;
        Console.Error.WriteLine($"[timing] {phase,-28} +{now - _last,5} ms  (t={now} ms)");
        _last = now;
    }

    /// <summary>Times a block of work and reports it under <paramref name="phase"/>.</summary>
    public static T Measure<T>(string phase, Func<T> work)
    {
        if (!Enabled) return work();
        long start = Clock.ElapsedMilliseconds;
        var result = work();
        long now = Clock.ElapsedMilliseconds;
        Console.Error.WriteLine($"[timing] {phase,-28} {now - start,6} ms");
        _last = now;
        return result;
    }
}
