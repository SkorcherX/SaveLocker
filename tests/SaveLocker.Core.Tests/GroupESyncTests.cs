using SaveLocker.Agent;
using Xunit;
using F = SaveLocker.Agent.Tests.ConsoleSaveFixtures;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// The Group E emulators end to end against a real server (tasks/emulator-saves Group E): one game's title-ID folder in a
/// folder every game shares travels between two machines, with its states, and nothing else in that folder moves.
/// </summary>
public sealed class GroupESyncTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-groupe-" + Guid.NewGuid().ToString("N"));
    // The class shares one server, so each test's game is its own.
    private readonly string _id = Random.Shared.Next(0x100000, 0xFFFFFF).ToString("x6");

    public GroupESyncTests(ServerProcess server)
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

    private const string Ids = "Nintendo 3DS/00000000000000000000000000000000/00000000000000000000000000000000/title/00040000";

    [Fact]
    public async Task A_3ds_game_moves_between_the_decks_sd_card_and_windows_portable_azahar_with_its_states()
    {
        var low = "00" + _id;
        var programId = "00040000" + low.ToUpperInvariant();
        const string deckSd = "SDCard/Emulation/storage/azahar/sdmc";
        const string deckStates = ".local/share/azahar-emu/states";
        const string winUser = "AppData/Roaming/EmuDeck/Emulators/azahar/user";
        // The Deck has the ROM (a product code no table knows, so its file name names it); Windows has none.
        F.Write(P("deck", $"SDCard/Emulation/roms/n3ds/Group E Quest {_id} (USA).cci"), F.Ncsd(Convert.ToUInt64(programId, 16), "CTR-P-ZZZZ"));
        Write("deck", $"{deckSd}/{Ids}/{low}/data/00000001/save.bin", "deck-save");
        Write("deck", $"{deckSd}/{Ids}/{low}/data/00000001.metadata", "deck-meta");
        Write("deck", $"{deckStates}/{programId}.01.cst", "deck-state");
        // Windows: an older save, the same game installed from a CIA, and another game with its own state.
        Write("win", $"{winUser}/sdmc/{Ids}/{low}/data/00000001/save.bin", "win-old");
        Write("win", $"{winUser}/sdmc/{Ids}/{low}/content/00000000.app", "win-installed-game");
        Write("win", $"{winUser}/sdmc/{Ids}/00054000/data/00000001/GameData.bin", "win-smb");
        Write("win", $"{winUser}/states/0004000000054000.01.cst", "win-smb-state");

        var deck = await Machine("deck");
        var win = await Machine("win");
        var deckFound = AzaharSaves.Scan([new AzaharFolders(P("deck", deckSd), P("deck", deckStates), [P("deck", "SDCard/Emulation/roms/n3ds")], EmuDeck: true)]);
        var winFound = AzaharSaves.Scan([new AzaharFolders(P("win", $"{winUser}/sdmc"), P("win", $"{winUser}/states"), [])]);
        var deckGame = await Enroll(deck, deckFound, programId);
        var winGame = await Enroll(win, winFound, programId);
        Assert.Equal($"Group E Quest {_id}", deckGame.Name);
        // Windows called it "3DS <ID>"; it joins the Deck's game by its folder all the same (D1).
        Assert.Equal($"3DS {low.ToUpperInvariant()}", winFound.Single(c => c.EmulatorRom == programId).Name);
        Assert.Equal(deckGame.GameId, winGame.GameId);

        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));
        await engineWin.PullAsync(winGame, force: true);

        Assert.Equal("deck-save", Read("win", $"{winUser}/sdmc/{Ids}/{low}/data/00000001/save.bin"));
        Assert.Equal("deck-meta", Read("win", $"{winUser}/sdmc/{Ids}/{low}/data/00000001.metadata"));
        Assert.Equal("deck-state", Read("win", $"{winUser}/states/{programId}.01.cst"));
        // The installed game beside the save, and the other game and its state, are Windows' own.
        Assert.Equal("win-installed-game", Read("win", $"{winUser}/sdmc/{Ids}/{low}/content/00000000.app"));
        Assert.Equal("win-smb", Read("win", $"{winUser}/sdmc/{Ids}/00054000/data/00000001/GameData.bin"));
        Assert.Equal("win-smb-state", Read("win", $"{winUser}/states/0004000000054000.01.cst"));

        // And back: a newer save made on Windows reaches the Deck.
        Write("win", $"{winUser}/sdmc/{Ids}/{low}/data/00000001/save.bin", "win-newer");
        Assert.NotNull(await engineWin.PushAsync(winGame, force: true));
        await engineDeck.PullAsync(deckGame, force: true);
        Assert.Equal("win-newer", Read("deck", $"{deckSd}/{Ids}/{low}/data/00000001/save.bin"));
        Assert.False(File.Exists(P("deck", $"{deckSd}/{Ids}/{low}/content/00000000.app")));
    }
    [Fact]
    public async Task An_xbox_360_save_moves_between_two_profiles_and_layouts_and_leaves_the_dlc()
    {
        // A title ID no table knows; the Deck saved under its profile, Windows under another profile.
        var title = "5A" + _id.ToUpperInvariant();
        const string deckContent = "SDCard/Emulation/roms/xbox360/content";
        const string winContent = "AppData/Roaming/EmuDeck/Emulators/xenia/content";
        Write("deck", $"{deckContent}/E030000033894516/{title}/00000001/Profile/save.dat", "deck-save");
        Write("deck", $"{deckContent}/E030000033894516/{title}/Headers/00000001/Profile.header", "deck-header");
        Write("win", $"{winContent}/E0300000AAAAAAAA/{title}/00000001/Profile/save.dat", "win-old");
        Write("win", $"{winContent}/E0300000AAAAAAAA/{title}/00000002/DLCPACK/dlc.bin", "win-dlc");
        Write("win", $"{winContent}/E0300000AAAAAAAA/4D5309C9/00000001/ForzaProfile/ForzaProfile", "win-forza");

        var deck = await Machine("deck");
        var win = await Machine("win");
        var deckGame = await Enroll(deck, XeniaSaves.Scan([new XeniaFolders(P("deck", deckContent), EmuDeck: true)]), title);
        var winGame = await Enroll(win, XeniaSaves.Scan([new XeniaFolders(P("win", winContent))]), title);
        Assert.Equal(deckGame.GameId, winGame.GameId);

        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));
        await engineWin.PullAsync(winGame, force: true);

        // Each machine syncs into its own profile's folder: the Deck's save replaces Windows' in Windows' profile, and the
        // DLC and the other game beside it stay as they were.
        Assert.Equal(P("win", $"{winContent}/E0300000AAAAAAAA"), AgentConfig.Load(win.ConfigPath).Games.Single(g => g.GameId == winGame.GameId).SaveDirectory);
        Assert.Equal("deck-save", Read("win", $"{winContent}/E0300000AAAAAAAA/{title}/00000001/Profile/save.dat"));
        Assert.Equal("deck-header", Read("win", $"{winContent}/E0300000AAAAAAAA/{title}/Headers/00000001/Profile.header"));
        Assert.Equal("win-dlc", Read("win", $"{winContent}/E0300000AAAAAAAA/{title}/00000002/DLCPACK/dlc.bin"));
        Assert.Equal("win-forza", Read("win", $"{winContent}/E0300000AAAAAAAA/4D5309C9/00000001/ForzaProfile/ForzaProfile"));
    }
    [Fact]
    public async Task An_xbox_360_device_save_moves_with_its_game_and_the_dlc_beside_it_stays()
    {
        var title = "5B" + _id.ToUpperInvariant();
        const string deckContent = "SDCard/Emulation/roms/xbox360/content";
        const string winContent = "AppData/Roaming/EmuDeck/Emulators/xenia/content";
        Write("deck", $"{deckContent}/E030000033894516/{title}/00000001/Profile/save.dat", "deck-save");
        Write("deck", $"{deckContent}/0000000000000000/{title}/00000001/globaloptions/options", "deck-calibration");
        Write("win", $"{winContent}/E0300000AAAAAAAA/{title}/00000001/Profile/save.dat", "win-old");
        Write("win", $"{winContent}/0000000000000000/{title}/00000002/SONGPACK/song.bin", "win-dlc");

        var deck = await Machine("deck");
        var win = await Machine("win");
        var deckGame = await Enroll(deck, XeniaSaves.Scan([new XeniaFolders(P("deck", deckContent), EmuDeck: true)]), title);
        var winGame = await Enroll(win, XeniaSaves.Scan([new XeniaFolders(P("win", winContent))]), title);
        Assert.Equal(deckGame.GameId, winGame.GameId);

        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));
        await engineWin.PullAsync(winGame, force: true);

        Assert.Equal("deck-save", Read("win", $"{winContent}/E0300000AAAAAAAA/{title}/00000001/Profile/save.dat"));
        Assert.Equal("deck-calibration", Read("win", $"{winContent}/0000000000000000/{title}/00000001/globaloptions/options"));
        Assert.Equal("win-dlc", Read("win", $"{winContent}/0000000000000000/{title}/00000002/SONGPACK/song.bin"));
    }
}
