using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>Supermodel and Model 2 discovery (tasks/emulator-saves Phase 9): saves named after an arcade set, and
/// the title that set stands for.</summary>
public sealed class ArcadeSavesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-arcade-" + Guid.NewGuid().ToString("N"));

    public ArcadeSavesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Touch(string rel, string content = "nvram")
    {
        var path = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // Upstream's Config/Games.xml, cut to two sets.
    private const string GamesXml = """
        <!-- Games.xml -->
        <games>
          <game name="scud">
            <identity>
              <title>Scud Race</title>
              <version>Export, Twin/DX</version>
            </identity>
            <roms><region name="crom"><file offset="0" name="epr-19731.17" crc32="0x3EE6447E" /></region></roms>
          </game>
          <game name="vf3" parent="vf3a">
            <identity><title>Virtua Fighter 3</title></identity>
          </game>
        </games>
        """;

    [Fact]
    public void Supermodel_nvram_is_a_game_named_from_games_xml_with_its_states()
    {
        Touch(".supermodel/Config/Games.xml", GamesXml);
        Touch(".supermodel/NVRAM/scud.nv");
        Touch(".supermodel/Saves/scud.st0", "state");
        Touch(".supermodel/NVRAM/lemans24.nv");

        var found = SupermodelSaves.Scan(new[] { (P(".supermodel"), true) }, Array.Empty<string>());

        Assert.Equal(new[] { "lemans24", "Scud Race" }, found.Select(c => c.Name));
        var scud = found[1];
        Assert.Equal(("Supermodel", "model3", "scud"), (scud.EmulatorName, scud.EmulatorSystem, scud.EmulatorRom));
        Assert.True(scud.ViaEmuDeck);
        Assert.Equal(P(".supermodel/NVRAM"), scud.SuggestedSaveDir);
        Assert.Equal(new[] { "scud.nv" }, scud.IncludeGlobs);
        var states = Assert.Single(scud.ExtraSaveDirs!);
        Assert.Equal((RomSaves.StatesKey, P(".supermodel/Saves")), (states.Key, states.Dir));
        Assert.Equal(new[] { "scud.st*" }, states.IncludeGlobs);
    }

    [Fact]
    public void Supermodel_marks_the_nvram_files_emudeck_installed_as_untouched_seeds_until_played()
    {
        Touch(".supermodel/NVRAM/vf3.nv", "emudeck-seed-vf3");
        Touch(".supermodel/NVRAM/scud.nv", "emudeck-seed-scud");
        var seeds = new HashSet<string> { Sha("emudeck-seed-vf3"), Sha("emudeck-seed-scud") };
        Assert.All(SupermodelSaves.Scan(new[] { (P(".supermodel"), true) }, Array.Empty<string>(), seeds), c => Assert.True(c.UntouchedSeed));

        Touch(".supermodel/NVRAM/scud.nv", "emudeck-seed-scud, then a lap record");
        Assert.Equal("scud", Assert.Single(SupermodelSaves.Scan(new[] { (P(".supermodel"), true) }, Array.Empty<string>(), seeds),
            c => !c.UntouchedSeed).EmulatorRom);
        Assert.Equal(29, SupermodelSaves.EmuDeckSeeds.Count);
    }

    [Fact]
    public void A_set_games_xml_does_not_name_falls_back_to_the_gamelist()
    {
        Touch("data/NVRAM/lemans24.nv");
        Touch("Emulation/storage/es-de/gamelists/model3/gamelist.xml",
            "<gameList><game><path>./lemans24.zip</path><name>Le Mans 24 (Japan, Revision B)</name></game></gameList>");

        var c = Assert.Single(SupermodelSaves.Scan(new[] { (P("data"), false) }, new[] { P("Emulation") }));
        Assert.Equal("Le Mans 24", c.Name);
        Assert.False(c.ViaEmuDeck);
    }

    [Fact]
    public void Games_xml_reader_survives_a_broken_file()
    {
        var t = SupermodelSaves.LoadGamesXml(Touch("Games.xml", GamesXml));
        Assert.Equal("Scud Race", t["scud"]);
        Assert.Equal("Virtua Fighter 3", t["vf3"]);
        Assert.Empty(SupermodelSaves.LoadGamesXml(P("missing.xml")));
        Assert.Equal("A", SupermodelSaves.LoadGamesXml(Touch("Broken.xml", "<games><game name=\"a\"><identity><title>A</title></identity></game><game"))["a"]);
    }

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void Model2_lists_played_sets_and_marks_emudecks_untouched_seed_files()
    {
        // EmuDeck's install copied both in; only daytona has been played since.
        Touch("Emulation/roms/model2/NVDATA/vf2.DAT", "emudeck-seed-vf2");
        Touch("Emulation/roms/model2/NVDATA/daytona.DAT", "my lap records");
        Touch("Emulation/roms/model2/NVDATA/notes.txt", "not a save");
        var seeds = new HashSet<string> { Sha("emudeck-seed-vf2"), Sha("emudeck-seed-daytona") };

        var found = Model2Saves.Scan(new[] { P("Emulation") }, Array.Empty<string>(), seeds);

        Assert.Equal("vf2", Assert.Single(found, c => c.UntouchedSeed).EmulatorRom);
        var daytona = Assert.Single(found, c => !c.UntouchedSeed);
        Assert.Equal("Daytona USA", daytona.Name);
        Assert.Equal(("Model 2", "model2", "daytona"), (daytona.EmulatorName, daytona.EmulatorSystem, daytona.EmulatorRom));
        Assert.Equal(P("Emulation/roms/model2/NVDATA"), daytona.SuggestedSaveDir);
        Assert.Equal(new[] { "daytona.DAT" }, daytona.IncludeGlobs);
        var states = Assert.Single(daytona.ExtraSaveDirs!);
        Assert.Equal((RomSaves.StatesKey, P("Emulation/roms/model2/STATES")), (states.Key, states.Dir));
        Assert.Equal(Enumerable.Range(0, 10).Select(n => $"daytona{n}.sta"), states.IncludeGlobs);
        Assert.True(daytona.ViaEmuDeck);

        // Once vf2 is played its file differs from the seed and it is a game too.
        Touch("Emulation/roms/model2/NVDATA/vf2.DAT", "emudeck-seed-vf2 + a high score");
        var played = Model2Saves.Scan(new[] { P("Emulation") }, Array.Empty<string>(), seeds);
        Assert.Equal(new[] { "Daytona USA", "Virtua Fighter 2" }, played.Select(c => c.Name));
        Assert.DoesNotContain(played, c => c.UntouchedSeed);
    }

    [Fact]
    public void Model2_states_are_scoped_slot_by_slot_and_count_as_play()
    {
        // vcop's NVRAM is still EmuDeck's, but it has a state: it was played. Virtua Cop 2's slot 0 is
        // vcop20.sta, which a vcop* pattern would take for Virtua Cop's.
        Touch("Emulation/roms/model2/NVDATA/vcop.DAT", "emudeck-seed-vcop");
        Touch("Emulation/roms/model2/NVDATA/vcop2.DAT", "emudeck-seed-vcop2");
        Touch("Emulation/roms/model2/STATES/vcop3.sta", "vcop slot 3");
        Touch("Emulation/roms/model2/STATES/vcop20.sta", "vcop2 slot 0");
        var seeds = new HashSet<string> { Sha("emudeck-seed-vcop"), Sha("emudeck-seed-vcop2") };

        var found = Model2Saves.Scan(new[] { P("Emulation") }, Array.Empty<string>(), seeds);

        Assert.Equal(new[] { "Virtua Cop", "Virtua Cop 2" }, found.Select(c => c.Name));
        Assert.DoesNotContain(found, c => c.UntouchedSeed);
        var vcop = found[0].ExtraSaveDirs!.Single().IncludeGlobs!;
        Assert.Contains("vcop3.sta", vcop);
        Assert.DoesNotContain("vcop20.sta", vcop);
        Assert.Contains("vcop20.sta", found[1].ExtraSaveDirs!.Single().IncludeGlobs!);
    }

    [Fact]
    public void Model2_reads_emudeck_for_windows_install_folder_too()
    {
        Touch("EmuDeck/Emulators/m2emulator/NVDATA/srallyc.DAT", "played");
        var c = Assert.Single(Model2Saves.Scan(Array.Empty<string>(), new[] { P("EmuDeck/Emulators/m2emulator") },
            new HashSet<string>()));
        Assert.Equal("Sega Rally Championship", c.Name);
        Assert.Equal(P("EmuDeck/Emulators/m2emulator/STATES"), c.ExtraSaveDirs!.Single().Dir);
    }

    [Fact]
    public void Every_emudeck_seed_has_a_title_and_a_hash()
    {
        Assert.Equal(39, Model2Saves.EmuDeckSeeds.Count);
        Assert.Equal(39, Model2Saves.Titles.Count);
        Assert.All(Model2Saves.EmuDeckSeeds, h => Assert.Matches("^[0-9a-f]{64}$", h));
    }

    [Fact]
    public void A_large_file_is_never_hashed_as_a_seed()
    {
        var big = Touch("big.DAT", new string('x', 1100 * 1024));
        Assert.False(Model2Saves.IsSeed(new FileInfo(big), new HashSet<string> { Sha(new string('x', 1100 * 1024)) }));
        // A Supermodel NVRAM file is ~128 KB: well inside the limit.
        var nv = Touch("x.nv", new string('n', 131406));
        Assert.True(RomSaves.IsUntouchedSeed(new FileInfo(nv), new HashSet<string> { Sha(new string('n', 131406)) }));
    }
}
