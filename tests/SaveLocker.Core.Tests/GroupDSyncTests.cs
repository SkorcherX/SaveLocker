using SaveLocker.Agent;
using Xunit;
using F = SaveLocker.Agent.Tests.ConsoleSaveFixtures;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// The Group D emulators end to end against a real server (tasks/emulator-saves Group D): one game's save folders in a
/// <c>SAVEDATA</c> every game shares travel between two machines with their states, and the other games' folders never
/// move.
/// </summary>
public sealed class GroupDSyncTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-groupd-" + Guid.NewGuid().ToString("N"));
    // The class shares one server, so each test's game is its own.
    private readonly string _id = Random.Shared.Next(10000, 99999).ToString();

    public GroupDSyncTests(ServerProcess server)
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
    public async Task A_psp_game_moves_between_the_deck_flatpak_and_windows_memstick_with_its_states()
    {
        // A PSN-style ID Redump has never seen, so the name comes from the save's own PARAM.SFO on both machines.
        var game = $"NPUH{_id}";
        const string deckPsp = ".var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP";
        const string winPsp = "AppData/Roaming/EmuDeck/Emulators/PPSSPP/memstick/PSP";
        F.Write(P("deck", $"{deckPsp}/SAVEDATA/{game}DATA00/PARAM.SFO"), F.ParamSfo(("TITLE", $"PSP Game {_id}")));
        Write("deck", $"{deckPsp}/SAVEDATA/{game}DATA00/DATA.BIN", "deck-slot");
        F.Write(P("deck", $"{deckPsp}/SAVEDATA/{game}SYSTEM/PARAM.SFO"), F.ParamSfo(("TITLE", $"PSP Game {_id}")));
        Write("deck", $"{deckPsp}/SAVEDATA/{game}SYSTEM/SYSTEM.BIN", "deck-system");
        Write("deck", $"{deckPsp}/PPSSPP_STATE/{game}_1.00_0.ppst", "deck-state");
        // Windows: an older save of the same game, and another game's folder and state beside it.
        F.Write(P("win", $"{winPsp}/SAVEDATA/{game}DATA00/PARAM.SFO"), F.ParamSfo(("TITLE", $"PSP Game {_id}")));
        Write("win", $"{winPsp}/SAVEDATA/{game}DATA00/DATA.BIN", "win-old");
        F.Write(P("win", $"{winPsp}/SAVEDATA/ULUS10041DATA00/PARAM.SFO"), F.ParamSfo(("TITLE", "GTA LCS")));
        Write("win", $"{winPsp}/SAVEDATA/ULUS10041DATA00/DATA.BIN", "win-lcs");
        Write("win", $"{winPsp}/PPSSPP_STATE/ULUS10041_1.00_0.ppst", "win-lcs-state");

        var deck = await Machine("deck");
        var win = await Machine("win");
        var deckGame = await Enroll(deck, PpssppSaves.Scan(PpssppSaves.Folders([], [P("deck", deckPsp)])), game);
        var winGame = await Enroll(win, PpssppSaves.Scan(PpssppSaves.Folders([], [P("win", winPsp)])), game);
        Assert.Equal(deckGame.GameId, winGame.GameId);
        Assert.Equal($"PSP Game {_id}", winGame.Name);

        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));
        await engineWin.PullAsync(winGame, force: true);

        Assert.Equal("deck-slot", Read("win", $"{winPsp}/SAVEDATA/{game}DATA00/DATA.BIN"));
        Assert.Equal("deck-system", Read("win", $"{winPsp}/SAVEDATA/{game}SYSTEM/SYSTEM.BIN"));
        Assert.Equal("deck-state", Read("win", $"{winPsp}/PPSSPP_STATE/{game}_1.00_0.ppst"));
        // The other game in the same SAVEDATA and its state are Windows' own.
        Assert.Equal("win-lcs", Read("win", $"{winPsp}/SAVEDATA/ULUS10041DATA00/DATA.BIN"));
        Assert.Equal("win-lcs-state", Read("win", $"{winPsp}/PPSSPP_STATE/ULUS10041_1.00_0.ppst"));
    }

    [Fact]
    public async Task A_psp_game_with_only_states_joins_and_its_first_save_follows()
    {
        var game = $"NPUH{_id}";
        const string deckPsp = ".var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP";
        const string winPsp = "AppData/Roaming/EmuDeck/Emulators/PPSSPP/memstick/PSP";
        // The Deck has played from states only; Windows has neither yet, and a neighbour game of its own.
        Write("deck", $"{deckPsp}/PPSSPP_STATE/{game}_1.00_0.ppst", "deck-state");
        Directory.CreateDirectory(P("deck", $"{deckPsp}/SAVEDATA"));
        Write("win", $"{winPsp}/SAVEDATA/ULUS10041DATA00/DATA.BIN", "win-lcs");
        Write("win", $"{winPsp}/PPSSPP_STATE/{game}_1.00_0.ppst", "win-state");

        var deck = await Machine("deck");
        var win = await Machine("win");
        var deckFound = PpssppSaves.Scan(PpssppSaves.Folders([], [P("deck", deckPsp)]));
        Assert.True(Assert.Single(deckFound).StatesOnly);
        var deckGame = await Enroll(deck, deckFound, game);
        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));

        var winGame = await Enroll(win, PpssppSaves.Scan(PpssppSaves.Folders([], [P("win", winPsp)])), game);
        Assert.Equal(deckGame.GameId, winGame.GameId);
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        await engineWin.PullAsync(winGame, force: true);
        Assert.Equal("deck-state", Read("win", $"{winPsp}/PPSSPP_STATE/{game}_1.00_0.ppst"));

        // The game's first in-game save on the Deck reaches Windows; the neighbour stays.
        Write("deck", $"{deckPsp}/SAVEDATA/{game}DATA00/DATA.BIN", "first-save");
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));
        await engineWin.PullAsync(winGame, force: true);
        Assert.Equal("first-save", Read("win", $"{winPsp}/SAVEDATA/{game}DATA00/DATA.BIN"));
        Assert.Equal("win-lcs", Read("win", $"{winPsp}/SAVEDATA/ULUS10041DATA00/DATA.BIN"));
    }

    [Fact]
    public async Task A_states_only_push_never_empties_another_machines_real_save_of_the_game()
    {
        var game = $"NPUH{_id}";
        const string deckPsp = ".var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP";
        const string winPsp = "AppData/Roaming/EmuDeck/Emulators/PPSSPP/memstick/PSP";
        Write("deck", $"{deckPsp}/PPSSPP_STATE/{game}_1.00_0.ppst", "deck-state");
        Directory.CreateDirectory(P("deck", $"{deckPsp}/SAVEDATA"));
        Write("win", $"{winPsp}/SAVEDATA/{game}DATA00/DATA.BIN", "win-real-save");

        var deck = await Machine("deck");
        var win = await Machine("win");
        var deckGame = await Enroll(deck, PpssppSaves.Scan(PpssppSaves.Folders([], [P("deck", deckPsp)])), game);
        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        Assert.NotNull(await engineDeck.PushAsync(deckGame, force: true));

        var winGame = await Enroll(win, PpssppSaves.Scan(PpssppSaves.Folders([], [P("win", winPsp)])), game);
        Assert.Equal(deckGame.GameId, winGame.GameId);
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));

        // The server's version has no save; Windows' save was never pushed, so the pull stops rather than empty it.
        Assert.False(await engineWin.PullAsync(winGame));
        Assert.Equal("win-real-save", Read("win", $"{winPsp}/SAVEDATA/{game}DATA00/DATA.BIN"));
    }
}
