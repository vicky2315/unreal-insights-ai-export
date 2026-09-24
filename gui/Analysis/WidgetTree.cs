using System.Text.RegularExpressions;

namespace InsightsExportGui.Analysis;

/// <summary>A UMG widget scope name split into its instance path.</summary>
/// <param name="ClassName">Leading class token, e.g. <c>WBP_Lord_C</c>.</param>
/// <param name="Segments">Instance path without the outer root and <c>WidgetTree_N</c> hops.</param>
/// <param name="Suffix">Scope kind on the last segment (<c>Tick</c>, <c>Paint</c>, ...), or null.</param>
public sealed record WidgetPath(string ClassName, IReadOnlyList<string> Segments, string? Suffix);

/// <summary>
/// Widget scopes export as
/// <c>WBP_X_C /Engine/Transient.UnrealEdEngine_0:BP_GameInstanceBase_C_2.WBP_MainHUD_C_0.WidgetTree_0.WBP_X_C_3_Paint</c>.
/// </summary>
public static partial class WidgetPaths
{
    [GeneratedRegex(@"^(?<cls>\S+) (?<outer>/\S*?):(?<obj>\S+)$")]
    private static partial Regex InstancePathRegex();

    [GeneratedRegex(@"^WidgetTree_\d+$")]
    private static partial Regex WidgetTreeSegmentRegex();

    [GeneratedRegex(@"_C_\d+$")]
    private static partial Regex InstanceNumberRegex();

    public static WidgetPath? TryParse(string name)
    {
        var m = InstancePathRegex().Match(name);
        if (!m.Success)
            return null;

        var parts = m.Groups["obj"].Value.Split('.').ToList();
        // First segment is the outer owner (e.g. BP_GameInstanceBase_C_2) whose instance number changes per PIE session.
        if (parts.Count > 1)
            parts.RemoveAt(0);
        parts.RemoveAll(p => WidgetTreeSegmentRegex().IsMatch(p));
        if (parts.Count == 0)
            return null;

        string? suffix = null;
        string last = parts[^1];
        int us = last.LastIndexOf('_');
        if (us > 0 && us < last.Length - 2)
        {
            string tail = last[(us + 1)..];
            if (tail.All(char.IsLetter))
            {
                suffix = tail;
                parts[^1] = last[..us];
            }
        }

        return new WidgetPath(m.Groups["cls"].Value, parts, suffix);
    }

    /// <summary>
    /// Stable identity across runs: widget scopes drop the engine/game-instance prefix (its instance
    /// numbers differ between PIE sessions); every other scope is keyed by its raw name.
    /// </summary>
    public static string JoinKey(string name) =>
        TryParse(name) is { } p ? "widget:" + string.Join('.', p.Segments) + (p.Suffix is null ? "" : "_" + p.Suffix) : name;

    /// <summary>Readable label: last two path segments + suffix for widget scopes, raw name otherwise.</summary>
    public static string ShortName(string name)
    {
        if (TryParse(name) is not { } p)
            return name;
        string path = string.Join(" › ", p.Segments.TakeLast(2));
        return p.Suffix is null ? path : $"{path} [{p.Suffix}]";
    }

    /// <summary><c>WBP_Card_C_3</c> → <c>WBP_Card_C_*</c> so sibling instances group together.</summary>
    public static string MergeInstance(string segment) => InstanceNumberRegex().Replace(segment, "_C_*");
}

public sealed class WidgetNode(string name)
{
    private readonly Dictionary<string, WidgetNode> _children = new(StringComparer.Ordinal);

    public string Name { get; } = name;
    public IEnumerable<WidgetNode> Children => _children.Values;
    /// <summary>Exclusive seconds recorded on this node itself, per scope suffix.</summary>
    public Dictionary<string, double> SelfBySuffix { get; } = new(StringComparer.Ordinal);
    public double SelfSeconds => SelfBySuffix.Values.Sum();
    /// <summary>Exclusive seconds of this node + all descendants (valid after <see cref="WidgetTree.Build"/>).</summary>
    public double SubtreeSeconds { get; private set; }
    public string? ExampleFullName { get; set; }

    public WidgetNode GetOrAdd(string childName)
    {
        if (!_children.TryGetValue(childName, out var child))
            _children[childName] = child = new WidgetNode(childName);
        return child;
    }

    public void AddSelf(string suffix, double seconds) =>
        SelfBySuffix[suffix] = SelfBySuffix.GetValueOrDefault(suffix) + seconds;

    internal double ComputeSubtree() => SubtreeSeconds = SelfSeconds + _children.Values.Sum(c => c.ComputeSubtree());
}

public static class WidgetTree
{
    public const string RootName = "(all widgets)";
    public const string UnparentedName = "(no instance path)";
    public const string NoSuffix = "(scope)";

    /// <summary>
    /// Builds a cost tree from widget instance paths. Uses EXCLUSIVE time only — inclusive would count
    /// a child's cost again in every ancestor.
    /// </summary>
    public static WidgetNode Build(TimerStats stats, bool mergeInstances)
    {
        var root = new WidgetNode(RootName);

        foreach (var row in stats.Rows)
        {
            var path = WidgetPaths.TryParse(row.Name);
            if (path == null)
            {
                // e.g. "WBP_ListOfTroopCards [InvalidationBox_0]" — widget cost without a parent chain.
                if (row.Name.StartsWith("WBP_", StringComparison.Ordinal))
                {
                    var leaf = root.GetOrAdd(UnparentedName).GetOrAdd(row.Name);
                    leaf.AddSelf(NoSuffix, row.ExclTotal);
                    leaf.ExampleFullName ??= row.Name;
                }
                continue;
            }

            var node = root;
            foreach (var seg in path.Segments)
                node = node.GetOrAdd(mergeInstances ? WidgetPaths.MergeInstance(seg) : seg);
            node.AddSelf(path.Suffix ?? NoSuffix, row.ExclTotal);
            node.ExampleFullName ??= row.Name;
        }

        root.ComputeSubtree();
        return root;
    }
}
