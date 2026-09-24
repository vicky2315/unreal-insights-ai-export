using InsightsExportGui.Analysis;
using InsightsExportGui.Ui;

namespace InsightsExportGui;

/// <summary>Tab host: Export | Runs | Diff | Widget Tree. Owns shared settings + run registry.</summary>
public sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly RunRegistry _registry = new(AppSettings.RunsPath);
    private readonly ExportTab _exportTab;

    public MainForm()
    {
        Text = "Unreal Insights CSV Export";
        MinimumSize = new Size(760, 520);
        Size = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;

        _exportTab = new ExportTab(_settings, RegisterExportedRunAsync);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Export", _exportTab));
        tabs.TabPages.Add(Page("Runs", new RunsTab(_registry, _settings)));
        tabs.TabPages.Add(Page("Diff", new DiffTab(_registry)));
        tabs.TabPages.Add(Page("Widget Tree", new WidgetTreeTab(_registry)));
        Controls.Add(tabs);

        FormClosing += OnFormClosing;
        Shown += (_, _) =>
        {
            if (_registry.LoadWarning is { } warning)
                Dialogs.Fail(this, warning, "Run list");
        };
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    /// <summary>After a successful export: record commit (only if a repo is linked), name the run, register it, log checks.</summary>
    private async Task RegisterExportedRunAsync(ExportOptions o)
    {
        if (!o.Exports.Contains(ExportKind.TimerStats))
        {
            _exportTab.AppendLog("Run not registered: timerstats wasn't exported (needed for Runs / Diff / Widget Tree).");
            return;
        }

        GitCommit? commit = null;
        if (_settings.LinkedRepo is { Length: > 0 } repo)
        {
            (commit, string? error) = await Task.Run(() => (GitInfo.TryRead(repo, out var e), e));
            _exportTab.AppendLog(commit != null
                ? $"Recorded commit {commit.Label} from {repo}" + (error != null ? $" (note: {error})" : "")
                : $"Commit not recorded: {error}");
        }

        var stats = TimerStatsCache.Get(o.OutDir);
        var existing = _registry.FindByFolder(o.OutDir);
        string defaultName = existing?.Name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(o.OutDir));
        string info = existing != null
            ? "This folder is already registered; its entry will be updated."
            : "Name this run so you can find it in the Runs / Diff tabs.";

        using var dlg = new RunInfoDialog("Register run", defaultName, existing?.Notes, info);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            _exportTab.AppendLog("Run not registered (cancelled). You can import the folder from the Runs tab later.");
            return;
        }

        var record = existing ?? new RunRecord { Folder = Path.GetFullPath(o.OutDir) };
        record.Name = dlg.RunName;
        record.Notes = dlg.Notes;
        record.CreatedUtc = DateTime.UtcNow;
        record.TracePath = o.TracePath;
        record.FrameCount = stats.FrameCount;
        record.GitSha = commit?.Sha;
        record.GitDirty = commit?.Dirty ?? false;
        _registry.Upsert(record);

        _exportTab.AppendLog($"Registered run '{record.Name}' ({stats.FrameCount:N0} frames).");
        foreach (var c in CaptureChecks.Run(stats))
            _exportTab.AppendLog($"  [{RunsTab.SeverityTag(c.Severity)}] {c.Title}: {c.Detail}");
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exportTab.IsRunning)
        {
            if (!Dialogs.Confirm(this, "An export is running. Cancel it and exit?"))
            {
                e.Cancel = true;
                return;
            }
            _exportTab.CancelAndKill();
        }
        _exportTab.SaveUiIntoSettings();
    }
}
