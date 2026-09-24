using System.Globalization;
using InsightsExportGui.Analysis;

namespace InsightsExportGui.Ui;

/// <summary>Widget instance paths rebuilt into a tree with self / subtree exclusive cost.</summary>
internal sealed class WidgetTreeTab : UserControl
{
    private const int AutoExpandDepth = 6;

    private readonly RunRegistry _registry;
    private readonly ComboBox _runBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly CheckBox _mergeCheck = new() { Text = "Merge instances (_C_0, _C_4 … → _C_*)", AutoSize = true, Padding = new Padding(8, 3, 0, 0) };
    private readonly Label _unitLabel = new()
    {
        Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 2, 8, 6), ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(4000, 0),
    };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, ShowNodeToolTips = true, HideSelection = false };

    public WidgetTreeTab(RunRegistry registry)
    {
        _registry = registry;
        Dock = DockStyle.Fill;
        _tree.Font = new Font(FontFamily.GenericMonospace, 9f);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
        top.Controls.AddRange([new Label { Text = "Run", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _runBox, _mergeCheck]);

        Controls.Add(_tree);
        Controls.Add(_unitLabel);
        Controls.Add(top);

        _runBox.SelectedIndexChanged += (_, _) => Rebuild();
        _mergeCheck.CheckedChanged += (_, _) => Rebuild();
        _registry.Changed += RefreshRuns;
        SizeChanged += (_, _) => _unitLabel.MaximumSize = new Size(Math.Max(200, ClientSize.Width), 0);
        RefreshRuns();
    }

    private void RefreshRuns()
    {
        string? id = (_runBox.SelectedItem as RunRecord)?.Id;
        var runs = _registry.Runs.OrderByDescending(r => r.CreatedUtc).ToArray();
        _runBox.BeginUpdate();
        _runBox.Items.Clear();
        _runBox.Items.AddRange(runs);
        _runBox.EndUpdate();
        int idx = id == null ? -1 : Array.FindIndex(runs, r => r.Id == id);
        _runBox.SelectedIndex = idx >= 0 ? idx : runs.Length > 0 ? 0 : -1;
        if (runs.Length == 0)
            Rebuild();
    }

    private void Rebuild()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        try
        {
            if (_runBox.SelectedItem is not RunRecord run)
            {
                _unitLabel.Text = "No runs registered yet.";
                return;
            }

            TimerStats stats;
            try
            {
                stats = TimerStatsCache.Get(run.Folder);
            }
            catch (Exception ex)
            {
                _unitLabel.Text = $"Could not load run: {ex.Message}";
                return;
            }

            var root = WidgetTree.Build(stats, _mergeCheck.Checked);
            long frames = stats.FrameCount;
            Func<double, double> toMs = frames > 0 ? s => s * 1000.0 / frames : s => s * 1000.0;

            _unitLabel.Text = (frames > 0
                    ? $"EXCLUSIVE time, ms per frame ({frames:N0} frames). "
                    : "No frame count (Slate::Prepass missing) — showing session totals in ms. ") +
                "Subtree = node + all descendants. Exclusive avoids counting a child's cost again in every parent. " +
                "Hover a node for its full scope name.";

            if (!root.Children.Any())
            {
                _unitLabel.Text += "\n\nNo widget instance-path scopes in this run.";
                return;
            }

            var rootNode = MakeNode(root, toMs);
            _tree.Nodes.Add(rootNode);
            AddChildren(rootNode, root, toMs);
            ExpandHeaviestPath(rootNode);
            _tree.SelectedNode = rootNode;
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private static void AddChildren(TreeNode parentNode, WidgetNode parent, Func<double, double> toMs)
    {
        foreach (var child in parent.Children.OrderByDescending(c => c.SubtreeSeconds))
        {
            var node = MakeNode(child, toMs);
            parentNode.Nodes.Add(node);
            AddChildren(node, child, toMs);
        }
    }

    private static TreeNode MakeNode(WidgetNode n, Func<double, double> toMs)
    {
        var inv = CultureInfo.InvariantCulture;
        string text = string.Create(inv, $"{toMs(n.SubtreeSeconds),9:0.000}  {n.Name}");
        if (n.SelfSeconds > 0)
        {
            var parts = n.SelfBySuffix.OrderByDescending(kv => kv.Value)
                .Select(kv => string.Create(inv, $"{kv.Key} {toMs(kv.Value):0.000}"));
            text += string.Create(inv, $"   (self {toMs(n.SelfSeconds):0.000}: {string.Join(", ", parts)})");
        }
        return new TreeNode(text) { ToolTipText = n.ExampleFullName ?? n.Name };
    }

    private static void ExpandHeaviestPath(TreeNode node)
    {
        for (int depth = 0; node != null && depth < AutoExpandDepth; depth++)
        {
            node.Expand();
            node = node.Nodes.Count > 0 ? node.Nodes[0] : null!;   // children are sorted heaviest-first
        }
    }
}
