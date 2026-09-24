using System.Globalization;

namespace InsightsExportGui.Analysis;

public enum CheckSeverity { Info, Warning, Error }

public sealed record CheckResult(CheckSeverity Severity, string Title, string Detail);

/// <summary>
/// Sanity checks that catch known capture mistakes before anyone trusts the numbers.
/// Each rule comes from a real bad capture (see research doc §6 / §8).
/// </summary>
public static class CaptureChecks
{
    /// <summary>Below this many frames, per-frame averages are too thin to compare.</summary>
    public const long ShortCaptureFrames = 300;

    /// <summary>Editor "Use Less CPU in Background" injects ~333 ms sleeps here when PIE is unfocused.</summary>
    public const string ThrottleTimerName = "FEngineLoop_UpdateTimeAndHandleMaxTickRate";
    public const double ThrottleMinMaxSeconds = 0.250;
    public const double ThrottleAvgOverMedian = 50;

    /// <summary>
    /// Hitch = a call longer than one 60 fps frame that is either rare (few calls — e.g. a panel open) or far
    /// above the scope's typical (median) call. Median, not average: one big spike drags a small sample's average up.
    /// </summary>
    public const double HitchMinMaxSeconds = 0.0166;
    public const double HitchMaxOverMedian = 20;
    public const long HitchRareCallCount = 10;
    /// <summary>
    /// Scopes called more often than this per frame (render-graph passes, task waits — millions of calls) are
    /// excluded: they always contain a few long outliers, often the throttle sleep itself, and drown the list.
    /// </summary>
    public const double HitchMaxCallsPerFrame = 2;
    public const int HitchListSize = 10;

    public static List<CheckResult> Run(TimerStats stats)
    {
        var results = new List<CheckResult>();
        CheckFrameCount(stats, results);
        CheckThrottle(stats, results);
        CheckHitches(stats, results);
        if (stats.SkippedLines > 0)
            results.Add(new CheckResult(CheckSeverity.Warning, "Unparsed lines",
                $"{stats.SkippedLines} line(s) in timerstats.csv didn't match the expected 13-column format."));
        return results;
    }

    public static bool HasProblems(IEnumerable<CheckResult> results) =>
        results.Any(r => r.Severity != CheckSeverity.Info);

    private static void CheckFrameCount(TimerStats stats, List<CheckResult> results)
    {
        var prepass = stats.Find(TimerStats.FrameTimerName);
        if (prepass == null)
        {
            results.Add(new CheckResult(CheckSeverity.Error, "No frame count",
                $"{TimerStats.FrameTimerName} not found. CPU (cpuprofiler) channel was likely off — per-frame numbers unavailable."));
            return;
        }
        if (prepass.Count <= 1)
        {
            results.Add(new CheckResult(CheckSeverity.Error, "CPU channel off",
                $"{TimerStats.FrameTimerName} count = {prepass.Count}. Slate scopes export count=1 when the cpuprofiler channel is off — Slate numbers are unusable."));
            return;
        }
        if (prepass.Count < ShortCaptureFrames)
            results.Add(new CheckResult(CheckSeverity.Warning, "Short capture",
                $"{prepass.Count:N0} frames (< {ShortCaptureFrames}). Averages are thin; capture longer."));
        else
            results.Add(new CheckResult(CheckSeverity.Info, "Frames", $"{prepass.Count:N0} frames."));
    }

    private static void CheckThrottle(TimerStats stats, List<CheckResult> results)
    {
        var row = stats.Find(ThrottleTimerName);
        if (row == null)
            return;

        bool spiky = row.InclMed <= 0 ? row.InclAvg > 0 : row.InclAvg / row.InclMed >= ThrottleAvgOverMedian;
        if (row.InclMax >= ThrottleMinMaxSeconds && spiky)
            results.Add(new CheckResult(CheckSeverity.Warning, "Background throttle",
                string.Create(CultureInfo.InvariantCulture,
                    $"{ThrottleTimerName}: max {row.InclMax * 1000:0} ms, avg {row.InclAvg * 1000:0.000} ms, median {row.InclMed * 1000:0.000} ms. ") +
                "PIE was probably unfocused (editor 'Use Less CPU in Background'). Recapture with the PIE window focused + t.MaxFPS 0."));
    }

    private static void CheckHitches(TimerStats stats, List<CheckResult> results)
    {
        long maxCalls = stats.FrameCount > 0 ? (long)(stats.FrameCount * HitchMaxCallsPerFrame) : long.MaxValue;
        var hitches = stats.Rows
            .Where(r => r.Name != ThrottleTimerName &&   // reported by the throttle check
                        r.Count <= maxCalls &&
                        r.ExclMax >= HitchMinMaxSeconds &&
                        (r.Count <= HitchRareCallCount || r.ExclMax >= HitchMaxOverMedian * r.ExclMed))
            .OrderByDescending(r => r.ExclMax)
            .ToList();
        if (hitches.Count == 0)
            return;

        var top = hitches.Take(HitchListSize).Select(r => string.Create(CultureInfo.InvariantCulture,
            $"{WidgetPaths.ShortName(r.Name)} (max {r.ExclMax * 1000:0.0} ms, avg {r.ExclAvg * 1000:0.000} ms, ×{r.Count:N0})"));
        results.Add(new CheckResult(CheckSeverity.Info, $"Hitch scopes ({hitches.Count})",
            "One-off spikes, not steady per-frame cost: " + string.Join("; ", top)));
    }
}
