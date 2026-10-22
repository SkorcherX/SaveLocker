namespace SaveLocker.Agent;

/// <summary>
/// Model 2 Emulator (ElSemi; Sega Model 2 arcade, Windows only — Proton on SteamOS), a row of
/// <see cref="RomSaves"/> (tasks/emulator-saves Phase 9). Each game's NVRAM is <c>NVDATA/&lt;set&gt;.DAT</c>
/// beside the emulator's exe: <c>Emulation/roms/model2</c> under EmuDeck on SteamOS (<c>Model2_emuPath</c>),
/// <c>%APPDATA%\EmuDeck\Emulators\m2emulator</c> under EmuDeck for Windows. Neither EmuDeck links it into
/// <c>Emulation/saves</c> (<c>Model2_setupSaves</c> is "NYI").
/// <para>
/// States are <c>STATES\&lt;set&gt;&lt;slot&gt;.sta</c>, slots 0–9 picked with the number keys — read from the
/// <c>EMULATOR.EXE</c> EmuDeck installs (closed source: the format string <c>.\STATES\%s%d.sta</c>, and the slot
/// select maps DirectInput keys 1–9, 0 to slots 1–9, 0). The path is relative to the working folder, which both
/// EmuDeck launchers set to the emulator's own (SteamOS: <c>cd $romsPath/model2</c>), so states sit beside
/// <c>NVDATA</c>. Each slot is named exactly: <c>vcop*.sta</c> would also take Virtua Cop 2's <c>vcop20.sta</c>.
/// </para>
/// <para>
/// <b>The trap:</b> <c>Model2_init</c> copies EmuDeck's own <c>configs/model2/NVDATA</c> into that folder — 39
/// files, one per common set — so a <c>.DAT</c> there does not mean the game was played. A file still
/// byte-identical to EmuDeck's copy (<see cref="EmuDeckSeeds"/>) is an untouched seed
/// (<see cref="ScanCandidate.UntouchedSeed"/>): listed only to join a server game that keeps it. Once the game
/// writes its own NVRAM it differs and is a candidate like any other — or as soon as it has a state.
/// </para>
/// </summary>
public static class Model2Saves
{
    public const string EmulatorName = "Model 2";

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(EmuDeckRoots.Find(),
            OperatingSystem.IsWindows() && EmulatorPaths.Standalone ? new[] { WindowsEmuDeckDir() } : Array.Empty<string>(), EmuDeckSeeds);

    /// <param name="emulatorDirs">Model 2 install folders outside an <c>Emulation</c> folder (EmuDeck for
    /// Windows' <c>m2emulator</c>).</param>
    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<string> emuDeckRoots, IEnumerable<string> emulatorDirs,
        IReadOnlySet<string> seeds)
    {
        var roots = emuDeckRoots.ToList();
        var rules = new RomSaveRules(EmulatorName, ".DAT", (set, ext) => new[] { set + ext }, StateGlobs,
            System: "model2", IsSeed: f => IsSeed(f, seeds) && !HasState(f), KnownTitle: set => Titles.GetValueOrDefault(set));
        var folders = roots.Select(r => Path.Combine(r, "roms", "model2"))
            .Concat(emulatorDirs)
            .Select(d => new RomSaveFolders(Path.Combine(d, "NVDATA"), Path.Combine(d, StatesFolder), EmuDeck: true));
        return RomSaves.Scan(rules, RomSaves.Existing(folders), roots, GamelistXml.Find(roots));
    }

    public const string StatesFolder = "STATES";

    /// <summary>One name per slot: the emulator writes <c>&lt;set&gt;&lt;0-9&gt;.sta</c> and nothing else there.</summary>
    public static IReadOnlyList<string> StateGlobs(string set) =>
        Enumerable.Range(0, 10).Select(slot => $"{set}{slot}.sta").ToArray();

    /// <summary>Has the set of <paramref name="nvram"/> any state in the <c>STATES</c> folder beside its
    /// <c>NVDATA</c>?</summary>
    private static bool HasState(FileInfo nvram)
    {
        if (nvram.Directory?.Parent is not { } emulatorDir) return false;
        var states = Path.Combine(emulatorDir.FullName, StatesFolder);
        try { return StateGlobs(Path.GetFileNameWithoutExtension(nvram.Name)).Any(n => File.Exists(Path.Combine(states, n))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string WindowsEmuDeckDir() => Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "m2emulator");

    /// <summary>Is <paramref name="file"/> one of EmuDeck's preinstalled NVRAM files, untouched?</summary>
    public static bool IsSeed(FileInfo file, IReadOnlySet<string> seeds) => RomSaves.IsUntouchedSeed(file, seeds);

    /// <summary>
    /// SHA-256 of each file in EmuDeck's <c>configs/model2/NVDATA</c> — identical in <c>dragoonDorise/EmuDeck</c>
    /// (SteamOS, one version since <c>f3ab8acb99</c>, 2024-01-26) and <c>EmuDeck/emudeck-we</c> (Windows,
    /// <c>d6a240f5c3</c>, 2025-02-03), read 2026-10-07, and the same 39 files found untouched on a real EmuDeck Deck
    /// the same day. A new EmuDeck seed shows up as a candidate until added here.
    /// </summary>
    public static readonly IReadOnlySet<string> EmuDeckSeeds = new HashSet<string>(StringComparer.Ordinal)
    {
        "d268846e3edef089512d6f836cb181797efab14c4531b631646332e476fd3160", // bel.DAT
        "360208504c2c6b7aec3f2f5eaac7651caec60704872688c83c8ce26ecd57d91b", // dayton93.DAT
        "ac6e0de0d7d4a82de1137b070a9c4fda148b3bed1c1967b7103493b110a0dd35", // daytona.DAT
        "8b4d60ec59c5d1649b0f207860c090dff2a4a8215a1e43a6b3c6d0187aa14ca0", // daytonagtx.DAT
        "db205cc3fee86397cdc45fbfff31d8dbec7fdffe1d6c5d8c8e520769f4152614", // daytonam.DAT
        "9302289826f2381fd9284a470bee29ba287c2fcd29ccd220f611f9f17923cf71", // daytonas.DAT
        "631ff64aa262e80b391cbb23bb50dcc48de9bf658e88a10e3719b922c39c239a", // daytonase.DAT
        "1a914e3670f85a225a7dfb501d388c14af0e384230a34095a2b56baac2da94a0", // daytonata.DAT
        "6ffc1363b5af81b70328c37ed7346765767aa5d1929f8db977c1faf4230cb6eb", // desert.DAT
        "c7a65ec85be0657d4bf754856d9faace3461b8708010cffdd6c3764b9ffa57ec", // doa.DAT
        "1b1c71a4198428ef2c58a7fdb14af5807879dd82e0aa2fe8d252498a1639cf84", // dynabb97.DAT
        "b578253dbe13dea357ce6d5df02452f1525c713bbfcb7cbe15bb330e01a70bb3", // dynamcop.DAT
        "05c5977971cf87bb58cd9981b6fd01cac59d6a164398296d009e46593d2aa3bc", // dyndeka2.DAT
        "9e59a71d56f6432ff204be6a0b63ba5ef85ba33e38696d083b4334e54b1712b5", // fvipers.DAT
        "2d45e1bda265c9711915c35bddcdbaf7aa509bf0551916ef3f8d6e38e5265227", // gunblade.DAT
        "cda7666ca73f4e9250fdc77f180f2cbce33203c9fe76d7381dc24e0a5447ebe1", // hotd.DAT
        "3a5e9a6e7cdf038573c3f726ddca9b539a7a8c8e5a88af73ae655969ab5bab8a", // indy500.DAT
        "0a060d210b9467e5cef4a30d50dfc743fa42a52be81d060cd1b30202d241b6da", // lastbrnx.DAT
        "90c10fe61a5eaf261aed7f7f8390f4d2408370df6a3886fbb7121128eb02c36f", // manxttc.DAT
        "8d99dc588c2a0ad3945f40689b8ac280346ebc26dc2effc44fd4d0708d0217d5", // motoraid.DAT
        "4ee5803c3f00fac949624aef4b4a597a45c7a230446b24b72f7bc69a7b51c299", // overrev.DAT
        "7954bd18c8f88337673c249c2c6e65ac446375fd1e86e9be57cc3df9b7047107", // pltkids.DAT
        "217c0b9a16f7bb567f826e68341461b191303c1a96ff9095f5628b142a4ca28a", // rchase2.DAT
        "6f630f468f3dc162db74d3bc59e39adece0138e5d347bbb5c595323f895a29ec", // schamp.DAT
        "067b843871f215ae94d85ae34a68ccbdd7cef8ce012e0f605505ba80d20d1d73", // segawski.DAT
        "4696ef606e7939667e1813cd4b5d2c34f3c4e34a744e4ffa78d241d0a57c9461", // sgt24h.DAT
        "98634a1504b39f80bc01df6b97971f5e714c1d120628806fa336c50f4e1a733c", // skisuprg.DAT
        "d173cd5602a31b871505e2b8ac5b37ec718024cd0fd64861fc25498a49af0301", // skytargt.DAT
        "fe6ea5db4fc41c52a5befe7849e592c3e31f962704d57d3a8973393ec842d7d2", // srallyc.DAT
        "f52894454a59dee127dbaefb457602714ed70a7368d271e2e0f096ed7b8ab2b3", // srallyp.DAT
        "aba55fac413a2cd9170938fffdb0ead430d3ba30068ecdb11863ad58ccd5b57e", // stcc.DAT
        "155dc94016f3a8734938a5edc933e85c4a079f0f50ac55ff631a40d882609ccb", // topskatr.DAT
        "329bff4ecce287582e3df08c68dc2f29efc5b81331da6859b888f9a5bd61d6ed", // vcop.DAT
        "5448eddf582ac0b99803d790ad588cd850fbef040734979e7528292a113b50d1", // vcop2.DAT
        "fa3f1e68a8b67f8f908b2965d3078dab857ac8993094c195bfd715cc3dc78e9e", // vf2.DAT
        "c6069813394a8d732428c6d2099db7144e9f988f54baf780cf3909bab8db7952", // von.DAT
        "3c497f54f62ab830ea942b99cbac5b4a74c3aced00f873523471d92b6dd1e322", // vstriker.DAT
        "667ae7ba4718a76e6f2b038a09be02db4bcca3d41f3aa8e87c53d117b7970d73", // waverunr.DAT
        "3c65f042e6acd9483e5dd5f0263e2956d9a890e07a6b0dd8a38d65566cd0493c", // zerogun.DAT
    };

    /// <summary>
    /// Set name → title: Model 2 Emulator ships no title table. The sets EmuDeck seeds, which are the common ones;
    /// any other set falls back to ES-DE's gamelist, then the set name itself (<see cref="RomNames.TitleFor"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bel"] = "Behind Enemy Lines",
        ["dayton93"] = "Daytona USA '93 Edition",
        ["daytona"] = "Daytona USA",
        ["daytonagtx"] = "Daytona USA GTX",
        ["daytonam"] = "Daytona USA To The MAXX",
        ["daytonas"] = "Daytona USA (Saturn Ads)",
        ["daytonase"] = "Daytona USA Special Edition",
        ["daytonata"] = "Daytona USA Turbo",
        ["desert"] = "Desert Tank",
        ["doa"] = "Dead or Alive",
        ["dynabb97"] = "Dynamite Baseball 97",
        ["dynamcop"] = "Dynamite Cop",
        ["dyndeka2"] = "Dynamite Deka 2",
        ["fvipers"] = "Fighting Vipers",
        ["gunblade"] = "Gunblade NY",
        ["hotd"] = "The House of the Dead",
        ["indy500"] = "Indy 500",
        ["lastbrnx"] = "Last Bronx",
        ["manxttc"] = "Manx TT Superbike",
        ["motoraid"] = "Motor Raid",
        ["overrev"] = "Over Rev",
        ["pltkids"] = "Pilot Kids",
        ["rchase2"] = "Rail Chase 2",
        ["schamp"] = "Sonic Championship",
        ["segawski"] = "Sega Water Ski",
        ["sgt24h"] = "Super GT 24h",
        ["skisuprg"] = "Sega Ski Super G",
        ["skytargt"] = "Sky Target",
        ["srallyc"] = "Sega Rally Championship",
        ["srallyp"] = "Sega Rally Pro Drivers",
        ["stcc"] = "Sega Touring Car Championship",
        ["topskatr"] = "Top Skater",
        ["vcop"] = "Virtua Cop",
        ["vcop2"] = "Virtua Cop 2",
        ["vf2"] = "Virtua Fighter 2",
        ["von"] = "Cyber Troopers Virtual-On",
        ["vstriker"] = "Virtua Striker",
        ["waverunr"] = "Wave Runner",
        ["zerogun"] = "Zero Gunner",
    };
}
