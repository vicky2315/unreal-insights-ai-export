namespace InsightsExportGui.Ui;

internal static class Dialogs
{
    public static bool Fail(IWin32Window owner, string message, string title = "Cannot continue")
    {
        MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    public static bool Confirm(IWin32Window owner, string message, string title = "Confirm") =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    public static string FirstExistingDir(params string?[] candidates) =>
        candidates.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)) ?? "";

    public static void OpenFolder(IWin32Window owner, string dir)
    {
        if (Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        else
            Fail(owner, $"Folder does not exist:\n{dir}");
    }
}

/// <summary>Name + notes editor used when registering or editing a run.</summary>
internal sealed class RunInfoDialog : Form
{
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _notesBox = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 90 };

    public string RunName => _nameBox.Text.Trim();
    public string? Notes => string.IsNullOrWhiteSpace(_notesBox.Text) ? null : _notesBox.Text.Trim();

    public RunInfoDialog(string title, string name, string? notes, string? info = null)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _nameBox.Text = name;
        _notesBox.Text = notes ?? "";

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(10), Width = 460 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));

        if (info != null)
        {
            var infoLabel = new Label { Text = info, AutoSize = true, MaximumSize = new Size(440, 0), Padding = new Padding(0, 0, 0, 8) };
            grid.Controls.Add(infoLabel, 0, 0);
            grid.SetColumnSpan(infoLabel, 2);
        }
        grid.Controls.Add(new Label { Text = "Name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        grid.Controls.Add(_nameBox, 1, 1);
        grid.Controls.Add(new Label { Text = "Notes", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left }, 0, 2);
        grid.Controls.Add(_notesBox, 1, 2);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        grid.Controls.Add(buttons, 1, 3);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && RunName.Length == 0)
            {
                Dialogs.Fail(this, "Name can't be empty.");
                e.Cancel = true;
            }
        };
    }
}
