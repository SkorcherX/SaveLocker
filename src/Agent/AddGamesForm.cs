using System.Drawing;
using System.Windows.Forms;
using LocalGameSync.Shared;

namespace LocalGameSync.Agent;

/// <summary>
/// Tray "Add games…" window: runs <see cref="GameScanner"/> and shows discovered
/// candidates as a checklist. The user ticks games (optionally fixing the save
/// folder) and enrolls them — the same path the <c>add-game</c> CLI takes:
/// create the server <c>Game</c> + add a local <see cref="TrackedGame"/>.
///
/// Uses a docked TableLayoutPanel + AutoScaleMode.Dpi so nothing clips at any
/// display scaling (see WS1 notes in Progress).
/// </summary>
internal sealed class AddGamesForm : Form
{
    private readonly AgentConfig _config;
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly Label _status = new() { AutoSize = true, Text = "" };
    private readonly Button _rescan = new() { Text = "Rescan", AutoSize = true };
    private readonly Button _setDir = new() { Text = "Set save folder…", AutoSize = true };
    private readonly Button _hideCloud = new() { Text = "Hide Steam Cloud", AutoSize = true };
    private readonly Button _enroll = new() { Text = "Enroll selected", AutoSize = true };
    private readonly Button _close = new() { Text = "Close", AutoSize = true };

    private bool _hideCloudGames;

    /// <summary>True if at least one game was enrolled, so the tray can rebuild.</summary>
    public bool EnrolledAny { get; private set; }

    public AddGamesForm(AgentConfig config)
    {
        _config = config;

        Text = "LocalGameSync — Add games";
        Icon = AppResources.Icon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 560);
        MinimumSize = new Size(520, 460);
        Padding = new Padding(12);

        _rescan.Click += (_, _) => _ = RescanAsync();
        _setDir.Click += (_, _) => SetSaveFolder();
        _hideCloud.Click += (_, _) => ToggleHideCloud();
        _enroll.Click += (_, _) => _ = EnrollSelectedAsync();
        _close.Click += (_, _) => Close();

        var topRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        topRow.Controls.Add(_rescan);
        topRow.Controls.Add(_setDir);
        topRow.Controls.Add(_hideCloud);

        var bottomRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        bottomRow.Controls.Add(_enroll);
        bottomRow.Controls.Add(_close);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(Control c, SizeType type = SizeType.AutoSize, float height = 0)
        {
            layout.RowStyles.Add(new RowStyle(type, height));
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 2, 0, 6);
            layout.Controls.Add(c);
        }

        AddRow(new Label
        {
            AutoSize = true,
            Text = "Tick games to sync. Games without a known save folder need one set before enrolling."
        });
        AddRow(topRow);
        AddRow(_list, SizeType.Percent, 100);
        AddRow(_status);
        AddRow(bottomRow);

        Controls.Add(layout);

        Shown += (_, _) => _ = RescanAsync();
    }

    // ----- scanning -----

    private async Task RescanAsync()
    {
        SetBusy(true, "Scanning for games…");
        try
        {
            var detection = new Detection(_config);
            var scanner = new GameScanner(detection);
            var candidates = await scanner.ScanAsync();

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in candidates)
            {
                if (_hideCloudGames && c.HasSteamCloud) continue;
                _list.Items.Add(new CandidateItem(c));
            }
            _list.EndUpdate();

            _status.Text = _list.Items.Count == 0
                ? "No games discovered."
                : $"Found {_list.Items.Count} candidate(s). Already-enrolled games are skipped on enroll.";
        }
        catch (Exception ex)
        {
            _status.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ----- save folder editing -----

    private void SetSaveFolder()
    {
        if (_list.SelectedItem is not CandidateItem item)
        {
            MessageBox.Show(this, "Select a game in the list first.", "LocalGameSync");
            return;
        }

        using var dlg = new SaveLocationDialog(item.SaveDir ?? item.Candidate.InstallDir);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPath is { } path)
        {
            item.SaveDir = path;
            // Refresh the row text to show the new folder.
            var i = _list.SelectedIndex;
            var wasChecked = _list.GetItemChecked(i);
            _list.Items[i] = item;
            _list.SetItemChecked(i, wasChecked);
        }
    }

    private void ToggleHideCloud()
    {
        _hideCloudGames = !_hideCloudGames;
        _hideCloud.Text = _hideCloudGames ? "Show Steam Cloud" : "Hide Steam Cloud";
        _ = RescanAsync();
    }

    // ----- enrollment -----

    private async Task EnrollSelectedAsync()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            MessageBox.Show(this, "Not registered yet. Open Settings… and click Register first.",
                "LocalGameSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var picked = _list.CheckedItems.Cast<CandidateItem>().ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one game to enroll.", "LocalGameSync");
            return;
        }

        // Every picked game needs a save folder; offer to set any that are missing.
        var missing = picked.Where(p => string.IsNullOrEmpty(p.SaveDir)).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this,
                "These games have no save folder set:\n  " +
                string.Join("\n  ", missing.Select(m => m.Candidate.Name)) +
                "\n\nSelect each and use “Set save folder…”, then enroll again.",
                "LocalGameSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true, "Enrolling…");
        var api = new ApiClient(_config.ServerUrl, _config.ApiKey);
        var enrolled = 0;
        var skipped = 0;
        try
        {
            foreach (var item in picked)
            {
                var c = item.Candidate;
                if (_config.FindGame(c.Name) is not null) { skipped++; continue; }

                var game = await api.CreateGameAsync(new CreateGameRequest(c.Name, c.ManifestKey, null));
                _config.Games.Add(new TrackedGame
                {
                    GameId = game.Id,
                    Name = game.Name,
                    ManifestKey = c.ManifestKey,
                    SaveDirectory = item.SaveDir!
                });
                enrolled++;
            }
            _config.Save();
            EnrolledAny |= enrolled > 0;

            var msg = $"Enrolled {enrolled} game(s)." + (skipped > 0 ? $" Skipped {skipped} already tracked." : "");
            _status.Text = msg;
            MessageBox.Show(this, msg, "LocalGameSync");
            await RescanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Enroll failed: " + ex.Message,
                "LocalGameSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ----- helpers -----

    private void SetBusy(bool busy, string? status = null)
    {
        if (status is not null) _status.Text = status;
        _list.Enabled = !busy;
        _rescan.Enabled = _setDir.Enabled = _hideCloud.Enabled = _enroll.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    /// <summary>List row wrapping a candidate with an editable, resolved save folder.</summary>
    private sealed class CandidateItem
    {
        public ScanCandidate Candidate { get; }
        public string? SaveDir { get; set; }

        public CandidateItem(ScanCandidate c)
        {
            Candidate = c;
            SaveDir = c.SuggestedSaveDir;
        }

        public override string ToString()
        {
            var cloud = Candidate.HasSteamCloud ? " [Steam Cloud]" : "";
            var save = string.IsNullOrEmpty(SaveDir) ? "(set save folder)" : SaveDir;
            return $"{Candidate.Name}  <{Candidate.Source}>{cloud}  —  {save}";
        }
    }
}
