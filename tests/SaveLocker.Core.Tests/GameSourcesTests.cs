using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

public sealed class GameSourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-sources-" + Guid.NewGuid().ToString("N"));

    public GameSourcesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ScanCandidate Emulated(string name, string? system = "snes", string dir = "/home/deck/Emulation/saves/retroarch/saves",
        bool viaEmuDeck = false) =>
        new(name, dir, ScanSource.Emulator, false, EmulatorName: RetroArchSaves.EmulatorName, EmulatorSystem: system,
            ViaEmuDeck: viaEmuDeck);

    [Fact]
    public void An_emulated_game_names_its_emulator_then_its_console()
    {
        var s = GameSources.From(Emulated("Chrono Trigger", dir: "/home/deck/.var/app/org.libretro.RetroArch/config/retroarch/saves"));
        Assert.Equal(GameSourceKinds.Emulator, s.Kind);
        Assert.Equal("RetroArch", s.Detail);
        Assert.Equal(new[] { "SNES", "Flatpak" }, s.Tags);
        Assert.Equal("Emulator › RetroArch", GameSources.Describe(s));
    }

    [Fact]
    public void EmuDeck_is_said_by_the_scanner_not_read_off_the_folder()
    {
        // EmuDeck's saves folder is a link into the Flatpak's, and the folder is recorded by its real path:
        // the path alone would never say EmuDeck. Mutation-checked: deriving it from the path fails this.
        var linked = Emulated("Chrono Trigger", dir: "/home/deck/.var/app/org.libretro.RetroArch/config/retroarch/saves", viaEmuDeck: true);
        Assert.Equal(new[] { "SNES", "Flatpak", "EmuDeck" }, GameSources.From(linked).Tags);
        Assert.Equal(new[] { "SNES" }, GameSources.From(Emulated("Chrono Trigger")).Tags);
    }

    [Fact]
    public void A_source_compares_as_the_server_stores_it()
    {
        Assert.True(new GameSourceDto("steam", "Installed game ", [" Proton", ""]).SameAs(new GameSourceDto("steam", "Installed game", ["Proton"])));
        Assert.False(new GameSourceDto("steam", "Installed game").SameAs(new GameSourceDto("steam", "Non-Steam shortcut")));
    }

    [Fact]
    public void Each_scanner_source_has_its_own_two_levels()
    {
        Assert.Equal(new GameSourceDto("steam", "Installed game", ["AppID 1245620", "Proton"]),
            GameSources.From(new ScanCandidate("Elden Ring", "/x", ScanSource.SteamInstalled, true, SteamAppId: "1245620", PrefixPath: "/p")),
            new SourceComparer());
        Assert.Equal("Epic Games", GameSources.From(new ScanCandidate("Hades II", "/x", ScanSource.Heroic, false, Store: GameStore.Epic)).Detail);
        Assert.Equal(new GameSourceDto("save-folder", "Saved Games"),
            GameSources.From(new ScanCandidate("Hades II", Path.Combine("C:", "Users", "me", "Saved Games", "Hades II"), ScanSource.SaveRoot, false)),
            new SourceComparer());
    }

    [Fact]
    public void An_emulated_games_names_come_from_its_save_file_alone()
    {
        var japan = Emulated("Chrono Trigger") with { EmulatorRom = "Chrono Trigger (Japan)" };
        Assert.Equal(new[] { "Chrono Trigger", "Chrono Trigger (RetroArch)", "Chrono Trigger (Japan) (RetroArch)" },
            Enroller.NamesFor(japan));
        Assert.Equal(new[] { "Tetris", "Tetris (RetroArch)" }, Enroller.NamesFor(Emulated("Tetris") with { EmulatorRom = "Tetris" }));
        Assert.Equal(new[] { "Hades II" }, Enroller.NamesFor(new ScanCandidate("Hades II", "/x", ScanSource.Heroic, false)));
    }

    [Fact]
    public void A_pc_game_and_an_emulator_save_with_one_title_stay_two_rows()
    {
        var steam = new ScanCandidate("Chrono Trigger", "/pc", ScanSource.SteamInstalled, false);
        var usa = Emulated("Chrono Trigger") with { EmulatorRom = "Chrono Trigger (USA)" };
        var japan = Emulated("Chrono Trigger") with { EmulatorRom = "Chrono Trigger (Japan)" };
        Assert.Equal(3, new[] { steam, usa, japan }.Select(ScanCandidate.DedupeKey).Distinct().Count());
        Assert.Equal(ScanCandidate.DedupeKey(steam),
            ScanCandidate.DedupeKey(new ScanCandidate("CHRONO TRIGGER", null, ScanSource.SteamShortcut, false)));
    }

    [Fact]
    public void A_game_enrolled_before_sources_gets_one_from_a_scan_that_found_it_at_the_same_folder()
    {
        var saves = Path.Combine(_dir, "saves");
        Directory.CreateDirectory(saves);
        var config = AgentConfig.Load(Path.Combine(_dir, "state", "config.json"));
        var same = new TrackedGame { GameId = Guid.NewGuid(), Name = "Chrono Trigger", SaveDirectory = saves };
        var elsewhere = new TrackedGame { GameId = Guid.NewGuid(), Name = "Super Metroid", SaveDirectory = Path.Combine(_dir, "other") };
        config.MutateGames(l => { l.Add(same); l.Add(elsewhere); });
        config.Save();

        var filled = GameSources.Backfill(config, [Emulated("Chrono Trigger", dir: saves), Emulated("Super Metroid", dir: saves)]);

        Assert.Equal(new[] { same.GameId }, filled.Select(g => g.GameId));
        var reloaded = AgentConfig.Load(config.ConfigPath);
        Assert.Equal("RetroArch", reloaded.FindGame("Chrono Trigger")!.Source?.Detail);
        Assert.Null(reloaded.FindGame("Super Metroid")!.Source);
    }

    [Fact]
    public void A_source_another_process_saved_survives_a_stale_hosts_save()
    {
        var config = AgentConfig.Load(Path.Combine(_dir, "state", "config.json"));
        var game = new TrackedGame { GameId = Guid.NewGuid(), Name = "Chrono Trigger", SaveDirectory = _dir };
        config.MutateGames(l => l.Add(game));
        config.Save();

        AgentConfig.Load(config.ConfigPath).SaveGameSource(game.GameId, GameSources.Manual(GameSources.FolderPickedInAgent));
        config.Save();

        Assert.Equal(GameSourceKinds.Manual, AgentConfig.Load(config.ConfigPath).FindGame("Chrono Trigger")!.Source?.Kind);
    }

    private sealed class SourceComparer : IEqualityComparer<GameSourceDto>
    {
        public bool Equals(GameSourceDto? x, GameSourceDto? y) => x is not null && x.SameAs(y);
        public int GetHashCode(GameSourceDto obj) => obj.Kind.GetHashCode();
    }
}
