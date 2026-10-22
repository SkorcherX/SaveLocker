using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// Phase 6 of multiple save paths: a manifest game's locations beyond the first are suggested as
/// "Also found", never adopted on their own; the first stays the primary folder exactly as before.
/// </summary>
public sealed class FolderSuggestionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savelocker-suggest-" + Guid.NewGuid().ToString("N"));

    // A Proton prefix layout, so the same fixture resolves on Linux (compatdata) and Windows (<base>).
    private string InstallDir => Path.Combine(_dir, "steamapps", "compatdata", "4242", "game");

    public FolderSuggestionsTests() => Directory.CreateDirectory(InstallDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Manifest = """
        Two Folders:
          files:
            <base>/Saves:
              tags: [save]
            <base>/Config/*.ini:
              tags: [save]
            <base>/Saves/Slots:
              tags: [save]
            <base>/Missing:
              tags: [save]
        """;

    private string Folder(string name)
    {
        var d = Path.Combine(InstallDir, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "f.dat"), name);
        return Path.GetFullPath(d);
    }

    private (AgentConfig Config, Detection Detection, TrackedGame Game) Tracked()
    {
        var config = AgentConfig.Load(Path.Combine(_dir, "agent", "config.json"));
        config.ManifestCachePath = Path.Combine(_dir, "manifest.yaml");
        File.WriteAllText(config.ManifestCachePath, Manifest);
        var game = new TrackedGame
        {
            GameId = Guid.NewGuid(), Name = "Two Folders", ManifestKey = "Two Folders",
            SaveDirectory = Folder("Saves"), InstallDir = InstallDir,
        };
        config.MutateGames(l => l.Add(game));
        config.Save();
        return (config, new Detection(config), game);
    }

    [Theory]
    [InlineData("<winAppData>/Game/Config/*.ini", "config")]
    [InlineData("<base>/Saves", "saves")]
    [InlineData("<winDocuments>/My Games/Foo Bar", "foo-bar")]
    [InlineData("<home>/*", "extra")]
    [InlineData("<base>/A Very Long Folder Name That Goes On And On", "a-very-long-folder-name-that-goe")]
    public void A_key_comes_from_the_templates_last_fixed_segment(string template, string key)
    {
        var made = FolderSuggestions.KeyFor(template, new HashSet<string>());
        Assert.Equal(key, made);
        Assert.Null(SaveRoot.ValidateExtraKey(made));
    }

    [Fact]
    public void A_taken_key_gets_the_next_free_suffix()
    {
        Assert.Equal("config-2", FolderSuggestions.KeyFor("<base>/Config", new HashSet<string> { "config" }));
        Assert.Equal("config-3", FolderSuggestions.KeyFor("<base>/Config", new HashSet<string> { "config", "config-2" }));
        Assert.Equal("main-2", FolderSuggestions.KeyFor("<base>/Main", new HashSet<string> { "main" }));
    }

    [Fact]
    public void Locations_keep_their_template_in_manifest_order_and_the_primary_is_unchanged()
    {
        var saves = Folder("Saves");
        var config = Folder("Config");
        Folder(Path.Combine("Saves", "Slots"));
        var manifest = ManifestLoader.Parse(Manifest);
        var resolver = PathResolver.Windows(InstallDir);

        var locations = manifest.ResolveSaveLocations("Two Folders", resolver);

        Assert.Equal(new[] { "<base>/Saves", "<base>/Config/*.ini", "<base>/Saves/Slots" }, locations.Select(l => l.Template));
        Assert.Equal(saves, locations[0].Directory);
        Assert.Equal(config, locations[1].Directory);
        Assert.Equal(locations.Select(l => l.Directory), manifest.ResolveSaveDirectories("Two Folders", resolver));
    }

    [Fact]
    public void Also_found_leaves_out_the_primary_folder_and_any_nested_in_it()
    {
        var saves = Folder("Saves");
        var config = Folder("Config");
        var slots = Folder(Path.Combine("Saves", "Slots"));
        var locations = new[]
        {
            new ResolvedSaveLocation("<base>/Saves", saves),
            new ResolvedSaveLocation("<base>/Config/*.ini", config),
            new ResolvedSaveLocation("<base>/Saves/Slots", slots),
            new ResolvedSaveLocation("<base>/Config", config),
        };

        var also = FolderSuggestions.AlsoFound(locations, saves);

        var only = Assert.Single(also!);
        Assert.Equal("config", only.Key);
        Assert.Equal(config, only.Dir);
        Assert.Null(FolderSuggestions.AlsoFound(locations[..1], saves));
    }

    [Fact]
    public async Task A_tracked_game_is_offered_its_other_folder_until_it_has_it_or_says_no()
    {
        Folder("Config");
        var (config, detection, game) = Tracked();
        var suggestions = new FolderSuggestions(config, detection);

        var offered = Assert.Single(await suggestions.ForGameAsync(game));
        Assert.Equal("config", offered.Key);
        Assert.Equal(Path.Combine(InstallDir, "Config"), offered.Directory);
        Assert.False(offered.Deferred);

        // "Skip for now": still offered, marked so the start-up prompt leaves it alone.
        config.SaveGameFolderChoices(game.GameId, defer: new[] { offered.Directory });
        Assert.True(Assert.Single(await suggestions.ForGameAsync(game)).Deferred);
        Assert.Contains(offered.Directory, AgentConfig.Load(config.ConfigPath).Games.Single().DeferredFolders);

        // "Don't sync": never offered again.
        config.SaveGameFolderChoices(game.GameId, ignore: new[] { offered.Directory });
        Assert.Empty(await suggestions.ForGameAsync(game));
        Assert.Empty(AgentConfig.Load(config.ConfigPath).Games.Single().DeferredFolders);
    }

    [Fact]
    public async Task A_folder_the_game_already_syncs_is_not_offered_and_a_used_key_is_not_reused()
    {
        var configDir = Folder("Config");
        var (config, detection, game) = Tracked();
        var suggestions = new FolderSuggestions(config, detection);

        game.ExtraPaths = [new TrackedSavePath { Key = "config", Directory = configDir }];
        Assert.Empty(await suggestions.ForGameAsync(game));

        // An unmapped folder whose template names it counts as the same folder.
        game.ExtraPaths = [new TrackedSavePath { Key = "config", Template = "<base>/Config" }];
        Assert.Empty(await suggestions.ForGameAsync(game));

        game.ExtraPaths = [];
        game.RemovedPathKeys = ["config"];
        Assert.Equal("config-2", Assert.Single(await suggestions.ForGameAsync(game)).Key);
    }

    [Fact]
    public async Task A_game_with_no_folder_here_gets_no_suggestions()
    {
        Folder("Config");
        var (config, detection, game) = Tracked();
        game.SaveDirectory = "";

        Assert.Empty(await new FolderSuggestions(config, detection).ForGameAsync(game));
    }
}
