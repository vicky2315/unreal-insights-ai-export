using InsightsExportGui.Analysis;

namespace InsightsExportGui.Ui;

/// <summary>Compare two registered runs scope-by-scope.</summary>
internal sealed class DiffTab : UserControl
{
    private enum ShowFilter { All, InBoth, OnlyA, OnlyB }

    private const int ColScope = 0, ColKind = 1, ColA = 2, ColB = 3, ColDelta = 4, ColPct = 5;

    private readonly RunRegistry _registry;

    private readonly ComboBox _aBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ComboBox _bBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ComboBox _metricBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _showBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly TextBox _filterBox = new() { Width = 180, PlaceholderText = "filter scope name" };
    private readonly NumericUpDown _minBox = new() { DecimalPlaces = 3, Increment = 0.01m, Maximum = 1_000_000, Value = 0.01m, Width = 80 };
    private readonly Button _copyButton = new() { Text = "Copy as Markdown", AutoSize = true };
    private readonly Label _banner = new()
    {
        Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 6), Visible = false,
        BackColor = Color.FromArgb(255, 244, 196), ForeColor = Color.FromArgb(90, 60, 0),
        MaximumSize = new Size(4000, 0),
    };
    private readonly Label _note = new()
    {
        Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 2, 8, 6), ForeColor = SystemColors.GrayText,
        Text = "Per-call averages compare well across runs; whole-frame totals are noisy unless the scene is held fixed. " +
               "Widget scopes are matched by instance path (game-instance prefix ignored).",
    };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, ShowItemToolTips = true, HideSelection = false,
    };
    private readonly Label _countLabel = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };

    private List<DiffRow> _rows = [];
    private List<DiffRow> _visible = [];
    private DiffMetric _computedMetric;
    private int _sortColumn = ColDelta;
    private bool _sortDescending = true;

    public DiffTab(RunRegistry registry)
    {
        _registry = registry;
        Dock = DockStyle.Fill;

        foreach (var m in Enum.GetValues<DiffMetric>())
            _metricBox.Items.Add(new MetricItem(m));
        _metricBox.SelectedIndex = 0;
        _showBox.Items.AddRange(["All", "In both", "Only in A", "Only in B"]);
        _showBox.SelectedIndex = 0;

        _list.Columns.Add("Scope", 420);
        _list.Columns.Add("Kind", 75);
        _list.Columns.Add("A", 90, HorizontalAlignment.Right);
        _list.Columns.Add("B", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Δ", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Δ %", 75, HorizontalAlignment.Right);

        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
        row1.Controls.AddRange([Lbl("A (base)"), _aBox, Lbl("B (new)"), _bBox, Lbl("Metric"), _metricBox]);
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 0, 6, 4) };
        row2.Controls.AddRange([Lbl("Show"), _showBox, Lbl("Filter"), _filterBox, Lbl("Hide below"), _minBox, _copyButton, _countLabel]);

        // Dock order: last added docks first (top-most).
        Controls.Add(_list);
        Controls.Add(_note);
        Controls.Add(_banner);
        Controls.Add(row2);
        Controls.Add(row1);

        _aBox.SelectedIndexChanged += (_, _) => Recompute();
        _bBox.SelectedIndexChanged += (_, _) => Recompute();
        _metricBox.SelectedIndexChanged += (_, _) => Recompute();
        _showBox.SelectedIndexChanged += (_, _) => ApplyView();
        _filterBox.TextChanged += (_, _) => ApplyView();
        _minBox.ValueChanged += (_, _) => ApplyView();
        _list.ColumnClick += OnColumnClick;
        _copyButton.Click += (_, _) => CopyMarkdown();
        _registry.Changed += RefreshRuns;
        // AutoSize labels only wrap within MaximumSize; track the tab width so long banners wrap.
        SizeChanged += (_, _) => _banner.MaximumSize = _note.MaximumSize = new Size(Math.Max(200, ClientSize.Width), 0);

        RefreshRuns();
    }

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(8, 6, 0, 0) };

    private sealed record MetricItem(DiffMetric Metric)
    {
        public override string ToString() => RunDiff.Label(Metric);
    }

    private DiffMetric Metric => ((MetricItem)_metricBox.SelectedItem!).Metric;

    private void RefreshRuns()
    {
        var runs = _registry.Runs.OrderByDescending(r => r.CreatedUtc).ToArray();
        string? aId = (_aBox.SelectedItem as RunRecord)?.Id;
        string? bId = (_bBox.SelectedItem as RunRecord)?.Id;

        foreach (var box in new[] { _aBox, _bBox })
        {
            box.BeginUpdate();
            box.Items.Clear();
            box.Items.AddRange(runs);
            box.EndUpdate();
        }

        // Default: B = newest, A = the one before it.
        Select(_aBox, aId, runs.Length > 1 ? 1 : -1);
        Select(_bBox, bId, runs.Length > 0 ? 0 : -1);
        Recompute();
    }

    private static void Select(ComboBox box, string? id, int fallbackIndex)
    {
        int idx = id == null ? -1 : box.Items.Cast<RunRecord>().ToList().FindIndex(r => r.Id == id);
        box.SelectedIndex = idx >= 0 ? idx : fallbackIndex;
    }

    private void Recompute()
    {
        _rows = [];
        _banner.Visible = false;

        if (_aBox.SelectedItem is not RunRecord a || _bBox.SelectedItem is not RunRecord b)
        {
            ShowBanner(_registry.Runs.Count < 2 ? "Register at least two runs (Export or Runs → Import) to compare." : "Pick runs A and B.");
            ApplyView();
            return;
        }

        TimerStats sa, sb;
        try
        {
            sa = TimerStatsCache.Get(a.Folder);
            sb = TimerStatsCache.Get(b.Folder);
        }
        catch (Exception ex)
        {
            ShowBanner($"Could not load a run: {ex.Message}");
            ApplyView();
            return;
        }

        _computedMetric = Metric;
        _rows = RunDiff.Compute(sa, sb, _computedMetric);

        var warnings = new List<string>();
        if (a.Id == b.Id)
            warnings.Add("A and B are the same run.");
        if (RunDiff.FrameCountsDiffer(sa, sb))
            warnings.Add($"Frame counts differ by more than {RunDiff.FrameCountTolerance:P0} (A {sa.FrameCount:N0}, B {sb.FrameCount:N0}).");
        foreach (var (run, stats) in new[] { (a, sa), (b, sb) })
        {
            var problems = CaptureChecks.Run(stats).Where(c => c.Severity != CheckSeverity.Info).Select(c => c.Title).ToList();
            if (problems.Count > 0)
                warnings.Add($"'{run.Name}' failed checks: {string.Join(", ", problems)} (see Runs tab).");
        }
        if ((_computedMetric is DiffMetric.PerFrameExcl or DiffMetric.PerFrameIncl) && (sa.FrameCount == 0 || sb.FrameCount == 0))
            warnings.Add("A run has no frame count, so per-frame values are blank. Use a per-call metric.");
        if (warnings.Count > 0)
            ShowBanner("Directional only — " + string.Join(" ", warnings));

        ApplyView();
    }

    private void ShowBanner(string text)
    {
        _banner.Text = text;
        _banner.Visible = true;
    }

    private void ApplyView()
    {
        var show = (ShowFilter)Math.Max(0, _showBox.SelectedIndex);
        string filter = _filterBox.Text.Trim();
        double min = (double)_minBox.Value;

        IEnumerable<DiffRow> q = _rows;
        q = show switch
        {
            ShowFilter.InBoth => q.Where(r => r.A.HasValue && r.B.HasValue),
            ShowFilter.OnlyA => q.Where(r => r.A.HasValue && !r.B.HasValue),
            ShowFilter.OnlyB => q.Where(r => !r.A.HasValue && r.B.HasValue),
            _ => q,
        };
        if (filter.Length > 0)
            q = q.Where(r => r.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        q = q.Where(r => Math.Max(Math.Abs(r.A ?? 0), Math.Abs(r.B ?? 0)) >= min);

        _visible = Sort(q).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        bool time = RunDiff.IsTime(_computedMetric);
        foreach (var r in _visible)
        {
            var item = new ListViewItem(r.DisplayName) { ToolTipText = r.FullName, UseItemStyleForSubItems = false };
            item.SubItems.Add(RunDiff.KindLabel(r.Kind));
            item.SubItems.Add(RunDiff.FormatValue(r.A, _computedMetric));
            item.SubItems.Add(RunDiff.FormatValue(r.B, _computedMetric));
            var delta = item.SubItems.Add(RunDiff.FormatDelta(r.Delta, _computedMetric));
            var pct = item.SubItems.Add(RunDiff.FormatPercent(r.DeltaPercent));
            if (time && r.Delta is { } d && d != 0)
                delta.ForeColor = pct.ForeColor = d > 0 ? Color.Firebrick : Color.ForestGreen;   // more time = worse
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        _countLabel.Text = $"{_visible.Count:N0} of {_rows.Count:N0} scopes";
        _copyButton.Enabled = _visible.Count > 0;
    }

    private IEnumerable<DiffRow> Sort(IEnumerable<DiffRow> rows)
    {
        if (_sortColumn is ColScope or ColKind)
        {
            Func<DiffRow, string> key = _sortColumn == ColScope ? r => r.DisplayName : r => RunDiff.KindLabel(r.Kind);
            return _sortDescending
                ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);
        }

        Func<DiffRow, double?> num = _sortColumn switch
        {
            ColA => r => r.A,
            ColB => r => r.B,
            ColDelta => r => r.Delta,
            _ => r => r.DeltaPercent,
        };
        // Blanks (scope missing from a run) always sink to the bottom.
        return _sortDescending
            ? rows.OrderBy(r => num(r).HasValue ? 0 : 1).ThenByDescending(r => num(r))
            : rows.OrderBy(r => num(r).HasValue ? 0 : 1).ThenBy(r => num(r));
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == _sortColumn)
            _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = e.Column;
            _sortDescending = e.Column is not (ColScope or ColKind);
        }
        ApplyView();
    }

    private void CopyMarkdown()
    {
        string a = (_aBox.SelectedItem as RunRecord)?.Name ?? "A";
        string b = (_bBox.SelectedItem as RunRecord)?.Name ?? "B";
        string md = RunDiff.ToMarkdown(_visible, _computedMetric, a, b);
        if (_banner.Visible)
            md = $"> {_banner.Text}\n\n" + md;
        Clipboard.SetText(md);
        _countLabel.Text = $"Copied {_visible.Count:N0} rows";
    }
}
