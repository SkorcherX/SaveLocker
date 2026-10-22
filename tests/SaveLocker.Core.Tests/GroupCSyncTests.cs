using SaveLocker.Agent;
using Xunit;
using F = SaveLocker.Agent.Tests.ConsoleSaveFixtures;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// The Group C emulators end to end against a real server (tasks/emulator-saves Phase 2): one game's saves on a
/// PCSX2 folder card, a DuckStation per-game card and a Dolphin Wii title folder travel between two machines with
/// their states — and the other games sharing the card or folder, and the card's own superblock, never move.
/// </summary>
public sealed class GroupCSyncTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-groupc-" + Guid.NewGuid().ToString("N"));
    // The class shares one server, so each test's game is its own.
    private readonly string _id = Random.Shared.Next(10000, 99999).ToString();

    public GroupCSyncTests(ServerProcess server)
    {
        _server = server;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<AgentConfig> Machine(string name)
    {
        var config = AgentConfig.Load(Path.Combine(_dir, name, "state", "config.json"));
        config.ServerUrl = _server.Url;
        var reg = await new ApiClient(config.ServerUrl, null).RegisterAsync(name + _id);
        config.ApiKey = reg.ApiKey;
        config.MachineId = reg.MachineId;
        config.MachineName = name;
        config.Save();
        return config;
    }

    private string P(string machine, string rel) => Path.Combine(_dir, machine, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string machine, string rel, string content)
    {
        var path = P(machine, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string machine, string rel) => File.ReadAllText(P(machine, rel));

    private static async Task<TrackedGame> Enroll(AgentConfig config, IReadOnlyList<ScanCandidate> found, string rom)
    {
        var id = found.ToList().FindIndex(c => c.EmulatorRom == rom);
        Assert.True(id >= 0, $"no candidate for {rom}");
        Assert.Equal((1, 0), await Enroller.EnrollAsync(config, found, new[] { id }));
        return Enroller.TrackedFor(AgentConfig.Load(config.ConfigPath), found[id])!;
    }

    [Fact]
    public async Task A_ps2_game_on_a_folder_card_moves_alone_with_its_states()
    {
        var serial = $"SLUS-{_id}";
        var save = $"BA{serial}";
        // Windows (EmuDeck's card name there): the game's two save folders, another game, and a state with its backup copy.
        Write("win", "pcsx2/memcards/MC1.ps2/_pcsx2_superblock", "win-superblock");
        F.Write(P("win", $"pcsx2/memcards/MC1.ps2/{save}SYS/icon.sys"), F.IconSys($"Game {_id}"));
        Write("win", $"pcsx2/memcards/MC1.ps2/{save}SYS/_pcsx2_index", "win-index");
        Write("win", $"pcsx2/memcards/MC1.ps2/{save}S01/data", "win-slot1");
        Write("win", "pcsx2/memcards/MC1.ps2/BESLES-50330GTA3/save", "win-gta");
        Write("win", $"pcsx2/sstates/{serial} (ABCD1234).01.p2s", "win-state");
        Write("win", $"pcsx2/sstates/{serial} (ABCD1234).01.p2s.backup", "win-backup");
        // Deck: an older save of the same game on a card named otherwise, its own card superblock and another game.
        Write("deck", "Emulation/saves/pcsx2/saves/Mcd001.ps2/_pcsx2_superblock", "deck-superblock");
        F.Write(P("deck", $"Emulation/saves/pcsx2/saves/Mcd001.ps2/{save}SYS/icon.sys"), F.IconSys($"Game {_id}"));
        Write("deck", $"Emulation/saves/pcsx2/saves/Mcd001.ps2/{save}SYS/_pcsx2_index", "deck-old");
        Write("deck", "Emulation/saves/pcsx2/saves/Mcd001.ps2/BASLUS-20000OTHER/save", "deck-other");
        Directory.CreateDirectory(P("deck", "Emulation/saves/pcsx2/states"));

        var win = await Machine("win");
        var deck = await Machine("deck");
        var winGame = await Enroll(win, Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("win", "pcsx2/memcards"), P("win", "pcsx2/sstates")) }), save);
        var deckGame = await Enroll(deck, Pcsx2Saves.Scan(Pcsx2Saves.Folders(new[] { P("deck", "Emulation") }, [])), save);
        Assert.Equal(winGame.GameId, deckGame.GameId);
        Assert.Equal($"Game {_id}", deckGame.Name);

        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        Assert.NotNull(await engineWin.PushAsync(winGame, force: true));
        await engineDeck.PullAsync(deckGame, force: true);

        Assert.Equal("win-index", Read("deck", $"Emulation/saves/pcsx2/saves/Mcd001.ps2/{save}SYS/_pcsx2_index"));
        Assert.Equal("win-slot1", Read("deck", $"Emulation/saves/pcsx2/saves/Mcd001.ps2/{save}S01/data"));
        Assert.Equal("win-state", Read("deck", $"Emulation/saves/pcsx2/states/{serial} (ABCD1234).01.p2s"));
        Assert.False(File.Exists(P("deck", $"Emulation/saves/pcsx2/states/{serial} (ABCD1234).01.p2s.backup")));
        // The rest of the Deck's card is its own.
        Assert.Equal("deck-superblock", Read("deck", "Emulation/saves/pcsx2/saves/Mcd001.ps2/_pcsx2_superblock"));
        Assert.Equal("deck-other", Read("deck", "Emulation/saves/pcsx2/saves/Mcd001.ps2/BASLUS-20000OTHER/save"));
        Assert.False(Directory.Exists(P("deck", "Emulation/saves/pcsx2/saves/Mcd001.ps2/BESLES-50330GTA3")));
    }

    [Fact]
    public async Task A_ps1_game_card_and_its_states_travel_and_the_neighbour_cards_stay()
    {
        var title = $"Crash {_id}";
        var code = $"SCUS-{_id}";
        F.Write(P("a", $"ds/memcards/{title}_1.mcd"), F.Ps1Card($"BA{code}CRASH"));
        Write("a", $"ds/savestates/{code}_1.sav", "a-state1");
        Write("a", $"ds/savestates/{code}_resume.sav", "a-resume");
        F.Write(P("b", $"ds/memcards/{title}_1.mcd"), F.Ps1Card($"BA{code}OLD"));
        F.Write(P("b", $"ds/memcards/{title} Team_1.mcd"), F.Ps1Card("BASCUS-94426CTR"));
        Write("b", "ds/savestates/SCUS-94426_1.sav", "b-ctr-state");

        var a = await Machine("a");
        var b = await Machine("b");
        var gameA = await Enroll(a, DuckStationSaves.Scan(new[] { new RomSaveFolders(P("a", "ds/memcards"), P("a", "ds/savestates")) }), title);
        var gameB = await Enroll(b, DuckStationSaves.Scan(new[] { new RomSaveFolders(P("b", "ds/memcards"), P("b", "ds/savestates")) }), title);
        Assert.Equal(gameA.GameId, gameB.GameId);

        await using var engineA = new SyncEngine(a, ApiClient.For(a));
        await using var engineB = new SyncEngine(b, ApiClient.For(b));
        Assert.NotNull(await engineA.PushAsync(gameA, force: true));
        await engineB.PullAsync(gameB, force: true);

        Assert.Equal(File.ReadAllBytes(P("a", $"ds/memcards/{title}_1.mcd")), File.ReadAllBytes(P("b", $"ds/memcards/{title}_1.mcd")));
        Assert.Equal("a-state1", Read("b", $"ds/savestates/{code}_1.sav"));
        Assert.Equal("a-resume", Read("b", $"ds/savestates/{code}_resume.sav"));
        Assert.True(File.Exists(P("b", $"ds/memcards/{title} Team_1.mcd")));
        Assert.Equal("b-ctr-state", Read("b", "ds/savestates/SCUS-94426_1.sav"));
    }

    [Fact]
    public async Task A_wii_save_travels_between_the_deck_and_windows_layouts()
    {
        // A made-up title ID per test, R + the test's digits: four ASCII characters, eight hex digits.
        var id = "R" + _id[..3];
        var hex = Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(id));
        var deckWii = $".var/app/org.DolphinEmu.dolphin-emu/data/dolphin-emu/Wii/title/00010000/{hex}/data";
        F.Write(P("deck", $"{deckWii}/banner.bin"), F.Banner($"Wii Game {_id}"));
        Write("deck", $"{deckWii}/save.bin", "deck-save");
        Write("deck", ".var/app/org.DolphinEmu.dolphin-emu/data/dolphin-emu/StateSaves/" + id + "01.s01", "deck-state");
        // Windows: EmuDeck's portable User folder, with another game's save beside it.
        F.Write(P("win", $"Dolphin-x64/User/Wii/title/00010000/{hex}/data/banner.bin"), F.Banner($"Wii Game {_id}"));
        Write("win", $"Dolphin-x64/User/Wii/title/00010000/{hex}/data/save.bin", "win-old");
        F.Write(P("win", "Dolphin-x64/User/Wii/title/00010000/52334f45/data/banner.bin"), F.Banner("Metroid: Other M"));
        Directory.CreateDirectory(P("win", "Dolphin-x64/User/StateSaves"));

        var deck = await Machine("deck");
        var win = await Machine("win");
        var deckGame = await Enroll(deck, DolphinSaves.Scan(DolphinSaves.Folders([],
            [(DolphinSaves.EmulatorName, P("deck", ".var/app/org.DolphinEmu.dolphin-emu/data/dolphin-emu"))])), id);
        var winGame = await Enroll(win, DolphinSaves.Scan(DolphinSaves.Folders([],
            [(DolphinSaves.EmulatorName, P("win", "Dolphin-x64/User"))])), id);
        Assert.Equal(deckGame.GameId, winGame.GameId);

        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));
        await engineWin.PullAsync(winGame, force: true);

        Assert.Equal("deck-save", Read("win", $"Dolphin-x64/User/Wii/title/00010000/{hex}/data/save.bin"));
        Assert.Equal("deck-state", Read("win", $"Dolphin-x64/User/StateSaves/{id}01.s01"));
        Assert.True(File.Exists(P("win", "Dolphin-x64/User/Wii/title/00010000/52334f45/data/banner.bin")));
    }

    // ---- Phase 4: PrimeHack, and saves from different emulators kept apart ----

    /// <summary>One Wii title saved under a Dolphin-family user folder: banner, save and a state.</summary>
    private (string Id, string Hex) WiiSave(string machine, string user, string save, string title)
    {
        var id = "P" + _id[..3];
        var hex = Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(id));
        F.Write(P(machine, $"{user}/Wii/title/00010000/{hex}/data/banner.bin"), F.Banner(title));
        Write(machine, $"{user}/Wii/title/00010000/{hex}/data/save.bin", save);
        Write(machine, $"{user}/StateSaves/{id}01.s01", save + "-state");
        return (id, hex);
    }

    private IReadOnlyList<ScanCandidate> ScanDolphins(string machine, params (string Emulator, string User)[] users) =>
        DolphinSaves.Scan(DolphinSaves.Folders([], users.Select(u => (u.Emulator, P(machine, u.User)))));

    [Fact]
    public async Task A_primehack_save_and_a_dolphin_save_of_one_game_are_two_games_and_never_cross()
    {
        const string prime = ".var/app/io.github.shiiion.primehack/data/dolphin-emu";
        const string dolphin = "Documents/Dolphin Emulator";
        var title = $"Trilogy {_id}";
        var (id, hex) = WiiSave("deck", prime, "deck-primehack", title);
        WiiSave("pc", dolphin, "pc-dolphin", title);
        WiiSave("laptop", prime, "laptop-primehack-old", title);

        var deck = await Machine("deck");
        var pc = await Machine("pc");
        var laptop = await Machine("laptop");
        var deckGame = await Enroll(deck, ScanDolphins("deck", (DolphinSaves.PrimeHackName, prime)), id);

        // The PC's Dolphin save keeps the same files, but another emulator made the server's game: the row shows
        // it greyed out, and adding it makes a game of its own.
        var pcScan = ScanDolphins("pc", (DolphinSaves.EmulatorName, dolphin));
        var options = EnrollLinks.For(await ApiClient.For(pc).ListGamesAsync(), pcScan[0]);
        Assert.Equal((LinkKind.New, $"{title} (Dolphin)"), (options[0].Kind, options[0].Name));
        Assert.Contains(options, o => o.Kind == LinkKind.Blocked && o.GameId == deckGame.GameId && o.Badge == "Another emulator");
        Assert.Contains("another emulator", EnrollLinks.Resolve(await ApiClient.For(pc).ListGamesAsync(), pcScan[0],
            pcScan[0].ExtraSaveDirs!, new LinkChoice(EnrollLinks.Game, deckGame.GameId)).Refusal);
        var pcGame = await Enroll(pc, pcScan, id);
        Assert.NotEqual(deckGame.GameId, pcGame.GameId);
        Assert.Equal($"{title} (Dolphin)", pcGame.Name);

        // Another PrimeHack machine joins the Deck's game, as before.
        var laptopGame = await Enroll(laptop, ScanDolphins("laptop", (DolphinSaves.PrimeHackName, prime)), id);
        Assert.Equal(deckGame.GameId, laptopGame.GameId);

        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        await using var enginePc = new SyncEngine(pc, ApiClient.For(pc));
        await using var engineLaptop = new SyncEngine(laptop, ApiClient.For(laptop));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));
        Assert.NotNull(await enginePc.PushAsync(pcGame, force: true));
        await engineLaptop.PullAsync(laptopGame, force: true);
        await enginePc.PullAsync(pcGame);

        Assert.Equal("deck-primehack", Read("laptop", $"{prime}/Wii/title/00010000/{hex}/data/save.bin"));
        Assert.Equal("deck-primehack-state", Read("laptop", $"{prime}/StateSaves/{id}01.s01"));
        Assert.Equal("pc-dolphin", Read("pc", $"{dolphin}/Wii/title/00010000/{hex}/data/save.bin"));
    }

    [Fact]
    public async Task One_batch_with_both_emulators_makes_two_games_on_one_machine()
    {
        // The maintainer's Deck: Metroid Prime Trilogy saved in Dolphin AND in PrimeHack, both added at once.
        const string prime = ".var/app/io.github.shiiion.primehack/data/dolphin-emu";
        const string dolphin = ".var/app/org.DolphinEmu.dolphin-emu/data/dolphin-emu";
        var (id, _) = WiiSave("deck", prime, "primehack-save", $"Both {_id}");
        WiiSave("deck", dolphin, "dolphin-save", $"Both {_id}");
        var deck = await Machine("deck");
        var found = ScanDolphins("deck", (DolphinSaves.EmulatorName, dolphin), (DolphinSaves.PrimeHackName, prime));
        Assert.Equal(new[] { "Dolphin", "PrimeHack" }, found.Select(c => c.EmulatorName).Order());

        Assert.Equal((2, 0), await Enroller.EnrollAsync(deck, found, [0, 1]));

        var config = AgentConfig.Load(deck.ConfigPath);
        var games = found.Select(c => Enroller.TrackedFor(config, c)!).ToList();
        Assert.NotEqual(games[0].GameId, games[1].GameId);
        Assert.All(games, g => Assert.True(g.IsEnrolledHere));
        Assert.Equal(found.Select(c => c.SuggestedSaveDir), games.Select(g => g.SaveDirectory));
    }
}
