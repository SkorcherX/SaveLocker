using System.Drawing;
using System.Windows.Forms;

namespace LocalGameSync.Agent;

/// <summary>
/// A save-folder picker that shows folders <b>and the files inside them</b>, so the
/// user can navigate their drives and confirm the save files before choosing a
/// folder. (The stock <see cref="FolderBrowserDialog"/> hides files, so you can't
/// verify you picked the right place.) Returns the chosen folder in
/// <see cref="SelectedPath"/>.
///
/// DPI-safe docked layout, matching the other agent windows.
/// </summary>
internal sealed class SaveLocationDialog : Form
{
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ListView _files = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false
    };
    private readonly Label _selected = new() { AutoSize = true, Text = "(no folder selected)" };
    private readonly Button _ok = new() { Text = "Use this folder", AutoSize = true, Enabled = false };

    // Marks a not-yet-populated folder node so it shows an expand arrow.
    private const string Placeholder = "\0__lazy__";

    public string? SelectedPath { get; private set; }

    public SaveLocationDialog(string? initialPath = null)
    {
        Text = "LocalGameSync — choose save folder";
        Icon = AppResources.Icon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(560, 380);
        Padding = new Padding(12);

        _files.Columns.Add("Name", 280);
        _files.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _files.Columns.Add("Modified", 150);

        _tree.BeforeExpand += (_, e) => PopulateChildren(e.Node!);
        _tree.AfterSelect += (_, e) => OnFolderSelected(e.Node!);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 300
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_files);

        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(Control c, SizeType type = SizeType.AutoSize, float h = 0)
        {
            layout.RowStyles.Add(new RowStyle(type, h));
            c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 2, 0, 6);
            layout.Controls.Add(c);
        }
        AddRow(new Label { Text = "Navigate to the folder that holds this game's save files:", AutoSize = true });
        AddRow(split, SizeType.Percent, 100);
        AddRow(_selected);
        AddRow(buttons);
        Controls.Add(layout);

        LoadRoots();
        if (!string.IsNullOrEmpty(initialPath)) TrySelectPath(initialPath);
    }

    // ----- tree population -----

    private void LoadRoots()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            var node = new TreeNode(drive.Name) { Tag = drive.RootDirectory.FullName };
            node.Nodes.Add(new TreeNode(Placeholder));
            _tree.Nodes.Add(node);
        }
        _tree.EndUpdate();
    }

    /// <summary>Replace a folder node's placeholder with its real subdirectories.</summary>
    private void PopulateChildren(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Text != Placeholder) return; // already loaded
        node.Nodes.Clear();

        var path = (string)node.Tag!;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var child = new TreeNode(Path.GetFileName(dir)) { Tag = dir };
                if (HasSubdirectories(dir)) child.Nodes.Add(new TreeNode(Placeholder));
                node.Nodes.Add(child);
            }
        }
        catch (UnauthorizedAccessException) { /* skip folders we can't read */ }
        catch (IOException) { }
    }

    private static bool HasSubdirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).Any(); }
        catch { return false; }
    }

    private void OnFolderSelected(TreeNode node)
    {
        var path = (string)node.Tag!;
        SelectedPath = path;
        _selected.Text = $"Selected: {path}";
        _ok.Enabled = true;
        ShowFiles(path);
    }

    /// <summary>List the files directly in the folder so the user can confirm saves.</summary>
    private void ShowFiles(string path)
    {
        _files.BeginUpdate();
        _files.Items.Clear();
        try
        {
            var files = new DirectoryInfo(path).EnumerateFiles()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in files)
            {
                var item = new ListViewItem(f.Name);
                item.SubItems.Add(FormatSize(f.Length));
                item.SubItems.Add(f.LastWriteTime.ToString("g"));
                _files.Items.Add(item);
            }
            if (files.Count == 0)
                _files.Items.Add(new ListViewItem("(no files directly in this folder)"));
        }
        catch (UnauthorizedAccessException) { _files.Items.Add(new ListViewItem("(access denied)")); }
        catch (IOException) { }
        _files.EndUpdate();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1 << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1 << 10):0.0} KB",
        _ => $"{bytes} B"
    };

    /// <summary>Expand the tree down to an existing path and select it.</summary>
    private void TrySelectPath(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            // First part is the drive (e.g. "C:"); match the "C:\" root node.
            var nodes = _tree.Nodes;
            TreeNode? current = null;
            var acc = parts[0] + Path.DirectorySeparatorChar;
            current = FindChild(nodes, acc);
            for (var i = 1; i < parts.Length && current is not null; i++)
            {
                current.Expand(); // triggers PopulateChildren
                acc = Path.Combine(acc, parts[i]);
                current = FindChild(current.Nodes, acc);
            }
            if (current is not null) { _tree.SelectedNode = current; current.EnsureVisible(); }
        }
        catch { /* best-effort pre-selection */ }
    }

    private static TreeNode? FindChild(TreeNodeCollection nodes, string fullPath) =>
        nodes.Cast<TreeNode>().FirstOrDefault(n =>
            string.Equals((string?)n.Tag, fullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(((string?)n.Tag)?.TrimEnd(Path.DirectorySeparatorChar),
                          fullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
}
