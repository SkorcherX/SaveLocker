using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// The agent half of multiple save paths (tasks/multiple-save-paths plan §3, §7): which folders a
/// tracked game hashes, the shadow an unmapped folder syncs through, and what mapping a folder does
/// with the copy it was syncing until then. None of this needs a server.
/// </summary>
public sealed class MultiPathAgentTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "savelocker-multipath-" + Guid.NewGuid().ToString("N"));

    public MultiPathAgentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (SyncEngine Engine, AgentConfig Config, TrackedGame Game) Rig(Action<TrackedGame>? setup = null)
    {
        var save = Dir("primary");
        Write(save, "slot1.sav", "primary progress");
        var game = new TrackedGame
        {
            GameId = Guid.NewGuid(), Name = "Multi", SaveDirectory = save,
            ExtraPaths = { new TrackedSavePath { Key = "states" } },
        };
        setup?.Invoke(game);

        var config = AgentConfig.Load(Path.Combine(_dir, "state", "config.json"));
        config.ServerUrl = "http://127.0.0.1:1";
        config.ApiKey = "multipath-test-key";
        config.Games.Add(game);
        config.Save();
        return (new SyncEngine(config, ApiClient.For(config)), config, game);
    }

    private string Dir(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string dir, string rel, string content)
    {
        var full = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Shadow(AgentConfig config, TrackedGame game, string key = "states") =>
        TrackedGame.ShadowDir(config.StateDir, game.GameId, key);

    [Fact]
    public void An_unmapped_folder_syncs_through_its_shadow_inside_the_state_dir()
    {
        var (_, config, game) = Rig();
        var roots = game.Roots(config.StateDir);

        Assert.Equal(new[] { "main", "states" }, roots.Select(r => r.Key));
        Assert.Equal(Shadow(config, game), roots[1].Directory);
        Assert.StartsWith(config.StateDir, roots[1].Directory);
        // A shadow is the agent's own copy: never watched, guarded or settled as a real folder.
        Assert.Equal(new[] { "main" }, game.RealRoots(config.StateDir).Select(r => r.Key));
    }

    [Fact]
    public void The_local_hash_covers_every_folder_not_just_the_primary_one()
    {
        var (_, config, game) = Rig();
        var primaryOnly = SaveArchive.HashDirectory(game.SaveDirectory);
        Assert.Equal(primaryOnly, game.LocalHash(config.StateDir));   // no shadow yet: no marker

        Write(Shadow(config, game), "quick.state", "state bytes");
        Assert.NotEqual(primaryOnly, game.LocalHash(config.StateDir));
        Assert.Equal(SaveArchive.HashDirectory(game.Roots(config.StateDir)), game.LocalHash(config.StateDir));
    }

    [Fact]
    public async Task Mapping_onto_an_empty_folder_moves_the_shadow_in_and_keeps_the_hash()
    {
        var (engine, config, game) = Rig();
        Write(Shadow(config, game), "quick.state", "state bytes");
        var before = game.LocalHash(config.StateDir);
        var target = Path.Combine(_dir, "real-states");   // does not exist yet: mapping creates it

        var result = await engine.MapSavePathAsync(game, "states", target);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("state bytes", File.ReadAllText(Path.Combine(target, "quick.state")));
        Assert.False(Directory.Exists(Shadow(config, game)));
        Assert.Equal(before, game.LocalHash(config.StateDir));   // same content, new home: nothing to push
    }

    [Fact]
    public async Task Mapping_onto_a_folder_with_different_files_asks_and_changes_nothing()
    {
        var (engine, config, game) = Rig();
        Write(Shadow(config, game), "quick.state", "cloud bytes");
        var target = Dir("real-states");
        Write(target, "quick.state", "local bytes");

        var result = await engine.MapSavePathAsync(game, "states", target);

        Assert.False(result.Ok);
        Assert.True(result.NeedsChoice);
        Assert.Null(game.ExtraPaths[0].Directory);
        Assert.True(Directory.Exists(Shadow(config, game)));
        Assert.Equal("local bytes", File.ReadAllText(Path.Combine(target, "quick.state")));
    }

    [Fact]
    public async Task Keeping_the_cloud_copy_replaces_the_folders_files()
    {
        var (engine, config, game) = Rig();
        Write(Shadow(config, game), "quick.state", "cloud bytes");
        var before = game.LocalHash(config.StateDir);
        var target = Dir("real-states");
        Write(target, "quick.state", "local bytes");
        Write(target, "stale.state", "only here");

        var result = await engine.MapSavePathAsync(game, "states", target, KeepSide.Cloud);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("cloud bytes", File.ReadAllText(Path.Combine(target, "quick.state")));
        Assert.False(File.Exists(Path.Combine(target, "stale.state")));
        Assert.Equal(before, game.LocalHash(config.StateDir));
        Assert.False(Directory.Exists(Shadow(config, game)));
    }

    [Fact]
    public async Task Keeping_the_local_copy_leaves_the_folder_alone_and_changes_the_hash()
    {
        var (engine, config, game) = Rig();
        Write(Shadow(config, game), "quick.state", "cloud bytes");
        var before = game.LocalHash(config.StateDir);
        var target = Dir("real-states");
        Write(target, "quick.state", "local bytes");

        var result = await engine.MapSavePathAsync(game, "states", target, KeepSide.Local);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("local bytes", File.ReadAllText(Path.Combine(target, "quick.state")));
        Assert.NotEqual(before, game.LocalHash(config.StateDir));   // the next push sends it
    }

    [Fact]
    public async Task Identical_files_on_both_sides_map_without_asking()
    {
        var (engine, config, game) = Rig();
        Write(Shadow(config, game), "quick.state", "same");
        var target = Dir("real-states");
        Write(target, "quick.state", "same");

        var result = await engine.MapSavePathAsync(game, "states", target);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.NeedsChoice);
    }

    [Fact]
    public async Task A_folder_inside_the_primary_one_is_refused()
    {
        var (engine, _, game) = Rig();
        var inside = Dir(Path.Combine("primary", "states"));

        var result = await engine.MapSavePathAsync(game, "states", inside);

        Assert.False(result.Ok);
        Assert.Contains("nested", result.Error);
        Assert.Null(game.ExtraPaths[0].Directory);
    }

    [Fact]
    public async Task Two_folders_may_share_a_directory_only_when_both_are_scoped()
    {
        var (engine, _, game) = Rig(g => g.ExtraPaths[0].IncludeGlobs = new() { "*.state" });
        Assert.False((await engine.MapSavePathAsync(game, "states", game.SaveDirectory)).Ok);

        var (engine2, _, scoped) = Rig(g =>
        {
            g.IncludeGlobs = new() { "*.sav" };
            g.ExtraPaths[0].IncludeGlobs = new() { "*.state" };
        });
        var result = await engine2.MapSavePathAsync(scoped, "states", scoped.SaveDirectory);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task A_mapping_survives_a_reload_and_a_sync_state_write_from_a_stale_copy()
    {
        var (engine, config, game) = Rig();
        var target = Dir("real-states");
        Assert.True((await engine.MapSavePathAsync(game, "states", target)).Ok);

        var reloaded = AgentConfig.Load(config.ConfigPath).Games.Single();
        Assert.Equal(target, reloaded.ExtraPaths.Single().Directory);

        // Another process's copy writes its sync state: the folders it carries are its own, current ones.
        reloaded.LastSyncedHash = "abc";
        config.SaveGameSyncState(reloaded);
        Assert.Equal(target, AgentConfig.Load(config.ConfigPath).Games.Single().ExtraPaths.Single().Directory);
    }

    [Fact]
    public void Forgetting_a_removed_folder_deletes_its_shadow_and_remembers_the_key()
    {
        var (_, config, game) = Rig();
        Write(Shadow(config, game), "quick.state", "bytes");

        Assert.True(game.ForgetExtraPath("states", config.StateDir));

        Assert.Empty(game.ExtraPaths);
        Assert.Contains("states", game.RemovedPathKeys);
        Assert.False(Directory.Exists(Shadow(config, game)));
    }

    [Fact]
    public async Task A_folder_never_received_here_asks_before_mapping_onto_files()
    {
        // The poller learns of a key before this machine has pulled it: no shadow exists, so an empty
        // copy says nothing about the fleet's files, and mapping these silently would end in a conflict.
        var (engine, _, game) = Rig();
        var target = Dir("real-states");
        Write(target, "quick.state", "this machine's own states");

        var asked = await engine.MapSavePathAsync(game, "states", target);
        Assert.False(asked.Ok);
        Assert.True(asked.NeedsChoice);
        Assert.Null(game.ExtraPaths[0].Directory);

        var cloud = await engine.MapSavePathAsync(game, "states", target, KeepSide.Cloud);
        Assert.False(cloud.Ok);
        Assert.False(cloud.NeedsChoice);   // there is no synced copy to take: pull first
        Assert.Equal("this machine's own states", File.ReadAllText(Path.Combine(target, "quick.state")));

        var local = await engine.MapSavePathAsync(game, "states", target, KeepSide.Local);
        Assert.True(local.Ok, local.Error);
        Assert.Equal(target, game.ExtraPaths[0].Directory);
    }

    [Fact]
    public async Task A_received_but_empty_copy_maps_onto_files_without_asking()
    {
        var (engine, config, game) = Rig();
        Directory.CreateDirectory(Shadow(config, game));   // a pull restored the key's (empty) slice
        var target = Dir("real-states");
        Write(target, "quick.state", "bytes");

        var result = await engine.MapSavePathAsync(game, "states", target);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Mapping_marks_the_folder_unreported_until_the_server_hears_of_it()
    {
        var (engine, config, game) = Rig();
        Assert.True((await engine.MapSavePathAsync(game, "states", Dir("real-states"))).Ok);

        Assert.True(game.ExtraPaths[0].PathUnreported);
        Assert.True(AgentConfig.Load(config.ConfigPath).Games.Single().ExtraPaths.Single().PathUnreported);
    }

    [Fact]
    public async Task Saving_folders_from_a_stale_copy_keeps_a_mapping_another_process_wrote()
    {
        var (_, config, game) = Rig();
        // The launch wrapper's own process maps the folder and writes it to disk.
        var other = AgentConfig.Load(config.ConfigPath);
        var otherEngine = new SyncEngine(other, ApiClient.For(other));
        var target = Dir("real-states");
        Assert.True((await otherEngine.MapSavePathAsync(other.Games.Single(), "states", target)).Ok);

        // This process still holds "not mapped" and writes its folders (a pull met a new key).
        game.ExtraPaths = [.. game.ExtraPaths, new TrackedSavePath { Key = "cfg" }];
        config.SaveGameFolders(game);

        var onDisk = AgentConfig.Load(config.ConfigPath).Games.Single();
        Assert.Equal(target, onDisk.ExtraPaths.Single(p => p.Key == "states").Directory);
        Assert.Contains(onDisk.ExtraPaths, p => p.Key == "cfg");
        Assert.Equal(target, game.ExtraPaths.Single(p => p.Key == "states").Directory);
    }

    [Fact]
    public async Task Forgetting_under_the_lock_swaps_the_list_rather_than_editing_it()
    {
        var (engine, config, game) = Rig();
        Write(Shadow(config, game), "quick.state", "bytes");
        var seenByAPush = game.ExtraPaths;   // a push on another thread is enumerating this

        Assert.True(await engine.ForgetSavePathAsync(game, "states"));

        Assert.Single(seenByAPush);
        Assert.Empty(game.ExtraPaths);
        Assert.Contains("states", game.RemovedPathKeys);
        Assert.False(Directory.Exists(Shadow(config, game)));
    }

    [Fact]
    public async Task The_settle_gate_waits_on_every_real_folder()
    {
        var a = Dir("a");
        var b = Dir("b");
        Write(a, "x.sav", "1");
        var roots = new[] { SaveRoot.Primary(a), new SaveRoot("states", b) };

        // A write landing in the second folder mid-wait must restart the quiet period.
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(150);
                Write(b, "s.state", "tick " + i);
            }
        });
        // The probe is pinned quiet: this is the fingerprint's job, and a CI runner's scanner holding
        // a just-written file reads as a writer for as long as it likes (FileLockProbe's doc). The
        // real probe would also hold its own deny-writers handle mid-write and fault the writer.
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var quiet = await SaveSettler.WaitForQuietAsync(roots, null,
            TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10),
            (_, _) => FileLockProbe.LockProbeResult.Quiet, log.Enqueue);
        await writer;

        Assert.True(quiet, string.Join(" / ", log));
        Assert.True(clock.ElapsedMilliseconds >= 900, $"settled after {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_lock_probe_sees_a_writer_in_any_folder_but_not_a_reader()
    {
        var a = Dir("pa");
        var b = Dir("pb");
        Write(a, "x.sav", "1");
        Write(b, "s.state", "2");
        var roots = new[] { SaveRoot.Primary(a), new SaveRoot("states", b) };
        var files = SaveArchive.ListSaveFiles(roots, null);

        using (new FileStream(Path.Combine(b, "s.state"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Null(QuietProbe(roots, files));

        using (new FileStream(Path.Combine(b, "s.state"), FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.Equal("s.state", FileLockProbe.FirstWriter(roots, files).LockedFile);

        Assert.Null(QuietProbe(roots, files));
    }

    /// <summary>
    /// The real probe, asked until it reports no writer or 5 s pass. A CI runner's scanner or indexer can hold a
    /// just-written file without sharing reads, which reads exactly like a writer (Gotchas → settle gate) — but
    /// only for a moment, while a writer this test holds would be reported for the whole wait.
    /// </summary>
    private static string? QuietProbe(IReadOnlyList<SaveRoot> roots, IReadOnlyList<SaveArchive.SaveFile> files)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var locked = FileLockProbe.FirstWriter(roots, files).LockedFile;
            if (locked is null || clock.Elapsed > TimeSpan.FromSeconds(5)) return locked;
            Thread.Sleep(100);
        }
    }

    [Fact]
    public async Task One_watcher_covers_every_folder_with_one_delay()
    {
        var a = Dir("wa");
        var b = Dir("wb");
        var fired = 0;
        using var watcher = new FolderWatcher(new[] { a, b, Path.Combine(_dir, "missing") },
            () => Interlocked.Increment(ref fired), debounceMs: 1000);

        Write(a, "x.sav", "1");
        Write(b, "y.state", "2");
        // The watcher's events and the delay's callback both run on the thread pool, which the rest of the suite
        // keeps busy: a fixed wait read 0 under load. Wait for the fire, then a quiet spell for a second one.
        Assert.True(await Eventually(() => Volatile.Read(ref fired) > 0), "the delay never fired");
        await Task.Delay(2000);
        Assert.Equal(1, Volatile.Read(ref fired));

        // The second folder is watched too, not merely written alongside the first.
        Write(b, "y.state", "3");
        Assert.True(await Eventually(() => Volatile.Read(ref fired) > 1), "a change in the second folder never fired");
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) return false;
            await Task.Delay(50);
        }
        return true;
    }
}
