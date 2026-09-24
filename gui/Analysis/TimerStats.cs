using System.Globalization;

namespace InsightsExportGui.Analysis;

/// <summary>One row of TimingInsights.ExportTimerStatistics output. All times in SECONDS.</summary>
public sealed record TimerRow(
    string Name, long Count,
    double InclTotal, double InclMin, double InclMax, double InclAvg, double InclMed,
    double ExclTotal, double ExclMin, double ExclMax, double ExclAvg, double ExclMed)
{
    /// <summary>Combine two rows that share a name/key. Median can't be merged exactly; keep the larger sample's.</summary>
    public TimerRow Merge(TimerRow o)
    {
        long count = Count + o.Count;
        double inclTotal = InclTotal + o.InclTotal;
        double exclTotal = ExclTotal + o.ExclTotal;
        bool thisLarger = Count >= o.Count;
        return this with
        {
            Count = count,
            InclTotal = inclTotal,
            InclMin = Math.Min(InclMin, o.InclMin),
            InclMax = Math.Max(InclMax, o.InclMax),
            InclAvg = count > 0 ? inclTotal / count : 0,
            InclMed = thisLarger ? InclMed : o.InclMed,
            ExclTotal = exclTotal,
            ExclMin = Math.Min(ExclMin, o.ExclMin),
            ExclMax = Math.Max(ExclMax, o.ExclMax),
            ExclAvg = count > 0 ? exclTotal / count : 0,
            ExclMed = thisLarger ? ExclMed : o.ExclMed,
        };
    }
}

public enum ScopeKind { PerFrame, Event }

/// <summary>
/// Parsed timerstats.csv. Header: Name,Count,C.Avg,Incl,I.Min,I.Max,I.Avg,I.Med,Excl,E.Min,E.Max,E.Avg,E.Med.
/// Parsed right-to-left: the last 12 fields are numeric, the name is everything before them.
/// </summary>
public sealed class TimerStats
{
    public const string FileName = "timerstats.csv";
    /// <summary>Runs exactly once per frame; its count is the frame count.</summary>
    public const string FrameTimerName = "Slate::Prepass";
    private const int NumericColumns = 12;

    private readonly Dictionary<string, TimerRow> _byName;

    public IReadOnlyList<TimerRow> Rows { get; }
    /// <summary><see cref="FrameTimerName"/> count, or 0 when the scope is missing.</summary>
    public long FrameCount { get; }
    public int SkippedLines { get; }

    private TimerStats(List<TimerRow> rows, Dictionary<string, TimerRow> byName, int skipped)
    {
        Rows = rows;
        _byName = byName;
        SkippedLines = skipped;
        FrameCount = byName.TryGetValue(FrameTimerName, out var prepass) ? prepass.Count : 0;
    }

    public TimerRow? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Session-total seconds → average ms per frame; null without a frame count.</summary>
    public double? PerFrameMs(double seconds) => FrameCount > 0 ? seconds * 1000.0 / FrameCount : null;

    public ScopeKind KindOf(TimerRow row) => Classify(row.Count, FrameCount);

    /// <summary>Per-frame when count ≈ an integer multiple (≥1) of the frame count, else event-driven.</summary>
    public static ScopeKind Classify(long count, long frames)
    {
        if (frames <= 1)
            return ScopeKind.Event;
        double ratio = count / (double)frames;
        if (ratio < 0.9)
            return ScopeKind.Event;
        double nearest = Math.Round(ratio);
        return Math.Abs(ratio - nearest) <= 0.1 * nearest ? ScopeKind.PerFrame : ScopeKind.Event;
    }

    public static TimerStats Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return Parse(reader);
    }

    public static TimerStats Parse(TextReader reader)
    {
        var rows = new List<TimerRow>();
        var byName = new Dictionary<string, TimerRow>(StringComparer.Ordinal);
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        int skipped = 0;
        bool first = true;

        while (reader.ReadLine() is { } line)
        {
            if (first)
            {
                first = false;
                if (line.StartsWith("Name,", StringComparison.Ordinal))
                    continue;
            }
            if (line.Length == 0)
                continue;
            if (!TryParseLine(line, out var row))
            {
                skipped++;
                continue;
            }

            // Insights can emit the same timer name more than once: merge, keep first-seen order.
            if (indexByName.TryGetValue(row.Name, out int idx))
            {
                rows[idx] = rows[idx].Merge(row);
                byName[row.Name] = rows[idx];
            }
            else
            {
                indexByName[row.Name] = rows.Count;
                rows.Add(row);
                byName[row.Name] = row;
            }
        }

        return new TimerStats(rows, byName, skipped);
    }

    internal static bool TryParseLine(string line, out TimerRow row)
    {
        row = null!;
        var fields = line.TrimEnd('\r').Split(',');
        if (fields.Length < NumericColumns + 1)
            return false;

        var n = new double[NumericColumns];
        int offset = fields.Length - NumericColumns;
        for (int i = 0; i < NumericColumns; i++)
        {
            if (!double.TryParse(fields[offset + i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]))
                return false;
        }

        string name = string.Join(',', fields, 0, offset);
        // n: 0 Count, 1 C.Avg, 2 Incl, 3 I.Min, 4 I.Max, 5 I.Avg, 6 I.Med, 7 Excl, 8 E.Min, 9 E.Max, 10 E.Avg, 11 E.Med
        row = new TimerRow(name, (long)n[0], n[2], n[3], n[4], n[5], n[6], n[7], n[8], n[9], n[10], n[11]);
        return true;
    }
}

/// <summary>Re-parses a run's timerstats.csv only when the file changed (write time + size).</summary>
public static class TimerStatsCache
{
    private static readonly Dictionary<string, (DateTime Stamp, long Length, TimerStats Stats)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static TimerStats Get(string runFolder)
    {
        string path = Path.GetFullPath(Path.Combine(runFolder, TimerStats.FileName));
        var fi = new FileInfo(path);
        if (!fi.Exists)
            throw new FileNotFoundException($"{TimerStats.FileName} not found", path);

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var hit) && hit.Stamp == fi.LastWriteTimeUtc && hit.Length == fi.Length)
                return hit.Stats;
        }

        var stats = TimerStats.Load(path);
        lock (Gate)
            Cache[path] = (fi.LastWriteTimeUtc, fi.Length, stats);
        return stats;
    }
}
