using System.Diagnostics;
using InsightsExportGui.Analysis;

namespace InsightsExportGui.Ui;

/// <summary>Export tab: pick trace + output folder, run the headless export. Layout built in code.</summary>
internal sealed class ExportTab : UserControl
{
    private readonly AppSettings _settings;
    private readonly Func<ExportOptions, Task> _onSucceeded;

    private readonly TextBox _traceBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _exeBox = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _threadsCheck = new() { Text = "threads", AutoSize = true };
    private readonly CheckBox _timerStatsCheck = new() { Text = "timerstats", AutoSize = true };
    private readonly CheckBox _countersCheck = new() { Text = "counters", AutoSize = true };
    private readonly NumericUpDown _retriesBox = new() { Minimum = 1, Maximum = 20, Width = 60 };
    private readonly NumericUpDown _timeoutBox = new() { Minimum = 1, Maximum = 240, Width = 60 };
    private readonly CheckBox _keepLogCheck = new() { Text = "Keep insights.log", AutoSize = true };
    private readonly Label _repoLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) };
    private readonly Button _linkRepoButton = new() { Text = "Link...", AutoSize = true };
    private readonly Button _unlinkRepoButton = new() { Text = "Unlink", AutoSize = true };
    private readonly Button _exportButton = new() { Text = "Export", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button _openOutButton = new() { Text = "Open output folder", AutoSize = true };
    private readonly Label _statusLabel = new() { Text = "Idle", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _logBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f),
    };
    private readonly System.Windows.Forms.Timer _elapsedTimer = new() { Interval = 1000 };

    private CancellationTokenSource? _cts;
    private Stopwatch? _runClock;

    public bool IsRunning => _cts != null;

    public ExportTab(AppSettings settings, Func<ExportOptions, Task> onSucceeded)
    {
        _settings = settings;
        _onSucceeded = onSucceeded;
        Dock = DockStyle.Fill;

        BuildLayout();
        LoadSettingsIntoUi();

        _exportButton.Click += async (_, _) => await OnExportClickAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _openOutButton.Click += (_, _) => Dialogs.OpenFolder(this, _outBox.Text.Trim().Trim('"'));
        _linkRepoButton.Click += (_, _) => LinkRepo();
        _unlinkRepoButton.Click += (_, _) => UnlinkRepo();
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
    }

    private void BuildLayout()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddPathRow(grid, "Trace (.utrace)", _traceBox, BrowseTrace);
        AddPathRow(grid, "Output folder", _outBox, BrowseOut);
        AddPathRow(grid, "UnrealInsights.exe", _exeBox, BrowseExe);

        var exportsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        exportsRow.Controls.AddRange([_threadsCheck, _timerStatsCheck, _countersCheck]);
        AddRow(grid, "Export", exportsRow);

        var optionsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        optionsRow.Controls.AddRange(
        [
            new Label { Text = "Retries", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _retriesBox,
            new Label { Text = "Timeout/attempt (min)", AutoSize = true, Padding = new Padding(10, 6, 0, 0) },
            _timeoutBox,
            _keepLogCheck,
        ]);
        AddRow(grid, "Options", optionsRow);

        var repoRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        repoRow.Controls.AddRange([_repoLabel, _linkRepoButton, _unlinkRepoButton]);
        AddRow(grid, "Git commit", repoRow);

        var buttonsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        buttonsRow.Controls.AddRange([_exportButton, _cancelButton, _openOutButton, _statusLabel]);
        AddRow(grid, "", buttonsRow);

        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(_logBox, 0, grid.RowCount);
        grid.SetColumnSpan(_logBox, 3);
        grid.RowCount++;

        Controls.Add(grid);
    }

    private static void AddPathRow(TableLayoutPanel grid, string label, TextBox box, Action browse)
    {
        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) => browse();
        AddRow(grid, label, box);
        grid.Controls.Add(button, 2, grid.RowCount - 1);
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, grid.RowCount);
        grid.Controls.Add(control, 1, grid.RowCount);
        grid.RowCount++;
    }

    private void LoadSettingsIntoUi()
    {
        _exeBox.Text = _settings.InsightsExe is { } exe && File.Exists(exe) ? exe : Exporter.AutoDetectInsightsExe() ?? "";
        _outBox.Text = _settings.LastOutDir ?? "";
        _threadsCheck.Checked = _settings.Exports.Contains(nameof(ExportKind.Threads));
        _timerStatsCheck.Checked = _settings.Exports.Contains(nameof(ExportKind.TimerStats));
        _countersCheck.Checked = _settings.Exports.Contains(nameof(ExportKind.Counters));
        _retriesBox.Value = Math.Clamp(_settings.Retries, (int)_retriesBox.Minimum, (int)_retriesBox.Maximum);
        _timeoutBox.Value = Math.Clamp(_settings.TimeoutMinutes, (int)_timeoutBox.Minimum, (int)_timeoutBox.Maximum);
        _keepLogCheck.Checked = _settings.KeepLog;
        UpdateRepoRow();
    }

    public void SaveUiIntoSettings()
    {
        _settings.InsightsExe = _exeBox.Text.Trim();
        _settings.LastOutDir = _outBox.Text.Trim();
        if (Path.GetDirectoryName(_traceBox.Text.Trim()) is { Length: > 0 } traceDir)
            _settings.LastTraceDir = traceDir;
        _settings.Exports = SelectedExports().Select(k => k.ToString()).ToList();
        _settings.Retries = (int)_retriesBox.Value;
        _settings.TimeoutMinutes = (int)_timeoutBox.Value;
        _settings.KeepLog = _keepLogCheck.Checked;
        _settings.Save();
    }

    private List<ExportKind> SelectedExports()
    {
        var list = new List<ExportKind>();
        if (_threadsCheck.Checked) list.Add(ExportKind.Threads);
        if (_timerStatsCheck.Checked) list.Add(ExportKind.TimerStats);
        if (_countersCheck.Checked) list.Add(ExportKind.Counters);
        return list;
    }

    // ---- Linked git repo (opt-in) ----

    private void UpdateRepoRow()
    {
        bool linked = !string.IsNullOrEmpty(_settings.LinkedRepo);
        _repoLabel.Text = linked ? $"Linked repo: {_settings.LinkedRepo}" : "Not linked (no git commands are run)";
        _unlinkRepoButton.Enabled = linked;
        _linkRepoButton.Text = linked ? "Change..." : "Link...";
    }

    private void LinkRepo()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select the git repo whose commit should be recorded with each run",
            UseDescriptionForTitle = true,
            InitialDirectory = Dialogs.FirstExistingDir(_settings.LinkedRepo),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        string repo = dlg.SelectedPath;
        if (!GitInfo.LooksLikeRepo(repo) &&
            !Dialogs.Confirm(this, $"No .git found in:\n{repo}\n\nThe commit lookup will likely fail. Link anyway?"))
            return;

        string consent =
            $"Link this git repo?\n\n{repo}\n\n" +
            "After each successful export the app will run these read-only commands in that folder:\n" +
            $"    {GitInfo.Describe(GitInfo.RevParseArgs)}\n" +
            $"    {GitInfo.Describe(GitInfo.StatusArgs)}\n" +
            "(with GIT_OPTIONAL_LOCKS=0, so git doesn't refresh its index).\n\n" +
            "Nothing is written to the repo. The commit ID is stored only in:\n" +
            $"    {AppSettings.RunsPath}\n\n" +
            "Every lookup is shown in the export log. You can unlink at any time.";
        if (!Dialogs.Confirm(this, consent, "Link git repo"))
            return;

        _settings.LinkedRepo = repo;
        _settings.Save();
        UpdateRepoRow();
        AppendLog($"Linked git repo: {repo}");
    }

    private void UnlinkRepo()
    {
        AppendLog($"Unlinked git repo: {_settings.LinkedRepo}. No git commands will run. Existing runs keep their recorded commit.");
        _settings.LinkedRepo = null;
        _settings.Save();
        UpdateRepoRow();
    }

    // ---- Browse ----

    private void BrowseTrace()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select Unreal Insights trace",
            Filter = "Unreal trace (*.utrace)|*.utrace|All files (*.*)|*.*",
            InitialDirectory = Dialogs.FirstExistingDir(_settings.LastTraceDir, Exporter.DefaultTraceStore),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _traceBox.Text = dlg.FileName;
    }

    private void BrowseOut()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select output folder for CSVs",
            UseDescriptionForTitle = true,
            InitialDirectory = Dialogs.FirstExistingDir(_outBox.Text.Trim(), _settings.LastOutDir),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _outBox.Text = dlg.SelectedPath;
    }

    private void BrowseExe()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Locate UnrealInsights.exe",
            Filter = "UnrealInsights.exe|UnrealInsights.exe|Executables (*.exe)|*.exe",
            InitialDirectory = Dialogs.FirstExistingDir(Path.GetDirectoryName(_exeBox.Text.Trim()),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games")),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _exeBox.Text = dlg.FileName;
    }

    // ---- Export ----

    private async Task OnExportClickAsync()
    {
        if (!ValidateInputs(out var options))
            return;

        SaveUiIntoSettings();
        SetRunning(true);
        _logBox.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendLog);
        bool exported = false;

        try
        {
            await Task.Run(() => Exporter.RunAsync(options, progress, _cts.Token));
            _statusLabel.Text = $"Done in {_runClock!.Elapsed:mm\\:ss}";
            exported = true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled. UnrealInsights.exe was killed.");
            _statusLabel.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            _statusLabel.Text = "Failed";
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }

        if (!exported)
            return;
        try
        {
            await _onSucceeded(options);
        }
        catch (Exception ex)
        {
            // Registration problems never turn a good export into a failure; the CSVs are on disk.
            AppendLog("Run registration failed: " + ex.Message);
        }
    }

    /// <summary>Cancel from outside (window closing). Kills the child process.</summary>
    public void CancelAndKill()
    {
        _cts?.Cancel();
        Exporter.KillStrays();
    }

    /// <summary>All pre-flight checks; any Yes/No confirmation the user declines aborts the run.</summary>
    private bool ValidateInputs(out ExportOptions options)
    {
        options = null!;
        string trace = _traceBox.Text.Trim().Trim('"');
        string outDir = _outBox.Text.Trim().Trim('"');
        string exe = _exeBox.Text.Trim().Trim('"');
        var exports = SelectedExports();

        // Trace
        if (trace.Length == 0)
            return Dialogs.Fail(this, "Select a .utrace file.");
        if (!File.Exists(trace))
            return Dialogs.Fail(this, $"Trace file not found:\n{trace}");
        if (!string.Equals(Path.GetExtension(trace), ".utrace", StringComparison.OrdinalIgnoreCase))
            return Dialogs.Fail(this, "Trace must be a .utrace file.");
        if (IsLockedForWrite(trace) &&
            !Dialogs.Confirm(this, "The trace file is open for writing by another process (a UE session may still be recording).\n\n" +
                                   "Exporting now may analyze an incomplete trace. Continue anyway?"))
            return false;

        // UnrealInsights.exe
        if (!File.Exists(exe))
        {
            string? detected = Exporter.AutoDetectInsightsExe();
            if (detected == null)
            {
                Dialogs.Fail(this, "UnrealInsights.exe not found. Browse to it once; it will be remembered.");
                BrowseExe();
                return false;
            }
            _exeBox.Text = exe = detected;
            AppendLog($"Auto-detected UnrealInsights.exe: {exe}");
        }

        // Exports
        if (exports.Count == 0)
            return Dialogs.Fail(this, "Select at least one export.");
        if (!exports.Contains(ExportKind.TimerStats) &&
            !Dialogs.Confirm(this, "timerstats isn't selected, so this run can't be registered for the Runs / Diff / Widget Tree tabs.\n\nContinue?"))
            return false;

        // Output folder
        if (outDir.Length == 0)
            return Dialogs.Fail(this, "Select an output folder.");
        try
        {
            outDir = Path.GetFullPath(outDir);
        }
        catch (Exception ex)
        {
            return Dialogs.Fail(this, $"Invalid output folder: {ex.Message}");
        }
        if (!Directory.Exists(outDir))
        {
            if (!Dialogs.Confirm(this, $"Output folder does not exist:\n{outDir}\n\nCreate it?"))
                return false;
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                return Dialogs.Fail(this, $"Could not create output folder: {ex.Message}");
            }
        }
        if (!IsWritable(outDir))
            return Dialogs.Fail(this, $"Output folder is not writable:\n{outDir}");

        var existing = exports.Select(Exporter.CsvFileName).Where(f => File.Exists(Path.Combine(outDir, f))).ToList();
        if (existing.Count > 0 &&
            !Dialogs.Confirm(this, $"These files already exist in the output folder and will be overwritten:\n  {string.Join("\n  ", existing)}\n\nContinue?"))
            return false;

        // Running Insights instances (the exporter must kill them; the user may have a GUI session open).
        int running = Exporter.FindRunningInsights().Length;
        if (running > 0 &&
            !Dialogs.Confirm(this, $"{running} UnrealInsights.exe process(es) are running. Overlapping instances break the export, " +
                                   "so they will be force-closed (any open Insights window will be lost).\n\nContinue?"))
            return false;

        options = new ExportOptions
        {
            TracePath = trace,
            OutDir = outDir,
            InsightsExe = exe,
            Exports = exports,
            Retries = (int)_retriesBox.Value,
            AttemptTimeout = TimeSpan.FromMinutes((double)_timeoutBox.Value),
            KeepLog = _keepLogCheck.Checked,
        };
        return true;
    }

    private static bool IsLockedForWrite(string path)
    {
        try
        {
            // FileShare.Read fails if anyone else holds a write handle.
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;   // access denied etc. surfaces later as a real export failure
        }
    }

    private static bool IsWritable(string dir)
    {
        string probe = Path.Combine(dir, $".write_probe_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- UI state ----

    private void SetRunning(bool running)
    {
        _exportButton.Enabled = !running;
        _cancelButton.Enabled = running;
        foreach (Control c in new Control[] { _traceBox, _outBox, _exeBox, _threadsCheck, _timerStatsCheck,
                     _countersCheck, _retriesBox, _timeoutBox, _keepLogCheck, _linkRepoButton, _unlinkRepoButton })
            c.Enabled = !running;
        if (!running)
            UpdateRepoRow();

        if (running)
        {
            _runClock = Stopwatch.StartNew();
            _statusLabel.Text = "Running 00:00";
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            _runClock?.Stop();
        }
    }

    private void UpdateElapsed()
    {
        if (_runClock != null)
            _statusLabel.Text = $"Running {_runClock.Elapsed:mm\\:ss}";
    }

    public void AppendLog(string line) => _logBox.AppendText(line + Environment.NewLine);
}
