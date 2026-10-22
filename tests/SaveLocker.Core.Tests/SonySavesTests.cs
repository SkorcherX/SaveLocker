using SaveLocker.Shared;
using Xunit;
using F = SaveLocker.Agent.Tests.ConsoleSaveFixtures;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// tasks/emulator-saves Group D: <c>PARAM.SFO</c>, the folder-per-game-ID shape (D4 shape 2) and the Sony emulators
/// that read it — PPSSPP, RPCS3, Vita3K and shadPS4. A game owns exactly the folders named from its ID; the folders of
/// every other game in the same <c>SAVEDATA</c> are never in its scope.
/// </summary>
public sealed class SonySavesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-sony-" + Guid.NewGuid().ToString("N"));

    public SonySavesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Text(string rel, string text = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text);
    }

    private void Sfo(string rel, params (string, object)[] entries) => F.Write(P(rel), F.ParamSfo(entries));

    // ---- PARAM.SFO ----

    [Fact]
    public void A_param_sfo_reads_its_text_and_number_entries()
    {
        var sfo = ParamSfo.Parse(F.ParamSfo(("CATEGORY", "SD"), ("PARENTAL_LEVEL", 3), ("TITLE", "Heavenly\nSword™"), ("SUB_TITLE", "Autosave")));

        Assert.NotNull(sfo);
        Assert.Equal("SD", sfo!["CATEGORY"]);
        Assert.Equal(3, sfo["PARENTAL_LEVEL"]);
        // A PS3 title often breaks its line; the name never does.
        Assert.Equal("Heavenly Sword", ParamSfo.Title(sfo));
    }

    [Fact]
    public void A_ps4_save_is_named_by_its_maintitle_and_no_param_sfo_names_nothing()
    {
        Assert.Equal("Bloodborne", ParamSfo.Title(ParamSfo.Parse(F.ParamSfo(("MAINTITLE", "Bloodborne"), ("SUBTITLE", "Hunter")))!));
        Assert.Null(ParamSfo.Parse("not a param.sfo at all"u8));
        // Truncated inside its own index: refused, not read past the end.
        Assert.Null(ParamSfo.Parse(F.ParamSfo(("TITLE", "Cut short")).AsSpan(0, 30)));
        Assert.Null(ParamSfo.Title(P("missing/PARAM.SFO")));
    }

    [Theory]
    [InlineData("psp", "ULUS10041", "USA")]
    [InlineData("psp", "ULES00151", "Europe")]
    [InlineData("psp", "NPJH50001", "Japan")]
    [InlineData("ps3", "NPUB30010", "USA")]
    [InlineData("ps3", "BLKS20001", "Korea")]
    [InlineData("psvita", "PCSE00120", "USA")]
    [InlineData("psvita", "PCSB00245", "Europe")]
    [InlineData("psvita", "PCSG00001", "Japan")]
    [InlineData("ps4", "CUSA00552", null)]
    [InlineData("psvita", "ABCD12345", null)]
    public void A_sony_id_prefix_names_its_region(string system, string id, string? region) =>
        Assert.Equal(region, ConsoleTitles.PrefixRegion(system, id));

    // ---- PPSSPP ----

    /// <summary>EmuDeck's PPSSPP folders: a Redump disc with two save folders, a PSN game Redump has no disc of, a
    /// folder no game's ID starts, and an empty save folder; states for the disc and for another game.</summary>
    private void PpssppEmuDeck(string root)
    {
        Sfo($"{root}/saves/ppsspp/saves/ULUS10041DATA00/PARAM.SFO", ("TITLE", "GTA LCS"), ("SAVEDATA_TITLE", "Slot 1"));
        Text($"{root}/saves/ppsspp/saves/ULUS10041DATA00/DATA.BIN", "lcs-1");
        Sfo($"{root}/saves/ppsspp/saves/ULUS10041SYSTEM/PARAM.SFO", ("TITLE", "GTA LCS"));
        Text($"{root}/saves/ppsspp/saves/ULUS10041SYSTEM/SYSTEM.BIN", "lcs-system");
        Sfo($"{root}/saves/ppsspp/saves/NPUH99999SAVE/PARAM.SFO", ("TITLE", "Homebrew Hero"));
        Text($"{root}/saves/ppsspp/saves/NPUH99999SAVE/SAVE.DAT", "hb");
        Text($"{root}/saves/ppsspp/saves/PPSSPP_MISC/notes.txt", "not a game");
        Directory.CreateDirectory(P($"{root}/saves/ppsspp/saves/ULES00151DATA"));
        Text($"{root}/saves/ppsspp/states/ULUS10041_1.00_0.ppst", "state");
        Text($"{root}/saves/ppsspp/states/ULUS10041_1.00_0.jpg", "thumb");
        Text($"{root}/saves/ppsspp/states/ULUS10042_1.00_0.ppst", "ssx-state");
    }

    [Fact]
    public void Ppsspp_saves_are_one_game_per_id_named_by_redump_then_param_sfo()
    {
        PpssppEmuDeck("Emulation");

        var found = PpssppSaves.Scan(PpssppSaves.Folders([P("Emulation")], []));

        Assert.Equal(new[] { "Grand Theft Auto - Liberty City Stories", "Homebrew Hero", "SSX on Tour" }, found.Select(c => c.Name));
        var lcs = found[0];
        Assert.False(lcs.StatesOnly);
        Assert.Equal(("PPSSPP", "psp", "ULUS10041", true), (lcs.EmulatorName, lcs.EmulatorSystem, lcs.EmulatorRom, lcs.ViaEmuDeck));
        Assert.Equal(("PlayStation Portable", "USA", "Redump"), (lcs.Release!.Console, lcs.Release.Region, lcs.Release.NamedBy));
        Assert.Equal(new[] { "ULUS10041*/**" }, lcs.IncludeGlobs);
        Assert.Equal(new[] { "ULUS10041DATA00/DATA.BIN", "ULUS10041DATA00/PARAM.SFO", "ULUS10041SYSTEM/PARAM.SFO", "ULUS10041SYSTEM/SYSTEM.BIN" },
            SaveArchive.ListFiles(lcs.SuggestedSaveDir!, null, lcs.IncludeGlobs).Order(StringComparer.Ordinal));

        var states = Assert.Single(lcs.ExtraSaveDirs!);
        Assert.Equal(RomSaves.StatesKey, states.Key);
        Assert.Equal(new[] { "ULUS10041_1.00_0.jpg", "ULUS10041_1.00_0.ppst" },
            SaveArchive.ListFiles(states.Dir, null, states.IncludeGlobs).Order(StringComparer.Ordinal));

        // Redump has no disc of a PSN game: the TITLE inside its save, the region from its ID.
        var psn = found[1];
        Assert.Equal(("USA", null), (psn.Release!.Region, psn.Release.NamedBy));
        Assert.Equal(new[] { "NPUH99999SAVE/PARAM.SFO", "NPUH99999SAVE/SAVE.DAT" },
            SaveArchive.ListFiles(psn.SuggestedSaveDir!, null, psn.IncludeGlobs).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_ppsspp_game_with_states_but_no_save_yet_is_a_game_with_its_save_folder_declared()
    {
        PpssppEmuDeck("Emulation");
        Text("Emulation/saves/ppsspp/states/load_undo.ppst", "the last load's undo, no game's");
        Text("Emulation/saves/ppsspp/states/ULES00151_1.00_0.ppst", "");
        Text("Emulation/saves/ppsspp/states/ULES00151_1.00_0.jpg", "an empty state is no state");

        var found = PpssppSaves.Scan(PpssppSaves.Folders([P("Emulation")], []));

        // SSX on Tour has a state and no save folder; ULES00151 has an empty folder and an empty state only.
        var ssx = Assert.Single(found, c => c.StatesOnly);
        Assert.Equal(("SSX on Tour", "ULUS10042", "USA"), (ssx.Name, ssx.EmulatorRom, ssx.Release!.Region));
        Assert.Equal(P("Emulation/saves/ppsspp/saves"), ssx.SuggestedSaveDir);
        Assert.Equal(new[] { "ULUS10042*/**" }, ssx.IncludeGlobs);
        Assert.Empty(SaveArchive.ListFiles(ssx.SuggestedSaveDir!, null, ssx.IncludeGlobs));
        var states = Assert.Single(ssx.ExtraSaveDirs!);
        Assert.Equal(new[] { "ULUS10042_1.00_0.ppst" }, SaveArchive.ListFiles(states.Dir, null, states.IncludeGlobs));
        Assert.DoesNotContain(found, c => c.EmulatorRom == "ULES00151");
    }

    [Fact]
    public void A_ppsspp_save_on_one_memory_stick_wins_over_the_same_games_states_alone_on_another()
    {
        Text("a/PSP/PPSSPP_STATE/ULUS10041_1.00_0.ppst", "newer state, no save");
        Sfo("b/PSP/SAVEDATA/ULUS10041DATA00/PARAM.SFO", ("TITLE", "GTA LCS"));
        Text("b/PSP/SAVEDATA/ULUS10041DATA00/DATA.BIN", "older save");
        File.SetLastWriteTimeUtc(P("b/PSP/SAVEDATA/ULUS10041DATA00/DATA.BIN"), DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(P("b/PSP/SAVEDATA/ULUS10041DATA00/PARAM.SFO"), DateTime.UtcNow.AddDays(-3));

        var game = Assert.Single(PpssppSaves.Scan(PpssppSaves.Folders([], [P("a/PSP"), P("b/PSP")])));

        Assert.False(game.StatesOnly);
        Assert.Equal(P("b/PSP/SAVEDATA"), game.SuggestedSaveDir);
    }

    [Fact]
    public void A_standalone_ppsspp_memory_stick_is_read_and_the_newest_copy_of_a_game_wins()
    {
        Sfo("old/PSP/SAVEDATA/ULUS10041DATA00/PARAM.SFO", ("TITLE", "GTA LCS"));
        Text("old/PSP/SAVEDATA/ULUS10041DATA00/DATA.BIN", "old");
        Sfo("new/PSP/SAVEDATA/ULUS10041DATA00/PARAM.SFO", ("TITLE", "GTA LCS"));
        Text("new/PSP/SAVEDATA/ULUS10041DATA00/DATA.BIN", "new");
        File.SetLastWriteTimeUtc(P("old/PSP/SAVEDATA/ULUS10041DATA00/DATA.BIN"), DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(P("old/PSP/SAVEDATA/ULUS10041DATA00/PARAM.SFO"), DateTime.UtcNow.AddDays(-3));

        var folders = PpssppSaves.Folders([], [P("old/PSP"), P("new/PSP"), P("missing/PSP")]);
        Assert.Equal(2, folders.Count);
        // A states folder PPSSPP has not made yet is still declared.
        Assert.Equal(P("new/PSP/PPSSPP_STATE"), folders[1].States);

        var game = Assert.Single(PpssppSaves.Scan(folders));
        Assert.Equal(P("new/PSP/SAVEDATA"), game.SuggestedSaveDir);
        Assert.False(game.ViaEmuDeck);
    }

    // ---- RPCS3 ----

    [Fact]
    public void Rpcs3_saves_are_named_by_redump_then_param_sfo_and_keep_the_trophies_out()
    {
        // The Deck's save (2026-10-11): Redump knows the disc, so the name is not the save's "Heavenly Sword™".
        Sfo("Emulation/saves/rpcs3/saves/BCUS98132-GAMEAUTOSAVE/PARAM.SFO", ("TITLE", "Heavenly Sword™"), ("SUB_TITLE", "Save Game"));
        Text("Emulation/saves/rpcs3/saves/BCUS98132-GAMEAUTOSAVE/USERDATA.DAT", "hs");
        Text("Emulation/saves/rpcs3/saves/BCUS98132-GAMEAUTOSAVE/ICON0.PNG", "icon");
        Sfo("Emulation/saves/rpcs3/saves/NPUB99999SLOT1/PARAM.SFO", ("TITLE", "A PSN\nGame"));
        Text("Emulation/saves/rpcs3/saves/NPUB99999SLOT1/SAVE.DAT", "psn");
        Text("Emulation/saves/rpcs3/trophy/NPWR00001_00/TROPCONF.SFM", "trophy");

        var found = Rpcs3Saves.Scan(Rpcs3Saves.Folders([P("Emulation")], []));

        Assert.Equal(new[] { "A PSN Game", "Heavenly Sword" }, found.Select(c => c.Name));
        var hs = found[1];
        Assert.Equal(("RPCS3", "ps3", "BCUS98132", true), (hs.EmulatorName, hs.EmulatorSystem, hs.EmulatorRom, hs.ViaEmuDeck));
        Assert.Equal(("PlayStation 3", "USA", "Redump"), (hs.Release!.Console, hs.Release.Region, hs.Release.NamedBy));
        Assert.Equal(new[] { "BCUS98132-GAMEAUTOSAVE/ICON0.PNG", "BCUS98132-GAMEAUTOSAVE/PARAM.SFO", "BCUS98132-GAMEAUTOSAVE/USERDATA.DAT" },
            SaveArchive.ListFiles(hs.SuggestedSaveDir!, null, hs.IncludeGlobs).Order(StringComparer.Ordinal));
        Assert.Null(hs.ExtraSaveDirs);
        Assert.Equal(("USA", null), (found[0].Release!.Region, found[0].Release!.NamedBy));
    }

    [Fact]
    public void Rpcs3_vfs_yml_says_where_dev_hdd0_is_on_the_deck_and_in_emudeck_for_windows()
    {
        // The Deck's ~/.config/rpcs3/vfs.yml, as read 2026-10-11: dev_hdd0 moved to the SD card.
        Text("deck/.config/rpcs3/vfs.yml", $"$(EmulatorDir): \"\"\n/dev_hdd0/: {P("sd/storage/rpcs3/dev_hdd0").Replace('\\', '/')}/\n/dev_hdd1/: $(EmulatorDir)dev_hdd1/\n/dev_bdvd/: \"\"\n");
        Sfo("sd/storage/rpcs3/dev_hdd0/home/00000001/savedata/BLUS30443-AUTO/PARAM.SFO", ("TITLE", "Demon's Souls"));
        Sfo("sd/storage/rpcs3/dev_hdd0/home/00000002/savedata/BLES00932-AUTO/PARAM.SFO", ("TITLE", "Demon's Souls"));
        // EmuDeck for Windows' RPCS3: config\vfs.yml beside rpcs3.exe, dev_hdd0 left at its default.
        Text("win/RPCS3/config/vfs.yml", "$(EmulatorDir): \"\"\n/dev_hdd1/: $(EmulatorDir)dev_hdd1/\n");
        Sfo("win/RPCS3/dev_hdd0/home/00000001/savedata/NPUB30010-SAVE/PARAM.SFO", ("TITLE", "Flower"));

        Assert.Equal(Path.GetFullPath(P("sd/storage/rpcs3/dev_hdd0")).TrimEnd(Path.DirectorySeparatorChar),
            Rpcs3Saves.DevHdd0(P("deck/.config/rpcs3"))!.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Null(Rpcs3Saves.DevHdd0(P("never-ran")));

        var folders = Rpcs3Saves.Folders([], [P("deck/.config/rpcs3"), P("win/RPCS3")]);
        Assert.Equal(3, folders.Count);
        Assert.Equal(new[] { "BLES00932", "BLUS30443", "NPUB30010" }, Rpcs3Saves.Scan(folders).Select(c => c.EmulatorRom).Order(StringComparer.Ordinal));
    }

    // ---- Vita3K ----

    [Fact]
    public void Vita3k_saves_are_one_folder_per_title_named_from_the_save_then_the_installed_game()
    {
        // EmuDeck for Windows' layout: the savedata link points into saves/Vita3K/saves, the games under storage.
        Sfo("Emulation/saves/Vita3K/saves/PCSE00120/sce_sys/param.sfo", ("TITLE", "Persona 4 Golden"));
        Text("Emulation/saves/Vita3K/saves/PCSE00120/data0000.bin", "p4g");
        Text("Emulation/saves/Vita3K/saves/PCSB00245/SAVEDATA.BIN", "tearaway");
        Sfo("Emulation/storage/Vita3K/ux0/app/PCSB00245/sce_sys/param.sfo", ("TITLE", "Tearaway"), ("STITLE", "Tearaway"));
        Text("Emulation/saves/Vita3K/saves/PCSG00001/save.bin", "unknown");
        Text("Emulation/saves/Vita3K/saves/PCSE00120_backup/save.bin", "not a title folder");

        var found = Vita3KSaves.Scan(Vita3KSaves.Folders([P("Emulation")], []));

        Assert.Equal(new[] { "PCSG00001", "Persona 4 Golden", "Tearaway" }, found.Select(c => c.Name));
        var p4 = found[1];
        Assert.Equal(("Vita3K", "psvita", "PCSE00120"), (p4.EmulatorName, p4.EmulatorSystem, p4.EmulatorRom));
        Assert.Equal(("PlayStation Vita", "USA"), (p4.Release!.Console, p4.Release.Region));
        Assert.Equal(new[] { "PCSE00120/**" }, p4.IncludeGlobs);
        Assert.Equal(new[] { "PCSE00120/data0000.bin", "PCSE00120/sce_sys/param.sfo" },
            SaveArchive.ListFiles(p4.SuggestedSaveDir!, null, p4.IncludeGlobs).Order(StringComparer.Ordinal));
        Assert.Equal("Europe", found[2].Release!.Region);
    }

    [Fact]
    public void Vita3k_config_yml_pref_path_is_read_and_the_installed_games_beside_it_name_the_saves()
    {
        // The Deck's ~/.config/Vita3K/config.yml (2026-10-11): pref-path on the SD card.
        Text("deck/.config/Vita3K/config.yml", $"---\ninitial-setup: false\npref-path: {P("sd/storage/Vita3K").Replace('\\', '/')}/\n");
        Text("sd/storage/Vita3K/ux0/user/00/savedata/PCSA00081/save.bin", "uncharted");
        Sfo("sd/storage/Vita3K/ux0/app/PCSA00081/sce_sys/param.sfo", ("TITLE", "Uncharted: Golden Abyss"));
        // No config.yml: SDL's own folder.
        Text("home/.local/share/Vita3K/Vita3K/ux0/user/00/savedata/PCSE00001/save.bin", "default");

        var folders = Vita3KSaves.Folders([], [
            (P("deck/.config/Vita3K/config.yml"), P("unused")),
            (P("home/.config/Vita3K/config.yml"), P("home/.local/share/Vita3K/Vita3K")),
        ]);

        Assert.Equal(2, folders.Count);
        Assert.Equal(new[] { "PCSE00001", "Uncharted: Golden Abyss" }, Vita3KSaves.Scan(folders).Select(c => c.Name));
    }

    [Fact]
    public void Vita3k_reads_every_users_savedata_not_only_user_00()
    {
        // EmuDeck links only user 00; a second Vita3K user (01) saves beside it under storage.
        Text("Emulation/saves/Vita3K/saves/PCSE00120/data0000.bin", "p4g user 00");
        Text("Emulation/storage/Vita3K/ux0/user/01/savedata/PCSA00081/save.bin", "uncharted user 01");
        Sfo("Emulation/storage/Vita3K/ux0/app/PCSA00081/sce_sys/param.sfo", ("TITLE", "Uncharted: Golden Abyss"));
        // Standalone: user 02 only.
        Text("vita/ux0/user/02/savedata/PCSE00001/save.bin", "user 02");

        var found = Vita3KSaves.Scan(Vita3KSaves.Folders([P("Emulation")], [(P("none/config.yml"), P("vita"))]));

        Assert.Equal(new[] { "PCSE00001", "PCSE00120", "Uncharted: Golden Abyss" }, found.Select(c => c.Name));
        Assert.Equal(P("Emulation/storage/Vita3K/ux0/user/01/savedata"), found[2].SuggestedSaveDir);
        Assert.True(found[2].ViaEmuDeck);
        Assert.Equal(P("vita/ux0/user/02/savedata"), found[0].SuggestedSaveDir);
    }

    // ---- shadPS4 ----

    [Fact]
    public void Shadps4_reads_both_layouts_and_names_a_game_by_its_saves_maintitle()
    {
        // The old layout under EmuDeck's link (savedata/<user>/<CUSA>/<save>), the new one in the user folder
        // (home/<user>/savedata/<CUSA>/<save>).
        Sfo("Emulation/saves/shadps4/saves/1/CUSA00900/SPRJ0005/sce_sys/param.sfo", ("MAINTITLE", "Bloodborne"), ("SUBTITLE", "Hunter"));
        Text("Emulation/saves/shadps4/saves/1/CUSA00900/SPRJ0005/userdata0000", "bb");
        Text("Emulation/saves/shadps4/saves/1/CUSA01234/SAVE0/data.bin", "no sfo");
        Sfo("user/home/1000/savedata/CUSA07408/SAVEDATA00/sce_sys/param.sfo", ("MAINTITLE", "Persona 5"));
        Text("user/home/1000/savedata/CUSA07408/SAVEDATA00/data.bin", "p5");

        var folders = ShadPs4Saves.Folders([P("Emulation")], [P("user")]);
        var found = ShadPs4Saves.Scan(folders);

        Assert.Equal(new[] { "Bloodborne", "CUSA01234", "Persona 5" }, found.Select(c => c.Name));
        var bb = found[0];
        Assert.Equal(P("Emulation/saves/shadps4/saves/1"), bb.SuggestedSaveDir);
        Assert.Equal(("shadPS4", "ps4", "CUSA00900", true), (bb.EmulatorName, bb.EmulatorSystem, bb.EmulatorRom, bb.ViaEmuDeck));
        Assert.Equal(("PlayStation 4", null), (bb.Release!.Console, bb.Release.Region));
        Assert.Equal(new[] { "CUSA00900/SPRJ0005/sce_sys/param.sfo", "CUSA00900/SPRJ0005/userdata0000" },
            SaveArchive.ListFiles(bb.SuggestedSaveDir!, null, bb.IncludeGlobs).Order(StringComparer.Ordinal));
        Assert.Equal(P("user/home/1000/savedata"), found[2].SuggestedSaveDir);
        Assert.Equal(new[] { "CUSA07408/**" }, found[2].IncludeGlobs);
    }

    [Fact]
    public void A_shadps4_game_in_both_layouts_is_the_copy_written_last()
    {
        Sfo("user/savedata/1/CUSA00900/SPRJ0005/sce_sys/param.sfo", ("MAINTITLE", "Bloodborne"));
        Text("user/savedata/1/CUSA00900/SPRJ0005/userdata0000", "old");
        foreach (var f in Directory.GetFiles(P("user/savedata/1"), "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-30));
        Sfo("user/home/1/savedata/CUSA00900/SPRJ0005/sce_sys/param.sfo", ("MAINTITLE", "Bloodborne"));
        Text("user/home/1/savedata/CUSA00900/SPRJ0005/userdata0000", "new");

        var game = Assert.Single(ShadPs4Saves.Scan(ShadPs4Saves.Folders([], [P("user")])));

        Assert.Equal(P("user/home/1/savedata"), game.SuggestedSaveDir);
    }
}
