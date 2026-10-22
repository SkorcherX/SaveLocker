using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>melonDS discovery (tasks/emulator-saves Phase 8): EmuDeck's folders, melonDS 1.0's TOML, the old
/// INI, and saves kept beside the ROM.</summary>
public sealed class MelonDsTests : IDisposable
{
    private const string Platinum = "Pokemon - Platinum Version (USA)";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-melon-" + Guid.NewGuid().ToString("N"));

    public MelonDsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Touch(string rel, string content = "save")
    {
        var path = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // TOML wants a path's backslashes escaped; a forward-slash path works on both OSes.
    private string Toml(string rel) => P(rel).Replace('\\', '/');

    [Fact]
    public void Emudeck_saves_are_one_candidate_each_with_states_declared()
    {
        Touch($"Emulation/saves/melonds/saves/{Platinum}.sav");
        Touch($"Emulation/saves/melonds/states/{Platinum}.ml1");
        Touch("Emulation/saves/melonds/saves/Mario Kart DS (USA).sav");
        Touch("Emulation/saves/melonds/saves/Mario Kart DS (USA).sav.2");   // a second instance's copy
        Touch("Emulation/saves/melonds/saves/notes.savx");
        Touch("Emulation/saves/melonds/saves/empty.sav", "");

        var found = MelonDsSaves.Scan(new[] { P("Emulation") }, Array.Empty<string>());

        Assert.Equal(new[] { "Mario Kart DS", "Pokemon - Platinum Version" }, found.Select(c => c.Name));
        var platinum = found[1];
        Assert.Equal("melonDS", platinum.EmulatorName);
        Assert.Equal("nds", platinum.EmulatorSystem);
        Assert.True(platinum.ViaEmuDeck);
        Assert.Equal(P("Emulation/saves/melonds/saves"), platinum.SuggestedSaveDir);
        Assert.Equal(new[] { Platinum + ".sav" }, platinum.IncludeGlobs);
        var states = Assert.Single(platinum.ExtraSaveDirs!);
        Assert.Equal((RomSaves.StatesKey, P("Emulation/saves/melonds/states")), (states.Key, states.Dir));
        Assert.Equal(new[] { Platinum + ".ml*" }, states.IncludeGlobs);
        // Mario Kart has no states folder content yet; the folder is still declared.
        Assert.Single(found[0].ExtraSaveDirs!);
    }

    [Fact]
    public void A_toml_config_is_read_and_wins_over_the_old_ini()
    {
        Touch($"mysaves/{Platinum}.sav");
        Touch($"inisaves/Other (USA).sav");
        Touch("config/melonDS.toml",
            $"LastROMFolder = \"{Toml("roms")}\"\n\n[Instance0]\nSaveFilePath = \"{Toml("mysaves")}\"\nSavestatePath = '{Toml("mystates")}'\n" +
            "\n[Instance0.Window0]\nWidth = 256\n");
        Touch("config/melonDS.ini", $"SaveFilePath={P("inisaves")}\n");

        var found = MelonDsSaves.Scan(Array.Empty<string>(), new[] { P("config") });

        var c = Assert.Single(found);
        Assert.Equal(P("mysaves"), c.SuggestedSaveDir);
        Assert.Equal(P("mystates"), c.ExtraSaveDirs![0].Dir);
        Assert.False(c.ViaEmuDeck);
    }

    [Fact]
    public void The_ini_is_read_when_there_is_no_toml()
    {
        Touch($"inisaves/{Platinum}.sav");
        Touch("config/melonDS.ini", $"BIOS9Path=/x\r\nSaveFilePath={P("inisaves")}\r\nSavestatePath=\r\n");

        var c = Assert.Single(MelonDsSaves.Scan(Array.Empty<string>(), new[] { P("config") }));
        Assert.Equal(P("inisaves"), c.SuggestedSaveDir);
        // No states path: beside the ROM, and with no ROM folder known, beside the save.
        Assert.Equal(P("inisaves"), c.ExtraSaveDirs![0].Dir);
    }

    [Fact]
    public void An_empty_save_path_means_beside_the_rom_and_the_scope_keeps_the_rom_out()
    {
        Touch($"roms/nds/{Platinum}.nds", "rom");
        Touch($"roms/nds/{Platinum}.sav");
        Touch("config/melonDS.toml", $"LastROMFolder = \"{Toml("roms/nds")}\"\n[Instance0]\nSaveFilePath = \"\"\n");

        var c = Assert.Single(MelonDsSaves.Scan(Array.Empty<string>(), new[] { P("config") }));
        Assert.Equal(P("roms/nds"), c.SuggestedSaveDir);
        Assert.Equal(P("roms/nds"), c.ExtraSaveDirs![0].Dir);
        Assert.Equal(new[] { Platinum + ".sav" }, SaveLocker.Shared.SaveArchive.ListFiles(c.SuggestedSaveDir!, null, c.IncludeGlobs));
    }

    [Fact]
    public void Beside_the_rom_only_a_ds_roms_save_is_melonds()
    {
        // A shared ROM folder: mGBA writes .sav beside its ROMs too, and an orphan save has no ROM at all.
        Touch($"roms/{Platinum}.zip", "rom");
        Touch($"roms/{Platinum}.sav");
        Touch("roms/Golden Sun (USA).gba", "rom");
        Touch("roms/Golden Sun (USA).sav");
        Touch("roms/Orphan.sav");
        Touch("config/melonDS.ini", $"LastROMFolder={P("roms")}\nSaveFilePath=\n");

        Assert.Equal(Platinum, Assert.Single(MelonDsSaves.Scan(Array.Empty<string>(), new[] { P("config") })).EmulatorRom);
    }

    [Fact]
    public void A_save_folder_of_its_own_needs_no_rom_beside_it()
    {
        Touch($"mysaves/{Platinum}.sav");
        Touch("config/melonDS.ini", $"SaveFilePath={P("mysaves")}\n");

        Assert.Single(MelonDsSaves.Scan(Array.Empty<string>(), new[] { P("config") }));
    }

    [Fact]
    public void One_folder_named_by_emudeck_and_by_the_config_is_one_setup_kept_as_emudeck()
    {
        Touch($"Emulation/saves/melonds/saves/{Platinum}.sav");
        Touch("config/melonDS.ini", $"SaveFilePath={P("Emulation/saves/melonds/saves")}\n");

        var folders = MelonDsSaves.Folders(new[] { P("Emulation") }, new[] { P("config") });
        Assert.True(Assert.Single(folders).EmuDeck);
    }

    [Fact]
    public void Toml_reader_handles_escapes_tables_and_junk()
    {
        var t = MelonDsSaves.Toml("# c\nA = \"x\\\\y \\\"q\\\"\"\n[Instance0]\nB = 'C:\\raw'\nC = [1, 2]\n[\"Quoted\"]\nD = \"d\"\nbroken\n");
        Assert.Equal("x\\y \"q\"", t["A"]);
        Assert.Equal("C:\\raw", t["Instance0.B"]);
        Assert.False(t.ContainsKey("Instance0.C"));
        Assert.Equal("d", t["Quoted.D"]);
    }

    [Fact]
    public void Nothing_found_is_no_candidates_not_an_error()
    {
        Assert.Empty(MelonDsSaves.Scan(new[] { P("nope") }, new[] { P("nope2"), P("") }));
    }
}
