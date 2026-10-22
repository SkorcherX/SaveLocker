using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>ScummVM discovery (tasks/emulator-saves Phase 10): targets from scummvm.ini, a global and a per-game
/// savepath, and saves from engines that name their files differently.</summary>
public sealed class ScummVmTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-scummvm-" + Guid.NewGuid().ToString("N"));

    public ScummVmTests() => Directory.CreateDirectory(_root);

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

    private string Ini() => $"""
        [scummvm]
        savepath={P("Emulation/saves/scummvm/saves")}
        versioninfo=2.9.0

        [keymapper]
        keymap_engine-default_SKIP=ESCAPE

        [monkey2]
        description=Monkey Island 2: LeChuck's Revenge (DOS/English)
        engineid=scumm
        gameid=monkey2

        [monkey]
        gameid=monkey
        engineid=scumm
        description=The Secret of Monkey Island (CD/DOS/English)

        [sky]
        engineid=sky
        gameid=sky
        description=Beneath a Steel Sky (CD/DOS)

        [kq4-own]
        gameid=kq4
        engineid=sci
        description=King's Quest IV
        savepath={P("own")}

        [neversaved]
        gameid=dig
        engineid=scumm
        """;

    [Fact]
    public void Targets_with_saves_are_games_scoped_to_their_own_files()
    {
        Touch("config/scummvm.ini", Ini());
        Touch("Emulation/saves/scummvm/saves/monkey2.s01");
        Touch("Emulation/saves/scummvm/saves/monkey2.s02");
        Touch("Emulation/saves/scummvm/saves/monkey.s00");
        Touch("Emulation/saves/scummvm/saves/SKY-VM.000");
        Touch("Emulation/saves/scummvm/saves/SKY-VM.SAV");
        Touch("own/kq4-own.001");

        var found = ScummVmSaves.Scan(new[] { new ScummVmConfig(P("config/scummvm.ini"), P("default")) }, new[] { P("Emulation") });

        Assert.Equal(new[] { "Beneath a Steel Sky", "King's Quest IV", "Monkey Island 2: LeChuck's Revenge", "The Secret of Monkey Island" },
            found.Select(c => c.Name));
        var mi2 = found[2];
        Assert.Equal(("ScummVM", "scummvm", "monkey2"), (mi2.EmulatorName, mi2.EmulatorSystem, mi2.EmulatorRom));
        Assert.Equal(new[] { "monkey2.*" }, mi2.IncludeGlobs);
        Assert.Equal(P("Emulation/saves/scummvm/saves"), mi2.SuggestedSaveDir);
        Assert.True(mi2.ViaEmuDeck);
        Assert.Null(mi2.ExtraSaveDirs);
        // monkey.* keeps Monkey Island 1's file and not the sequel's.
        Assert.Equal(new[] { "monkey.s00" }, SaveLocker.Shared.SaveArchive.ListFiles(found[3].SuggestedSaveDir!, null, found[3].IncludeGlobs));
        Assert.Equal(new[] { "SKY-VM.*" }, found[0].IncludeGlobs);
        Assert.Equal(P("own"), found[1].SuggestedSaveDir);
        Assert.False(found[1].ViaEmuDeck);
    }

    [Fact]
    public void An_old_config_with_only_a_game_id_still_finds_a_fixed_name_engine()
    {
        // Configs from before ScummVM wrote engineid: Broken Sword 1's Mac release is gameid sword1mac.
        Touch("config/scummvm.ini", "[bs1]\ngameid=sword1mac\n\n[sky-cd]\ngameid=sky\n\n[sword1x]\ngameid=sword1x\n");
        Touch("default/sword1.001");
        Touch("default/SKY-VM.001");
        Touch("default/sword1x.001");

        var found = ScummVmSaves.Scan(new[] { new ScummVmConfig(P("config/scummvm.ini"), P("default")) }, Array.Empty<string>());
        Assert.Equal(new[] { "SKY-VM.*", "sword1.*", "sword1x.*" }, found.Select(c => c.IncludeGlobs![0]).Order(StringComparer.Ordinal));
        Assert.Equal("sword1", ScummVmSaves.FixedPrefix(new Dictionary<string, string> { ["engineid"] = "sword1", ["gameid"] = "sword1psxdemo" }));
        Assert.Null(ScummVmSaves.FixedPrefix(new Dictionary<string, string> { ["engineid"] = "scumm", ["gameid"] = "queen" }));
    }

    [Fact]
    public void With_no_savepath_the_default_folder_is_used()
    {
        Touch("config/scummvm.ini", "[tentacle]\ngameid=tentacle\n");
        Touch("default/tentacle.s05");

        var c = Assert.Single(ScummVmSaves.Scan(new[] { new ScummVmConfig(P("config/scummvm.ini"), P("default")) }, Array.Empty<string>()));
        Assert.Equal("tentacle", c.Name);
        Assert.Equal(P("default"), c.SuggestedSaveDir);
    }

    [Fact]
    public void Ini_parser_keeps_sections_apart_and_ignores_junk()
    {
        var ini = ScummVmSaves.ParseIni("junk\n[a]\nX=1\n# c\n; c\n[B]\nx = 2\nx=3\nbad\n");
        Assert.Equal("1", ini["a"]["x"]);
        Assert.Equal("3", ini["b"]["X"]);
        Assert.Empty(ScummVmSaves.Scan(new[] { new ScummVmConfig(P("missing.ini"), P("d")) }, Array.Empty<string>()));
    }
}
