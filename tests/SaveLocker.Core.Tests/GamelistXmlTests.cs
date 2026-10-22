using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>ES-DE's gamelist.xml as a source of arcade titles (tasks/emulator-saves Phase 3).</summary>
public sealed class GamelistXmlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sl-gamelist-" + Guid.NewGuid().ToString("N"));

    public GamelistXmlTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // What ES-DE writes: an <alternativeEmulator> root beside <gameList>, paths relative to the system folder.
    private const string Fbneo = """
        <?xml version="1.0"?>
        <alternativeEmulator>
        	<label>FinalBurn Neo</label>
        </alternativeEmulator>
        <gameList>
        	<game>
        		<path>./sf2.zip</path>
        		<name>Street Fighter II: The World Warrior (World 910522)</name>
        		<rating>0.8</rating>
        	</game>
        	<game><path>./sub/mslug.zip</path><name>Metal Slug</name></game>
        	<game>
        		<name>Name first</name>
        		<path>./dino.zip</path>
        	</game>
        	<game><path>./nameless.zip</path></game>
        	<game />
        	<folder><path>./sub</path><name>A folder, not a game</name></folder>
        </gameList>
        """;

    [Fact]
    public void Reads_every_game_whatever_the_element_order_and_layout()
    {
        var names = GamelistXml.Load(Write("fbneo/gamelist.xml", Fbneo));

        Assert.Equal("Street Fighter II: The World Warrior (World 910522)", names["sf2"]);
        Assert.Equal("Metal Slug", names["mslug"]);
        Assert.Equal("Name first", names["dino"]);
        Assert.False(names.ContainsKey("nameless"));
        Assert.False(names.ContainsKey("sub"));
    }

    [Fact]
    public void A_broken_or_hostile_file_never_throws()
    {
        Assert.Empty(GamelistXml.Load(Path.Combine(_root, "missing.xml")));
        var truncated = GamelistXml.Load(Write("a/gamelist.xml", "<gameList><game><path>./a.zip</path><name>A</name></game><game><pa"));
        Assert.Equal("A", truncated["a"]);
        var dtd = Write("b/gamelist.xml",
            "<!DOCTYPE gameList [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><gameList><game><path>./b.zip</path><name>&x;</name></game></gameList>");
        Assert.Empty(GamelistXml.Load(dtd));
    }

    [Fact]
    public void Only_arcade_systems_take_a_gamelist_title()
    {
        Write("gamelists/fbneo/gamelist.xml", Fbneo);
        Write("gamelists/snes/gamelist.xml", "<gameList><game><path>./Chrono Trigger (USA).sfc</path><name>CT</name></game></gameList>");
        var lists = new GamelistXml(new[] { Path.Combine(_root, "nowhere"), Path.Combine(_root, "gamelists") });

        Assert.Equal("Metal Slug", lists.ArcadeTitle("fbneo", "mslug"));
        Assert.Null(lists.ArcadeTitle("snes", "Chrono Trigger (USA)"));
        Assert.Null(lists.ArcadeTitle(null, "mslug"));
        Assert.Null(lists.ArcadeTitle("fbneo", "unknown"));
        Assert.Equal("Street Fighter II: The World Warrior", RomNames.TitleFor("sf2", "fbneo", lists));
        Assert.Equal("Chrono Trigger", RomNames.TitleFor("Chrono Trigger (USA)", "snes", lists));
        Assert.Equal("sf2", RomNames.TitleFor("sf2", "fbneo", GamelistXml.None));
        Assert.Equal("Known", RomNames.TitleFor("sf2", "fbneo", lists, known: "Known"));
    }

    [Fact]
    public void A_retroarch_arcade_save_is_named_from_the_emudeck_gamelist()
    {
        var emulation = Path.Combine(_root, "Emulation");
        Write("Emulation/saves/retroarch/saves/sf2.srm", "nvram");
        Write("Emulation/saves/retroarch/saves/Chrono Trigger (USA).srm", "sram");
        Write("Emulation/roms/fbneo/sf2.zip", "");
        Write("Emulation/storage/es-de/gamelists/fbneo/gamelist.xml", Fbneo);

        var found = RetroArchSaves.Scan(RetroArchConfig.Folders(new[] { emulation }, Array.Empty<string>()),
            new[] { emulation }, GamelistXml.Find(new[] { emulation }));

        var sf2 = found.Single(c => c.EmulatorRom == "sf2");
        Assert.Equal("Street Fighter II: The World Warrior", sf2.Name);
        Assert.Equal(new[] { "sf2.srm", "sf2.rtc" }, sf2.IncludeGlobs);
        Assert.Equal("Chrono Trigger", found.Single(c => c.EmulatorRom == "Chrono Trigger (USA)").Name);
    }
}
