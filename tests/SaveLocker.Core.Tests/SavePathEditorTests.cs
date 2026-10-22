using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// Group C of multiple save paths: the steps behind the agent UI's "Add save folder", the "Also found"
/// prompt and `add-path`/`remove-path`, against a real server — one implementation for all of them.
/// </summary>
public sealed class SavePathEditorTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-editor-" + Guid.NewGuid().ToString("N"));

    public SavePathEditorTests(ServerProcess server)
    {
        _server = server;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name, string? file = "f.dat")
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        if (file is not null) File.WriteAllText(Path.Combine(d, file), name);
        return d;
    }

    /// <param name="machine">Another machine enrolling the same game: its own state and main folder.</param>
    private async Task<(AgentConfig Config, TrackedGame Game, SyncEngine Engine)> Enrolled(string game, string? machine = null)
    {
        var id = machine is null ? game : $"{game}-{machine}";
        var config = AgentConfig.Load(Path.Combine(_dir, "agent-" + id, "config.json"));
        config.ServerUrl = _server.Url;
        var reg = await new ApiClient(config.ServerUrl, null).RegisterAsync("m-" + id);
        (config.ApiKey, config.MachineId, config.MachineName) = (reg.ApiKey, reg.MachineId, "m-" + id);
        config.Save();
        await Enroller.EnrollAsync(config, new[] { new ScanCandidate(game, Folder(id + "-main"), ScanSource.SaveRoot, false) }, new[] { 0 });
        return (config, config.FindGame(game)!, new SyncEngine(config, ApiClient.For(config)));
    }

    private static Func<TrackedGame, string, string, KeepSide?, Task<MapFolderResult>> Map(SyncEngine e) =>
        (g, k, d, keep) => e.MapSavePathAsync(g, k, d, keep);

    [Fact]
    public async Task A_folder_added_here_is_defined_for_the_fleet_and_mapped_here()
    {
        var name = "Edit-" + Guid.NewGuid().ToString("N")[..8];
        var (config, game, engine) = await Enrolled(name);
        var states = Folder(name + "-states");

        var result = await SavePathEditor.AddAsync(config, ApiClient.For(config), Map(engine), game, "states", states,
            include: new[] { "*.state" });

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Added);
        Assert.True(result.Reported);
        var tracked = AgentConfig.Load(config.ConfigPath).FindGame(name)!;
        Assert.Equal(states, Assert.Single(tracked.ExtraPaths).Directory);
        var server = Assert.Single((await ApiClient.For(config).ListGamesAsync()).Single(g => g.Name == name).ExtraPaths!);
        Assert.Equal("states", server.Key);
        Assert.Equal(new[] { "*.state" }, server.IncludeGlobs);
        Assert.Equal(states, server.MachinePath);
    }

    [Fact]
    public async Task A_suggested_key_the_game_had_before_moves_to_the_next_free_one()
    {
        var name = "Retired-" + Guid.NewGuid().ToString("N")[..8];
        var (config, game, engine) = await Enrolled(name);
        var api = ApiClient.For(config);
        Assert.True((await SavePathEditor.AddAsync(config, api, Map(engine), game, "config", Folder(name + "-a"))).Ok);
        Assert.True((await SavePathEditor.RemoveAsync(config, api, (g, k) => engine.ForgetSavePathAsync(g, k), game, "config")).Ok);

        var named = await SavePathEditor.AddAsync(config, api, Map(engine), game, "config", Folder(name + "-b"));
        var suggested = await SavePathEditor.AddAsync(config, api, Map(engine), game, "config", Folder(name + "-b"), pickFreeKey: true);

        Assert.False(named.Ok);
        Assert.Contains("before", named.Error);
        Assert.True(suggested.Ok, suggested.Error);
        Assert.Equal("config-2", suggested.Key);
    }

    [Fact]
    public async Task Adding_under_a_key_the_game_has_is_refused_rather_than_moving_that_folder()
    {
        var name = "MustBeNew-" + Guid.NewGuid().ToString("N")[..8];
        var (config, game, engine) = await Enrolled(name);
        var api = ApiClient.For(config);
        var first = Folder(name + "-docs-saves");
        Assert.True((await SavePathEditor.AddAsync(config, api, Map(engine), game, "saves", first)).Ok);

        var second = await SavePathEditor.AddAsync(config, api, Map(engine), game, "saves", Folder(name + "-appdata-saves"),
            mustBeNew: true);

        Assert.False(second.Ok);
        Assert.Contains("already has a save folder called 'saves'", second.Error);
        Assert.Equal(first, AgentConfig.Load(config.ConfigPath).FindGame(name)!.ExtraPaths.Single(p => p.Key == "saves").Directory);
        Assert.Equal(first, Assert.Single((await api.ListGamesAsync()).Single(g => g.Name == name).ExtraPaths!).MachinePath);
    }

    [Fact]
    public async Task A_folder_another_machine_added_first_is_joined_not_defined_twice()
    {
        var name = "Joined-" + Guid.NewGuid().ToString("N")[..8];
        var (config, game, engine) = await Enrolled(name);
        // The second machine enrolls the same game and has not polled since the first added the folder: its
        // own list of the game's folders is empty, so only the server can tell it the folder is there.
        var (config2, game2, engine2) = await Enrolled(name, machine: "second");
        var shared = Folder(name + "-config");
        var resolver = new PathResolver(new Dictionary<string, string> { ["<base>"] = _dir });
        Assert.True((await SavePathEditor.AddAsync(config, ApiClient.For(config), Map(engine), game, "config", shared,
            resolver: resolver)).Ok);

        var second = await SavePathEditor.AddAsync(config2, ApiClient.For(config2), Map(engine2), game2, "config", shared,
            resolver: resolver, pickFreeKey: true);

        // Joined under the first machine's key, never defined a second time. The fleet's copy is one this
        // machine has not received, so it asks which to keep rather than mapping over its files.
        Assert.True(second.Joined);
        Assert.False(second.Added);
        Assert.True(second.NeedsChoice, second.Error);
        Assert.Equal("config", second.Key);
        Assert.Equal("config", Assert.Single((await ApiClient.For(config2).ListGamesAsync()).Single(g => g.Name == name).ExtraPaths!).Key);

        // The answer goes to that key, as the agent UI sends it.
        var answered = await SavePathEditor.AddAsync(config2, ApiClient.For(config2), Map(engine2), game2, second.Key!, shared,
            keep: KeepSide.Local);
        Assert.True(answered.Ok, answered.Error);
        Assert.Equal(shared, AgentConfig.Load(config2.ConfigPath).FindGame(name)!.ExtraPaths.Single().Directory);
        Assert.Single((await ApiClient.For(config2).ListGamesAsync()).Single(g => g.Name == name).ExtraPaths!);
    }

    [Fact]
    public async Task Refusals_leave_nothing_behind_on_the_server()
    {
        var name = "Refuse-" + Guid.NewGuid().ToString("N")[..8];
        var (config, game, engine) = await Enrolled(name);
        var api = ApiClient.For(config);

        var nested = await SavePathEditor.AddAsync(config, api, Map(engine), game, "inner",
            Folder(Path.Combine(name + "-main", "inner")));
        var badKey = await SavePathEditor.AddAsync(config, api, Map(engine), game, "Not A Key", Folder(name + "-x"));

        Assert.False(nested.Ok);
        Assert.False(nested.Added);
        Assert.False(badKey.Ok);
        Assert.Empty((await api.ListGamesAsync()).Single(g => g.Name == name).ExtraPaths ?? []);
    }

    [Fact]
    public async Task Removing_a_folder_drops_it_everywhere_and_leaves_its_files()
    {
        var name = "Remove-" + Guid.NewGuid().ToString("N")[..8];
        var (config, game, engine) = await Enrolled(name);
        var api = ApiClient.For(config);
        var dir = Folder(name + "-extra");
        await SavePathEditor.AddAsync(config, api, Map(engine), game, "extra", dir);

        var removed = await SavePathEditor.RemoveAsync(config, api, (g, k) => engine.ForgetSavePathAsync(g, k), game, "extra");

        Assert.True(removed.Ok, removed.Error);
        Assert.False(removed.Deferred);
        Assert.Empty(AgentConfig.Load(config.ConfigPath).FindGame(name)!.ExtraPaths);
        Assert.Empty((await api.ListGamesAsync()).Single(g => g.Name == name).ExtraPaths ?? []);
        Assert.True(File.Exists(Path.Combine(dir, "f.dat")));
    }
}
