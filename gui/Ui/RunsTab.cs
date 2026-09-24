using System.Text;
using InsightsExportGui.Analysis;

namespace InsightsExportGui.Ui;

/// <summary>Registered runs + capture checks for the selected one.</summary>
internal sealed class RunsTab : UserControl
{
    private readonly RunRegistry _registry;
    private readonly AppSettings _settings;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, ShowItemToolTips = true,
    };
    private readonly TextBox _checksBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };
    private readonly Button _importButton = new() { Text = "Import folder(s)...", AutoSize = true };
    private readonly Button _editButton = new() { Text = "Edit...", AutoSize = true };
    private readonly Button _removeButton = new() { Text = "Remove from list", AutoSize = true };
    private readonly Button _openButton = new() { Text = "Open folder", AutoSize = true };

    public RunsTab(RunRegistry registry, AppSettings settings)
    {
        _registry = registry;
        _settings = settings;
        Dock = DockStyle.Fill;

        _list.Columns.Add("Name", 180);
        _list.Columns.Add("Captured", 130);
        _list.Columns.Add("Frames", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Commit", 150);
        _list.Columns.Add("Notes", 260);
        _list.Columns.Add("Folder", 320);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        buttons.Controls.AddRange([_importButton, _editButton, _removeButton, _openButton,
            new Label
            {
                AutoSize = true, Padding = new Padding(8, 6, 0, 0), ForeColor = SystemColors.GrayText,
                Text = "Removing only takes a run off this list; files on disk are never deleted.",
            }]);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_list);
        var checksGroup = new GroupBox { Text = "Capture checks (selected run)", Dock = DockStyle.Fill };
        checksGroup.Controls.Add(_checksBox);
        split.Panel2.Controls.Add(checksGroup);

        Controls.Add(split);
        Controls.Add(buttons);
        // Size the split once the control has a real height (setting it earlier throws when out of range).
        bool splitSized = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitSized || split.Height < 250)
                return;
            split.SplitterDistance = (int)(split.Height * 0.55);
            splitSized = true;
        };

        _importButton.Click += (_, _) => Import();
        _editButton.Click += (_, _) => EditSelected();
        _list.DoubleClick += (_, _) => EditSelected();
        _removeButton.Click += (_, _) => RemoveSelected();
        _openButton.Click += (_, _) => { if (Selected is { } r) Dialogs.OpenFolder(this, r.Folder); };
        _list.SelectedIndexChanged += (_, _) => { UpdateButtons(); ShowChecks(); };
        _registry.Changed += Reload;

        Reload();
    }

    private RunRecord? Selected => _list.SelectedItems.Count > 0 ? (RunRecord)_list.SelectedItems[0].Tag! : null;

    private void Reload()
    {
        string? selectedId = Selected?.Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _registry.Runs.OrderByDescending(r => r.CreatedUtc))
        {
            bool exists = r.Exists;
            var item = new ListViewItem(r.Name) { Tag = r, ToolTipText = exists ? r.Folder : $"MISSING: {r.TimerStatsPath}" };
            item.SubItems.Add(r.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(r.FrameCount > 0 ? r.FrameCount.ToString("N0") : "—");
            item.SubItems.Add(r.CommitLabel);
            item.SubItems.Add(r.Notes?.ReplaceLineEndings(" ") ?? "");
            item.SubItems.Add(exists ? r.Folder : "(missing) " + r.Folder);
            if (!exists)
                item.ForeColor = SystemColors.GrayText;
            _list.Items.Add(item);
            if (r.Id == selectedId)
                item.Selected = true;
        }
        _list.EndUpdate();
        UpdateButtons();
        ShowChecks();
    }

    private void UpdateButtons()
    {
        bool any = Selected != null;
        _editButton.Enabled = _removeButton.Enabled = _openButton.Enabled = any;
    }

    private void ShowChecks()
    {
        if (Selected is not { } run)
        {
            _checksBox.Text = _registry.Runs.Count == 0
                ? "No runs yet. Export a trace, or use 'Import folder(s)...' to register existing export folders."
                : "Select a run.";
            return;
        }
        if (!run.Exists)
        {
            _checksBox.Text = $"timerstats.csv not found:\r\n{run.TimerStatsPath}";
            return;
        }

        try
        {
            var stats = TimerStatsCache.Get(run.Folder);
            var sb = new StringBuilder();
            sb.AppendLine($"{run.Name} — {stats.Rows.Count:N0} scopes, {stats.FrameCount:N0} frames");
            if (!string.IsNullOrEmpty(run.TracePath))
                sb.AppendLine($"Trace: {run.TracePath}");
            sb.AppendLine();
            foreach (var c in CaptureChecks.Run(stats))
                sb.AppendLine($"[{SeverityTag(c.Severity)}] {c.Title}: {c.Detail}").AppendLine();
            _checksBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            _checksBox.Text = $"Could not read timerstats.csv: {ex.Message}";
        }
    }

    public static string SeverityTag(CheckSeverity s) => s switch
    {
        CheckSeverity.Error => "ERROR",
        CheckSeverity.Warning => "WARN ",
        _ => "info ",
    };

    private void Import()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select an export folder, or a parent folder whose subfolders are exports",
            UseDescriptionForTitle = true,
            InitialDirectory = Dialogs.FirstExistingDir(_settings.LastImportDir, _settings.LastOutDir),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        _settings.LastImportDir = dlg.SelectedPath;
        _settings.Save();

        List<RunRecord> added;
        Cursor = Cursors.WaitCursor;
        try
        {
            added = _registry.ImportFolders(dlg.SelectedPath);
        }
        catch (Exception ex)
        {
            Dialogs.Fail(this, $"Import failed: {ex.Message}");
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        MessageBox.Show(this,
            added.Count == 0
                ? "No new folders with timerstats.csv found (already-registered folders are skipped)."
                : $"Imported {added.Count} run(s):\n  " + string.Join("\n  ", added.Select(a => a.Name)) +
                  "\n\nImported runs have no commit recorded (it isn't known after the fact); add context in Notes.",
            "Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void EditSelected()
    {
        if (Selected is not { } run)
            return;
        using var dlg = new RunInfoDialog("Edit run", run.Name, run.Notes);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        run.Name = dlg.RunName;
        run.Notes = dlg.Notes;
        _registry.Commit();
    }

    private void RemoveSelected()
    {
        if (Selected is not { } run)
            return;
        if (Dialogs.Confirm(this, $"Remove run '{run.Name}' from the list?\n\nFiles in {run.Folder} are NOT deleted."))
            _registry.Remove(run);
    }
}
