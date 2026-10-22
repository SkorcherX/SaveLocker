using System.IO.Compression;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// One game scoped to its own files inside a folder other games share (tasks/emulator-saves Phase 1,
/// ported with the include primitive by tasks/multiple-save-paths Phase 1).
/// The restore tests are the ones that matter: a restore deletes every local file that is not in the
/// archive, so an unscoped pull into a RetroArch saves folder deletes every other ROM's save.
/// </summary>
public sealed class IncludeGlobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-inc-" + Guid.NewGuid().ToString("N"));

    public IncludeGlobTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly string[] Chrono = { "Chrono Trigger (USA).srm", "Chrono Trigger (USA).rtc" };

    private string Dir(string name, params (string Rel, string Content)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return dir;
    }

    private string SharedSaves(string name) => Dir(name,
        ("Chrono Trigger (USA).srm", "chrono"),
        ("Super Metroid (USA) [!].srm", "metroid"),
        ("Zelda.srm", "zelda"),
        ("Snes9x/Chrono Trigger (USA).srm", "other core's copy"));

    [Fact]
    public void Hash_and_listing_cover_only_the_included_files()
    {
        var shared = SharedSaves("shared");
        var alone = Dir("alone", ("Chrono Trigger (USA).srm", "chrono"));

        Assert.Equal(new[] { "Chrono Trigger (USA).srm" }, SaveArchive.ListFiles(shared, includeGlobs: Chrono));
        Assert.Equal(SaveArchive.HashDirectory(alone), SaveArchive.HashDirectory(shared, includeGlobs: Chrono));
        Assert.NotEqual(SaveArchive.HashDirectory(shared), SaveArchive.HashDirectory(shared, includeGlobs: Chrono));

        var (files, hash) = SaveArchive.ComputeManifest(shared, includeGlobs: Chrono);
        Assert.Single(files);
        Assert.Equal(SaveArchive.HashDirectory(shared, includeGlobs: Chrono), hash);
    }

    [Fact]
    public void Bracketed_rom_names_are_literal_not_patterns()
    {
        var shared = SharedSaves("brackets");
        Assert.Equal(new[] { "Super Metroid (USA) [!].srm" },
            SaveArchive.ListFiles(shared, includeGlobs: new[] { "Super Metroid (USA) [!].srm" }));
    }

    [Fact]
    public void Excludes_still_apply_inside_the_include_scope()
    {
        var shared = SharedSaves("excl");
        File.WriteAllText(Path.Combine(shared, "Chrono Trigger (USA).rtc"), "clock");
        Assert.Equal(new[] { "Chrono Trigger (USA).srm" },
            SaveArchive.ListFiles(shared, new[] { "*.rtc" }, Chrono));
    }

    [Fact]
    public void Archive_holds_only_the_included_files()
    {
        var shared = SharedSaves("arch");
        var zip = Path.Combine(_root, "a.zip");
        SaveArchive.CreateArchive(shared, zip, includeGlobs: Chrono);
        Assert.Equal(new[] { "Chrono Trigger (USA).srm" }, SaveArchive.ListArchiveEntries(zip));
    }

    [Fact]
    public void Scoped_restore_never_touches_another_games_save()
    {
        var shared = SharedSaves("restore");
        File.WriteAllText(Path.Combine(shared, "Chrono Trigger (USA).rtc"), "stale clock");
        var zip = Path.Combine(_root, "server.zip");
        SaveArchive.CreateArchive(Dir("server", ("Chrono Trigger (USA).srm", "newer chrono")), zip);

        SaveArchive.RestoreArchive(zip, shared, Path.Combine(_root, "stage"), Chrono);

        Assert.Equal("newer chrono", File.ReadAllText(Path.Combine(shared, "Chrono Trigger (USA).srm")));
        // In scope and absent from the archive: the server's truth says it is gone.
        Assert.False(File.Exists(Path.Combine(shared, "Chrono Trigger (USA).rtc")));
        // Out of scope: untouched.
        Assert.Equal("metroid", File.ReadAllText(Path.Combine(shared, "Super Metroid (USA) [!].srm")));
        Assert.Equal("zelda", File.ReadAllText(Path.Combine(shared, "Zelda.srm")));
        Assert.Equal("other core's copy", File.ReadAllText(Path.Combine(shared, "Snes9x", "Chrono Trigger (USA).srm")));
    }

    [Fact]
    public void Unscoped_restore_into_a_shared_folder_deletes_the_other_saves()
    {
        // The control for the test above — what a pull did before include scoping existed.
        var shared = SharedSaves("control");
        var zip = Path.Combine(_root, "server.zip");
        SaveArchive.CreateArchive(Dir("server2", ("Chrono Trigger (USA).srm", "newer chrono")), zip);

        SaveArchive.RestoreArchive(zip, shared, Path.Combine(_root, "stage2"));

        Assert.False(File.Exists(Path.Combine(shared, "Zelda.srm")));
    }

    [Fact]
    public void Scoped_restore_ignores_other_games_files_carried_in_the_archive()
    {
        // Another machine mapped the whole folder and uploaded every ROM's save under this game.
        var shared = SharedSaves("foreign");
        var zip = Path.Combine(_root, "whole.zip");
        SaveArchive.CreateArchive(Dir("whole",
            ("Chrono Trigger (USA).srm", "newer chrono"), ("Zelda.srm", "OLD zelda")), zip);

        SaveArchive.RestoreArchive(zip, shared, Path.Combine(_root, "stage3"), Chrono);

        Assert.Equal("newer chrono", File.ReadAllText(Path.Combine(shared, "Chrono Trigger (USA).srm")));
        Assert.Equal("zelda", File.ReadAllText(Path.Combine(shared, "Zelda.srm")));
    }

    [Fact]
    public void Scoped_archive_restores_byte_identical_into_a_folder_with_no_save_yet()
    {
        var shared = SharedSaves("src");
        var zip = Path.Combine(_root, "rt.zip");
        SaveArchive.CreateArchive(shared, zip, includeGlobs: Chrono);
        var target = Dir("target", ("Zelda.srm", "zelda"));

        SaveArchive.RestoreArchive(zip, target, Path.Combine(_root, "stage4"), Chrono);

        Assert.Equal(SaveArchive.HashDirectory(shared, includeGlobs: Chrono),
            SaveArchive.HashDirectory(target, includeGlobs: Chrono));
        Assert.True(File.Exists(Path.Combine(target, "Zelda.srm")));
    }

    [Fact]
    public void Include_validation_flags_a_bad_pattern()
    {
        Assert.Null(SaveArchive.ValidateIncludeGlob("Chrono Trigger (USA).srm"));
        Assert.NotNull(SaveArchive.ValidateIncludeGlob("saves/../x.srm"));
    }
}
