using System.Drawing;
using System.Windows.Forms;

namespace LocalGameSync.Agent;

/// <summary>
/// Tray settings / connect window: set the server endpoint and machine name,
/// register (which fills in the API key with a Copy button), and view/remove the
/// games this machine tracks. A GUI over the register / set-server / list CLI
/// commands so everyday users never need a terminal.
///
/// Uses a docked TableLayoutPanel + AutoScaleMode.Dpi so nothing clips at any
/// display scaling.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AgentConfig _config;
    private readonly TextBox _server = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _key = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly CheckBox _autoStart = new() { Text = "Start with Windows (launch agent at login)", AutoSize = true };
    private readonly ListBox _games = new() { Dock = DockStyle.Fill };

    public SettingsForm(AgentConfig config)
    {
        _config = config;

        Text = "LocalGameSync — Settings";
        Icon = AppResources.Icon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 580);
        MinimumSize = new Size(460, 560);
        Padding = new Padding(12);

        _server.Text = config.ServerUrl;
        _name.Text = config.MachineName;
        _key.Text = config.ApiKey ?? "";
        _autoStart.Checked = AutoStart.IsEnabled();
        _autoStart.CheckedChanged += AutoStart_Toggled;

        // Save / Register row.
        var btnSave = new Button { Text = "Save", AutoSize = true };
        btnSave.Click += (_, _) => Save(notify: true);
        var btnReg = new Button { Text = "Register / Re-register", AutoSize = true };
        btnReg.Click += Register_Click;
        var saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        saveRow.Controls.Add(btnSave);
        saveRow.Controls.Add(btnReg);

        // API key + Copy row.
        var btnCopy = new Button { Text = "Copy", AutoSize = true };
        btnCopy.Click += (_, _) => CopyKey();
        var keyRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyRow.Controls.Add(_key, 0, 0);
        keyRow.Controls.Add(btnCopy, 1, 0);

        // Bottom buttons.
        var btnSetDir = new Button { Text = "Set save folder…", AutoSize = true };
        btnSetDir.Click += (_, _) => SetSaveFolder();
        var btnRemove = new Button { Text = "Remove selected", AutoSize = true };
        btnRemove.Click += (_, _) => RemoveSelected();
        var btnClose = new Button { Text = "Close", AutoSize = true };
        btnClose.Click += (_, _) => Close();
        var bottomRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        bottomRow.Controls.Add(btnSetDir);
        bottomRow.Controls.Add(btnRemove);
        bottomRow.Controls.Add(btnClose);

        // Vertical stack; the games list takes the remaining space.
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(Control c, SizeType type = SizeType.AutoSize, float height = 0)
        {
            layout.RowStyles.Add(new RowStyle(type, height));
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 2, 0, 6);
            layout.Controls.Add(c);
        }

        AddRow(new Label { Text = "Server URL", AutoSize = true });
        AddRow(_server);
        AddRow(new Label { Text = "Machine name", AutoSize = true });
        AddRow(_name);
        AddRow(saveRow);
        AddRow(new Label { Text = "API key (paste into the dashboard)", AutoSize = true });
        AddRow(keyRow);
        AddRow(_autoStart);
        AddRow(new Label { Text = "Tracked games", AutoSize = true });
        AddRow(_games, SizeType.Percent, 100);
        AddRow(bottomRow);

        Controls.Add(layout);
        RefreshGames();
    }

    private void Save(bool notify)
    {
        _config.ServerUrl = _server.Text.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(_name.Text))
            _config.MachineName = _name.Text.Trim();
        _config.Save();
        if (notify) MessageBox.Show(this, "Settings saved.", "LocalGameSync");
    }

    private async void Register_Click(object? sender, EventArgs e)
    {
        Save(notify: false); // persist server + name first
        try
        {
            var api = new ApiClient(_config.ServerUrl, null);
            var reg = await api.RegisterAsync(_config.MachineName);
            _config.ApiKey = reg.ApiKey;
            _config.MachineId = reg.MachineId;
            _config.Save();
            _key.Text = reg.ApiKey;
            MessageBox.Show(this,
                "Registered. Click Copy and paste the API key into the dashboard.",
                "LocalGameSync");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Registration failed: " + ex.Message,
                "LocalGameSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AutoStart_Toggled(object? sender, EventArgs e)
    {
        // Enabling makes a system change (a per-user startup registry entry) — get
        // explicit consent before writing it. Disabling just removes our own entry.
        if (_autoStart.Checked)
        {
            var consent = MessageBox.Show(this,
                "Add LocalGameSync to Windows startup?\n\n" +
                "This creates a per-user registry entry (HKCU\\…\\CurrentVersion\\Run) so the " +
                "agent launches automatically when you log in. No administrator rights are " +
                "needed. You can turn this off here at any time, and uninstalling LocalGameSync " +
                "removes the entry.",
                "Start with Windows",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (consent != DialogResult.Yes)
            {
                SetCheckedSilently(false);
                return;
            }
        }

        try
        {
            if (!AutoStart.SetEnabled(_autoStart.Checked))
                MessageBox.Show(this, "Could not update the login entry.", "LocalGameSync",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not update the login entry: " + ex.Message, "LocalGameSync",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetCheckedSilently(AutoStart.IsEnabled());
        }
    }

    /// <summary>Set the checkbox without re-firing <see cref="AutoStart_Toggled"/>.</summary>
    private void SetCheckedSilently(bool value)
    {
        _autoStart.CheckedChanged -= AutoStart_Toggled;
        _autoStart.Checked = value;
        _autoStart.CheckedChanged += AutoStart_Toggled;
    }

    private void CopyKey()
    {
        if (string.IsNullOrEmpty(_key.Text))
        {
            MessageBox.Show(this, "No API key yet — click Register first.", "LocalGameSync");
            return;
        }
        if (AppResources.TryCopy(_key.Text))
            MessageBox.Show(this, "API key copied to clipboard.", "LocalGameSync");
        else
            MessageBox.Show(this, "Could not access the clipboard. Select the text and copy manually.",
                "LocalGameSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void SetSaveFolder()
    {
        if (_games.SelectedItem is not GameItem gi)
        {
            MessageBox.Show(this, "Select a game in the list first.", "LocalGameSync");
            return;
        }

        using var dlg = new SaveLocationDialog(string.IsNullOrEmpty(gi.Game.SaveDirectory) ? null : gi.Game.SaveDirectory);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPath is { } path)
        {
            gi.Game.SaveDirectory = path;
            _config.Save();
            RefreshGames();
        }
    }

    private void RemoveSelected()
    {
        if (_games.SelectedItem is GameItem gi)
        {
            _config.Games.RemoveAll(g => g.GameId == gi.Game.GameId);
            _config.Save();
            RefreshGames();
        }
    }

    private void RefreshGames()
    {
        _games.Items.Clear();
        foreach (var g in _config.Games)
            _games.Items.Add(new GameItem(g));
    }

    private sealed record GameItem(TrackedGame Game)
    {
        public override string ToString() =>
            $"{Game.Name}  —  " +
            (string.IsNullOrEmpty(Game.SaveDirectory) ? "(no folder set — click Set save folder…)" : Game.SaveDirectory);
    }
}
