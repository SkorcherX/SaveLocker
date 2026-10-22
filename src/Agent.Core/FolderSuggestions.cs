using System.Text;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>One manifest location a tracked game could also sync. <paramref name="Deferred"/>: the user
/// chose "Skip for now", so it is listed on the game's page but not asked about again.</summary>
public sealed record FolderSuggestion(Guid GameId, string GameName, string Key, string Directory, bool Deferred);

/// <summary>
/// "Also found" (tasks/multiple-save-paths plan §8): a manifest game's locations beyond the first that
/// exist on this machine. The first stays the primary folder, exactly as detection always chose it;
/// the rest are only ever suggested, because the manifest cannot say whether two locations are both
/// saves or alternatives (DRAGON QUEST III's second one is its Config folder).
/// </summary>
public sealed class FolderSuggestions
{
    private readonly AgentConfig _config;
    private readonly Detection _detection;
    private readonly Func<string, string?>? _prefixForAppId;

    public FolderSuggestions(AgentConfig config, Detection detection, Func<string, string?>? prefixForAppId = null)
    {
        _config = config;
        _detection = detection;
        _prefixForAppId = prefixForAppId;
    }

    /// <summary>Every tracked game's suggestions, ignored ones left out.</summary>
    public async Task<IReadOnlyList<FolderSuggestion>> AllAsync(CancellationToken ct = default)
    {
        var all = new List<FolderSuggestion>();
        foreach (var game in _config.Games)
            all.AddRange(await ForGameAsync(game, ct));
        return all;
    }

    /// <summary>
    /// One game's suggestions: its manifest locations here, minus the folders it already syncs (or
    /// that one of its folders' templates names), the ones the user said not to sync, and any that
    /// break the rules between one game's folders. Empty for a game with no folder here yet — the
    /// primary folder comes first.
    /// </summary>
    public async Task<IReadOnlyList<FolderSuggestion>> ForGameAsync(TrackedGame game, CancellationToken ct = default)
    {
        if (!game.IsEnrolledHere) return Array.Empty<FolderSuggestion>();
        var resolver = ResolverFor(game, _prefixForAppId);
        if (resolver is null) return Array.Empty<FolderSuggestion>();

        IReadOnlyList<ResolvedSaveLocation> locations;
        try { locations = await _detection.ResolveSaveLocationsAsync(game.ManifestKey ?? game.Name, resolver, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLogger.LogException($"FolderSuggestions '{game.Name}'", ex);
            return Array.Empty<FolderSuggestion>();
        }

        var known = new List<string?>(game.ExtraPaths.Select(p => p.Directory));
        known.AddRange(game.ExtraPaths
            .Where(p => !string.IsNullOrWhiteSpace(p.Template))
            .Select(p => PathResolver.IsTemplate(p.Template) ? resolver.ResolveToDirectory(p.Template!) : p.Template));
        known.AddRange(game.IgnoredFolders);
        var skip = known.Select(SavePathGuard.Canonicalize).OfType<string>().ToHashSet(PathComparer);

        var deferred = game.DeferredFolders.Select(SavePathGuard.Canonicalize).OfType<string>().ToHashSet(PathComparer);
        var taken = game.ExtraPaths.Select(p => p.Key).Concat(game.RemovedPathKeys);
        return Alternates(locations.Where(l => !skip.Contains(SavePathGuard.Canonicalize(l.Directory) ?? "")),
                game.RealRoots(_config.StateDir), taken, _config.StateDir)
            .Select(a => new FolderSuggestion(game.GameId, game.Name, a.Key, a.Dir, deferred.Contains(a.Dir)))
            .ToList();
    }

    /// <summary>
    /// The locations that may join <paramref name="roots"/> as extra folders: not already one of them,
    /// a folder <see cref="SavePathGuard"/> allows, and nested in none of them nor in each other. Each
    /// gets a key from its template (<see cref="KeyFor"/>) that none of <paramref name="takenKeys"/>
    /// already uses — a removed key is never reused.
    /// </summary>
    public static List<DeclaredSavePath> Alternates(IEnumerable<ResolvedSaveLocation> locations,
        IReadOnlyList<SaveRoot> roots, IEnumerable<string> takenKeys, string? stateDir)
    {
        var result = new List<DeclaredSavePath>();
        var taken = new HashSet<string>(takenKeys, StringComparer.Ordinal) { SaveRoot.PrimaryKey };
        var accepted = roots.Where(r => !string.IsNullOrWhiteSpace(r.Directory)).ToList();
        var seen = accepted.Select(r => SavePathGuard.Canonicalize(r.Directory)).OfType<string>().ToHashSet(PathComparer);

        foreach (var loc in locations)
        {
            var check = SavePathGuard.Check(loc.Directory, stateDir);
            if (!check.Ok || !seen.Add(check.Canonical!)) continue;
            var key = KeyFor(loc.Template, taken);
            var root = new SaveRoot(key, check.Canonical!);
            if (SaveArchive.FolderRulesError([.. accepted, root]) is not null) continue;
            accepted.Add(root);
            taken.Add(key);
            result.Add(new DeclaredSavePath(key, check.Canonical!));
        }
        return result;
    }

    /// <summary>
    /// What a scanner offers as "Also found" for a candidate whose primary folder is
    /// <paramref name="primary"/>: every other location in <paramref name="locations"/> that may join it.
    /// Null when there is none, so a candidate without any carries nothing.
    /// </summary>
    public static IReadOnlyList<DeclaredSavePath>? AlsoFound(IReadOnlyList<ResolvedSaveLocation> locations, string? primary)
    {
        if (string.IsNullOrWhiteSpace(primary) || locations.Count < 2) return null;
        var also = Alternates(locations, [SaveRoot.Primary(primary)], [], stateDir: null);
        return also.Count == 0 ? null : also;
    }

    /// <summary>
    /// A save folder key from a manifest template: its last fixed segment, slugged
    /// (<c>&lt;winAppData&gt;/Game/Config/*.ini</c> → <c>config</c>), with <c>-2</c>, <c>-3</c>… when
    /// <paramref name="taken"/> has it already. <c>extra</c> when no segment is fixed.
    /// </summary>
    public static string KeyFor(string template, IReadOnlySet<string> taken)
    {
        string? last = null;
        foreach (var segment in template.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // Everything from the first wildcard on is trimmed off a resolved folder, so it names nothing.
            if (segment.IndexOfAny(['*', '?', '[', '{']) >= 0) break;
            if (segment.StartsWith('<') && segment.EndsWith('>')) continue;
            last = segment;
        }

        var stem = Slug(last ?? "");
        if (stem.Length == 0) stem = "extra";
        var key = stem;
        for (var n = 2; taken.Contains(key) || SaveRoot.ValidateExtraKey(key) is not null; n++)
        {
            var suffix = "-" + n;
            key = stem[..Math.Min(stem.Length, 32 - suffix.Length)].TrimEnd('-') + suffix;
        }
        return key;
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length <= 32 ? slug : slug[..32].TrimEnd('-');
    }

    /// <summary>
    /// The resolver that expands a tracked game's templates where it runs: the host's own folders on
    /// Windows; on Linux the Proton prefix its folder is in (or its AppID's), else the Wine prefix its
    /// folder is in. Null when there is none — the host's own folders mean nothing for a Windows game.
    /// </summary>
    public static PathResolver? ResolverFor(TrackedGame game, Func<string, string?>? prefixForAppId = null)
    {
        if (OperatingSystem.IsWindows()) return PathResolver.Windows(game.InstallDir);

        var compat = CompatDataOf(game.SaveDirectory)
                     ?? (game.ResolveSteamAppId() is { Length: > 0 } appId ? prefixForAppId?.Invoke(appId) : null);
        if (!string.IsNullOrWhiteSpace(compat))
            return PathResolver.Proton(compat, game.InstallDir, SteamLayout.RootFromCompatData(compat));
        return WinePrefix.ContainingPrefix(game.SaveDirectory) is { } prefix
            ? WinePrefix.ResolverFor(prefix, game.InstallDir)
            : null;
    }

    private static string? CompatDataOf(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var parts = dir.Replace('\\', '/').Split('/');
        var i = Array.FindLastIndex(parts, p => p == "compatdata");
        return i >= 0 && i + 1 < parts.Length ? string.Join('/', parts[..(i + 2)]) : null;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
