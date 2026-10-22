using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// Which server game an emulator save and a same-titled PC game land in, against a real server
/// (review of PR #60): the name is the server-side identity, so it has to come out the same on every
/// machine, and a game must never join one whose include patterns exclude every file it has.
/// </summary>
public sealed class EnrollNamingTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-naming-" + Guid.NewGuid().ToString("N"));
    // The class shares one server, so each test's titles are its own.
    private readonly string _title = "Chrono Trigger " + Guid.NewGuid().ToString("N")[..6];

    public EnrollNamingTests(ServerProcess server)
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

    /// <summary>One machine's RetroArch save of <paramref name="region"/>, scanned the way the agent does.</summary>
    private ScanCandidate Emulated(string machine, string region)
    {
        var emulation = Path.Combine(_dir, machine, "Emulation");
        var saves = Path.Combine(emulation, "saves", "retroarch", "saves");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, $"{_title} ({region}).srm"), $"{machine}-{region}");
        return RetroArchSaves.Scan(RetroArchConfig.Folders(new[] { emulation }, Array.Empty<string>()), Array.Empty<string>())
            .Single(c => c.EmulatorRom == $"{_title} ({region})");
    }

    private ScanCandidate Pc(string machine, string? file = "save1.dat")
    {
        var dir = Path.Combine(_dir, machine, "Saved Games", _title);
        Directory.CreateDirectory(dir);
        if (file is not null) File.WriteAllText(Path.Combine(dir, file), $"{machine}-pc");
        return new ScanCandidate(_title, dir, ScanSource.SteamInstalled, false, ManifestKey: _title);
    }

    private static async Task<(int enrolled, int skipped)> Enroll(AgentConfig config, ScanCandidate c) =>
        await Enroller.EnrollAsync(config, new[] { c }, new[] { 0 });

    private static TrackedGame Tracked(AgentConfig config, ScanCandidate c) =>
        Enroller.TrackedFor(AgentConfig.Load(config.ConfigPath), c)!;

    [Fact]
    public async Task A_pc_game_never_joins_an_emulator_game_that_would_keep_none_of_its_files()
    {
        var deck = await Machine("deck");
        Assert.Equal((1, 0), await Enroll(deck, Emulated("deck", "USA")));

        var pc = await Machine("pc");
        Assert.Equal((0, 1), await Enroll(pc, Pc("pc")));
        Assert.Null(AgentConfig.Load(pc.ConfigPath).FindGame(_title));
    }

    [Fact]
    public async Task A_pc_game_still_joins_one_whose_patterns_were_set_for_its_own_files()
    {
        var first = await Machine("first");
        Assert.Equal((1, 0), await Enroll(first, Pc("first")));
        var game = (await ApiClient.For(first).ListGamesAsync()).Single(g => g.Name == _title);
        using (var http = new HttpClient())
            (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
                http, $"{_server.Url}/api/games/{game.Id}/include-globs", new[] { "*.dat" })).EnsureSuccessStatusCode();

        var second = await Machine("second");
        Assert.Equal((1, 0), await Enroll(second, Pc("second")));
        Assert.Equal(game.Id, AgentConfig.Load(second.ConfigPath).FindGame(_title)!.GameId);
    }

    [Fact]
    public async Task One_rom_lands_in_one_game_whichever_machine_enrolls_first()
    {
        // The PC release takes the plain title first; then two machines' SNES saves of the same ROM.
        var pc = await Machine("pc");
        Assert.Equal((1, 0), await Enroll(pc, Pc("pc")));

        var deck = await Machine("deck");
        var deckUsa = Emulated("deck", "USA");
        Assert.Equal((1, 0), await Enroll(deck, deckUsa));
        var win = await Machine("win");
        var winUsa = Emulated("win", "USA");
        Assert.Equal((1, 0), await Enroll(win, winUsa));

        var deckGame = Tracked(deck, deckUsa);
        Assert.Equal($"{_title} (RetroArch)", deckGame.Name);
        Assert.Equal(deckGame.GameId, Tracked(win, winUsa).GameId);
        Assert.NotEqual(deckGame.GameId, AgentConfig.Load(pc.ConfigPath).FindGame(_title)!.GameId);
    }

    [Fact]
    public async Task Two_regions_of_one_title_are_two_games_on_every_machine()
    {
        // Both on the Deck, in one batch; then a machine with only the second region joins its game.
        var deck = await Machine("deck");
        var usa = Emulated("deck", "USA");
        var japan = Emulated("deck", "Japan");
        Assert.Equal((2, 0), await Enroller.EnrollAsync(deck, new[] { usa, japan }, new[] { 0, 1 }));
        Assert.NotEqual(Tracked(deck, usa).GameId, Tracked(deck, japan).GameId);

        var win = await Machine("win");
        var winJapan = Emulated("win", "Japan");
        Assert.Equal((1, 0), await Enroll(win, winJapan));
        Assert.Equal(Tracked(deck, japan).GameId, Tracked(win, winJapan).GameId);
    }

    [Fact]
    public async Task A_report_the_server_answered_is_settled_and_an_unreachable_one_is_not()
    {
        var config = await Machine("reporter");
        var game = new TrackedGame { GameId = Guid.NewGuid(), Name = "Nowhere", SaveDirectory = _dir, Source = GameSources.Manual("x") };
        // An unknown game: the server answers 404, exactly what a console older than the route says.
        Assert.True(await GameSources.ReportAsync(ApiClient.For(config), game));

        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Assert.False(await GameSources.ReportAsync(new ApiClient($"http://127.0.0.1:{port}", config.ApiKey), game));
    }

    [Fact]
    public async Task The_server_bounds_what_a_machine_can_store_as_a_source()
    {
        var config = await Machine("bounds");
        Assert.Equal((1, 0), await Enroll(config, Pc("bounds")));
        var id = AgentConfig.Load(config.ConfigPath).FindGame(_title)!.GameId;
        var api = ApiClient.For(config);

        await api.SetGameSourceAsync(id, new GameSourceDto("steam", "Installed game", ["Proton"]));
        foreach (var bad in new[]
                 {
                     new GameSourceDto("steam", new string('x', GameSourceDto.MaxDetailLength + 1)),
                     new GameSourceDto("steam", "Installed game", [new string('t', GameSourceDto.MaxTagLength + 1)]),
                     new GameSourceDto("steam", "Installed game", Enumerable.Range(0, GameSourceDto.MaxTags + 1).Select(i => $"t{i}").ToArray()),
                     new GameSourceDto("steam", "two\nlines"),
                 })
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.SetGameSourceAsync(id, bad));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.StatusCode);
        }
    }
}
