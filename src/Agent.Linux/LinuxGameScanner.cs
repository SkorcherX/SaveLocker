using SaveLocker.Shared;

namespace SaveLocker.Agent.Linux;

/// <summary>
/// Discovers non-Steam Steam shortcuts, installed Steam games and Heroic games, and where possible
/// their Proton/Wine save directories.
///
/// <para>
/// Installed Steam games are discovered but <b>flagged</b> <c>HasSteamCloud</c>, not enrolled by
/// default — the same shape the Windows scanner has always had. They were originally left out
/// entirely (Decisions.md, "Linux discovery"), on the reasoning that Steam Cloud already covers
/// them. That reasoning holds for the default view and nothing else: not every Steam title opts
/// into Cloud, and a user who wants a local copy of one had no way to reach it, because the UI
/// filters what the scan returns and the scan returned nothing to filter.
/// </para>
///
/// <para>
/// The flag itself is the manifest's answer, not the storefront's: most Steam titles have no Steam
/// Cloud (<see cref="Detection.HasSteamCloudAsync"/>), so flagging on "Steam installed it" hid
/// thousands of games that nothing else was backing up.
/// </para>
/// </summary>
public sealed class LinuxGameScanner : IGameScanner
{
    private readonly Detection _detection;

    public LinuxGameScanner(Detection detection) => _detection = detection;

    public async Task<IReadOnlyList<ScanCandidate>> ScanAsync(CancellationToken ct = default)
    {
        // Paired with a flag for the final dedupe below: true only when a SteamShortcut candidate
        // resolved through the MoonDeck fallback (a DIFFERENT game's prefix, not its own) rather than
        // its own compatdata or a portable folder. Local to this method on purpose — nothing
        // downstream of the dedupe needs to know how a survivor was found, only that it was.
        var results = new List<(ScanCandidate Candidate, bool ViaMoonDeck)>();

        foreach (var root in SteamRoots.Find())
        {
            // Steam's own record of what is installed where, across every configured library —
            // mounted or not (SteamRoots.AllKnownInstalledAppIds). A MoonDeck shortcut whose real
            // AppID appears here is not a "non-Steam shortcut" at all; it is a genuine Steam-Store
            // install that merely cannot be read from directly right now, because its library's
            // media is not currently connected. Computed once per root, reused for every shortcut.
            var knownInstalled = SteamRoots.AllKnownInstalledAppIds(root);

            foreach (var s in await SteamShortcuts.ReadAllAsync(root, ct))
            {
                ct.ThrowIfCancellationRequested();
                var prefix = s.AppId is null ? null : SteamRoots.CompatDataPath(root, s.AppId);
                var (save, usedPrefix) = await SuggestSaveDirAsync(s, root, prefix, ct);

                // A resolved compatdata prefix alone can never make this distinction: Steam does not
                // clean up compatdata on uninstall, so a real prefix full of real save data proves
                // nothing about whether the game is STILL installed anywhere. The apps block does.
                var realAppId = s.MoonDeckAppId;
                var confirmedInstalled = realAppId is not null && knownInstalled.Contains(realAppId);

                results.Add((new ScanCandidate(
                    Name: s.AppName,
                    SuggestedSaveDir: save,
                    Source: confirmedInstalled ? ScanSource.SteamInstalled : ScanSource.SteamShortcut,
                    // A confirmed install gets the manifest's real answer, exactly like any other
                    // installed game — not the shortcut default, which exists only because a
                    // non-Steam shortcut is never Steam Cloud-eligible in the first place.
                    HasSteamCloud: confirmedInstalled
                        ? await _detection.HasSteamCloudAsync(s.AppName, ct) ?? true
                        : false,
                    ManifestKey: save is null
                        ? null
                        : await _detection.CanonicalNameAsync(s.AppName, ct) ?? s.AppName,
                    // s.StartDir is the shortcut's own launch directory — for a MoonDeck shortcut
                    // that is MoonDeck's script folder, not the game's, and would be actively
                    // misleading attached to a candidate now presented as genuinely installed. We do
                    // not know the real install directory without the ACF, which is exactly what is
                    // unreachable here — admitted, not guessed at.
                    InstallDir: confirmedInstalled ? null : s.StartDir,
                    // The real AppID, not the shortcut's synthetic one, once confirmed — it is what
                    // the launch wrapper needs to match a genuine LOCAL Steam+Proton launch, the only
                    // kind it can ever hook (a MoonDeck-streamed session cannot be, regardless of
                    // which AppID is recorded).
                    SteamAppId: confirmedInstalled ? realAppId : s.AppId,
                    // The prefix that actually produced the save dir, for the path browser — a
                    // MoonDeck shortcut resolves via a DIFFERENT prefix than its own (below), and
                    // opening the browser at the dead one would strand the user at an empty prefix.
                    PrefixPath: usedPrefix ?? prefix,
                    // Carried for parity, and because config is shared between hosts — but Linux
                    // drives lifecycle from the launch wrapper, not from process polling, so
                    // nothing here depends on it (Decisions.md §3). Null once confirmedInstalled for
                    // the same reason every other SteamInstalled candidate carries null: a script's
                    // name is not the game's process name any more than a folder's is.
                    SuggestedProcessName: confirmedInstalled
                        ? null
                        : GameActivity.ProcessNameFromExe(s.Exe)),
                    // Still tracked even once reclassified: if the real ACF ever becomes reachable
                    // too (the library gets mounted), that direct read is more authoritative than
                    // this inferred stand-in, and should keep winning the dedupe below.
                    ViaMoonDeck: usedPrefix is not null && usedPrefix != prefix));
            }

            results.AddRange((await ScanInstalledSteamGamesAsync(root, ct)).Select(c => (c, false)));
        }

        results.AddRange((await ScanHeroicAsync(ct)).Select(c => (c, false)));

        // Normalised, for the same reason as the Windows scanner: one game, one row, however the
        // shortcut happens to be spelled.
        //
        // This also collapses the Heroic double-listing: "Add to Steam" gives a Heroic game a real
        // shortcuts.vdf entry, so it is discovered twice. Preferring the row that HAS a save dir
        // picks the Heroic one — the shortcut's copy cannot resolve, because Steam never made a
        // compatdata prefix for a game it does not launch.
        return results
            .GroupBy(r => ManifestLoader.NormalizeName(r.Candidate.Name), StringComparer.Ordinal)
            .Select(g => g
                .OrderByDescending(r => r.Candidate.SuggestedSaveDir is not null)
                // A MoonDeck shortcut only ever resolves by pointing at ANOTHER install's real
                // prefix — it is never itself a local install — so it loses to a genuine one before
                // the Cloud tie-break below even applies. Without this, a MoonDeck shortcut for a
                // game that is ALSO genuinely installed with real Steam Cloud would win the Cloud
                // tie-break purely because a SteamShortcut candidate is unconditionally
                // HasSteamCloud: false, handing the survivor the streaming pointer's own AppID —
                // one the launch wrapper only ever sees during a MoonDeck session, never a real one.
                .ThenBy(r => r.ViaMoonDeck)
                // A tie goes to the copy Steam does NOT back up. The same game can be both an
                // installed Steam title and a shortcut the user made to a DRM-free build; enrolling
                // the one that already has Cloud is the less useful of the two.
                .ThenBy(r => r.Candidate.HasSteamCloud)
                .First().Candidate)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Steam appids that appear as installed "apps" but are not games. A backstop only — see
    /// <see cref="IsCompatTool"/> for the check that actually carries this.
    /// </summary>
    private static readonly HashSet<string> NonGameAppIds = new(StringComparer.Ordinal)
    {
        "228980",   // Steamworks Common Redistributables — no toolmanifest, so only this catches it
    };

    /// <summary>
    /// Is this installed app a Steam compatibility tool (Proton, the Linux runtimes) rather than a
    /// game? Decided by the <c>toolmanifest.vdf</c> Steam itself requires in a compat tool's install
    /// directory, not by a list of appids.
    /// <para>
    /// The list came first and was wrong on real hardware within one release: Valve ships a new
    /// Proton every year and each gets a NEW appid, so any enumeration is stale the day it is
    /// written — a Deck listed Proton 9/10/11, Proton Hotfix and Steam Linux Runtime 4.0 as
    /// enrollable games. The marker file is version-independent and is what Steam keys on too.
    /// </para>
    /// </summary>
    private static bool IsCompatTool(string? installPath) =>
        installPath is not null && File.Exists(Path.Combine(installPath, "toolmanifest.vdf"));

    /// <summary>
    /// Games installed from the Steam store, read from <c>appmanifest_*.acf</c> in every library.
    ///
    /// <para>
    /// A Proton game's saves live in its own prefix, and for an installed title that prefix is
    /// normally <c>compatdata/&lt;appid&gt;</c> in the library the game is installed in — not in the
    /// main Steam root, which is where a non-Steam shortcut's prefix always goes. A game on an SD
    /// card therefore resolves only if the library and the prefix are walked together, which is why
    /// this iterates <see cref="SteamRoots.LibraryPaths"/> rather than reusing the caller's root.
    /// </para>
    ///
    /// <para>
    /// "Normally", not always: moving a game's install location (most commonly internal storage to
    /// an SD card, found on a real Deck 2026-08-19 — Borderlands 2) does not always take a USABLE
    /// prefix with it. Steam can spin up a fresh one at the new location whose special folders were
    /// auto-created with different casing than an existing manifest path expects — Borderlands 2's
    /// SD-card prefix has <c>My games</c>, the manifest wants <c>My Games</c>, and Linux is
    /// case-sensitive — while the ORIGINAL prefix, still sitting in the main root from before the
    /// move, has the real save history and resolves fine. The fallback is therefore tried whenever
    /// the current library's prefix resolves NOTHING, not only when it is missing outright — a
    /// present-but-unusable prefix and an absent one look identical from here. And some templates
    /// (Steam Cloud's own sync folder, <c>&lt;root&gt;/userdata/…</c> — Left 4 Dead 2 on the same
    /// Deck) live under the Steam root itself and need no prefix to exist at all, which is why
    /// resolution is attempted regardless of whether either prefix is found.
    /// </para>
    ///
    /// <para>
    /// A title that resolves nothing at all is a native-Linux build (Decisions.md §1: out of scope)
    /// or one that has never actually been launched under Proton. It is still listed, with no save
    /// folder — the user can set one, and the alternative is a game that is plainly installed
    /// silently missing from the list.
    /// </para>
    /// </summary>
    private async Task<List<ScanCandidate>> ScanInstalledSteamGamesAsync(
        string steamRoot, CancellationToken ct)
    {
        var results = new List<ScanCandidate>();

        foreach (var library in SteamRoots.LibraryPaths(steamRoot))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var acf in SafeEnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                ct.ThrowIfCancellationRequested();

                var state = SteamRoots.ParseVdf(acf)?.Object("AppState");
                var name = state?.String("name")?.Trim();
                var appId = state?.String("appid");
                if (string.IsNullOrWhiteSpace(name) || appId is null) continue;
                if (NonGameAppIds.Contains(appId)) continue;

                var installDir = state?.String("installdir");
                var installPath = installDir is null
                    ? null
                    : NullIfMissing(Path.Combine(steamapps, "common", installDir));
                if (IsCompatTool(installPath)) continue;

                // <root> is built from steamRoot directly everywhere below, NOT derived from
                // whichever prefix is tried (Detection.ResolveProtonAsync's usual
                // SteamLayout.RootFromCompatData) — Steam Cloud's own sync folder
                // (<root>/userdata/<storeUserId>/<appid>/remote) lives under userdata, which exists
                // exactly once, at the real Steam client root, regardless of which library holds the
                // game's files, its compatdata, or whether a prefix exists at all. Deriving <root>
                // from a secondary library's prefix path would silently point it at a library with no
                // userdata folder at all.
                var prefix = Path.Combine(steamapps, "compatdata", appId);
                var save = await ResolveInstalledAsync(name, prefix, installPath, steamRoot, ct);

                if (save is null && !string.Equals(steamRoot, library, StringComparison.Ordinal))
                {
                    var mainRootPrefix = Path.Combine(steamRoot, "steamapps", "compatdata", appId);
                    var fallbackSave = await ResolveInstalledAsync(
                        name, mainRootPrefix, installPath, steamRoot, ct);
                    if (fallbackSave is not null) (save, prefix) = (fallbackSave, mainRootPrefix);
                }

                results.Add(new ScanCandidate(
                    Name: name,
                    SuggestedSaveDir: save,
                    Source: ScanSource.SteamInstalled,
                    // Flagged, not hidden here. The UI's default view is what hides them; the scan
                    // must still return them or no filter can bring them back.
                    //
                    // WHICH ones get flagged comes from the manifest, not from the fact that Steam
                    // installed them — see Detection.HasSteamCloudAsync. A title the manifest does
                    // not know keeps the old assumption.
                    HasSteamCloud: await _detection.HasSteamCloudAsync(name, ct) ?? true,
                    ManifestKey: save is null
                        ? null
                        : await _detection.CanonicalNameAsync(name, ct) ?? name,
                    InstallDir: installPath,
                    SteamAppId: appId,
                    PrefixPath: Directory.Exists(prefix) ? prefix : null,
                    // The launch wrapper matches on the AppID Steam hands it, so process polling is
                    // as irrelevant here as it is for a shortcut (Decisions.md §3).
                    SuggestedProcessName: null,
                    Store: GameStore.Steam));
            }
        }

        return results;
    }

    /// <summary>
    /// Try one compatdata prefix (which need not exist — see below) for an installed Steam game.
    /// Shared so the main-root fallback in <see cref="ScanInstalledSteamGamesAsync"/> is a second
    /// call with a different prefix, not a second copy of the resolution call.
    /// <para>
    /// <paramref name="storeRoot"/> is always the caller's verified Steam root, never derived from
    /// <paramref name="prefix"/> — see the caller for why. <see cref="PathResolver.Proton"/> only
    /// builds strings; a prefix that does not exist just yields tokens that fail their own
    /// <c>Directory.Exists</c> check inside <see cref="ManifestLoader.ResolveSaveDirectories"/>,
    /// which is what lets a <c>&lt;root&gt;</c>-anchored template (Steam Cloud's own sync folder)
    /// resolve even when there is no prefix anywhere at all.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveInstalledAsync(
        string name, string prefix, string? installPath, string storeRoot, CancellationToken ct)
    {
        var resolver = PathResolver.Proton(prefix, installPath, storeRoot: storeRoot);
        return (await _detection.ResolveSaveDirectoriesAsync(name, resolver, ct)).FirstOrDefault();
    }

    /// <summary>
    /// Enumerate lazily without letting one unreadable library cost the rest of the scan.
    /// <see cref="Directory.EnumerateFiles(string, string)"/> fails on the <c>MoveNext</c> that
    /// reaches the bad entry, not at the call, so a try around the call site catches nothing — an
    /// SD card pulled mid-scan is exactly this case on a Deck.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        IEnumerator<string> e;
        try { e = Directory.EnumerateFiles(dir, pattern).GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        try
        {
            while (true)
            {
                string current;
                try
                {
                    if (!e.MoveNext()) yield break;
                    current = e.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }
                yield return current;
            }
        }
        finally { e.Dispose(); }
    }

    private static string? NullIfMissing(string? dir) =>
        !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? Path.GetFullPath(dir) : null;

    /// <summary>
    /// Games staged in Heroic Games Launcher.
    ///
    /// <para>
    /// These are worth a second discovery pass rather than being left to the shortcut scan above,
    /// even though most of them also appear in <c>shortcuts.vdf</c> via Heroic's "Add to Steam".
    /// The shortcut carries an AppID, and an AppID names a <c>compatdata</c> prefix — but Heroic
    /// does not launch through Steam, so that prefix does not exist and the game arrives with no
    /// save folder every time. The prefix that does exist is Heroic's, and only Heroic's config
    /// knows where it is.
    /// </para>
    ///
    /// <para>
    /// No Steam Cloud here by definition: Heroic installs Epic, GOG, Amazon and sideloaded games.
    /// </para>
    /// </summary>
    private async Task<List<ScanCandidate>> ScanHeroicAsync(CancellationToken ct)
    {
        var results = new List<ScanCandidate>();

        foreach (var configRoot in HeroicRoots.Find())
        {
            foreach (var game in HeroicLibrary.Read(configRoot))
            {
                ct.ThrowIfCancellationRequested();

                var prefix = HeroicRoots.PrefixFor(configRoot, game.AppName);
                var save = await HeroicSaveDirAsync(game, prefix, ct);

                results.Add(new ScanCandidate(
                    Name: game.Title,
                    SuggestedSaveDir: save,
                    Source: ScanSource.Heroic,
                    HasSteamCloud: false,
                    ManifestKey: save is null
                        ? null
                        : await _detection.CanonicalNameAsync(game.Title, ct) ?? game.Title,
                    InstallDir: game.InstallPath,
                    // Deliberately null. Heroic's app_name is not a Steam AppID, and the Deck's
                    // launch wrapper matches on the AppID Steam passes it — writing Heroic's id
                    // here would make a tracked game the wrapper can never fire for look correct.
                    SteamAppId: null,
                    PrefixPath: prefix,
                    SuggestedProcessName: null,
                    Store: StoreForRunner(game.Runner)));
            }
        }

        return results;
    }

    /// <summary>
    /// Heroic names the store by its CLI tool, not by the storefront users know it as. Unrecognised
    /// runners map to <see cref="GameStore.Unknown"/> rather than being guessed at — a new Heroic
    /// runner must not land in the wrong store filter.
    /// </summary>
    private static GameStore StoreForRunner(string? runner) => runner?.ToLowerInvariant() switch
    {
        "legendary" => GameStore.Epic,
        "gog" or "gogdl" => GameStore.Gog,
        "nile" => GameStore.Amazon,
        "sideload" => GameStore.Sideload,
        _ => GameStore.Unknown,
    };

    /// <summary>
    /// Best guess at a Heroic game's save directory: inside its Wine prefix per the manifest, else
    /// portable, beside the executable. The same two shapes as a Steam shortcut, resolved through
    /// Heroic's prefix instead of Steam's — and for a sideloaded GOG game the portable case is the
    /// likelier of the two.
    /// </summary>
    private async Task<string?> HeroicSaveDirAsync(
        HeroicGame game, string? prefix, CancellationToken ct)
    {
        // ONLY the configured prefix. Heroic launches the game in whatever `winePrefix` says, so
        // that is where the game writes — even when the game's own files are somewhere else.
        //
        // Falling back to the prefix the game is INSTALLED in is deliberately not done. The two
        // disagree when a prefix has been renamed on disk without updating Heroic's config: Heroic
        // then creates a fresh empty prefix at the configured path and runs the game there, so the
        // install-side prefix still holds a full set of real save files that nothing writes to any
        // more. Resolving to it would hand back a wrong path that looks more convincing than the
        // right one — see Doctor, which reports the disagreement instead of guessing past it.
        if (prefix is not null)
        {
            var dirs = await _detection.ResolveWineAsync(
                game.Title, prefix, installDir: game.InstallPath, ct);
            if (dirs.FirstOrDefault() is { } inPrefix) return inPrefix;
        }

        return PortableSaveDir(game.InstallPath);
    }

    /// <summary>
    /// Best guess at a shortcut's save directory. Three shapes exist and all are real:
    ///
    /// 1. <b>In-prefix</b> — the game writes to Windows paths inside its Wine prefix. The Ludusavi
    ///    manifest's tokens resolve there, via a resolver built for THIS game's prefix.
    /// 2. <b>MoonDeck-streamed</b> — the shortcut's own <c>Exe</c> launches MoonDeck's streaming
    ///    client, not the game, so Steam never creates a compatdata prefix for it. When the same
    ///    title is ALSO installed locally under Proton, <c>LaunchOptions</c> names the real AppID
    ///    (<see cref="SteamShortcut.MoonDeckAppId"/>) and that prefix is where local saves live.
    /// 3. <b>Portable</b> — the game writes next to its own .exe. That is a plain Linux path on the
    ///    native filesystem and needs no prefix resolution at all. Common for the standalone builds
    ///    this feature exists for, so it is checked even when a prefix exists.
    ///
    /// Null when none resolves, which on Linux is the expected case rather than a failure: most
    /// standalone builds are absent from the manifest, so <c>add-game --dir</c> is the primary path.
    /// </summary>
    private async Task<(string? Save, string? UsedPrefix)> SuggestSaveDirAsync(
        SteamShortcut shortcut, string steamRoot, string? compatDataPath, CancellationToken ct)
    {
        if (compatDataPath is not null)
        {
            // StartDir is the game's install directory, which is exactly what <base> means — the
            // most common placeholder in the manifest by a wide margin. Passing it is most of the
            // reason a shortcut now resolves at all.
            var dirs = await _detection.ResolveProtonAsync(
                shortcut.AppName, compatDataPath, installDir: shortcut.StartDir, ct);
            if (dirs.FirstOrDefault() is { } inPrefix) return (inPrefix, compatDataPath);
        }

        if (shortcut.MoonDeckAppId is { } moonDeckAppId &&
            SteamRoots.CompatDataPath(steamRoot, moonDeckAppId) is { } moonDeckPrefix)
        {
            // installDir is deliberately NOT shortcut.StartDir here: StartDir is MoonDeck's own
            // streaming-client script directory, not the game's — passing it as <base> would
            // resolve template paths into a directory that has nothing to do with the game.
            var dirs = await _detection.ResolveProtonAsync(shortcut.AppName, moonDeckPrefix, installDir: null, ct);
            if (dirs.FirstOrDefault() is { } inPrefix) return (inPrefix, moonDeckPrefix);
        }

        return (PortableSaveDir(shortcut), null);
    }

    /// <summary>
    /// A save folder sitting beside the game's executable. We only claim one when it is
    /// unambiguous — a single conventionally-named directory — because guessing wrong here points
    /// the archiver at the wrong tree, and the user can always say exactly what they mean with
    /// <c>--dir</c>.
    /// </summary>
    private static string? PortableSaveDir(SteamShortcut shortcut) =>
        PortableSaveDir(shortcut.StartDir
                        ?? (shortcut.Exe is { } exe ? Path.GetDirectoryName(exe) : null));

    /// <inheritdoc cref="PortableSaveDir(SteamShortcut)"/>
    private static string? PortableSaveDir(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return null;

        string[] names = ["Saves", "Save", "SaveGames", "savegames", "saves", "save"];
        var hits = names
            .Select(n => Path.Combine(installDir, n))
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return hits.Count == 1 ? Path.GetFullPath(hits[0]) : null;
    }
}
