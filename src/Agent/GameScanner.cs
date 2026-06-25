using Microsoft.Win32;

namespace LocalGameSync.Agent;

/// <summary>Where a <see cref="ScanCandidate"/> was discovered.</summary>
public enum ScanSource
{
    /// <summary>A non-Steam game added to Steam (read from shortcuts.vdf).</summary>
    SteamShortcut,
    /// <summary>An installed Steam game (read from appmanifest_*.acf).</summary>
    SteamInstalled,
    /// <summary>A folder under a common save root whose name matches the manifest.</summary>
    SaveRoot
}

/// <summary>
/// A discovered game the user might want to enroll. <see cref="SuggestedSaveDir"/>
/// is our best guess at the local save folder (may be null if we couldn't resolve
/// one yet — the user can fill it in).
/// </summary>
public sealed record ScanCandidate(
    string Name,
    string? SuggestedSaveDir,
    ScanSource Source,
    bool HasSteamCloud,
    string? ManifestKey = null,
    string? InstallDir = null);

/// <summary>
/// Agent-side game discovery. Aggregates several local sources into a list of
/// enrollment candidates (see <c>Game Discovery and Art.md</c>):
/// non-Steam Steam shortcuts, installed Steam games, and a save-root heuristic
/// matched against the Ludusavi manifest. Windows-only (registry + known folders).
/// </summary>
public sealed class GameScanner
{
    private readonly Detection _detection;

    /// <summary>Steam appids that appear as installed "apps" but aren't games.</summary>
    private static readonly HashSet<string> NonGameAppIds = new()
    {
        "228980", // Steamworks Common Redistributables
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - Soldier
        "1628350", // Steam Linux Runtime - Sniper
    };

    public GameScanner(Detection detection) => _detection = detection;

    /// <summary>Run every source and return de-duplicated candidates, sorted by name.</summary>
    public async Task<IReadOnlyList<ScanCandidate>> ScanAsync(CancellationToken ct = default)
    {
        var all = new List<ScanCandidate>();
        var steamPath = FindSteamPath();

        if (steamPath is not null)
        {
            all.AddRange(await ScanSteamShortcutsAsync(steamPath, ct));
            all.AddRange(ScanInstalledSteamGames(steamPath));
        }

        all.AddRange(await ScanSaveRootsAsync(ct));

        // De-dupe by name: prefer a candidate that already has a suggested save dir.
        return all
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.SuggestedSaveDir is not null).First())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ----- Steam location -----

    /// <summary>Locate the Steam install via the registry; null if Steam isn't installed.</summary>
    public static string? FindSteamPath()
    {
        // HKCU is set per-user when Steam runs; HKLM is the machine install path.
        var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
            ?.GetValue("SteamPath") as string;
        if (!string.IsNullOrEmpty(hkcu) && Directory.Exists(hkcu))
            return Path.GetFullPath(hkcu);

        var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
            ?.GetValue("InstallPath") as string;
        if (!string.IsNullOrEmpty(hklm) && Directory.Exists(hklm))
            return Path.GetFullPath(hklm);

        return null;
    }

    // ----- Source 1: non-Steam shortcuts (shortcuts.vdf, binary) -----

    private async Task<IReadOnlyList<ScanCandidate>> ScanSteamShortcutsAsync(
        string steamPath, CancellationToken ct)
    {
        var results = new List<ScanCandidate>();
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return results;

        foreach (var userDir in Directory.EnumerateDirectories(userdata))
        {
            var vdf = Path.Combine(userDir, "config", "shortcuts.vdf");
            if (!File.Exists(vdf)) continue;

            SteamVdf.VdfObject root;
            try { root = SteamVdf.Parse(await File.ReadAllBytesAsync(vdf, ct)); }
            catch (InvalidDataException) { continue; } // skip a malformed/empty file

            foreach (var entry in root.Children)
            {
                var name = entry.String("AppName") ?? entry.String("appname");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var startDir = (entry.String("StartDir") ?? entry.String("startdir"))?.Trim('"');
                var save = await SuggestSaveDirAsync(name, ct);
                results.Add(new ScanCandidate(
                    name.Trim(), save, ScanSource.SteamShortcut,
                    HasSteamCloud: false, ManifestKey: save is null ? null : name.Trim(),
                    InstallDir: NullIfMissing(startDir)));
            }
        }
        return results;
    }

    // ----- Source 2: installed Steam games (libraryfolders.vdf + *.acf, text) -----

    private IReadOnlyList<ScanCandidate> ScanInstalledSteamGames(string steamPath)
    {
        var results = new List<ScanCandidate>();
        foreach (var library in SteamLibraryPaths(steamPath))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                SteamVdf.VdfObject root;
                try { root = SteamTextVdf.Parse(File.ReadAllText(acf)); }
                catch (InvalidDataException) { continue; }

                var state = root.Object("AppState");
                var name = state?.String("name");
                var installDir = state?.String("installdir");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (state?.String("appid") is { } appid && NonGameAppIds.Contains(appid)) continue;

                var installPath = installDir is null
                    ? null
                    : NullIfMissing(Path.Combine(steamapps, "common", installDir));

                // Installed Steam titles usually have Steam Cloud; flag rather than
                // hide them so the user can still enroll if they want a local copy.
                results.Add(new ScanCandidate(
                    name.Trim(), SuggestedSaveDir: null, ScanSource.SteamInstalled,
                    HasSteamCloud: true, ManifestKey: null, InstallDir: installPath));
            }
        }
        return results;
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
        var file = candidates.FirstOrDefault(File.Exists);
        if (file is null) yield break;

        SteamVdf.VdfObject root;
        try { root = SteamTextVdf.Parse(File.ReadAllText(file)); }
        catch (InvalidDataException) { yield break; }

        var folders = root.Object("libraryfolders");
        if (folders is null) yield break;

        foreach (var lib in folders.Children)
        {
            var path = lib.String("path");
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path))
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
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(dir);
                if (manifestNames.Contains(folderName))
                    results.Add(new ScanCandidate(
                        folderName, Path.GetFullPath(dir), ScanSource.SaveRoot,
                        HasSteamCloud: false, ManifestKey: folderName));
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

    /// <summary>Resolve the first existing save dir the manifest knows for a name.</summary>
    private async Task<string?> SuggestSaveDirAsync(string name, CancellationToken ct)
    {
        var dirs = await _detection.ResolveSaveDirectoriesAsync(name, ct);
        return dirs.FirstOrDefault();
    }

    private static string? NullIfMissing(string? dir) =>
        !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? Path.GetFullPath(dir) : null;
}
