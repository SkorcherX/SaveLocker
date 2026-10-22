using SaveLocker.Shared;
using Xunit;
using F = SaveLocker.Agent.Tests.ConsoleSaveFixtures;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// tasks/emulator-saves Phase 2: PCSX2, DuckStation and Dolphin discovery, and the shared-memory-card warning — the
/// one real data-safety risk in the task. A card every game saves to is never a game of its own; a per-game save is
/// scoped so that its archive holds exactly that game's files.
/// </summary>
public sealed class MemoryCardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-cards-" + Guid.NewGuid().ToString("N"));

    public MemoryCardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Bytes(string rel, byte[] bytes) => F.Write(P(rel), bytes);

    private void Text(string rel, string text = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text);
    }

    // ---- PCSX2 ----

    /// <summary>A folder card with three games' saves (one a disc Redump has never seen), system data, and PCSX2's
    /// own index files.</summary>
    private void Pcsx2FolderCard(string memcards)
    {
        Text($"{memcards}/Mcd001.ps2/_pcsx2_superblock");
        Text($"{memcards}/Mcd001.ps2/_pcsx2_index");
        Bytes($"{memcards}/Mcd001.ps2/BASLUS-21005SYS/icon.sys", F.IconSys("Kingdom Hearts II", "System data"));
        Text($"{memcards}/Mcd001.ps2/BASLUS-21005SYS/_pcsx2_index");
        Text($"{memcards}/Mcd001.ps2/BASLUS-21005SYS/kh2.ico", "icon");
        Text($"{memcards}/Mcd001.ps2/BASLUS-21005S01/data", "slot1");
        Bytes($"{memcards}/Mcd001.ps2/BESLES-50330GTA3/icon.sys", F.IconSys("GTA3", "", fullWidth: false));
        Text($"{memcards}/Mcd001.ps2/BESLES-50330GTA3/save", "gta");
        Bytes($"{memcards}/Mcd001.ps2/BASLUS-99999HB/icon.sys", F.IconSys("Homebrew", "Save"));
        Text($"{memcards}/Mcd001.ps2/BASLUS-99999HB/save", "homebrew");
        Text($"{memcards}/Mcd001.ps2/BADATA-SYSTEM/history", "system");
    }

    [Fact]
    public void A_pcsx2_folder_card_is_one_game_per_product_code_named_by_its_serial()
    {
        Pcsx2FolderCard("saves/pcsx2/saves");
        Directory.CreateDirectory(P("saves/pcsx2/states"));

        var found = Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("saves/pcsx2/saves"), P("saves/pcsx2/states"), EmuDeck: true) });

        // A serial Redump has no disc of is named by the title the game wrote into its icon.sys.
        // The region is the release's, beside the name.
        Assert.Equal(new[] { "Grand Theft Auto III", "Homebrew", "Kingdom Hearts II" }, found.Select(c => c.Name));
        Assert.Equal(new[] { "Europe", null, "USA" }, found.Select(c => c.Release?.Region));
        var kh = found[2];
        Assert.Equal(("PlayStation 2", "Redump"), (kh.Release!.Console, kh.Release.NamedBy));
        Assert.Equal(new[] { "SLUS-21005" }, kh.Release.GameIds);
        // Homebrew: the console and its game ID, nothing Redump would know.
        Assert.Equal(new GameReleaseDto("PlayStation 2"), found[1].Release! with { GameIds = null });
        Assert.Equal(new[] { "SLUS-99999" }, found[1].Release!.GameIds);
        Assert.Equal(("PCSX2", "ps2", "BASLUS-21005"), (kh.EmulatorName, kh.EmulatorSystem, kh.EmulatorRom));
        // The card is the folder, so what is kept is named the same whatever each machine calls its card.
        Assert.Equal(P("saves/pcsx2/saves/Mcd001.ps2"), kh.SuggestedSaveDir);
        Assert.Equal(new[] { "BASLUS-21005*/**" }, kh.IncludeGlobs);
        Assert.Null(kh.NotSyncable);
        Assert.True(kh.ViaEmuDeck);
        var states = Assert.Single(kh.ExtraSaveDirs!);
        Assert.Equal(RomSaves.StatesKey, states.Key);
        Assert.Equal(new[] { "SLUS-21005 (*.p2s" }, states.IncludeGlobs);

        // The archive holds both of the game's save folders, their index files, and nothing else on the card.
        Assert.Equal(
            new[]
            {
                "BASLUS-21005S01/data", "BASLUS-21005SYS/_pcsx2_index",
                "BASLUS-21005SYS/icon.sys", "BASLUS-21005SYS/kh2.ico",
            },
            SaveArchive.ListFiles(kh.SuggestedSaveDir!, null, kh.IncludeGlobs).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Two_sequels_whose_saves_say_only_the_series_and_one_game_from_two_regions_are_three_named_games()
    {
        // The maintainer's Deck: Warrior Within and The Two Thrones both write "Prince of Persia" as the save's title.
        Bytes("mc/Mcd001.ps2/BASLUS-21022WW/icon.sys", F.IconSys("Prince of Persia", "Warrior Within"));
        Text("mc/Mcd001.ps2/BASLUS-21022WW/save");
        Bytes("mc/Mcd001.ps2/BASLUS-21287TT/icon.sys", F.IconSys("Prince of Persia", "The Two Thrones"));
        Text("mc/Mcd001.ps2/BASLUS-21287TT/save");
        // The European disc has its own serial and reads only its own saves: another game.
        Bytes("mc/Mcd001.ps2/BESLES-52822WW/icon.sys", F.IconSys("Prince of Persia", "Warrior Within"));
        Text("mc/Mcd001.ps2/BESLES-52822WW/save");
        Text("mc/Mcd001.ps2/_pcsx2_superblock");

        var found = Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("mc"), null) });

        // One name for both Warrior Withins: the region tells them apart.
        Assert.Equal(
            new (string, string?, string)[]
            {
                ("Prince of Persia - The Two Thrones", "USA", "BASLUS-21287*/**"),
                ("Prince of Persia - Warrior Within", "USA", "BASLUS-21022*/**"),
                ("Prince of Persia - Warrior Within", "Europe, Australia", "BESLES-52822*/**"),
            },
            found.Select(c => (c.Name, c.Release?.Region, Assert.Single(c.IncludeGlobs!))));
        Assert.Equal(new[] { "En", "Fr", "Es" }, found[1].Release!.Languages);
    }

    [Fact]
    public void Console_titles_take_every_spelling_of_a_serial_and_know_nothing_of_another_console()
    {
        static (string, string?)? Of(string system, string serial) =>
            ConsoleTitles.For(system, serial) is { } t ? (t.Title, t.Region) : null;
        Assert.Equal(("Prince of Persia - The Two Thrones", "USA"), Of("ps2", "SLUS-21287"));
        Assert.Equal(("Prince of Persia - The Two Thrones", "USA"), Of("ps2", "slus21287"));
        Assert.Equal(("Prince of Persia - The Two Thrones", "USA"), Of("ps2", "SLUS_212.87"));
        Assert.Equal(("Final Fantasy VII", "USA"), Of("psx", "SCUS-94163"));
        Assert.Equal(("Super Smash Bros. Melee", "USA"), Of("gc", "GALE01"));
        Assert.Equal(("Super Smash Bros. Melee", "Europe, Australia"), Of("gc", "GALP"));
        Assert.Equal(("Metroid Prime Trilogy", "USA"), Of("wii", "R3ME"));
        Assert.Equal(("Ridge Racer 7", "USA"), Of("ps3", "BLUS-30001"));
        Assert.Equal(new[] { "En", "Fr", "De", "Es", "It", "Nl" }, ConsoleTitles.For("ps2", "SLES-52822")!.Languages);
        Assert.Null(ConsoleTitles.For("psx", "SLUS-21287"));
        Assert.Null(ConsoleTitles.For("ps2", "SLUS-99999"));
        Assert.Null(ConsoleTitles.For("ps2", "BADATA"));
        Assert.Null(ConsoleTitles.For("ps2", null));
    }

    [Fact]
    public void Pcsx2_states_scope_takes_every_slot_and_resume_but_not_the_backups_or_another_game()
    {
        Text("sstates/SLUS-21050 (ABCD1234).01.p2s");
        Text("sstates/SLUS-21050 (ABCD1234).02.p2s");
        Text("sstates/SLUS-21050 (ABCD1234).resume.p2s");
        Text("sstates/SLUS-21050 (ABCD1234).01.p2s.backup");
        Text("sstates/SLUS-21051 (00000000).01.p2s");

        Assert.Equal(
            new[] { "SLUS-21050 (ABCD1234).01.p2s", "SLUS-21050 (ABCD1234).02.p2s", "SLUS-21050 (ABCD1234).resume.p2s" },
            SaveArchive.ListFiles(P("sstates"), null, Pcsx2Saves.StateGlobsFor("SLUS-21050")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_pcsx2_file_card_is_listed_as_shared_and_never_enrolled()
    {
        // The Deck's own shape: one 64 MB file card under a custom name.
        Bytes("memcards/PS2MC-1.ps2", F.Ps2FileCard());

        var card = Assert.Single(Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("memcards"), null) }));

        Assert.Equal("PS2 memory card (PS2MC-1.ps2)", card.Name);
        Assert.Contains("Convert", card.NotSyncable);
        Assert.Empty(EnrollLinks.For(Array.Empty<GameDto>(), card));
    }

    [Fact]
    public async Task The_enroller_refuses_a_shared_card_with_its_reason()
    {
        Bytes("memcards/Mcd001.ps2", F.Ps2FileCard());
        var found = Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("memcards"), null) });
        var config = AgentConfig.Load(P("state/config.json"));
        config.ServerUrl = "http://127.0.0.1:9";
        config.ApiKey = "never-used";

        // Refused before anything is asked of the server (there is none at that address).
        Assert.Equal((0, 1), await Enroller.EnrollAsync(config, found, new[] { 0 }));
        Assert.Empty(AgentConfig.Load(P("state/config.json")).Games);
    }

    [Fact]
    public void Pcsx2_config_names_the_folders_relative_or_absolute()
    {
        Text("data/inis/PCSX2.ini", $"[Folders]\nMemoryCards = cards\nSavestates = {P("elsewhere/states")}\n");
        Directory.CreateDirectory(P("data/cards"));
        Text("default/inis/PCSX2.ini", "[EmuCore]\nMcdFolderAutoManage = true\n");
        Directory.CreateDirectory(P("default/memcards"));

        var folders = Pcsx2Saves.Folders(Array.Empty<string>(), new[] { P("data"), P("default"), P("missing") });

        Assert.Equal(new[] { P("data/cards"), P("default/memcards") }, folders.Select(f => f.Saves));
        Assert.Equal(new[] { P("elsewhere/states"), P("default/sstates") }, folders.Select(f => f.States));
    }

    // ---- DuckStation ----

    [Fact]
    public void Duckstation_per_game_cards_are_games_with_their_serials_states()
    {
        Bytes("ds/saves/Final Fantasy VII (USA)_1.mcd", F.Ps1Card("BASCUS-94163FF7S01", "BASCUS-94163FF7S02"));
        Bytes("ds/saves/Crash Bandicoot_1.mcd", F.Ps1Card("BASCUS-94900CRASH"));
        Bytes("ds/saves/SLUS-00067_1.mcd", F.Ps1Card("BASLUS-00067CVSOTN"));
        Bytes("ds/saves/Homebrew_1.mcd", F.Ps1Card("BASLUS-99999HB"));
        // Formatted when the game started, nothing saved: not a game yet.
        Bytes("ds/saves/Tekken 3_1.mcd", F.Ps1Card());
        Directory.CreateDirectory(P("ds/states"));

        var found = DuckStationSaves.Scan(new[] { new RomSaveFolders(P("ds/saves"), P("ds/states"), EmuDeck: true) });

        // Named by the serial its saves carry, the region beside it; one Redump has no disc of by the card's own name.
        Assert.Equal(
            new[] { "Castlevania - Symphony of the Night", "Crash Bandicoot", "Final Fantasy VII", "Homebrew" },
            found.Select(c => c.Name));
        Assert.Equal(new[] { "USA", "USA", "USA", null }, found.Select(c => c.Release?.Region));
        var ff7 = found[2];
        Assert.Equal(new[] { "SCUS-94163" }, ff7.Release!.GameIds);
        Assert.Equal(("DuckStation", "psx", "Final Fantasy VII (USA)"), (ff7.EmulatorName, ff7.EmulatorSystem, ff7.EmulatorRom));
        Assert.Equal(new[] { "Final Fantasy VII (USA)_1.mcd", "Final Fantasy VII (USA)_2.mcd" }, ff7.IncludeGlobs);
        Assert.Equal(new[] { "SCUS-94163_*.sav" }, Assert.Single(ff7.ExtraSaveDirs!).IncludeGlobs);
        // A card per serial is named by the very code its states are.
        Assert.Equal(new[] { "SLUS-00067_*.sav" }, Assert.Single(found[0].ExtraSaveDirs!).IncludeGlobs);
    }

    [Fact]
    public void A_duckstation_card_scope_never_takes_a_longer_named_neighbour()
    {
        Bytes("ds/Crash_1.mcd", F.Ps1Card("BASCUS-94900CRASH"));
        Bytes("ds/Crash_Team_1.mcd", F.Ps1Card("BASCUS-94426CTR"));
        Text("ds/SCUS-94900_1.sav");
        Text("ds/SCUS-94900_resume.sav");
        Text("ds/SCUS-94426_1.sav");

        Assert.Equal(new[] { "Crash_1.mcd" }, SaveArchive.ListFiles(P("ds"), null, DuckStationSaves.SaveGlobsFor("Crash")));
        Assert.Equal(new[] { "SCUS-94900_1.sav", "SCUS-94900_resume.sav" },
            SaveArchive.ListFiles(P("ds"), null, DuckStationSaves.StateGlobsFor(["SCUS-94900"])).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_duckstation_shared_card_or_a_card_of_several_games_is_shared()
    {
        Bytes("ds/shared_card_1.mcd", F.Ps1Card("BASCUS-94163FF7S01"));
        Bytes("ds/Mixed_1.mcd", F.Ps1Card("BASCUS-94163FF7S01", "BASLUS-00067CVSOTN"));

        var found = DuckStationSaves.Scan(new[] { new RomSaveFolders(P("ds"), null) });

        Assert.Equal(new[] { "PS1 memory card (Mixed_1.mcd)", "PS1 memory card (shared_card_1.mcd)" }, found.Select(c => c.Name));
        Assert.All(found, c => Assert.Contains("Separate Card Per Game", c.NotSyncable));
    }

    [Fact]
    public void A_duckstation_card_named_after_its_game_is_that_games_with_every_disc_and_not_an_imported_save()
    {
        // Metal Gear Solid's discs each save under their own code, onto the one card DuckStation keeps per title.
        Bytes("ds/saves/Metal Gear Solid_1.mcd", F.Ps1Card("BASLUS-00594MGS01", "BASLUS-00776MGS02"));
        // Suikoden II reads a Suikoden save copied onto its own card.
        Bytes("ds/saves/Suikoden II_1.mcd", F.Ps1Card("BASLUS-00292SUIKO", "BASLUS-00958SUIKO2"));
        Directory.CreateDirectory(P("ds/states"));

        var found = DuckStationSaves.Scan(new[] { new RomSaveFolders(P("ds/saves"), P("ds/states")) });

        Assert.Equal(new[] { "Metal Gear Solid", "Suikoden II" }, found.Select(c => c.Name));
        Assert.All(found, c => Assert.Null(c.NotSyncable));
        Assert.Equal(new[] { "SLUS-00594_*.sav", "SLUS-00776_*.sav" }, Assert.Single(found[0].ExtraSaveDirs!).IncludeGlobs);
        // The imported game's states are its own game's, never also this one's.
        Assert.Equal(new[] { "SLUS-00958_*.sav" }, Assert.Single(found[1].ExtraSaveDirs!).IncludeGlobs);
        Assert.Empty(SaveDirSanity.Inspect(P("ds/saves"), includeGlobs: DuckStationSaves.SaveGlobsFor("Suikoden II")));
    }

    [Fact]
    public void A_duckstation_card_in_a_slot_its_scope_never_takes_is_no_game()
    {
        Bytes("ds/Crash_3.mcd", F.Ps1Card("BASCUS-94900CRASH"));

        Assert.Empty(DuckStationSaves.Scan(new[] { new RomSaveFolders(P("ds"), null) }));
    }

    [Fact]
    public void Duckstation_settings_name_the_card_and_states_folders()
    {
        Text("data/settings.ini", $"[MemoryCards]\nCard1Type = PerGameTitle\nDirectory = {P("cards")}\n\n[Folders]\nSaveStates = st\n");
        Directory.CreateDirectory(P("cards"));

        var folder = Assert.Single(DuckStationSaves.Folders(Array.Empty<string>(), new[] { P("data") }));

        Assert.Equal((P("cards"), P("data/st")), (folder.Saves, folder.States));
    }

    // ---- Dolphin ----

    private DolphinFolders DolphinSetup(string user, bool emuDeck = false) =>
        new("Dolphin", P($"{user}/GC"), P($"{user}/Wii"), P($"{user}/StateSaves"), emuDeck);

    [Fact]
    public void Dolphin_gci_saves_are_one_game_per_game_id_named_by_it()
    {
        Bytes("d/GC/USA/Card A/01-GM8E-MetroidPrime A.gci", F.Gci("GM8E", "01", "Metroid Prime"));
        Bytes("d/GC/USA/Card A/01-GM8E-MetroidPrime B.gci", F.Gci("GM8E", "01", "Metroid Prime"));
        Bytes("d/GC/USA/Card A/8P-GALE-SuperSmashBros0110290334.gci", F.Gci("GALE", "8P", "Super Smash Bros. Melee"));
        Bytes("d/GC/JAP/Card A/01-GZLJ-gczelda2.gci", F.Gci("GZLJ", "01", "ゼルダの伝説"));
        Bytes("d/GC/USA/Card A/HB-ZZZE-homebrew.gci", F.Gci("ZZZE", "HB", "Homebrew"));
        Directory.CreateDirectory(P("d/StateSaves"));

        var found = DolphinSaves.Scan(new[] { DolphinSetup("d") });

        // One Redump has no disc of is named by the comment the game wrote into its save.
        Assert.Equal(
            new[] { "Homebrew", "Metroid Prime", "Super Smash Bros. Melee", "Zelda no Densetsu - Kaze no Takt" },
            found.Select(c => c.Name));
        Assert.Equal(new[] { null, "USA", "USA", "Japan" }, found.Select(c => c.Release?.Region));
        Assert.Equal(("GameCube", "GM8E01"), (found[1].Release!.Console, Assert.Single(found[1].Release!.GameIds!)));
        var prime = found[1];
        Assert.Equal(("Dolphin", "gc", "GM8E01"), (prime.EmulatorName, prime.EmulatorSystem, prime.EmulatorRom));
        Assert.Equal(new[] { "01-GM8E-*.gci" }, prime.IncludeGlobs);
        Assert.Equal(P("d/GC/USA/Card A"), prime.SuggestedSaveDir);
        Assert.Equal(new[] { "GM8E01.s*" }, Assert.Single(prime.ExtraSaveDirs!).IncludeGlobs);
        Assert.Equal(2, SaveArchive.ListFiles(prime.SuggestedSaveDir!, null, prime.IncludeGlobs).Count);
    }

    [Fact]
    public void A_wii_save_is_its_title_folder_named_by_its_game_id_else_its_banner()
    {
        // The Deck's own: Metroid Prime Trilogy (R3ME) and Metroid: Other M (R3OE), and a system title with no banner.
        Bytes("d/Wii/title/00010000/52334d45/data/banner.bin", F.Banner("Metroid Prime Trilogy", "The Complete Epic"));
        Text("d/Wii/title/00010000/52334d45/data/save.bin");
        Text("d/Wii/title/00010000/52334d45/content/title.tmd", "installed, not a save");
        Bytes("d/Wii/title/00010000/52334f45/data/banner.bin", F.Banner("Metroid: Other M"));
        Text("d/Wii/title/00010000/52334f45/data/share/save0.dat");
        // WiiWare is no disc: named by its banner.
        Bytes("d/Wii/title/00010001/57414245/data/banner.bin", F.Banner("A WiiWare Game"));
        Text("d/Wii/title/00000001/00000002/data/setting.txt");
        Text("d/Wii/title/00010008/48414b45/data/x");

        var found = DolphinSaves.Scan(new[] { DolphinSetup("d") });

        Assert.Equal(new[] { "A WiiWare Game", "Metroid - Other M", "Metroid Prime Trilogy" }, found.Select(c => c.Name));
        // WiiWare is no disc Redump lists: no region, its game ID all the same.
        Assert.Equal(new[] { null, "USA", "USA" }, found.Select(c => c.Release?.Region));
        Assert.Equal(new GameReleaseDto("Wii"), found[0].Release! with { GameIds = null });
        Assert.Equal("WABE", Assert.Single(found[0].Release!.GameIds!));
        var trilogy = found[2];
        Assert.Equal(("wii", "R3ME"), (trilogy.EmulatorSystem, trilogy.EmulatorRom));
        Assert.Equal(P("d/Wii/title"), trilogy.SuggestedSaveDir);
        Assert.Equal(new[] { "00010000/52334d45/data/**" }, trilogy.IncludeGlobs);
        Assert.Equal(new[] { "R3ME*.s*" }, Assert.Single(trilogy.ExtraSaveDirs!).IncludeGlobs);
        Assert.Equal(new[] { "00010000/52334d45/data/banner.bin", "00010000/52334d45/data/save.bin" },
            SaveArchive.ListFiles(trilogy.SuggestedSaveDir!, null, trilogy.IncludeGlobs).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_dolphin_raw_card_is_shared_and_the_global_undo_state_is_no_games()
    {
        Bytes("d/GC/MemoryCardA.USA.raw", new byte[512 * 1024]);
        Text("d/StateSaves/GM8E01.s01");
        Text("d/StateSaves/lastState.sav");

        var card = Assert.Single(DolphinSaves.Scan(new[] { DolphinSetup("d") }));

        Assert.Equal("GameCube memory card (MemoryCardA.USA.raw)", card.Name);
        Assert.Contains("GCI Folder", card.NotSyncable);
        Assert.Equal(new[] { "GM8E01.s01" }, SaveArchive.ListFiles(P("d/StateSaves"), null, DolphinSaves.StateGlobsFor("GM8E01")));
    }

    [Fact]
    public void Emudeck_dolphin_states_are_found_under_either_name()
    {
        Directory.CreateDirectory(P("Emulation/saves/dolphin/GC"));
        Directory.CreateDirectory(P("Emulation/saves/dolphin/states"));

        var setup = Assert.Single(DolphinSaves.Folders(new[] { P("Emulation") }, Array.Empty<(string, string)>()));

        Assert.Equal(P("Emulation/saves/dolphin/states"), setup.States);
        Assert.True(setup.EmuDeck);
    }

    [Fact]
    public void Primehack_is_found_through_emudeck_under_its_own_name()
    {
        // EmuDeck on SteamOS links primehack/StateSaves; on Windows the same folder is primehack/states.
        Bytes("Emulation/saves/primehack/Wii/title/00010000/52334d45/data/banner.bin", F.Banner("Metroid Prime Trilogy"));
        Directory.CreateDirectory(P("Emulation/saves/primehack/StateSaves"));
        Bytes("Emulation/saves/dolphin/Wii/title/00010000/52334d45/data/banner.bin", F.Banner("Metroid Prime Trilogy"));

        var found = DolphinSaves.Scan(DolphinSaves.Folders(new[] { P("Emulation") }, Array.Empty<(string, string)>()));

        // Two rows, one per emulator: the scan never merges another emulator's save into this one.
        Assert.Equal(new[] { "Dolphin", "PrimeHack" }, found.Select(c => c.EmulatorName).Order());
        var prime = found.Single(c => c.EmulatorName == "PrimeHack");
        Assert.Equal(P("Emulation/saves/primehack/StateSaves"), Assert.Single(prime.ExtraSaveDirs!).Dir);
        Assert.NotEqual(ScanCandidate.DedupeKey(found[0]), ScanCandidate.DedupeKey(found[1]));
        Assert.Equal(GameSourceKinds.Emulator + "/PrimeHack", $"{GameSources.From(prime).Kind}/{GameSources.From(prime).Detail}");
    }

    [Fact]
    public void Another_regions_release_takes_the_same_name_and_never_joins()
    {
        Bytes("mc/Mcd001.ps2/BASLUS-21022WW/icon.sys", F.IconSys("Prince of Persia", "Warrior Within"));
        Text("mc/Mcd001.ps2/BASLUS-21022WW/save");
        Bytes("mc/Mcd001.ps2/BESLES-52822WW/icon.sys", F.IconSys("Prince of Persia", "Warrior Within"));
        Text("mc/Mcd001.ps2/BESLES-52822WW/save");
        Text("mc/Mcd001.ps2/_pcsx2_superblock");
        var found = Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("mc"), null) });
        var (usa, europe) = (found[0], found[1]);
        var deck = new GameDto(Guid.NewGuid(), usa.Name, null, null, true, IncludeGlobs: usa.IncludeGlobs!.ToArray(),
            Emulators: ["PCSX2"], Release: usa.Release);

        // Another machine's USA save keeps the Deck's files: it joins the Deck's game.
        Assert.True(Enroller.SameFiles(deck, usa));
        Assert.Equal("Prince of Persia - Warrior Within", Enroller.ServerNameFor([deck], usa, []));
        // The European disc's save takes the same name; the server keeps it apart by its region.
        Assert.Equal("Prince of Persia - Warrior Within", Enroller.ServerNameFor([deck], europe, []));
        var option = Assert.Single(EnrollLinks.For([deck], europe), o => o.GameId == deck.Id);
        Assert.Equal((LinkKind.Blocked, "Another region"), (option.Kind, option.Badge));
        // A game of the name with no region recorded (made before releases were) holds the name.
        Assert.Equal("Prince of Persia - Warrior Within (PCSX2)",
            Enroller.ServerNameFor([deck with { Release = null }], europe, []));
    }

    [Fact]
    public void Two_regions_cards_of_one_title_keep_the_same_files_and_still_never_join()
    {
        // DuckStation names a card per game by its title, which the USA and the European disc share.
        Bytes("usa/Crash Bandicoot_1.mcd", F.Ps1Card("BASCUS-94900CRASH"));
        Bytes("eu/Crash Bandicoot_1.mcd", F.Ps1Card("BESCES-00344CRASH"));
        var usa = Assert.Single(DuckStationSaves.Scan(new[] { new RomSaveFolders(P("usa"), null) }));
        var europe = Assert.Single(DuckStationSaves.Scan(new[] { new RomSaveFolders(P("eu"), null) }));
        var server = new GameDto(Guid.NewGuid(), usa.Name, null, null, true, IncludeGlobs: usa.IncludeGlobs!.ToArray(),
            Emulators: ["DuckStation"], Release: usa.Release);
        Assert.Equal(usa.IncludeGlobs, europe.IncludeGlobs);

        Assert.False(Enroller.SameFiles(server, europe));
        var option = Assert.Single(EnrollLinks.For([server], europe), o => o.GameId == server.Id);
        Assert.Equal((LinkKind.Blocked, "Another region"), (option.Kind, option.Badge));
        Assert.Contains("USA release", EnrollLinks.Resolve([server], europe, [], new LinkChoice(EnrollLinks.Game, server.Id)).Refusal);
        // Nor does this machine take a tracked game of the other region for it.
        var config = AgentConfig.Load(P("state/config.json"));
        config.Games.Add(new TrackedGame
        {
            GameId = server.Id, Name = server.Name, SaveDirectory = P("eu"), IncludeGlobs = usa.IncludeGlobs!.ToList(),
            Release = usa.Release,
        });
        Assert.Null(Enroller.TrackedFor(config, europe));
        Assert.NotNull(Enroller.TrackedFor(config, usa));
    }

    [Fact]
    public void Two_releases_of_one_name_are_both_watched_for_their_process()
    {
        TrackedGame Release(string proc) => new() { GameId = Guid.NewGuid(), Name = "Prince of Persia - Warrior Within", ProcessNames = [proc] };
        var (usa, europe) = (Release("pcsx2-qt"), Release("pcsx2-qtx64"));

        var watched = ProcessWatcher.ByGameId([usa, europe, new TrackedGame { GameId = Guid.NewGuid(), Name = "No process" }]);

        Assert.Equal(new[] { "pcsx2-qt" }, watched[usa.GameId.ToString()]);
        Assert.Equal(new[] { "pcsx2-qtx64" }, watched[europe.GameId.ToString()]);
        Assert.Equal(2, watched.Count);
    }

    [Fact]
    public void One_release_on_two_differently_named_cards_is_one_game()
    {
        // The maintainer's Deck and Windows PC: one European Warrior Within, each on its own machine's card.
        Bytes("deck/PS2MC-1_converted.ps2/BESLES-52822WW/icon.sys", F.IconSys("Prince of Persia", "Warrior Within"));
        Text("deck/PS2MC-1_converted.ps2/BESLES-52822WW/save");
        Text("deck/PS2MC-1_converted.ps2/_pcsx2_superblock");
        Bytes("pc/MC1.ps2/BESLES-52822WW/icon.sys", F.IconSys("Prince of Persia", "Warrior Within"));
        Text("pc/MC1.ps2/BESLES-52822WW/save");
        Text("pc/MC1.ps2/_pcsx2_superblock");
        var onDeck = Assert.Single(Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("deck"), null) }));
        var onPc = Assert.Single(Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("pc"), null) }));
        var server = new GameDto(Guid.NewGuid(), onDeck.Name, null, null, true, IncludeGlobs: onDeck.IncludeGlobs!.ToArray(),
            Emulators: ["PCSX2"], Release: onDeck.Release);

        Assert.Equal(P("pc/MC1.ps2"), onPc.SuggestedSaveDir);
        Assert.True(Enroller.SameFiles(server, onPc));
        Assert.Equal("Prince of Persia - Warrior Within", Enroller.ServerNameFor([server], onPc, []));
        Assert.Equal((LinkKind.Join, server.Id), EnrollLinks.For([server], onPc)[0] is var o ? (o.Kind, o.GameId) : default);
    }

    [Fact]
    public void A_same_files_server_game_from_another_emulator_is_not_the_same_game()
    {
        Bytes("p/Wii/title/00010000/52334d45/data/banner.bin", F.Banner("Metroid Prime Trilogy"));
        var prime = Assert.Single(DolphinSaves.Scan(new[] { new DolphinFolders("PrimeHack", null, P("p/Wii"), null) }));
        GameDto Game(string name, params string[]? emulators) =>
            new(Guid.NewGuid(), name, null, null, true, IncludeGlobs: prime.IncludeGlobs!.ToArray(), Emulators: emulators);

        Assert.False(Enroller.SameFiles(Game(prime.Name, "Dolphin"), prime));
        Assert.True(Enroller.SameFiles(Game(prime.Name, "Dolphin", "PrimeHack"), prime));
        // Made before sources were recorded, or by hand: open to any emulator, as before.
        Assert.True(Enroller.SameFiles(Game(prime.Name), prime));
        Assert.Equal("Metroid Prime Trilogy (PrimeHack)",
            Enroller.ServerNameFor([Game(prime.Name, "Dolphin")], prime, prime.ExtraSaveDirs ?? []));
    }

    [Fact]
    public void The_poller_suggests_a_folder_only_from_the_same_save()
    {
        Bytes("p/Wii/title/00010000/52334d45/data/banner.bin", F.Banner("Metroid Prime Trilogy"));
        Bytes("ps2/PS2MC-1.ps2", F.Ps2FileCard());
        var prime = Assert.Single(DolphinSaves.Scan(new[] { new DolphinFolders("PrimeHack", null, P("p/Wii"), null) }));
        var card = Assert.Single(Pcsx2Saves.Scan(new[] { new RomSaveFolders(P("ps2"), null) }));
        TrackedGame Tracked(string name, IEnumerable<string>? scope = null) =>
            new() { GameId = Guid.NewGuid(), Name = name, IncludeGlobs = (scope ?? prime.IncludeGlobs!).ToList() };
        GameDto Server(TrackedGame g, params string[] emulators) =>
            new(g.GameId, g.Name, null, null, true, IncludeGlobs: g.IncludeGlobs.ToArray(), Emulators: emulators);

        var dolphins = Tracked(prime.Name);
        Assert.Null(CommandPoller.PathCandidateFor(dolphins, Server(dolphins, "Dolphin"), [prime]));
        Assert.Same(prime, CommandPoller.PathCandidateFor(dolphins, Server(dolphins, "PrimeHack"), [prime]));
        // The same title with no scope (a PC release of it) is not this save either.
        var pc = Tracked(prime.Name, []);
        Assert.Null(CommandPoller.PathCandidateFor(pc, Server(pc), [prime]));
        Assert.Null(CommandPoller.PathCandidateFor(Tracked(card.Name, []), null, [card]));
    }

    // ---- The shared-card warning (SaveDirSanity) ----

    [Fact]
    public void Sanity_warns_about_a_shared_card_mapped_by_hand_and_not_about_one_games_scope()
    {
        Bytes("pcsx2/Mcd001.ps2", F.Ps2FileCard());
        Pcsx2FolderCard("folder");
        Bytes("ds/shared_card_1.mcd", F.Ps1Card("BASCUS-94163FF7S01"));
        Bytes("ds/Crash_1.mcd", F.Ps1Card("BASCUS-94900CRASH"));
        Bytes("gc/01-GM8E-a.gci", F.Gci("GM8E", "01", "Metroid Prime"));
        Bytes("gc/8P-GALE-b.gci", F.Gci("GALE", "8P", "Melee"));

        Assert.Contains(SaveDirSanity.Inspect(P("pcsx2")), p => p.Contains("SHARED PS2 memory card"));
        Assert.Contains(SaveDirSanity.Inspect(P("folder")), p => p.Contains("whole PCSX2 folder memory card"));
        Assert.Contains(SaveDirSanity.Inspect(P("folder/Mcd001.ps2")), p => p.Contains("whole PCSX2 folder memory card"));
        Assert.Contains(SaveDirSanity.Inspect(P("ds")), p => p.Contains("SHARED PS1 memory card"));
        Assert.Contains(SaveDirSanity.Inspect(P("gc")), p => p.Contains("2 GameCube games"));
        Bytes("ds2/Crash_1.mcd", F.Ps1Card("BASCUS-94900CRASH"));
        Bytes("ds2/Crash_2.mcd", F.Ps1Card("BASCUS-94900CRASH"));
        Bytes("ds2/Spyro_1.mcd", F.Ps1Card("BASCUS-94228SPYRO"));
        Assert.Contains(SaveDirSanity.Inspect(P("ds2")), p => p.Contains("2 PS1 games"));

        // What Add games maps: one game's own files, which say nothing.
        Assert.Empty(SaveDirSanity.Inspect(P("folder/Mcd001.ps2"), includeGlobs: ["BASLUS-21050*/**"]));
        Assert.Empty(SaveDirSanity.Inspect(P("ds"), includeGlobs: DuckStationSaves.SaveGlobsFor("Crash")));
        Assert.Empty(SaveDirSanity.Inspect(P("gc"), includeGlobs: ["01-GM8E-*.gci"]));
    }

    [Fact]
    public void Console_text_folds_full_width_letters_and_reads_shift_jis()
    {
        Assert.Equal("KINGDOM HEARTS II", ConsoleText.Tidy("ＫＩＮＧＤＯＭ　ＨＥＡＲＴＳ　ＩＩ"));
        Assert.Equal("Kingdom Hearts II", Pcsx2Saves.IconSysTitle(WriteTemp(F.IconSys("Kingdom Hearts II", "Save 1"))));
        Assert.Null(Pcsx2Saves.IconSysTitle(WriteTemp(new byte[16])));
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = P("tmp/" + Guid.NewGuid().ToString("N"));
        F.Write(path, bytes);
        return path;
    }
}
