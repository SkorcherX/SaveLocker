using System.Xml;

namespace SaveLocker.Agent;

/// <summary>
/// Supermodel (Sega Model 3 arcade), a row of <see cref="RomSaves"/> (tasks/emulator-saves Phase 9). NVRAM is
/// <c>NVRAM/&lt;set&gt;.nv</c>, written on every exit; states <c>Saves/&lt;set&gt;.st&lt;slot&gt;</c> (<c>Src/OSD/SDL/Main.cpp</c>).
/// <para>
/// Where (<c>Src/OSD/{Unix,Windows}/FileSystemPath.cpp</c>): on Linux <c>~/.supermodel/{NVRAM,Saves}</c> when
/// <c>~/.supermodel</c> exists — EmuDeck's choice, it installs the Flatpak <c>com.supermodel3.Supermodel</c> but keeps
/// everything there and links nothing (<c>Supermodel_setupSaves</c> is "NYI") — else <c>$XDG_DATA_HOME/supermodel</c>,
/// by default <c>~/.local/share/supermodel</c> (inside the Flatpak, its own data folder). On Windows the folders are
/// relative to the emulator's own: EmuDeck for Windows keeps it in <c>%APPDATA%\EmuDeck\Emulators\Supermodel</c> and
/// links its states folder to <c>Emulation\saves\supermodel\saves</c>; NVRAM is only in the emulator's folder. A
/// standalone Windows copy can be anywhere, so it is not looked for.
/// </para>
/// <para>
/// <b>The trap, found on a real EmuDeck Deck:</b> <c>Supermodel_init</c> rsyncs EmuDeck's whole
/// <c>configs/supermodel</c> into <c>~/.supermodel</c>, NVRAM included — 29 <c>.nv</c> files, one per common set — so a
/// file there does not mean the game was played. A file still byte-identical to EmuDeck's (<see cref="EmuDeckSeeds"/>)
/// is an untouched seed (<see cref="ScanCandidate.UntouchedSeed"/>), listed only to join a server game that keeps
/// it. EmuDeck for Windows seeds none.
/// </para>
/// <para>
/// Name: Supermodel's own <c>Config/Games.xml</c> (<c>&lt;game name="scud"&gt;&lt;identity&gt;&lt;title&gt;</c>), which it
/// needs to load any ROM and EmuDeck downloads from upstream — then ES-DE's gamelist, then the set name.
/// </para>
/// </summary>
public static class SupermodelSaves
{
    public const string EmulatorName = "Supermodel";

    public static IReadOnlyList<ScanCandidate> Scan()
    {
        var roots = EmuDeckRoots.Find();
        return Scan(EmulatorPaths.Standalone ? DataRoots(roots.Count > 0) : Array.Empty<(string, bool)>(), roots,
            gamelists: GamelistXml.Find(roots));
    }

    /// <param name="dataRoots">Supermodel folders holding <c>NVRAM</c>, <c>Saves</c> and <c>Config</c>, with whether
    /// EmuDeck set each up.</param>
    /// <param name="gamelists">ES-DE's arcade titles; when omitted, only those inside <paramref name="emuDeckRoots"/>, so a test
    /// never reads the machine's own.</param>
    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<(string Dir, bool EmuDeck)> dataRoots, IEnumerable<string> emuDeckRoots,
        IReadOnlySet<string>? seeds = null, GamelistXml? gamelists = null)
    {
        seeds ??= EmuDeckSeeds;
        var roots = dataRoots.ToList();
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, _) in roots)
            foreach (var config in ConfigDirs(dir))
                foreach (var (set, title) in LoadGamesXml(Path.Combine(config, "Games.xml")))
                    titles.TryAdd(set, title);

        var rules = new RomSaveRules(EmulatorName, ".nv", (set, ext) => new[] { set + ext }, set => new[] { set + ".st*" },
            System: "model3", IsSeed: f => RomSaves.IsUntouchedSeed(f, seeds),
            KnownTitle: set => titles.GetValueOrDefault(set));
        var folders = roots.Select(r => new RomSaveFolders(Path.Combine(r.Dir, "NVRAM"), Path.Combine(r.Dir, "Saves"), r.EmuDeck));
        var emu = emuDeckRoots.ToList();
        return RomSaves.Scan(rules, RomSaves.Existing(folders), emu, gamelists ?? GamelistXml.In(emu));
    }

    /// <summary>Supermodel's data folders on this machine. <paramref name="emuDeck"/>: an EmuDeck install exists,
    /// so <c>~/.supermodel</c> is EmuDeck's.</summary>
    public static IReadOnlyList<(string, bool)> DataRoots(bool emuDeck)
    {
        var home = EmulatorPaths.Home;
        if (OperatingSystem.IsWindows())
            return new[] { (Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "Supermodel"), true) };
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x && EmulatorPaths.HomeOverride is null
            ? x : Path.Combine(home, ".local", "share");
        return new[]
        {
            (Path.Combine(home, ".supermodel"), emuDeck),
            (Path.Combine(xdgData, "supermodel"), false),
            (Path.Combine(home, ".var", "app", "com.supermodel3.Supermodel", "data", "supermodel"), false),
        };
    }

    /// <summary>Where a data folder's <c>Games.xml</c> may be: its own <c>Config</c>, or — for the XDG layout, which
    /// keeps config apart — <c>~/.config/supermodel/Config</c>.</summary>
    private static IEnumerable<string> ConfigDirs(string dataDir)
    {
        yield return Path.Combine(dataDir, "Config");
        yield return Path.Combine(EmulatorPaths.Home, ".config", "supermodel", "Config");
    }

    /// <summary>Set name → <c>&lt;identity&gt;&lt;title&gt;</c> from one <c>Games.xml</c>; empty when it is missing or
    /// broken (whatever parsed before the fault is kept). No DTD, no external entities.</summary>
    public static IReadOnlyDictionary<string, string> LoadGamesXml(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(file)) return result;
            using var reader = XmlReader.Create(file, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
            string? set = null;
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "game")
                    set = reader.GetAttribute("name");
                else if (set is not null && reader.NodeType == XmlNodeType.Element && reader.Name == "title")
                {
                    var title = reader.ReadElementContentAsString().Trim();
                    if (set.Length > 0 && title.Length > 0) result.TryAdd(set, title);
                    set = null;
                    continue;
                }
                reader.Read();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }
        return result;
    }

    /// <summary>
    /// SHA-256 of each file in EmuDeck's <c>configs/supermodel/NVRAM</c> (<c>dragoonDorise/EmuDeck</c>, one version
    /// since <c>ec20d41bd2</c>, 2024-01-31), read 2026-10-07 — and all 29 found untouched in a real EmuDeck Deck's
    /// <c>~/.supermodel/NVRAM</c> the same day.
    /// </summary>
    public static readonly IReadOnlySet<string> EmuDeckSeeds = new HashSet<string>(StringComparer.Ordinal)
    {
        "5627631ab900a0564c0642d43bbe6a133d361a8e236be0bd4bfbf8c51dc64623", // dayto2pe.nv
        "686289bd2a3023b559e27c9133e08f836bc81ce06042bbdb83f660e51f9d6fff", // daytona2.nv
        "af11b7ad4b9b65d6f8af89fc2f82d417b88c608a5a80f51be2d7e3862aa66f57", // dirtdvls.nv
        "883053b509fd011b8301b0c0aafd4e29c1b6830d3d1a25b089ef8da8b21d830b", // eca.nv
        "4bef59431d27aa7ff28e844c6b0c4d124506b380d7c335591658b3084c3dfcc6", // fvipers2.nv
        "a59445f86e8f6f9348c83ddc3dcdbaa7ff87d14917817d1bf2806e1e4d731414", // getbassur.nv
        "1193f64bb1ad65034b1a15acf1ec02cc48de1818f6b057769a7a4efc8f6af0cb", // harley.nv
        "b63bef5d3ca9a80aee504133b4c79fb7a58342163c24e5abc57591fb59f51812", // lamachin.nv
        "8fc1715df17cdf878296bae57b7e335290af34b8fade247b5820795d66d34747", // lemans24.nv
        "6c02a315fbcc6b0014f00284684010e527298b1e0809b26d63ec82f78104f989", // lostwsga (Calibrated for 4x3 resolutions).nv
        "d3b4ad4d77ffaeb6826ebde3c5e28b5419c785cd8f0621ca01b1e84dba051024", // lostwsga.nv
        "3aadf9a8b3a09c4fe0a730a11307d36d5f0129fa54e42aa6d07c431a672aa52a", // magtruck.nv
        "c50b97f3d617c3a740a4a633f86952a4ab88f881a2ef26273caa637bc2de65de", // mgtrkbad.nv
        "491387045d9176810bc2116199c330f29dbe0ed87aa5ebb58ece712e5676bae3", // oceanhun.nv
        "bad406ba2199dd53e539ecaa56a9f20bef0bca1b8376c16c130fa09e95f0e761", // oceanhuna.nv
        "83fa90f689f742ac2ec4a07b0623f82b888d07e2e83477e6dc2a9caeb499f288", // scud.nv
        "f9216786b173766fefadd6d2db26a155629a0b4dd38d8ac9ea5fea63749bf8b5", // scudplus.nv
        "e551209491667d8e53c4eeb2090427906c85b5c9a3b4748ca95166468dcb6fd3", // skichamp.nv
        "af34465074f8b7ea9b2bfa7689ca335b58b48e9196e73798d0324119323a6b02", // spikeofe.nv
        "a36aa9786e2cc103fd6de5880565bc5a72f924d7d28ab7e485ba3c0a7606dfaa", // spikeout.nv
        "9864ebd64d803fd00b8f4908df625f49b45df80d916ea16080ba15db61ea1720", // srally2.nv
        "8a77f149fa5af1e85be0a9ab3c745a5d2ce5ef8ac462b04f0da7f812816ca93c", // srally2dx.nv
        "d0b4ca492564902a0b13e45dd14a0ef108cf7a91b0b7b4e165e4580e556927f0", // swtrilgy.nv
        "b0a6111caa2c24706b68b6db28f419b309c85ff0359e13e4161eec7ab45b2369", // vf3.nv
        "c812ddd48f2e54cc038885910e5bf04dd1aabe9609ae0534550e4945ef9f3537", // vf3tb.nv
        "2ee77b81f55966286e91dc2884fc19c75dd6f41d9d3615aec1b71bc3434d4ef6", // von2.nv
        "94b7ca983d2adfdfee1332d1322177739fbd1d4109c70f0c7766621a2c3461a0", // vs2.nv
        "ac863629e5ae18aebe630159eb3de7eb32a30b88eacf52864b8f416e63272280", // vs298.nv
        "25e510a2acec8775b3e97df954e1309bfd0381bc1a6349150662dba6a3f7a436", // vs2v991.nv
    };
}
