using System.Globalization;
using System.Text;

namespace InsightsExportGui.Analysis;

public enum DiffMetric
{
    PerFrameExcl,
    PerFrameIncl,
    PerCallExcl,
    PerCallIncl,
    Count,
}

/// <summary>One scope compared across run A (base) and B (new). A/B are null when the scope is missing from that run.</summary>
public sealed record DiffRow(string Key, string DisplayName, string FullName, ScopeKind Kind, double? A, double? B)
{
    public double? Delta => A.HasValue && B.HasValue ? B - A : null;
    public double? DeltaPercent => A is > 0 && B.HasValue ? (B - A) / A * 100.0 : null;
}

public static class RunDiff
{
    /// <summary>Frame counts differing by more than this fraction → whole-frame comparisons are suspect.</summary>
    public const double FrameCountTolerance = 0.20;

    public static string Label(DiffMetric m) => m switch
    {
        DiffMetric.PerFrameExcl => "Per-frame exclusive (ms)",
        DiffMetric.PerFrameIncl => "Per-frame inclusive (ms)",
        DiffMetric.PerCallExcl => "Per-call exclusive avg (ms)",
        DiffMetric.PerCallIncl => "Per-call inclusive avg (ms)",
        DiffMetric.Count => "Call count",
        _ => m.ToString(),
    };

    public static bool IsTime(DiffMetric m) => m != DiffMetric.Count;

    /// <summary>Metric value for a row; null when per-frame is requested but the run has no frame count.</summary>
    public static double? Value(TimerRow r, TimerStats stats, DiffMetric m) => m switch
    {
        DiffMetric.PerFrameExcl => stats.PerFrameMs(r.ExclTotal),
        DiffMetric.PerFrameIncl => stats.PerFrameMs(r.InclTotal),
        DiffMetric.PerCallExcl => r.ExclAvg * 1000.0,
        DiffMetric.PerCallIncl => r.InclAvg * 1000.0,
        DiffMetric.Count => r.Count,
        _ => null,
    };

    public static List<DiffRow> Compute(TimerStats a, TimerStats b, DiffMetric metric)
    {
        var byA = ByKey(a);
        var byB = ByKey(b);
        var rows = new List<DiffRow>(Math.Max(byA.Count, byB.Count));

        foreach (var key in byA.Keys.Union(byB.Keys))
        {
            byA.TryGetValue(key, out var ra);
            byB.TryGetValue(key, out var rb);
            var any = rb ?? ra!;
            var kind = rb != null ? b.KindOf(rb) : a.KindOf(ra!);
            rows.Add(new DiffRow(
                key,
                WidgetPaths.ShortName(any.Name),
                any.Name,
                kind,
                ra != null ? Value(ra, a, metric) : null,
                rb != null ? Value(rb, b, metric) : null));
        }
        return rows;
    }

    public static bool FrameCountsDiffer(TimerStats a, TimerStats b)
    {
        if (a.FrameCount <= 0 || b.FrameCount <= 0)
            return true;
        double hi = Math.Max(a.FrameCount, b.FrameCount), lo = Math.Min(a.FrameCount, b.FrameCount);
        return (hi - lo) / hi > FrameCountTolerance;
    }

    public static string ToMarkdown(IEnumerable<DiffRow> rows, DiffMetric metric, string nameA, string nameB)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"Metric: {Label(metric)}");
        sb.AppendLine();
        sb.AppendLine($"| Scope | Kind | {Escape(nameA)} | {Escape(nameB)} | Δ | Δ% |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            sb.Append("| ").Append(Escape(r.DisplayName))
              .Append(" | ").Append(KindLabel(r.Kind))
              .Append(" | ").Append(FormatValue(r.A, metric, inv))
              .Append(" | ").Append(FormatValue(r.B, metric, inv))
              .Append(" | ").Append(FormatDelta(r.Delta, metric, inv))
              .Append(" | ").Append(FormatPercent(r.DeltaPercent, inv))
              .AppendLine(" |");
        }
        return sb.ToString();
    }

    public static string KindLabel(ScopeKind k) => k == ScopeKind.PerFrame ? "per-frame" : "event";

    public static string FormatValue(double? v, DiffMetric m, IFormatProvider? fp = null) =>
        v is null ? "—" : IsTime(m) ? v.Value.ToString("0.000", fp) : v.Value.ToString("N0", fp);

    public static string FormatDelta(double? v, DiffMetric m, IFormatProvider? fp = null) =>
        v is null ? "—" : IsTime(m) ? v.Value.ToString("+0.000;-0.000;0.000", fp) : v.Value.ToString("+#,0;-#,0;0", fp);

    public static string FormatPercent(double? v, IFormatProvider? fp = null) =>
        v is null ? "—" : v.Value.ToString("+0.0;-0.0;0.0", fp) + "%";

    private static Dictionary<string, TimerRow> ByKey(TimerStats s)
    {
        var map = new Dictionary<string, TimerRow>(StringComparer.Ordinal);
        foreach (var r in s.Rows)
        {
            string key = WidgetPaths.JoinKey(r.Name);
            map[key] = map.TryGetValue(key, out var existing) ? existing.Merge(r) : r;
        }
        return map;
    }

    private static string Escape(string s) => s.Replace("|", "\\|");
}
