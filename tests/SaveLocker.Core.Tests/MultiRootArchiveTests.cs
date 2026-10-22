using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// Several save folders of one game in one archive (tasks/multiple-save-paths Phase 1): the layout, the
/// one-order hash an older agent must agree with, and the restore rules — a folder whose marker is
/// absent is left alone, and a bad slice stops the whole restore before anything is written.
/// </summary>
public sealed class MultiRootArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-multi-" + Guid.NewGuid().ToString("N"));

    public MultiRootArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string P(params string[] parts) => Path.Combine(_root, Path.Combine(parts));

    private string Stage() => P("stage-" + Guid.NewGuid().ToString("N"));

    private string Dir(string name, params (string Rel, string Content)[] files)
    {
        var dir = P(name.Split('/'));
        Directory.CreateDirectory(dir);
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return dir;
    }

    private static SaveRoot[] Game(string saves, string states, IReadOnlyList<string>? statesScope = null) =>
        new[] { SaveRoot.Primary(saves), new SaveRoot("states", states, statesScope) };

    private static IReadOnlyList<string> Entries(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return z.Entries.Select(e => e.FullName).ToList();
    }

    private static IEnumerable<string> FilesUnder(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    /// <summary>The hash an agent older than this feature computes: every file under one folder, chained
    /// as name + bytes in Ordinal order of the names. Written out here so the tests do not lean on the
    /// code they check.</summary>
    private static string OlderAgentHash(string dir)
    {
        using var sha = SHA256.Create();
        foreach (var rel in FilesUnder(dir))
        {
            var name = Encoding.UTF8.GetBytes(rel + "\n");
            sha.TransformBlock(name, 0, name.Length, null, 0);
            var bytes = File.ReadAllBytes(Path.Combine(dir, rel));
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    [Fact]
    public void Two_folders_round_trip_byte_identical_each_in_its_own_scope()
    {
        var saves = Dir("src/saves", ("slot1.sav", "one"), ("sub/slot2.sav", "two"));
        var states = Dir("src/states", ("Game.state", "s0"), ("Game.state1", "s1"), ("Other.state", "not ours"));
        var game = Game(saves, states, new[] { "Game.state*" });
        var zip = P("a.zip");

        SaveArchive.CreateArchive(game, zip);

        // One Ordinal order over the final names: the reserved prefix sorts before "slot1.sav".
        Assert.Equal(new[]
        {
            ".savelocker/keys/states",
            ".savelocker/paths/states/Game.state",
            ".savelocker/paths/states/Game.state1",
            "slot1.sav",
            "sub/slot2.sav",
        }, Entries(zip));

        var targetStates = Dir("dst/states", ("Other.state", "theirs"));
        var target = Game(P("dst", "saves"), targetStates, new[] { "Game.state*" });
        var result = SaveArchive.RestoreArchive(zip, target, Stage());

        Assert.Equal(new[] { "main", "states" }, result.RestoredKeys);
        Assert.Empty(result.SkippedKeys);
        Assert.Equal(SaveArchive.HashDirectory(game), SaveArchive.HashDirectory(target));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(targetStates, "Other.state")));
    }

    [Fact]
    public void A_single_folder_games_archive_and_hash_are_unchanged()
    {
        var saves = Dir("one", ("b.sav", "b"), ("a/c.sav", "c"));
        var zip = P("one.zip");

        SaveArchive.CreateArchive(new[] { SaveRoot.Primary(saves) }, zip);

        Assert.Equal(new[] { "a/c.sav", "b.sav" }, Entries(zip));
        Assert.Equal(OlderAgentHash(saves), SaveArchive.HashDirectory(saves));
        Assert.Equal(OlderAgentHash(saves), SaveArchive.HashDirectory(new[] { SaveRoot.Primary(saves) }));
        Assert.Equal(OlderAgentHash(saves), SaveArchive.ComputeManifest(saves).ContentHash);
    }

    [Fact]
    public void An_older_agent_holding_the_same_save_computes_the_same_hash_and_names()
    {
        // An older agent restores a two-folder version as plain files inside its primary folder. "-" sorts
        // before ".", so hashing folder by folder (primary first) would put "-first.sav" in the wrong place.
        var game = Game(
            Dir("new/saves", ("-first.sav", "dash"), ("slot.sav", "s")),
            Dir("new/states", ("Game.state", "st")));
        var older = Dir("older",
            ("-first.sav", "dash"), ("slot.sav", "s"),
            (".savelocker/paths/states/Game.state", "st"), (".savelocker/keys/states", ""));

        var (files, hash) = SaveArchive.ComputeManifest(game);

        Assert.Equal(OlderAgentHash(older), hash);
        Assert.Equal(OlderAgentHash(older), SaveArchive.HashDirectory(game));
        Assert.Equal(FilesUnder(older), files.Select(f => f.Path));
    }

    [Fact]
    public void An_extra_folder_whose_marker_is_absent_is_left_alone()
    {
        // A version made before the folder existed (or by a machine where it was missing).
        var zip = P("old.zip");
        SaveArchive.CreateArchive(Dir("srv", ("slot.sav", "server")), zip);
        var targetStates = Dir("t/states", ("Game.state", "mine"));

        var result = SaveArchive.RestoreArchive(zip, Game(P("t", "saves"), targetStates), Stage());

        Assert.Equal(new[] { "main" }, result.RestoredKeys);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(targetStates, "Game.state")));
        Assert.Equal("server", File.ReadAllText(P("t", "saves", "slot.sav")));
    }

    [Fact]
    public void An_empty_extra_folder_with_its_marker_empties_it_on_restore()
    {
        var game = Game(Dir("e/saves", ("slot.sav", "s")), Dir("e/states"));
        var zip = P("empty.zip");
        SaveArchive.CreateArchive(game, zip);
        Assert.Contains(".savelocker/keys/states", Entries(zip));

        var targetStates = Dir("et/states", ("Game.state", "stale"), ("deep/Game.state2", "stale"));
        SaveArchive.RestoreArchive(zip, Game(P("et", "saves"), targetStates), Stage());

        Assert.Empty(Directory.EnumerateFileSystemEntries(targetStates));
    }

    [Fact]
    public void A_missing_extra_folder_writes_nothing_and_a_pull_creates_it()
    {
        var states = Dir("m/states-src", ("Game.state", "st"));
        var missing = Game(Dir("m/saves", ("slot.sav", "s")), P("m", "states-not-here"));
        var zip = P("missing.zip");
        SaveArchive.CreateArchive(missing, zip);
        Assert.DoesNotContain(Entries(zip), e => e.StartsWith(SaveArchive.ReservedPrefix, StringComparison.Ordinal));

        var full = Game(Dir("f/saves", ("slot.sav", "s")), states);
        var fullZip = P("full.zip");
        SaveArchive.CreateArchive(full, fullZip);
        var targetStates = P("ft", "states");
        SaveArchive.RestoreArchive(fullZip, Game(P("ft", "saves"), targetStates), Stage());
        Assert.Equal("st", File.ReadAllText(Path.Combine(targetStates, "Game.state")));

        // The primary folder is the one that must exist; with no folder at all the hash is all zeros.
        Assert.Throws<DirectoryNotFoundException>(() => SaveArchive.CreateArchive(Game(P("nope"), states), P("x.zip")));
        Assert.Equal(new string('0', 64), SaveArchive.HashDirectory(Game(P("none1"), P("none2"))));
    }

    [Fact]
    public void The_nested_guard_judges_each_folder_by_its_own_entries()
    {
        // The states folder was archived from X; this machine maps it at X/inner — one level too deep.
        // Over the whole archive there is no common prefix (slot.sav sits at the root), so only a
        // per-folder check sees it.
        var game = Game(Dir("n/saves", ("slot.sav", "new")), Dir("n/states", ("inner/a.state", "a"), ("inner/b.state", "b")));
        var zip = P("nested.zip");
        SaveArchive.CreateArchive(game, zip);
        var targetSaves = Dir("nt/saves", ("slot.sav", "untouched"));

        var ex = Assert.Throws<SaveArchive.UnsafeArchiveException>(() =>
            SaveArchive.RestoreArchive(zip, Game(targetSaves, P("nt", "states", "inner")), Stage()));

        Assert.Contains("deeper", ex.Message);
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(targetSaves, "slot.sav")));
    }

    [Fact]
    public void A_bad_slice_refuses_the_whole_restore_before_anything_is_written()
    {
        var game = Game(Dir("b/saves", ("slot.sav", "new")), Dir("b/states", ("sub/a.state", "a")));
        var zip = P("bad.zip");
        SaveArchive.CreateArchive(game, zip);
        var targetSaves = Dir("bt/saves", ("slot.sav", "old"));
        // A file where the states slice needs a directory: refused — and the primary folder, checked
        // and passed first, must not have been written either.
        var targetStates = Dir("bt/states", ("sub", "a file, not a folder"));

        Assert.Throws<SaveArchive.UnsafeArchiveException>(() =>
            SaveArchive.RestoreArchive(zip, Game(targetSaves, targetStates), Stage()));

        Assert.Equal("old", File.ReadAllText(Path.Combine(targetSaves, "slot.sav")));
    }

    [Fact]
    public void Folders_that_would_share_a_file_are_refused()
    {
        var shared = Dir("s/shared", ("Game.srm", "save"), ("Game.state", "state"));

        // One directory, but not both scoped.
        Assert.Throws<ArgumentException>(() => SaveArchive.HashDirectory(new[]
            { SaveRoot.Primary(shared), new SaveRoot("states", shared, new[] { "*.state" }) }));
        // Both scoped, but both claim Game.state: found only once a file matches both, so it has its own type.
        Assert.Throws<SaveArchive.OverlappingSaveFoldersException>(() => SaveArchive.HashDirectory(new[]
            { SaveRoot.Primary(shared, new[] { "Game.*" }), new SaveRoot("states", shared, new[] { "*.state" }) }));
        // One inside the other.
        Assert.Throws<ArgumentException>(() => SaveArchive.HashDirectory(Game(shared, Path.Combine(shared, "states"))));

        // Disjoint scopes in one directory are fine, both ways.
        SaveRoot[] Disjoint(string dir) =>
            new[] { SaveRoot.Primary(dir, new[] { "Game.srm" }), new SaveRoot("states", dir, new[] { "Game.state*" }) };
        var disjoint = Disjoint(shared);
        var zip = P("shared.zip");
        SaveArchive.CreateArchive(disjoint, zip);
        Assert.Equal(new[] { ".savelocker/keys/states", ".savelocker/paths/states/Game.state", "Game.srm" }, Entries(zip));

        var target = Dir("st/shared", ("Other.srm", "another game"));
        SaveArchive.RestoreArchive(zip, Disjoint(target), Stage());
        Assert.Equal(new[] { "Game.srm", "Game.state", "Other.srm" }, FilesUnder(target));
    }

    [Fact]
    public void A_restore_refuses_a_file_two_folders_sharing_a_directory_would_both_claim()
    {
        // Archived where the folders were apart; restored where they share a directory and the
        // primary folder's scope also covers the incoming state.
        var game = Game(Dir("cl/saves", ("Game.srm", "s")), Dir("cl/states", ("Game.state", "st")));
        var zip = P("clash.zip");
        SaveArchive.CreateArchive(game, zip);
        var shared = Dir("clt/shared", ("Game.srm", "old"));

        Assert.Throws<SaveArchive.UnsafeArchiveException>(() => SaveArchive.RestoreArchive(zip,
            new[] { SaveRoot.Primary(shared, new[] { "Game.*" }), new SaveRoot("states", shared, new[] { "*.state" }) },
            Stage()));
        Assert.Equal("old", File.ReadAllText(Path.Combine(shared, "Game.srm")));
    }

    [Fact]
    public void Keys_this_machine_lacks_are_skipped_and_reported_never_written_elsewhere()
    {
        var game = Game(Dir("k/saves", ("slot.sav", "s")), Dir("k/states", ("Game.state", "st")));
        var zip = P("keys.zip");
        SaveArchive.CreateArchive(game, zip);
        var targetSaves = P("kt", "saves");

        var result = SaveArchive.RestoreArchive(zip, new[] { SaveRoot.Primary(targetSaves) }, Stage());

        Assert.Equal(new[] { "states" }, result.SkippedKeys);
        Assert.Equal(new[] { "slot.sav" }, FilesUnder(targetSaves));
    }

    [Fact]
    public void The_delta_payload_maps_names_back_to_their_folders()
    {
        var game = Game(Dir("d/saves", ("slot.sav", "s")), Dir("d/states", ("Game.state", "st")));
        var zip = P("delta.zip");

        SaveArchive.CreateArchiveSubset(game, zip,
            new[] { ".savelocker/keys/states", ".savelocker/paths/states/Game.state", "slot.sav" });

        Assert.Equal(3, Entries(zip).Count);
        using (var z = ZipFile.OpenRead(zip))
        using (var reader = new StreamReader(z.GetEntry(".savelocker/paths/states/Game.state")!.Open()))
            Assert.Equal("st", reader.ReadToEnd());

        // The server names these paths; anything this game did not declare is refused.
        Assert.Throws<SaveArchive.UnsafeArchiveException>(() =>
            SaveArchive.CreateArchiveSubset(game, P("x.zip"), new[] { ".savelocker/paths/other/a.state" }));
        Assert.Throws<SaveArchive.UnsafeArchiveException>(() =>
            SaveArchive.CreateArchiveSubset(game, P("y.zip"), new[] { ".savelocker/paths/states/../../outside.txt" }));
        Assert.Throws<SaveArchive.UnsafeArchiveException>(() =>
            SaveArchive.CreateArchiveSubset(game, P("z.zip"), new[] { ".savelocker/keys/other" }));
    }

    [Fact]
    public void The_primary_folder_never_archives_or_deletes_the_reserved_folder()
    {
        // What an older agent left in a primary folder before this machine updated.
        var saves = Dir("r/saves", ("slot.sav", "s"), (".savelocker/paths/states/old.state", "passenger"));

        Assert.Equal(new[] { "slot.sav" }, SaveArchive.ListFiles(saves));
        Assert.Equal(SaveArchive.HashDirectory(Dir("r/clean", ("slot.sav", "s"))), SaveArchive.HashDirectory(saves));

        var zip = P("srv.zip");
        SaveArchive.CreateArchive(Dir("r/srv", ("slot.sav", "server")), zip);
        SaveArchive.RestoreArchive(zip, saves, Stage());

        Assert.Equal("server", File.ReadAllText(Path.Combine(saves, "slot.sav")));
        Assert.True(File.Exists(Path.Combine(saves, ".savelocker", "paths", "states", "old.state")));
    }

    [Fact]
    public void A_later_formats_part_of_the_reserved_folder_passes_through_the_primary_folder()
    {
        // A newer agent's version carries a .savelocker/ subtree this one does not know. Dropping it would
        // take it out of the head on the next push, and the hash would never match the head again.
        var newer = Dir("lf/newer", ("slot.sav", "s"), (".savelocker/registry/HKCU.reg", "reg"));
        var zip = P("later.zip");
        SaveArchive.CreateArchive(newer, zip);
        Assert.Contains(".savelocker/registry/HKCU.reg", Entries(zip));

        var saves = Dir("lf/saves", ("slot.sav", "old"));
        var states = P("lf", "states");
        SaveArchive.RestoreArchive(zip, Game(saves, states), Stage());

        Assert.Equal("reg", File.ReadAllText(Path.Combine(saves, ".savelocker", "registry", "HKCU.reg")));
        Assert.Equal(SaveArchive.HashDirectory(newer), SaveArchive.HashDirectory(Game(saves, states)));

        // And it goes again when a version without it is restored, like any file of the primary folder.
        var plain = P("plain.zip");
        SaveArchive.CreateArchive(Dir("lf/plain", ("slot.sav", "s")), plain);
        SaveArchive.RestoreArchive(plain, Game(saves, states), Stage());
        Assert.Equal(new[] { "slot.sav" }, FilesUnder(saves));
    }

    [Fact]
    public void Excludes_match_archive_names_and_markers_ride_in_the_manifest()
    {
        var game = Game(
            Dir("x/saves", ("slot.sav", "s"), ("thumb.png", "p"), ("cache/c.bin", "c")),
            Dir("x/states", ("Game.state", "st"), ("Game.state.png", "p"), ("cache/c.bin", "c")));
        var excludes = new[] { "*.png", "cache/**" };

        // A bare pattern applies everywhere; a rooted one to the primary folder only.
        Assert.Equal(new[] { ".savelocker/paths/states/Game.state", ".savelocker/paths/states/cache/c.bin", "slot.sav" },
            SaveArchive.ListSaveFiles(game, excludes).Select(f => f.ArchiveName));

        var (files, hash) = SaveArchive.ComputeManifest(game, excludes);
        var marker = Assert.Single(files, f => f.Path == ".savelocker/keys/states");
        Assert.Equal(0, marker.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(), marker.Sha256);
        Assert.Equal(SaveArchive.HashDirectory(game, excludes), hash);
    }

    [Fact]
    public void Markers_are_not_counted_as_files_of_the_save()
    {
        var game = Game(Dir("c/saves", ("slot.sav", "s")), Dir("c/states", ("Game.state", "st")));
        var zip = P("count.zip");
        SaveArchive.CreateArchive(game, zip);

        Assert.Equal(2, SaveArchive.GetArchiveStats(zip).FileCount);
        Assert.DoesNotContain(".savelocker/keys/states", SaveArchive.ListArchiveEntries(zip));
    }

    [Fact]
    public void Keys_and_folder_sets_are_validated()
    {
        Assert.Null(SaveRoot.ValidateExtraKey("states"));
        Assert.Null(SaveRoot.ValidateExtraKey("gc-2"));
        Assert.NotNull(SaveRoot.ValidateExtraKey("main"));
        Assert.NotNull(SaveRoot.ValidateExtraKey("States"));
        Assert.NotNull(SaveRoot.ValidateExtraKey("a/b"));
        Assert.NotNull(SaveRoot.ValidateExtraKey("-x"));
        Assert.NotNull(SaveRoot.ValidateExtraKey(new string('a', 33)));
        Assert.NotNull(SaveRoot.ValidateExtraKey(""));
        Assert.NotNull(SaveRoot.ValidateExtraKey("states\n"));

        var a = P("va");
        Assert.Throws<ArgumentException>(() => SaveArchive.HashDirectory(new[] { new SaveRoot("states", a) }));
        Assert.Throws<ArgumentException>(() => SaveArchive.HashDirectory(new[]
            { SaveRoot.Primary(a), new SaveRoot("states", P("vb")), new SaveRoot("states", P("vc")) }));
        Assert.Throws<ArgumentException>(() => SaveArchive.HashDirectory(new[]
            { SaveRoot.Primary(a), new SaveRoot("../up", P("vb")) }));
    }

    [Fact]
    public void The_console_listing_groups_files_by_folder_and_keeps_an_emptied_one()
    {
        var saves = Dir("ls/saves", ("slot1.sav", "a"), ("sub/slot2.sav", "bb"));
        var states = Dir("ls/states", ("x.state", "ccc"));
        var empty = Dir("ls/empty");
        var zip = P("ls.zip");
        SaveArchive.CreateArchive(new[] { SaveRoot.Primary(saves), new SaveRoot("states", states), new SaveRoot("config", empty) }, zip);

        var folders = SaveArchive.ListArchiveFolders(zip);

        Assert.Equal(new[] { "main", "config", "states" }, folders.Select(f => f.Key));
        var main = folders[0];
        Assert.Equal(new[] { "slot1.sav", "sub/slot2.sav" }, main.Files.Select(f => f.Path));
        Assert.Equal(3, main.TotalBytes);
        Assert.Equal(0, folders[1].FileCount);
        Assert.Equal("x.state", Assert.Single(folders[2].Files).Path);

        // A single-folder archive (what every older version is) lists as its main folder alone.
        var single = P("single.zip");
        SaveArchive.CreateArchive(saves, single);
        Assert.Equal("main", Assert.Single(SaveArchive.ListArchiveFolders(single)).Key);
        Assert.Single(SaveArchive.ListArchiveFolders(zip, maxFilesPerFolder: 1)[0].Files);
    }
}
