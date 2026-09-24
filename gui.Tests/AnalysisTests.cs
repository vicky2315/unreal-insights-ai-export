using InsightsExportGui.Analysis;
using Xunit;
using static InsightsExportGui.Tests.Fixtures;

namespace InsightsExportGui.Tests;

public class TimerStatsTests
{
    [Fact]
    public void Parse_HeaderSkipped_FrameCountFromPrepass()
    {
        var s = Parse(Prepass(7124), Row("Foo", 10, 0.5));
        Assert.Equal(2, s.Rows.Count);
        Assert.Equal(7124, s.FrameCount);
        Assert.Equal(0, s.SkippedLines);
    }

    [Fact]
    public void Parse_ColumnsMappedToSecondsFields()
    {
        var s = Parse(Row("Foo", 4, exclTotal: 0.2, exclMax: 0.09, exclMed: 0.04, inclTotal: 0.8, inclMax: 0.3, inclMed: 0.15));
        var r = s.Find("Foo")!;
        Assert.Equal(4, r.Count);
        Assert.Equal(0.8, r.InclTotal, 9);
        Assert.Equal(0.3, r.InclMax, 9);
        Assert.Equal(0.2, r.InclAvg, 9);
        Assert.Equal(0.15, r.InclMed, 9);
        Assert.Equal(0.2, r.ExclTotal, 9);
        Assert.Equal(0.09, r.ExclMax, 9);
        Assert.Equal(0.05, r.ExclAvg, 9);
        Assert.Equal(0.04, r.ExclMed, 9);
    }

    [Fact]
    public void Parse_NameContainingComma_ParsedRightToLeft()
    {
        var s = Parse(Row("Weird,Name,Here", 3, 0.1));
        Assert.NotNull(s.Find("Weird,Name,Here"));
    }

    [Fact]
    public void Parse_DuplicateNames_Merged()
    {
        var s = Parse(Row("Dup", 10, 1.0, exclMax: 0.2), Row("Dup", 30, 2.0, exclMax: 0.5));
        var r = Assert.Single(s.Rows);
        Assert.Equal(40, r.Count);
        Assert.Equal(3.0, r.ExclTotal, 9);
        Assert.Equal(0.5, r.ExclMax, 9);
        Assert.Equal(3.0 / 40, r.ExclAvg, 9);
    }

    [Fact]
    public void Parse_MalformedLines_SkippedAndCounted()
    {
        var s = Parse(Row("Good", 1, 0.1), "too,few,columns", "Bad,x,1,1,1,1,1,1,1,1,1,1,1");
        Assert.Single(s.Rows);
        Assert.Equal(2, s.SkippedLines);
    }

    [Fact]
    public void Parse_NoPrepass_FrameCountZero()
    {
        Assert.Equal(0, Parse(Row("Foo", 1, 0.1)).FrameCount);
    }

    [Theory]
    [InlineData(1000, 1000, ScopeKind.PerFrame)]
    [InlineData(1050, 1000, ScopeKind.PerFrame)]
    [InlineData(2000, 1000, ScopeKind.PerFrame)]
    [InlineData(1500, 1000, ScopeKind.Event)]
    [InlineData(50, 1000, ScopeKind.Event)]
    [InlineData(5, 0, ScopeKind.Event)]
    public void Classify(long count, long frames, ScopeKind expected) =>
        Assert.Equal(expected, TimerStats.Classify(count, frames));

    [Fact]
    public void PerFrameMs_DividesByFrames()
    {
        var s = Parse(Prepass(1000));
        Assert.Equal(2.0, s.PerFrameMs(2.0)!.Value, 9);   // 2 s over 1000 frames = 2 ms/frame
    }
}

public class CaptureCheckTests
{
    [Fact]
    public void NoPrepass_Error()
    {
        var checks = CaptureChecks.Run(Parse(Row("Foo", 1, 0.1)));
        Assert.Contains(checks, c => c.Severity == CheckSeverity.Error && c.Title == "No frame count");
    }

    [Fact]
    public void PrepassCountOne_CpuChannelError()
    {
        var checks = CaptureChecks.Run(Parse(Row(TimerStats.FrameTimerName, 1, 24.9)));
        Assert.Contains(checks, c => c.Severity == CheckSeverity.Error && c.Title == "CPU channel off");
    }

    [Fact]
    public void ShortCapture_Warns()
    {
        var checks = CaptureChecks.Run(Parse(Prepass(120)));
        Assert.Contains(checks, c => c.Severity == CheckSeverity.Warning && c.Title == "Short capture");
    }

    [Fact]
    public void ThrottleSignature_Warns()
    {
        // Shape of a real unfocused-PIE capture: rare ~317 ms sleeps, tiny median.
        var throttle = Row(CaptureChecks.ThrottleTimerName, 7123, exclTotal: 21.575, exclMax: 0.317, exclMed: 0.000006);
        var checks = CaptureChecks.Run(Parse(Prepass(7124), throttle));
        Assert.Contains(checks, c => c.Severity == CheckSeverity.Warning && c.Title == "Background throttle");
    }

    [Fact]
    public void SteadyFrameCap_NoThrottleWarning()
    {
        // Focused, frame-capped: every call waits a few ms — avg ≈ median, max modest.
        var capped = Row(CaptureChecks.ThrottleTimerName, 7000, exclTotal: 7000 * 0.004, exclMax: 0.012, exclMed: 0.004);
        var checks = CaptureChecks.Run(Parse(Prepass(7000), capped));
        Assert.False(CaptureChecks.HasProblems(checks));
    }

    [Fact]
    public void HitchScope_ListedAsInfo_SteadyCostIsNot()
    {
        var hitch = Row("OpenPanel", 5, exclTotal: 0.053, exclMax: 0.049, exclMed: 0.001);        // one 49 ms spike, rare
        var spiky = Row("UsuallyCheap", 7000, exclTotal: 7.0, exclMax: 0.040, exclMed: 0.001);     // 1 ms typical, one 40 ms
        var steady = Row("SteadyThing", 7000, exclTotal: 7000 * 0.02, exclMax: 0.03, exclMed: 0.02); // 20 ms every frame
        var checks = CaptureChecks.Run(Parse(Prepass(7000), hitch, spiky, steady));
        var h = Assert.Single(checks, c => c.Title.StartsWith("Hitch scopes"));
        Assert.Equal(CheckSeverity.Info, h.Severity);
        Assert.Contains("OpenPanel", h.Detail);
        Assert.Contains("UsuallyCheap", h.Detail);
        Assert.DoesNotContain("SteadyThing", h.Detail);
    }

    [Fact]
    public void HitchCheck_IgnoresHighFrequencyScopesAndThrottleTimer()
    {
        // Render-graph-like scope: ~250 calls/frame with one 300 ms outlier; throttle timer: sleeps each frame.
        var engine = Row("RenderGraphLike", 1_750_000, exclTotal: 150, exclMax: 0.33, exclMed: 0.00005);
        var throttle = Row(CaptureChecks.ThrottleTimerName, 7000, exclTotal: 21, exclMax: 0.317, exclMed: 0.000006);
        var checks = CaptureChecks.Run(Parse(Prepass(7000), engine, throttle));
        Assert.DoesNotContain(checks, c => c.Title.StartsWith("Hitch scopes"));
    }

    [Fact]
    public void HitchCheck_NoFrameCount_FrequencyFilterSkipped()
    {
        var hitch = Row("OpenPanel", 5, exclTotal: 0.053, exclMax: 0.049, exclMed: 0.001);
        var checks = CaptureChecks.Run(Parse(hitch));
        Assert.Contains(checks, c => c.Title.StartsWith("Hitch scopes") && c.Detail.Contains("OpenPanel"));
    }
}

public class WidgetPathTests
{
    [Fact]
    public void TryParse_DropsRootAndWidgetTreeHops_SplitsSuffix()
    {
        string name = WidgetScope("WBP_Card_C", 2, "Paint", "WBP_Hud_C_0", "WBP_Card_C_3");
        var p = WidgetPaths.TryParse(name)!;
        Assert.Equal("WBP_Card_C", p.ClassName);
        Assert.Equal(new[] { "WBP_Hud_C_0", "WBP_Card_C_3" }, p.Segments);
        Assert.Equal("Paint", p.Suffix);
    }

    [Fact]
    public void TryParse_NonWidgetScope_Null()
    {
        Assert.Null(WidgetPaths.TryParse("Slate::Prepass"));
        Assert.Null(WidgetPaths.TryParse("WBP_List [InvalidationBox_0]"));
    }

    [Fact]
    public void JoinKey_IgnoresGameInstanceNumber()
    {
        string a = WidgetScope("WBP_Card_C", 0, "Tick", "WBP_Hud_C_0", "WBP_Card_C_3");
        string b = WidgetScope("WBP_Card_C", 2, "Tick", "WBP_Hud_C_0", "WBP_Card_C_3");
        Assert.NotEqual(a, b);
        Assert.Equal(WidgetPaths.JoinKey(a), WidgetPaths.JoinKey(b));
    }

    [Fact]
    public void JoinKey_DifferentSuffix_Differs()
    {
        string tick = WidgetScope("WBP_Card_C", 0, "Tick", "WBP_Hud_C_0");
        string paint = WidgetScope("WBP_Card_C", 0, "Paint", "WBP_Hud_C_0");
        Assert.NotEqual(WidgetPaths.JoinKey(tick), WidgetPaths.JoinKey(paint));
    }

    [Fact]
    public void ShortName_LastTwoSegmentsPlusSuffix()
    {
        string name = WidgetScope("WBP_Icon_C", 0, "Tick", "WBP_Hud_C_0", "WBP_TopBar", "WBP_Icon");
        Assert.Equal("WBP_TopBar › WBP_Icon [Tick]", WidgetPaths.ShortName(name));
        Assert.Equal("Slate::Prepass", WidgetPaths.ShortName("Slate::Prepass"));
    }
}

public class WidgetTreeTests
{
    private static TimerStats Sample() => Parse(
        Prepass(1000),
        Row(WidgetScope("WBP_Hud_C", 0, "Paint", "WBP_Hud_C_0"), 1000, 0.5),
        Row(WidgetScope("WBP_Card_C", 0, "Paint", "WBP_Hud_C_0", "WBP_Card_C_3"), 1000, 1.0),
        Row(WidgetScope("WBP_Card_C", 0, "Tick", "WBP_Hud_C_0", "WBP_Card_C_3"), 1000, 0.25),
        Row(WidgetScope("WBP_Card_C", 0, "Paint", "WBP_Hud_C_0", "WBP_Card_C_4"), 1000, 2.0),
        Row("WBP_List [InvalidationBox_0]", 1000, 0.7),
        Row("Slate::DrawWindows", 1000, 9.0));   // non-widget: excluded from tree

    [Fact]
    public void Build_SubtreeSumsExclusive()
    {
        var root = WidgetTree.Build(Sample(), mergeInstances: false);
        var hud = root.Children.Single(c => c.Name == "WBP_Hud_C_0");
        Assert.Equal(0.5, hud.SelfSeconds, 9);
        Assert.Equal(0.5 + 1.0 + 0.25 + 2.0, hud.SubtreeSeconds, 9);

        var card3 = hud.Children.Single(c => c.Name == "WBP_Card_C_3");
        Assert.Equal(1.0, card3.SelfBySuffix["Paint"], 9);
        Assert.Equal(0.25, card3.SelfBySuffix["Tick"], 9);
    }

    [Fact]
    public void Build_UnparentedWidgetsGrouped_NonWidgetsExcluded()
    {
        var root = WidgetTree.Build(Sample(), mergeInstances: false);
        var orphan = root.Children.Single(c => c.Name == WidgetTree.UnparentedName);
        Assert.Equal(0.7, orphan.SubtreeSeconds, 9);
        Assert.Equal(0.5 + 1.0 + 0.25 + 2.0 + 0.7, root.SubtreeSeconds, 9);   // DrawWindows' 9.0 not included
    }

    [Fact]
    public void Build_MergeInstances_GroupsSiblings()
    {
        var root = WidgetTree.Build(Sample(), mergeInstances: true);
        var hud = root.Children.Single(c => c.Name == "WBP_Hud_C_*");
        var cards = Assert.Single(hud.Children);
        Assert.Equal("WBP_Card_C_*", cards.Name);
        Assert.Equal(1.0 + 0.25 + 2.0, cards.SubtreeSeconds, 9);
    }
}

public class RunDiffTests
{
    [Fact]
    public void PerFrame_NormalizesByEachRunsFrameCount()
    {
        var a = Parse(Prepass(1000), Row("Foo", 1000, 1.0));   // 1 ms/frame
        var b = Parse(Prepass(2000), Row("Foo", 2000, 1.0));   // 0.5 ms/frame
        var row = RunDiff.Compute(a, b, DiffMetric.PerFrameExcl).Single(r => r.Key == "Foo");
        Assert.Equal(1.0, row.A!.Value, 9);
        Assert.Equal(0.5, row.B!.Value, 9);
        Assert.Equal(-0.5, row.Delta!.Value, 9);
        Assert.Equal(-50.0, row.DeltaPercent!.Value, 6);
    }

    [Fact]
    public void MissingScope_NullOnThatSide()
    {
        var a = Parse(Prepass(100), Row("OnlyA", 10, 0.1));
        var b = Parse(Prepass(100), Row("OnlyB", 10, 0.1));
        var rows = RunDiff.Compute(a, b, DiffMetric.PerCallExcl);
        var onlyA = rows.Single(r => r.Key == "OnlyA");
        Assert.NotNull(onlyA.A);
        Assert.Null(onlyA.B);
        Assert.Null(onlyA.Delta);
    }

    [Fact]
    public void WidgetScopes_MatchAcrossPieSessions()
    {
        var a = Parse(Prepass(100), Row(WidgetScope("WBP_Card_C", 0, "Paint", "WBP_Hud_C_0", "WBP_Card_C_3"), 100, 0.2));
        var b = Parse(Prepass(100), Row(WidgetScope("WBP_Card_C", 2, "Paint", "WBP_Hud_C_0", "WBP_Card_C_3"), 100, 0.1));
        var widget = RunDiff.Compute(a, b, DiffMetric.PerFrameExcl).Single(r => r.Key.StartsWith("widget:"));
        Assert.NotNull(widget.A);
        Assert.NotNull(widget.B);
    }

    [Theory]
    [InlineData(1000, 1100, false)]
    [InlineData(1000, 1300, true)]
    [InlineData(0, 1000, true)]
    public void FrameCountsDiffer(long fa, long fb, bool expected)
    {
        var a = fa > 0 ? Parse(Prepass(fa)) : Parse(Row("x", 1, 0.1));
        var b = Parse(Prepass(fb));
        Assert.Equal(expected, RunDiff.FrameCountsDiffer(a, b));
    }

    [Fact]
    public void Markdown_EscapesPipesAndUsesInvariantNumbers()
    {
        var rows = new[] { new DiffRow("k", "a|b", "a|b", ScopeKind.PerFrame, 1.5, 1.25) };
        string md = RunDiff.ToMarkdown(rows, DiffMetric.PerCallExcl, "base", "new");
        Assert.Contains("| a\\|b | per-frame | 1.500 | 1.250 | -0.250 | -16.7% |", md);
    }
}

public class RunRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "insights-gui-tests-" + Guid.NewGuid().ToString("N"));

    public RunRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeExportFolder(string name, long frames)
    {
        string folder = Path.Combine(_dir, "exports", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TimerStats.FileName), Header + "\n" + Prepass(frames) + "\n");
        return folder;
    }

    [Fact]
    public void Upsert_SaveReload_RoundTrips()
    {
        string path = Path.Combine(_dir, "runs.json");
        var reg = new RunRegistry(path);
        reg.Upsert(new RunRecord { Name = "base", Folder = MakeExportFolder("base", 500), GitSha = "abc1234", GitDirty = true, Notes = "n" });

        var reloaded = new RunRegistry(path);
        var r = Assert.Single(reloaded.Runs);
        Assert.Equal("base", r.Name);
        Assert.Equal("abc1234+uncommitted", r.CommitLabel);
        Assert.Null(reloaded.LoadWarning);
    }

    [Fact]
    public void Upsert_SameFolder_ReplacesInsteadOfDuplicating()
    {
        var reg = new RunRegistry(Path.Combine(_dir, "runs.json"));
        string folder = MakeExportFolder("run", 500);
        reg.Upsert(new RunRecord { Name = "first", Folder = folder });
        reg.Upsert(new RunRecord { Name = "second", Folder = folder + Path.DirectorySeparatorChar });
        Assert.Equal("second", Assert.Single(reg.Runs).Name);
    }

    [Fact]
    public void ImportFolders_AddsSubfoldersWithTimerStats_SkipsKnownAndEmpty()
    {
        var reg = new RunRegistry(Path.Combine(_dir, "runs.json"));
        MakeExportFolder("a", 700);
        MakeExportFolder("b", 800);
        Directory.CreateDirectory(Path.Combine(_dir, "exports", "empty"));

        var first = reg.ImportFolders(Path.Combine(_dir, "exports"));
        Assert.Equal(2, first.Count);
        Assert.Equal(800, first.Single(r => r.Name == "b").FrameCount);

        Assert.Empty(reg.ImportFolders(Path.Combine(_dir, "exports")));
        Assert.Equal(2, reg.Runs.Count);
    }

    [Fact]
    public void Remove_NeverDeletesFiles()
    {
        var reg = new RunRegistry(Path.Combine(_dir, "runs.json"));
        string folder = MakeExportFolder("keep", 100);
        var rec = new RunRecord { Name = "keep", Folder = folder };
        reg.Upsert(rec);
        reg.Remove(rec);
        Assert.Empty(reg.Runs);
        Assert.True(File.Exists(Path.Combine(folder, TimerStats.FileName)));
    }

    [Fact]
    public void CorruptFile_MovedAside_NotOverwritten()
    {
        string path = Path.Combine(_dir, "runs.json");
        File.WriteAllText(path, "{ not json");
        var reg = new RunRegistry(path);
        Assert.Empty(reg.Runs);
        Assert.NotNull(reg.LoadWarning);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "runs.json.corrupt-*"));
    }
}
