using SaveLocker.Shared;

namespace SaveLocker.Agent.Linux;

/// <summary>
/// Discovers non-Steam Steam shortcuts and, where possible, their Proton save directories.
///
/// This is deliberately NOT the Windows scanner's shape. Steam-store games are out of scope —
/// they have Steam Cloud (Decisions.md §0) — so there is no <c>libraryfolders.vdf</c> / <c>*.acf</c>
/// scan here. Discovery is <c>shortcuts.vdf</c>, and the shortcut's AppID is what names the
/// Proton prefix we then resolve saves inside.
/// </summary>
public sealed class LinuxGameScanner : IGameScanner
{
    private readonly Detection _detection;

    public LinuxGameScanner(Detection detection) => _detection = detection;

    public async Task<IReadOnlyList<ScanCandidate>> ScanAsync(CancellationToken ct = default)
    {
        var results = new List<ScanCandidate>();

        foreach (var root in SteamRoots.Find())
        {
            foreach (var s in await SteamShortcuts.ReadAllAsync(root, ct))
            {
                ct.ThrowIfCancellationRequested();
                var prefix = s.AppId is null ? null : SteamRoots.CompatDataPath(root, s.AppId);
                var save = await SuggestSaveDirAsync(s, prefix, ct);

                results.Add(new ScanCandidate(
                    Name: s.AppName,
                    SuggestedSaveDir: save,
                    Source: ScanSource.SteamShortcut,
                    HasSteamCloud: false,          // non-Steam shortcuts have no Cloud, by definition
                    ManifestKey: save is null ? null : s.AppName,
                    InstallDir: s.StartDir,
                    SteamAppId: s.AppId));
            }
        }

        return results
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.SuggestedSaveDir is not null).First())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Best guess at a shortcut's save directory. Two shapes exist and both are real:
    ///
    /// 1. <b>In-prefix</b> — the game writes to Windows paths inside its Wine prefix. The Ludusavi
    ///    manifest's tokens resolve there, via a resolver built for THIS game's prefix.
    /// 2. <b>Portable</b> — the game writes next to its own .exe. That is a plain Linux path on the
    ///    native filesystem and needs no prefix resolution at all. Common for the standalone builds
    ///    this feature exists for, so it is checked even when a prefix exists.
    ///
    /// Null when neither resolves, which on Linux is the expected case rather than a failure: most
    /// standalone builds are absent from the manifest, so <c>add-game --dir</c> is the primary path.
    /// </summary>
    private async Task<string?> SuggestSaveDirAsync(
        SteamShortcut shortcut, string? compatDataPath, CancellationToken ct)
    {
        if (compatDataPath is not null)
        {
            var dirs = await _detection.ResolveSaveDirectoriesAsync(
                shortcut.AppName, PathResolver.Proton(compatDataPath), ct);
            if (dirs.FirstOrDefault() is { } inPrefix) return inPrefix;
        }

        return PortableSaveDir(shortcut);
    }

    /// <summary>
    /// A save folder sitting beside the game's executable. We only claim one when it is
    /// unambiguous — a single conventionally-named directory — because guessing wrong here points
    /// the archiver at the wrong tree, and the user can always say exactly what they mean with
    /// <c>--dir</c>.
    /// </summary>
    private static string? PortableSaveDir(SteamShortcut shortcut)
    {
        var installDir = shortcut.StartDir
                         ?? (shortcut.Exe is { } exe ? Path.GetDirectoryName(exe) : null);
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
