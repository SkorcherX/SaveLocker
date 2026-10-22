using SaveLocker.Agent;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// RetroArch end to end against a real server (tasks/emulator-saves Phase 1b): one ROM is a game made
/// of two scoped folders other ROMs share — its save and its save states — and syncing it between two
/// machines moves exactly that ROM's files, never the neighbours'.
/// </summary>
public sealed class RetroArchSyncTests : IClassFixture<ServerProcess>, IDisposable
{
    private const string Chrono = "Chrono Trigger (USA)";
    private const string Zelda = "Zelda (USA)";

    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-ra-sync-" + Guid.NewGuid().ToString("N"));

    public RetroArchSyncTests(ServerProcess server)
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
        var reg = await new ApiClient(config.ServerUrl, null).RegisterAsync(name + Guid.NewGuid().ToString("N")[..6]);
        config.ApiKey = reg.ApiKey;
        config.MachineId = reg.MachineId;
        config.MachineName = name;
        config.Save();
        return config;
    }

    private string Emulation(string machine) => Path.Combine(_dir, machine, "Emulation");
    private string Saves(string machine) => Path.Combine(Emulation(machine), "saves", "retroarch", "saves");
    private string States(string machine) => Path.Combine(Emulation(machine), "saves", "retroarch", "states");

    private static void Write(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content);
    }

    private static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    private async Task<TrackedGame> Enroll(AgentConfig config, string machine)
    {
        var found = RetroArchSaves.Scan(RetroArchConfig.Folders(new[] { Emulation(machine) }, Array.Empty<string>()),
            Array.Empty<string>()).ToList();
        var id = found.FindIndex(c => c.Name == "Chrono Trigger");
        Assert.Equal((1, 0), await Enroller.EnrollAsync(config, found, new[] { id }));
        return AgentConfig.Load(config.ConfigPath).FindGame("Chrono Trigger")!;
    }

    [Fact]
    public async Task One_roms_save_and_states_travel_and_its_neighbours_stay_put()
    {
        // A: Chrono with a save, a slot and its thumbnail; Zelda beside it in both folders.
        Write(Saves("a"), Chrono + ".srm", "a-chrono-sram");
        Write(States("a"), Chrono + ".state1", "a-chrono-slot1");
        Write(States("a"), Chrono + ".state1.png", "a-thumb");
        Write(Saves("a"), Zelda + ".srm", "a-zelda-sram");
        Write(States("a"), Zelda + ".state", "a-zelda-state");
        // B: its own Zelda, and an old Chrono save with no states yet.
        Write(Saves("b"), Chrono + ".srm", "b-old-chrono");
        Write(Saves("b"), Zelda + ".srm", "b-zelda-sram");
        Write(States("b"), Zelda + ".state", "b-zelda-state");

        var a = await Machine("a");
        var b = await Machine("b");
        var gameA = await Enroll(a, "a");
        var gameB = await Enroll(b, "b");
        Assert.Equal(gameA.GameId, gameB.GameId);

        // Each machine told the server how it found the game, and reads its own back on the game list.
        using (var http = new HttpClient())
        {
            var sources = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<List<SaveLocker.Shared.MachineGameSourceDto>>(
                http, $"{_server.Url}/api/games/{gameA.GameId}/sources");
            // The class shares one server, and its other test enrolls the same game on machines of its own.
            sources = sources!.Where(s => s.MachineId == a.MachineId || s.MachineId == b.MachineId).ToList();
            Assert.Equal(2, sources.Count);
            Assert.All(sources, s => Assert.True(
                new SaveLocker.Shared.GameSourceDto("emulator", "RetroArch", ["EmuDeck"]).SameAs(s.Source)));
        }
        Assert.Equal("RetroArch", (await ApiClient.For(a).ListGamesAsync()).Single(g => g.Id == gameA.GameId).MachineSource?.Detail);
        Assert.Equal("RetroArch", gameA.Source?.Detail);

        await using var engineA = new SyncEngine(a, ApiClient.For(a));
        await using var engineB = new SyncEngine(b, ApiClient.For(b));
        Assert.NotNull(await engineA.PushAsync(gameA, force: true));

        // The stored version holds Chrono's files only: the save at the root, the states under their key.
        var zip = Path.Combine(_dir, "head.zip");
        Assert.NotNull(await ApiClient.For(a).DownloadHeadAsync(gameA.GameId, zip));
        Assert.Equal(
            new[]
            {
                ".savelocker/paths/states/" + Chrono + ".state1",
                ".savelocker/paths/states/" + Chrono + ".state1.png",
                Chrono + ".srm",
            },
            SaveLocker.Shared.SaveArchive.ListArchiveEntries(zip).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { RetroArchSaves.StatesKey }, SaveLocker.Shared.SaveArchive.ArchiveKeys(zip));

        await engineB.PullAsync(gameB, force: true);

        Assert.Equal("a-chrono-sram", Read(Saves("b"), Chrono + ".srm"));
        Assert.Equal("a-chrono-slot1", Read(States("b"), Chrono + ".state1"));
        Assert.Equal("a-thumb", Read(States("b"), Chrono + ".state1.png"));
        Assert.Equal("b-zelda-sram", Read(Saves("b"), Zelda + ".srm"));
        Assert.Equal("b-zelda-state", Read(States("b"), Zelda + ".state"));

        // B plays: a new slot and an overwritten save, then deletes slot 1. A pulls all three changes.
        Write(Saves("b"), Chrono + ".srm", "b-chrono-sram");
        Write(States("b"), Chrono + ".state2", "b-chrono-slot2");
        File.Delete(Path.Combine(States("b"), Chrono + ".state1"));
        File.Delete(Path.Combine(States("b"), Chrono + ".state1.png"));
        Assert.NotNull(await engineB.PushAsync(gameB));
        await engineA.PullAsync(gameA);

        Assert.Equal("b-chrono-sram", Read(Saves("a"), Chrono + ".srm"));
        Assert.Equal("b-chrono-slot2", Read(States("a"), Chrono + ".state2"));
        Assert.False(File.Exists(Path.Combine(States("a"), Chrono + ".state1")));
        Assert.False(File.Exists(Path.Combine(States("a"), Chrono + ".state1.png")));
        Assert.Equal("a-zelda-sram", Read(Saves("a"), Zelda + ".srm"));
        Assert.Equal("a-zelda-state", Read(States("a"), Zelda + ".state"));
    }

    [Fact]
    public async Task A_machine_with_no_states_folder_yet_gets_one_from_a_pull()
    {
        Write(Saves("a"), Chrono + ".srm", "a-chrono-sram");
        Write(States("a"), Chrono + ".state.auto", "a-auto");
        Write(Saves("c"), Chrono + ".srm", "c-old");

        var a = await Machine("a");
        var c = await Machine("c");
        var gameA = await Enroll(a, "a");
        var gameC = await Enroll(c, "c");
        Assert.False(Directory.Exists(States("c")));

        await using var engineA = new SyncEngine(a, ApiClient.For(a));
        await using var engineC = new SyncEngine(c, ApiClient.For(c));
        Assert.NotNull(await engineA.PushAsync(gameA, force: true));
        await engineC.PullAsync(gameC, force: true);

        Assert.Equal("a-auto", Read(States("c"), Chrono + ".state.auto"));
        Assert.Equal(gameA.LocalHash(a.StateDir), AgentConfig.Load(c.ConfigPath).FindGame(gameC.Name)!.LocalHash(c.StateDir));
    }
}
