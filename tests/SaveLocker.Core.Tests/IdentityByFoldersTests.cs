using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// D1 (tasks/emulator-saves Phase 3), against a real server: an emulator save joins the server game that keeps
/// exactly its files, whatever either machine would call it — so a title only one machine can read (its ES-DE
/// gamelist) never splits one game in two.
/// </summary>
public sealed class IdentityByFoldersTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-d1-" + Guid.NewGuid().ToString("N"));
    // The class shares one server, so each test's set name is its own.
    private readonly string _set = "sf2" + Guid.NewGuid().ToString("N")[..6];

    public IdentityByFoldersTests(ServerProcess server)
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

    /// <summary>A machine's FinalBurn Neo save of the set; with <paramref name="title"/>, its ROM and an ES-DE
    /// gamelist that names it.</summary>
    private ScanCandidate Scan(string machine, string? title)
    {
        var emulation = Path.Combine(_dir, machine, "Emulation");
        var saves = Path.Combine(emulation, "saves", "retroarch", "saves");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, _set + ".srm"), machine + "-nvram");
        if (title is not null)
        {
            Directory.CreateDirectory(Path.Combine(emulation, "roms", "fbneo"));
            File.WriteAllText(Path.Combine(emulation, "roms", "fbneo", _set + ".zip"), "");
            var lists = Path.Combine(emulation, "storage", "es-de", "gamelists", "fbneo");
            Directory.CreateDirectory(lists);
            File.WriteAllText(Path.Combine(lists, "gamelist.xml"),
                $"<gameList><game><path>./{_set}.zip</path><name>{title}</name></game></gameList>");
        }
        return RetroArchSaves.Scan(RetroArchConfig.Folders(new[] { emulation }, Array.Empty<string>()),
                new[] { emulation }, new GamelistXml(new[] { Path.Combine(emulation, "storage", "es-de", "gamelists") }))
            .Single(c => c.EmulatorRom == _set);
    }

    private static TrackedGame Tracked(AgentConfig config, ScanCandidate c) =>
        Enroller.TrackedFor(AgentConfig.Load(config.ConfigPath), c)!;

    [Fact]
    public async Task A_name_only_one_machine_can_read_still_makes_one_game()
    {
        var title = "Street Fighter " + _set;
        var deck = await Machine("deck");
        var deckSave = Scan("deck", title);
        Assert.Equal(title, deckSave.Name);
        Assert.Equal((1, 0), await Enroller.EnrollAsync(deck, new[] { deckSave }, new[] { 0 }));

        // No ROM, no gamelist here: this machine would call it by its set name.
        var pc = await Machine("pc");
        var pcSave = Scan("pc", title: null);
        Assert.Equal(_set, pcSave.Name);
        Assert.Equal((1, 0), await Enroller.EnrollAsync(pc, new[] { pcSave }, new[] { 0 }));

        var deckGame = Tracked(deck, deckSave);
        var pcGame = Tracked(pc, pcSave);
        Assert.Equal(deckGame.GameId, pcGame.GameId);
        Assert.Equal(title, pcGame.Name);
        Assert.DoesNotContain(await ApiClient.For(pc).ListGamesAsync(), g => g.Name == _set);
    }

    [Fact]
    public async Task The_other_order_lands_in_the_same_one_game_too()
    {
        var title = "Street Fighter " + _set;
        var pc = await Machine("pc");
        var pcSave = Scan("pc", title: null);
        Assert.Equal((1, 0), await Enroller.EnrollAsync(pc, new[] { pcSave }, new[] { 0 }));
        var deck = await Machine("deck");
        var deckSave = Scan("deck", title);
        Assert.Equal((1, 0), await Enroller.EnrollAsync(deck, new[] { deckSave }, new[] { 0 }));

        Assert.Equal(Tracked(pc, pcSave).GameId, Tracked(deck, deckSave).GameId);
        Assert.Equal(_set, Tracked(deck, deckSave).Name);
    }

    [Fact]
    public void A_game_adopted_from_the_server_is_found_by_its_files_under_any_name()
    {
        var c = Scan("x", title: null);
        var config = AgentConfig.Load(Path.Combine(_dir, "x", "state", "config.json"));
        config.Games.Add(new TrackedGame
        {
            GameId = Guid.NewGuid(), Name = "Whatever the Deck called it", SaveDirectory = "",
            IncludeGlobs = c.IncludeGlobs!.ToList(),
            ExtraPaths = c.ExtraSaveDirs!.Select(e => new TrackedSavePath { Key = e.Key, IncludeGlobs = e.IncludeGlobs!.ToList() }).ToList(),
        });
        Assert.Equal("Whatever the Deck called it", Enroller.TrackedFor(config, c)?.Name);

        // Another ROM's game is not it. The same save with other folders is: the save file decides, not which
        // other folders an agent version declares.
        var other = c with { IncludeGlobs = new[] { "other.srm", "other.rtc" } };
        Assert.Null(Enroller.TrackedFor(config, other));
        Assert.Equal("Whatever the Deck called it",
            Enroller.TrackedFor(config, c with { ExtraSaveDirs = Array.Empty<DeclaredSavePath>() })?.Name);
    }

    [Fact]
    public void Server_name_prefers_the_game_keeping_these_files_then_a_free_name()
    {
        var c = Scan("y", title: null);
        var extras = c.ExtraSaveDirs!.ToList();
        GameDto Game(string name, string[]? scope, SavePathDto[]? paths = null) => new(Guid.NewGuid(), name, null, null, true,
            IncludeGlobs: scope, ExtraPaths: paths);
        var states = extras.Select(e => new SavePathDto(e.Key, null, null, e.IncludeGlobs!.ToArray())).ToArray();

        // Same files under another name: that name.
        Assert.Equal("Named elsewhere", Enroller.ServerNameFor(
            new[] { Game(c.Name, new[] { "x.srm" }), Game("Named elsewhere", c.IncludeGlobs!.ToArray(), states) }, c, extras));
        // The same save with fewer folders (an older agent's game) is still it; of two such games, the one with
        // exactly these folders wins.
        var scope = c.IncludeGlobs!.Select(g => g.ToUpperInvariant()).ToArray();
        Assert.Equal("Old agent's", Enroller.ServerNameFor(new[] { Game("Old agent's", scope) }, c, extras));
        Assert.Equal("Exact", Enroller.ServerNameFor(new[] { Game("A first by name", scope), Game("Exact", scope, states) }, c, extras));
        // A PC game holding the plain name: the next free one.
        Assert.Equal($"{c.Name} (RetroArch)", Enroller.ServerNameFor(new[] { Game(c.Name, null) }, c, extras));
        // An unscoped candidate is never matched by files (it names none).
        var pc = new ScanCandidate("Pc", "/x", ScanSource.Emulator, false);
        Assert.Equal("Pc", Enroller.ServerNameFor(new[] { Game("Other", null) }, pc, Array.Empty<DeclaredSavePath>()));
    }
}
