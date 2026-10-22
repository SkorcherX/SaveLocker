using Microsoft.Win32;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Agent-side game discovery. Aggregates several local sources into a list of
/// enrollment candidates (see <c>Game Discovery and Art.md</c>):
/// non-Steam Steam shortcuts, installed Steam games, and a save-root heuristic
/// matched against the Ludusavi manifest. Windows-only (registry + known folders).
/// </summary>
public sealed class GameScanner : IGameScanner
{
    private readonly Detection _detection;

    /// <summary>
    /// Steam appids that appear as installed "apps" but aren't games. A backstop only — the load-
    /// bearing check is <see cref="IsCompatTool"/>. An appid list cannot be maintained: Valve ships
    /// new runtimes with new ids, and this one had already fallen behind by three.
    /// </summary>
    private static readonly HashSet<string> NonGameAppIds = new()
    {
        "228980", // Steamworks Common Redistributables — ships no toolmanifest, so only this catches it
    };

    /// <summary>
    /// A Steam compatibility tool (Proton, the Linux runtimes), identified by the
    /// <c>toolmanifest.vdf</c> Steam requires in its install directory rather than by appid.
    /// </summary>
    private static bool IsCompatTool(string? installPath) =>
        installPath is not null && SafeFileExists(Path.Combine(installPath, "toolmanifest.vdf"));

    public GameScanner(Detection detection) => _detection = detection;

    /// <summary>
    /// Run every source and return de-duplicated candidates, sorted by name.
    ///
    /// <para>
    /// Every source is isolated. Discovery reads places the agent does not control — a Steam library
    /// on a drive that can be unplugged mid-enumeration, a userdata directory another account owns, a
    /// manifest download that fails offline — and any one of those used to throw out of the whole
    /// scan. The user saw a failed rescan with no candidates at all, which also blocks the manual
    /// setup path they would otherwise use to work around it. A source that fails is logged and
    /// skipped; the healthy ones still answer. WA-11.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ScanCandidate>> ScanAsync(CancellationToken ct = default)
    {
        var all = new List<ScanCandidate>();
        var steamPath = FindSteamPath();

        if (steamPath is not null)
        {
            all.AddRange(await SafeSourceAsync("Steam shortcuts",
                () => ScanSteamShortcutsAsync(steamPath, ct), ct));
            all.AddRange(await SafeSourceAsync("installed Steam games",
                () => ScanInstalledSteamGamesAsync(steamPath, ct), ct));
        }

        all.AddRange(await SafeSourceAsync("common save roots", () => ScanSaveRootsAsync(ct), ct));

        // De-dupe by name: prefer a candidate that already has a suggested save dir.
        // Grouped on the NORMALISED name: the same game reaches us spelled differently by
        // different sources — a Steam shortcut says "DRAGON QUEST III HD 2D Remake", the save-root
        // heuristic reads "DRAGON QUEST III HD-2D Remake" off disk — and grouping on the raw name
        // listed it twice, once with a path and once without.
        return all
            .GroupBy(c => ManifestLoader.NormalizeName(c.Name), StringComparer.Ordinal)
            .Select(MergeGroup)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ScanCandidate MergeGroup(IEnumerable<ScanCandidate> group)
    {
        var ordered = group.OrderByDescending(c => c.SuggestedSaveDir is not null).ToList();
        var winner = ordered[0];
        if (winner.DuplicateShortcuts is not null) return winner;
        // A common-save-root candidate with the same name can win the merge, and the shortcut is the
        // only source that knows the ids. No entry is tracked then: the scan follows the winner's
        // save folder, not either shortcut, and a marker would name an AppID the tracked game lacks.
        var duplicates = ordered.Select(c => c.DuplicateShortcuts).FirstOrDefault(d => d is not null)
            ?.Select(d => d with { Tracked = false }).ToList();
        return duplicates is null ? winner : winner with { DuplicateShortcuts = duplicates };
    }

    // ----- Steam location -----

    /// <summary>
    /// Locate the Steam install via the registry; null if Steam isn't installed — or if the keys
    /// cannot be read, which on a managed machine is a policy restriction rather than an absence.
    /// Both keys are disposed deterministically; they used to be opened inline and left to the
    /// finalizer, holding a native registry handle for however long that took.
    /// </summary>
    public static string? FindSteamPath()
    {
        // HKCU is set per-user when Steam runs; HKLM is the machine install path.
        var hkcu = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrEmpty(hkcu) && SafeDirectoryExists(hkcu))
            return Path.GetFullPath(hkcu);

        var hklm = ReadRegistryString(
            Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrEmpty(hklm) && SafeDirectoryExists(hklm))
            return Path.GetFullPath(hklm);

        return null;
    }

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (IsAccessOrIo(ex))
        {
            AgentLogger.Log($"Scan: could not read {hive.Name}\\{subKey} ({ex.GetType().Name}: {ex.Message}). Skipping this Steam location.");
            return null;
        }
    }

    // ----- Source 1: non-Steam shortcuts (shortcuts.vdf, binary) -----

    private async Task<IReadOnlyList<ScanCandidate>> ScanSteamShortcutsAsync(
        string steamPath, CancellationToken ct)
    {
        var results = new List<ScanCandidate>();
        var shortcuts = await SteamShortcuts.ReadAllAsync(steamPath, ct);

        // Same-named shortcuts under different AppIDs never collapse inside this source (they are
        // one row only after ScanAsync's dedupe), so each shortcut's candidate carries the group.
        var duplicateShortcuts = new Dictionary<string, IReadOnlyList<SteamShortcut>>(StringComparer.Ordinal);
        foreach (var g in SteamShortcuts.DuplicateNameGroups(shortcuts))
            duplicateShortcuts[ManifestLoader.NormalizeName(g.Name)] = g.Entries;

        foreach (var s in shortcuts)
        {
            var (save, also) = await SuggestSaveDirAsync(s.AppName, ct, NullIfMissing(s.StartDir), steamPath);
            results.Add(new ScanCandidate(
                s.AppName, save, ScanSource.SteamShortcut, AlternateSaveDirs: also,
                HasSteamCloud: false, ManifestKey: await CanonicalKeyAsync(s.AppName, save, ct),
                InstallDir: NullIfMissing(s.StartDir),
                SteamAppId: s.AppId,
                // Steam recorded the exact executable the user picked, so this is the one source
                // where the process name is known rather than guessed. It was being discarded,
                // which is why every GUI-enrolled game had an empty watcher mapping. WA-08.
                SuggestedProcessName: GameActivity.ProcessNameFromExe(s.Exe),
                DuplicateShortcuts: duplicateShortcuts.TryGetValue(ManifestLoader.NormalizeName(s.AppName), out var sameName)
                    ? SteamShortcuts.DuplicatesFor(sameName, s.AppId)
                    : null));
        }
        return results;
    }

    // ----- Source 2: installed Steam games (libraryfolders.vdf + *.acf, text) -----

    private async Task<IReadOnlyList<ScanCandidate>> ScanInstalledSteamGamesAsync(
        string steamPath, CancellationToken ct)
    {
        var results = new List<ScanCandidate>();
        foreach (var library in SteamLibraryPaths(steamPath))
        {
            // The library this game lives under is what <root> means for it — which is NOT always
            // steamPath, since a game can sit in a library on another drive entirely.
            var libraryRoot = library;
            var steamapps = Path.Combine(library, "steamapps");
            if (!SafeDirectoryExists(steamapps)) continue;

            // Guarded rather than wrapped in a try: EnumerateFiles fails LAZILY, so a library on a
            // drive unplugged mid-scan throws from inside the loop, not at the call. The entries
            // already read are still good.
            foreach (var acf in SafeEnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                if (SafeReadAllText(acf) is not { } acfText) continue;

                SteamVdf.VdfObject root;
                try { root = SteamTextVdf.Parse(acfText); }
                catch (InvalidDataException) { continue; }

                var state = root.Object("AppState");
                var name = state?.String("name");
                var installDir = state?.String("installdir");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (state?.String("appid") is { } appid && NonGameAppIds.Contains(appid)) continue;

                var installPath = installDir is null
                    ? null
                    : NullIfMissing(SafeCombine(steamapps, "common", installDir));
                if (IsCompatTool(installPath)) continue;

                // The manifest is consulted here now. It never used to be: this source built every
                // candidate with SuggestedSaveDir and ManifestKey null while holding installPath in
                // a local one line above — so an installed Steam game ALWAYS arrived with no save
                // folder and the user was sent to type one by hand, even for the thousands of
                // titles the manifest describes exactly.
                var trimmed = name.Trim();
                var (save, also) = await SuggestSaveDirAsync(trimmed, ct, installPath, libraryRoot);

                // Flagged rather than hidden here — the UI's default view is what hides them, and
                // the scan must still return them or no filter could bring them back.
                //
                // Which titles get flagged is READ from the manifest, not assumed. This was
                // hardcoded true for anything installed through Steam, on the reasoning that Steam
                // titles usually have Cloud; they usually do not (Detection.HasSteamCloudAsync), and
                // a wrong "yes" hides a game the user owns behind a filter they never chose. A title
                // absent from the manifest keeps the old assumption: no data is not evidence of no
                // Cloud, and that case is unchanged from before.
                results.Add(new ScanCandidate(
                    trimmed, save, ScanSource.SteamInstalled, AlternateSaveDirs: also,
                    HasSteamCloud: await _detection.HasSteamCloudAsync(trimmed, ct) ?? true,
                    ManifestKey: await CanonicalKeyAsync(trimmed, save, ct),
                    InstallDir: installPath, Store: GameStore.Steam));
            }
        }
        return results;
    }

    /// <summary>
    /// Extra roots the agent UI's path browser may descend into, beyond the user's profile.
    /// Steam libraries are the case that matters: a game installed to <c>D:\SteamLibrary</c> keeps
    /// its portable saves there, and no amount of probing under the profile will reach them.
    /// </summary>
    public static IEnumerable<string> BrowseRoots()
    {
        var steamPath = FindSteamPath();
        return steamPath is null ? Array.Empty<string>() : SteamLibraryPaths(steamPath);
    }

    /// <summary>All Steam library roots: the install itself plus libraryfolders.vdf entries.</summary>
    private static IEnumerable<string> SteamLibraryPaths(string steamPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        yield return steamPath;

        // Newer Steam stores this under steamapps\; older builds used config\.
        var candidates = new[]
        {
            Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamPath, "config", "libraryfolders.vdf"),
        };
        var file = candidates.FirstOrDefault(SafeFileExists);
        if (file is null) yield break;

        if (SafeReadAllText(file) is not { } text) yield break;

        SteamVdf.VdfObject root;
        try { root = SteamTextVdf.Parse(text); }
        catch (InvalidDataException) { yield break; }

        var folders = root.Object("libraryfolders");
        if (folders is null) yield break;

        foreach (var lib in folders.Children)
        {
            var path = lib.String("path");
            // A library on a drive that is not currently attached is skipped, not fatal — the same
            // reason SafeDirectoryExists exists at all: Directory.Exists throws on some failures
            // (a disconnected UNC share) rather than returning false.
            if (!string.IsNullOrEmpty(path) && SafeDirectoryExists(path) && seen.Add(path))
                yield return Path.GetFullPath(path);
        }
    }

    // ----- Source 3: common save roots matched against the manifest -----

    private async Task<IReadOnlyList<ScanCandidate>> ScanSaveRootsAsync(CancellationToken ct)
    {
        var results = new List<ScanCandidate>();
        var manifest = await _detection.GetManifestAsync(ct: ct);
        var manifestNames = new HashSet<string>(manifest.GameNames, StringComparer.OrdinalIgnoreCase);

        foreach (var root in CommonSaveRoots())
        {
            if (!SafeDirectoryExists(root)) continue;
            // One unreadable root — a redirected Documents folder, an OneDrive placeholder that
            // cannot be materialised offline — must not cost the user the other five.
            foreach (var dir in SafeEnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(dir);
                if (manifestNames.Contains(folderName))
                    results.Add(new ScanCandidate(
                        folderName, Path.GetFullPath(dir), ScanSource.SaveRoot,
                        HasSteamCloud: false,
                        ManifestKey: manifest.CanonicalName(folderName) ?? folderName));
            }
        }
        return results;
    }

    private static IEnumerable<string> CommonSaveRoots()
    {
        string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";

        return new[]
        {
            Special(Environment.SpecialFolder.ApplicationData),       // Roaming
            Special(Environment.SpecialFolder.LocalApplicationData),  // Local
            Path.Combine(home, "AppData", "LocalLow"),
            Path.Combine(Special(Environment.SpecialFolder.MyDocuments), "My Games"),
            Special(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Saved Games"),
        };
    }

    // ----- Helpers -----

    /// <summary>
    /// Resolve the first existing save dir the manifest knows for a name, and the other locations
    /// that exist too — offered as "Also found", never adopted on their own (multiple-save-paths §8).
    /// <para>
    /// <paramref name="installDir"/> and <paramref name="storeRoot"/> are what the manifest calls
    /// <c>&lt;base&gt;</c> and <c>&lt;root&gt;</c>. They are optional because not every discovery
    /// source knows them, but supplying them is what lets the majority of manifest entries resolve
    /// at all — <c>&lt;base&gt;</c> alone appears in more save paths than every other placeholder
    /// combined.
    /// </para>
    /// </summary>
    private async Task<(string? Save, IReadOnlyList<DeclaredSavePath>? Also)> SuggestSaveDirAsync(
        string name, CancellationToken ct, string? installDir = null, string? storeRoot = null)
    {
        var locations = await _detection.ResolveWindowsLocationsAsync(name, installDir, storeRoot, ct);
        var save = locations.FirstOrDefault()?.Directory;
        return (save, FolderSuggestions.AlsoFound(locations, save));
    }

    /// <summary>
    /// The manifest's own spelling for a discovered name, or null when we did not match the manifest.
    /// Enrollment creates the server-side game under this, so two machines that spell the same
    /// shortcut differently still converge on one game.
    /// </summary>
    private async Task<string?> CanonicalKeyAsync(string name, string? save, CancellationToken ct)
    {
        if (save is null) return null;
        return await _detection.CanonicalNameAsync(name, ct) ?? name;
    }

    private static string? NullIfMissing(string? dir) =>
        !string.IsNullOrEmpty(dir) && SafeDirectoryExists(dir) ? Path.GetFullPath(dir) : null;

    // ----- Failure isolation (WA-11) -----

    /// <summary>
    /// The failures a discovery source is expected to hit and must survive: a directory another
    /// account owns, a drive that went away, a path the OS will not accept. Anything else —
    /// <see cref="OperationCanceledException"/> above all — is a real fault and propagates.
    /// </summary>
    private static bool IsAccessOrIo(Exception ex) =>
        ex is UnauthorizedAccessException
           or IOException
           or System.Security.SecurityException
           or ArgumentException          // an invalid path from a VDF or the registry
           or NotSupportedException;     // a path form this platform will not open

    /// <summary>Run one discovery source; log and skip it if it fails, keeping the others.</summary>
    private static async Task<IReadOnlyList<ScanCandidate>> SafeSourceAsync(
        string what, Func<Task<IReadOnlyList<ScanCandidate>>> source, CancellationToken ct)
    {
        try { return await source(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Deliberately broader than IsAccessOrIo: a source is a whole subsystem — the manifest
            // download here is network code — and no failure inside one is worth losing the rest of
            // the scan and the manual setup path along with it.
            AgentLogger.LogException($"Scan source '{what}'", ex);
            return Array.Empty<ScanCandidate>();
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception ex) when (IsAccessOrIo(ex)) { return false; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (IsAccessOrIo(ex)) { return false; }
    }

    private static string? SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (IsAccessOrIo(ex))
        {
            AgentLogger.Log($"Scan: could not read '{path}' ({ex.GetType().Name}). Skipping it.");
            return null;
        }
    }

    private static string? SafeCombine(params string[] parts)
    {
        try { return Path.Combine(parts); }
        catch (ArgumentException) { return null; }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path) =>
        Guarded(() => Directory.EnumerateDirectories(path), $"directories under '{path}'");

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern) =>
        Guarded(() => Directory.EnumerateFiles(path, pattern), $"'{pattern}' under '{path}'");

    /// <summary>
    /// Enumerate lazily, stopping at the first access/IO failure instead of throwing it at the
    /// caller. The lazy part is the point: <see cref="Directory.EnumerateDirectories(string)"/>
    /// does not fail when it is called, it fails on the <c>MoveNext</c> that reaches the bad entry,
    /// so a plain try/catch around the call site catches nothing. Whatever was read before the
    /// failure is kept.
    /// </summary>
    private static IEnumerable<string> Guarded(Func<IEnumerable<string>> factory, string what)
    {
        IEnumerator<string> e;
        try { e = factory().GetEnumerator(); }
        catch (Exception ex) when (IsAccessOrIo(ex))
        {
            AgentLogger.Log($"Scan: could not enumerate {what} ({ex.GetType().Name}). Skipping it.");
            yield break;
        }

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
                catch (Exception ex) when (IsAccessOrIo(ex))
                {
                    AgentLogger.Log($"Scan: stopped enumerating {what} ({ex.GetType().Name}). Keeping what was found.");
                    yield break;
                }
                yield return current;
            }
        }
        finally { e.Dispose(); }
    }
}
