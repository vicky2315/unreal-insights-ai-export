using System.Globalization;
using System.Text;
using InsightsExportGui.Analysis;

namespace InsightsExportGui.Tests;

/// <summary>
/// Synthetic timerstats.csv builder. Format mirrors real UE 5.7 exports; names are anonymized
/// (no project widget names in this public repo).
/// </summary>
internal static class Fixtures
{
    public const string Header = "Name,Count,C.Avg,Incl,I.Min,I.Max,I.Avg,I.Med,Excl,E.Min,E.Max,E.Avg,E.Med";

    /// <summary>Widget scope as UE writes it: class, transient outer, game-instance root, WidgetTree hops, suffix.</summary>
    public static string WidgetScope(string cls, int gameInstance, string suffix, params string[] path)
    {
        var segs = new List<string>();
        for (int i = 0; i < path.Length; i++)
        {
            segs.Add(path[i]);
            if (i < path.Length - 1)
                segs.Add("WidgetTree_0");
        }
        return $"{cls} /Engine/Transient.UnrealEdEngine_0:BP_GameInstanceBase_C_{gameInstance}.{string.Join('.', segs)}_{suffix}";
    }

    /// <summary>One CSV row. Seconds. Inclusive defaults to exclusive when not given.</summary>
    public static string Row(string name, long count, double exclTotal, double exclMax = 0, double exclMed = -1,
        double? inclTotal = null, double? inclMax = null, double? inclMed = null)
    {
        double incl = inclTotal ?? exclTotal;
        double exclAvg = count > 0 ? exclTotal / count : 0;
        double inclAvg = count > 0 ? incl / count : 0;
        double eMed = exclMed >= 0 ? exclMed : exclAvg;
        var inv = CultureInfo.InvariantCulture;
        return string.Join(',', name,
            count.ToString(inv), count.ToString("0.000000", inv),
            F(incl), F(0), F(inclMax ?? exclMax), F(inclAvg), F(inclMed ?? eMed),
            F(exclTotal), F(0), F(exclMax), F(exclAvg), F(eMed));

        static string F(double v) => v.ToString("0.000000###", CultureInfo.InvariantCulture);
    }

    public static TimerStats Parse(params string[] rows)
    {
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var r in rows)
            sb.Append(r).Append('\n');
        return TimerStats.Parse(new StringReader(sb.ToString()));
    }

    public static string Prepass(long frames) => Row(TimerStats.FrameTimerName, frames, frames * 0.003, 0.03);
}
