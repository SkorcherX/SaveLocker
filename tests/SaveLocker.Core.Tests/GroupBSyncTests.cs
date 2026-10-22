using SaveLocker.Agent;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// The Group B emulators end to end against a real server (tasks/emulator-saves Phases 8–10): a Supermodel set
/// and a ScummVM target sync between two machines whose folders lay out differently (EmuDeck for Windows vs
/// SteamOS, a global vs a per-game savepath) — exactly that game's files move, never its neighbours'.
/// </summary>
public sealed class GroupBSyncTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-groupb-" + Guid.NewGuid().ToString("N"));
    // The class shares one server, so each test's game is its own.
    private readonly string _id = Guid.NewGuid().ToString("N")[..6];

    public GroupBSyncTests(ServerProcess server)
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
    public async Task A_supermodel_set_and_its_states_travel_between_two_layouts()
    {
        var set = "scud" + _id;
        // Windows (EmuDeck's Emulators\Supermodel): NVRAM, a state, and another set beside it.
        Write("win", $"sm/NVRAM/{set}.nv", "win-nvram");
        Write("win", $"sm/Saves/{set}.st0", "win-state0");
        Write("win", "sm/NVRAM/lemans24.nv", "win-lemans");
        // SteamOS (~/.supermodel): an older NVRAM, no states folder yet, its own other set.
        Write("deck", $".supermodel/NVRAM/{set}.nv", "deck-old");
        Write("deck", ".supermodel/NVRAM/lemans24.nv", "deck-lemans");

        var win = await Machine("win");
        var deck = await Machine("deck");
        var winGame = await Enroll(win, SupermodelSaves.Scan(new[] { (P("win", "sm"), true) }, Array.Empty<string>()), set);
        var deckGame = await Enroll(deck, SupermodelSaves.Scan(new[] { (P("deck", ".supermodel"), true) }, Array.Empty<string>()), set);
        Assert.Equal(winGame.GameId, deckGame.GameId);

        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        Assert.NotNull(await engineWin.PushAsync(winGame, force: true));
        await engineDeck.PullAsync(deckGame, force: true);

        Assert.Equal("win-nvram", Read("deck", $".supermodel/NVRAM/{set}.nv"));
        Assert.Equal("win-state0", Read("deck", $".supermodel/Saves/{set}.st0"));
        Assert.Equal("deck-lemans", Read("deck", ".supermodel/NVRAM/lemans24.nv"));

        // The Deck plays and pushes; Windows pulls it, its other set untouched.
        Write("deck", $".supermodel/NVRAM/{set}.nv", "deck-new");
        Assert.NotNull(await engineDeck.PushAsync(deckGame));
        await engineWin.PullAsync(winGame);
        Assert.Equal("deck-new", Read("win", $"sm/NVRAM/{set}.nv"));
        Assert.Equal("win-lemans", Read("win", "sm/NVRAM/lemans24.nv"));
    }

    private static string Sha(string content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task An_untouched_emudeck_seed_joins_the_fleets_game_and_takes_its_save()
    {
        var set = "scud" + _id;
        var seeds = new HashSet<string> { Sha("emudeck-seed") };
        Write("win", $"sm/NVRAM/{set}.nv", "win-played");
        Write("deck", $".supermodel/NVRAM/{set}.nv", "emudeck-seed");
        var deckScan = () => SupermodelSaves.Scan(new[] { (P("deck", ".supermodel"), true) }, Array.Empty<string>(), seeds);
        Assert.True(Assert.Single(deckScan()).UntouchedSeed);

        // Nothing on the server keeps it yet: never a game of its own, and the row says why.
        var deck = await Machine("deck");
        var options = EnrollLinks.For(await ApiClient.For(deck).ListGamesAsync(), deckScan()[0]);
        Assert.Equal((LinkKind.Blocked, "Not played"), (Assert.Single(options).Kind, options[0].Badge));
        Assert.Equal((0, 1), await Enroller.EnrollAsync(deck, deckScan(), new[] { 0 }));
        Assert.Contains("preinstalled", EnrollLinks.Resolve(await ApiClient.For(deck).ListGamesAsync(), deckScan()[0],
            deckScan()[0].ExtraSaveDirs!, null).Refusal);
        Assert.Contains("preinstalled", EnrollLinks.Resolve(await ApiClient.For(deck).ListGamesAsync(), deckScan()[0],
            deckScan()[0].ExtraSaveDirs!, new LinkChoice(EnrollLinks.Separate)).Refusal);

        var win = await Machine("win");
        var winGame = await Enroll(win, SupermodelSaves.Scan(new[] { (P("win", "sm"), true) }, Array.Empty<string>(), seeds), set);
        await using var engineWin = new SyncEngine(win, ApiClient.For(win));
        Assert.NotNull(await engineWin.PushAsync(winGame));

        // Now it joins, and the first pull replaces the seed without force: there was nothing of the user's to keep.
        Assert.Equal(LinkKind.Join, EnrollLinks.For(await ApiClient.For(deck).ListGamesAsync(), deckScan()[0])[0].Kind);
        var deckGame = await Enroll(deck, deckScan(), set);
        Assert.Equal(winGame.GameId, deckGame.GameId);
        await using var engineDeck = new SyncEngine(deck, ApiClient.For(deck));
        Assert.True(await engineDeck.PullAsync(deckGame));
        Assert.Equal("win-played", Read("deck", $".supermodel/NVRAM/{set}.nv"));
    }

    [Fact]
    public async Task A_scummvm_target_syncs_and_a_same_prefixed_sequel_stays_put()
    {
        var target = "monkey" + _id;
        string Ini(string saves) =>
            $"[scummvm]\nsavepath={saves}\n\n[{target}]\ngameid=monkey\nengineid=scumm\ndescription=Monkey {_id} (CD/DOS)\n\n" +
            $"[{target}2]\ngameid=monkey2\nengineid=scumm\n";
        Write("a", "svm/scummvm.ini", Ini(P("a", "saves")));
        Write("a", $"saves/{target}.s00", "a-slot0");
        Write("a", $"saves/{target}.s01", "a-slot1");
        Write("a", $"saves/{target}2.s00", "a-sequel");
        // B keeps its saves in the default folder, with the sequel's and an old slot of its own.
        Write("b", "svm/scummvm.ini", $"[{target}]\ngameid=monkey\nengineid=scumm\n\n[{target}2]\ngameid=monkey2\n");
        Write("b", $"default/{target}.s00", "b-old");
        Write("b", $"default/{target}2.s00", "b-sequel");

        var a = await Machine("a");
        var b = await Machine("b");
        var gameA = await Enroll(a, ScummVmSaves.Scan(new[] { new ScummVmConfig(P("a", "svm/scummvm.ini"), P("a", "nope")) },
            Array.Empty<string>()), target);
        // B's config has no description, so it would call the game by its target — the files decide (D1).
        var gameB = await Enroll(b, ScummVmSaves.Scan(new[] { new ScummVmConfig(P("b", "svm/scummvm.ini"), P("b", "default")) },
            Array.Empty<string>()), target);
        Assert.Equal(gameA.GameId, gameB.GameId);
        Assert.Equal($"Monkey {_id}", gameB.Name);

        await using var engineA = new SyncEngine(a, ApiClient.For(a));
        await using var engineB = new SyncEngine(b, ApiClient.For(b));
        Assert.NotNull(await engineA.PushAsync(gameA, force: true));
        await engineB.PullAsync(gameB, force: true);

        Assert.Equal("a-slot0", Read("b", $"default/{target}.s00"));
        Assert.Equal("a-slot1", Read("b", $"default/{target}.s01"));
        Assert.Equal("b-sequel", Read("b", $"default/{target}2.s00"));
    }
}
