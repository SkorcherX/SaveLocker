namespace SaveLocker.Shared;

/// <summary>
/// Expands Ludusavi manifest path placeholders (e.g. &lt;winAppData&gt;) into
/// concrete filesystem paths for the current machine, then trims the result at
/// the first wildcard segment so callers get a real directory to watch/archive.
/// </summary>
public sealed class PathResolver
{
    private readonly Dictionary<string, string> _tokens;

    /// <summary>
    /// True for a resolver built over a Wine/Proton prefix — where the manifest's exact-case
    /// template can legitimately not match what a game (or a relocated/recreated prefix) actually
    /// wrote to a case-sensitive filesystem. False for <see cref="Windows"/>: NTFS is already
    /// case-insensitive at the OS level, so the fallback below would never even trigger there.
    /// </summary>
    private readonly bool _caseInsensitiveFilesystem;

    /// <summary>
    /// Tokens holding an identifier rather than a path. <see cref="Tokenize"/> must skip them: they
    /// would match anywhere the name or id happens to appear in a path and rewrite that fragment
    /// into a placeholder, producing a template that expands to nonsense on the next machine.
    /// </summary>
    private static readonly HashSet<string> NonPathTokens =
        new(StringComparer.OrdinalIgnoreCase) { "<osUserName>", "<storeUserId>" };

    public PathResolver(Dictionary<string, string> tokens, bool caseInsensitiveFilesystem = false)
    {
        _caseInsensitiveFilesystem = caseInsensitiveFilesystem;

        // Separators are normalised ONCE, here, so every comparison downstream is separator-safe.
        //
        // ResolveToDirectory rewrites the manifest's forward slashes to the platform separator, but
        // token VALUES arrive however the caller built them — and a caller that passes
        // "C:/games/steam" makes a token value that no expanded path will ever prefix-match. That
        // silently defeats both Tokenize and the known-root guard. Replacing '/' is safe on Linux
        // too: DirectorySeparatorChar is '/' there, so it is a no-op rather than a corruption.
        _tokens = tokens.ToDictionary(
            t => t.Key,
            t => NonPathTokens.Contains(t.Key)
                ? t.Value
                : t.Value?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build a resolver populated with this Windows user's known folders.
    /// </summary>
    /// <param name="installDir">
    /// The game's own install directory — what <c>&lt;base&gt;</c> expands to. Per-GAME, not
    /// per-machine, which is why it is a parameter: <c>&lt;base&gt;</c> is the most common token in
    /// the manifest by a wide margin, and without it roughly a third of resolvable games are lost.
    /// </param>
    /// <param name="storeRoot">The store library root the game is installed under — <c>&lt;root&gt;</c>.</param>
    /// <param name="storeUserId">
    /// Steam's 32-bit account id, i.e. the <c>userdata/&lt;id&gt;</c> folder name — NOT the 64-bit
    /// SteamID64. Games that shard saves per account nest them under it.
    /// </param>
    public static PathResolver Windows(
        string? installDir = null, string? storeRoot = null, string? storeUserId = null)
    {
        string Env(string var) => Environment.GetEnvironmentVariable(var) ?? string.Empty;
        string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        var appData = Special(Environment.SpecialFolder.ApplicationData);            // Roaming
        var localAppData = Special(Environment.SpecialFolder.LocalApplicationData);  // Local
        var localLow = Path.Combine(Env("USERPROFILE"), "AppData", "LocalLow");
        var documents = Special(Environment.SpecialFolder.MyDocuments);
        var home = Env("USERPROFILE");

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["<winAppData>"] = appData,
            ["<winLocalAppData>"] = localAppData,
            ["<winLocalAppDataLow>"] = localLow,
            ["<winDocuments>"] = documents,
            // %PUBLIC% — C:\Users\Public, the profile ROOT, not its Documents folder.
            //
            // This was SpecialFolder.CommonDocuments (C:\Users\Public\Documents), one level too
            // deep. Real manifest entries look like "<winPublic>/Documents/City Interactive/…", so
            // that produced "C:\Users\Public\Documents\Documents\…" — a path that does not exist,
            // silently costing detection for all 44 <winPublic> games on Windows.
            //
            // The Proton map below already had it right (drive_c/users/Public), so Windows and a
            // Deck disagreed by exactly one segment about where the same game's saves live. That is
            // the cross-machine root divergence that makes a restore nest a folder under itself and
            // delete the correctly-placed copy — arrived at with no user error at all.
            ["<winPublic>"] = Env("PUBLIC") is { Length: > 0 } pub
                ? pub
                : Path.GetDirectoryName(Special(Environment.SpecialFolder.CommonDocuments)) ?? "",
            ["<winProgramData>"] = Special(Environment.SpecialFolder.CommonApplicationData),
            ["<winDir>"] = Env("WINDIR"),
            ["<home>"] = home,
            ["<osUserName>"] = Env("USERNAME"),
            // Windows "Saved Games" has no SpecialFolder enum value.
            ["<winSavedGames>"] = Path.Combine(home, "Saved Games"),
        };

        AddGameTokens(tokens, installDir, storeRoot, storeUserId);
        return new PathResolver(tokens, caseInsensitiveFilesystem: false);
    }

    /// <summary>
    /// Add the three per-game placeholders shared by both platforms.
    /// <para>
    /// A null or empty value is <b>omitted entirely</b> rather than mapped to "". That distinction
    /// matters: an unknown token left in place trips the leftover-placeholder guard in
    /// <see cref="ResolveToDirectory"/> and yields null — "we cannot resolve this" — whereas an
    /// empty mapping would silently produce a truncated path pointing somewhere real and wrong.
    /// </para>
    /// </summary>
    private static void AddGameTokens(
        Dictionary<string, string> tokens, string? installDir, string? storeRoot, string? storeUserId)
    {
        if (!string.IsNullOrWhiteSpace(installDir)) tokens["<base>"] = installDir;
        if (!string.IsNullOrWhiteSpace(storeRoot)) tokens["<root>"] = storeRoot;
        if (!string.IsNullOrWhiteSpace(storeUserId)) tokens["<storeUserId>"] = storeUserId;
    }

    /// <summary>
    /// Build a resolver for a game running under Proton. The same Windows tokens resolve, but
    /// <b>inside the Wine prefix</b> rather than against this machine's folders — so unlike
    /// <see cref="Windows"/> this map is per-game, not per-machine.
    /// </summary>
    /// <param name="compatDataPath">
    /// The game's <c>compatdata/&lt;appid&gt;</c> directory — exactly what Steam passes as
    /// <c>STEAM_COMPAT_DATA_PATH</c>. The prefix is the <c>pfx</c> subdirectory of it.
    /// </param>
    /// <param name="installDir">
    /// The game's install directory on the HOST filesystem — <c>&lt;base&gt;</c>. Under Proton this
    /// is a native Linux path, not a path inside the prefix: a non-Steam shortcut's is its
    /// <c>StartDir</c>, and a Steam game's is <c>steamapps/common/&lt;installdir&gt;</c>.
    /// </param>
    /// <param name="storeRoot">The Steam library root — <c>&lt;root&gt;</c>. Also a host path.</param>
    /// <param name="storeUserId">
    /// Steam's 32-bit account id, i.e. the <c>userdata/&lt;id&gt;</c> folder name — NOT SteamID64.
    /// </param>
    public static PathResolver Proton(
        string compatDataPath,
        string? installDir = null,
        string? storeRoot = null,
        string? storeUserId = null) =>
        // Steam's two conventions, and the only place they are assumed: the prefix is the `pfx`
        // subdirectory, and every game runs as the fixed Wine user "steamuser".
        Wine(Path.Combine(compatDataPath, "pfx", "drive_c"), "steamuser",
             installDir, storeRoot, storeUserId);

    /// <summary>
    /// Build a resolver for a game in an arbitrary Wine prefix — the general form of
    /// <see cref="Proton"/>, for prefixes that are not Steam's.
    /// <para>
    /// A launcher that manages its own prefixes (Heroic) obeys neither Steam convention: it nests
    /// <c>pfx</c> only for a Proton runner, and a plain Wine runner runs the game as the Linux
    /// user, not <c>steamuser</c>. Both are therefore parameters here. Use
    /// <see cref="WinePrefix.Locate"/> to discover them from a prefix on disk; this method itself
    /// never touches the filesystem.
    /// </para>
    /// </summary>
    /// <param name="driveC">The prefix's <c>drive_c</c> directory.</param>
    /// <param name="userName">The Wine user the game runs as — the <c>users/</c> subdirectory.</param>
    public static PathResolver Wine(
        string driveC,
        string userName,
        string? installDir = null,
        string? storeRoot = null,
        string? storeUserId = null)
    {
        var userHome = Path.Combine(driveC, "users", userName);
        var appData = Path.Combine(userHome, "AppData");

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["<winAppData>"] = Path.Combine(appData, "Roaming"),
            ["<winLocalAppData>"] = Path.Combine(appData, "Local"),
            ["<winLocalAppDataLow>"] = Path.Combine(appData, "LocalLow"),
            ["<winDocuments>"] = Path.Combine(userHome, "Documents"),
            ["<winPublic>"] = Path.Combine(driveC, "users", "Public"),
            ["<winProgramData>"] = Path.Combine(driveC, "ProgramData"),
            ["<winDir>"] = Path.Combine(driveC, "windows"),
            ["<home>"] = userHome,
            ["<osUserName>"] = userName,
            ["<winSavedGames>"] = Path.Combine(userHome, "Saved Games"),
        };

        AddGameTokens(tokens, installDir, storeRoot, storeUserId);
        return new PathResolver(tokens, caseInsensitiveFilesystem: true);
    }

    /// <summary>
    /// True when <paramref name="directory"/> is exactly the game's install directory —
    /// <c>&lt;base&gt;</c> — rather than a folder inside it.
    /// <para>
    /// Some games keep their saves as loose files beside their own executable: Cave Story+ writes
    /// <c>&lt;base&gt;/Save.dat</c> and <c>&lt;base&gt;/Profile.dat</c>. Since a save location is a
    /// DIRECTORY here, every such path collapses to the install directory, and the install
    /// directory is the entire game.
    /// </para>
    /// <para>
    /// Syncing it would not merely be wasteful. Restore deletes files in the save folder that the
    /// archive does not contain, so pulling one machine's copy over another's would prune that
    /// machine's game installation to match — from two installs that differ by so much as a patch.
    /// Refusing to answer, and letting the user name a folder, is the same trade v0.5.2 made for
    /// config-tagged paths.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Exact match only. A subdirectory of the install — the portable <c>&lt;base&gt;/Saves</c>
    /// shape — is a perfectly good save folder and must keep resolving.
    /// </remarks>
    public bool IsInstallRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        if (!_tokens.TryGetValue("<base>", out var installDir) ||
            string.IsNullOrWhiteSpace(installDir))
            return false;

        return string.Equals(Normalize(directory), Normalize(installDir), PathComparison);

        static string Normalize(string p) =>
            Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar))
                .TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>Path comparison for the host: Windows ignores case, Linux does not.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The reverse of <see cref="ResolveToDirectory"/>: rewrite a concrete path from THIS machine
    /// back into a template, or null when nothing matches.
    /// <para>
    /// This is what lets one correctly-configured machine describe a save location in terms every
    /// other machine can expand for itself. Storing the literal path instead is what allows two
    /// machines to disagree about the same game's save root — and a root that differs by even one
    /// segment makes a restore nest a folder under itself and delete the correctly-placed copy.
    /// </para>
    /// <para>
    /// Longest value wins, so <c>C:\Users\me\Documents</c> becomes <c>&lt;winDocuments&gt;</c> rather
    /// than <c>&lt;home&gt;/Documents</c>. <c>&lt;osUserName&gt;</c> is excluded: it holds a name, not
    /// a path, and would match anywhere the username happens to appear.
    /// </para>
    /// </summary>
    public string? Tokenize(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        var path = absolutePath.Replace('/', Path.DirectorySeparatorChar)
                               .TrimEnd(Path.DirectorySeparatorChar);
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var best = _tokens
            .Where(t => !NonPathTokens.Contains(t.Key) && !string.IsNullOrEmpty(t.Value))
            .Select(t => (t.Key, Value: t.Value.TrimEnd(Path.DirectorySeparatorChar)))
            .Where(t => path.Equals(t.Value, cmp) ||
                        path.StartsWith(t.Value + Path.DirectorySeparatorChar, cmp))
            .OrderByDescending(t => t.Value.Length)
            .FirstOrDefault();

        if (best.Key is null) return null;

        var rest = path[best.Value.Length..].TrimStart(Path.DirectorySeparatorChar);
        // Manifest templates use forward slashes, and ResolveToDirectory converts back on expansion.
        return rest.Length == 0 ? best.Key : $"{best.Key}/{rest.Replace('\\', '/')}";
    }

    /// <summary>
    /// The longest token root value that is an exact-case prefix of <paramref name="path"/>, or
    /// null if none matches. Always <c>Ordinal</c>, unlike <see cref="StartsWithKnownRoot"/> and
    /// <see cref="Tokenize"/>: token substitution is verbatim (<see cref="ResolveToDirectory"/>
    /// splices a token's stored value into the template byte-for-byte), so a naive joined path is
    /// guaranteed to contain some token's exact-case value as a prefix whenever it is rooted in
    /// anything we know at all. <see cref="ResolveCaseInsensitive"/> anchors its walk here rather
    /// than at the filesystem root, so correction never touches the token-root portion of the path
    /// — which is what lets <see cref="StartsWithKnownRoot"/> be checked once, on the naive path,
    /// and never re-checked on a corrected one.
    /// </summary>
    private string? LongestMatchingRoot(string path) =>
        _tokens
            .Where(t => !NonPathTokens.Contains(t.Key) && !string.IsNullOrEmpty(t.Value))
            .Select(t => t.Value.TrimEnd(Path.DirectorySeparatorChar))
            .Where(root => path.Equals(root, StringComparison.Ordinal) ||
                           path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

    /// <summary>
    /// True when an expanded path actually sits under one of this resolver's own roots. Anything
    /// else came from a template we did not fully expand, and is not ours to return.
    /// </summary>
    private bool StartsWithKnownRoot(string path)
    {
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return _tokens
            .Where(t => !NonPathTokens.Contains(t.Key) && !string.IsNullOrEmpty(t.Value))
            .Select(t => t.Value.TrimEnd(Path.DirectorySeparatorChar))
            .Any(root => path.Equals(root, cmp) ||
                         path.StartsWith(root + Path.DirectorySeparatorChar, cmp));
    }

    /// <summary>True when this value is a template rather than a concrete path.</summary>
    public static bool IsTemplate(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('<') && value.Contains('>');

    /// <summary>
    /// Every existing directory a template designates, fanning <c>&lt;storeUserId&gt;</c> out over
    /// what is actually on disk.
    /// <para>
    /// The id cannot be derived reliably, so it is discovered rather than guessed. Steam exposes at
    /// least two forms — the 32-bit account id that names <c>userdata/&lt;id&gt;</c>, and the 64-bit
    /// SteamID64 — and games pick whichever they like. DRAGON QUEST III writes
    /// <c>…/Steam/76561197960271872/SaveGames</c> while the same machine's userdata folders are
    /// <c>14297026</c> and <c>19627</c>; the two do not correspond, so any resolver that substituted
    /// an id from userdata produced a path that does not exist. Enumerating the parent is correct
    /// for both forms, for a machine with several Steam accounts, and for stores that are not Steam.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ResolveToDirectories(string template)
    {
        if (!template.Contains("<storeUserId>", StringComparison.OrdinalIgnoreCase))
            return ResolveToDirectory(template) is { } single ? [single] : [];

        // Split at the token and resolve the part before it; that parent is what we enumerate.
        var idx = template.IndexOf("<storeUserId>", StringComparison.OrdinalIgnoreCase);
        var before = template[..idx];
        var after = template[(idx + "<storeUserId>".Length)..];

        // Only when the token is a WHOLE path segment. The manifest also embeds it inside one —
        // "…/Max Gentlemen Sexy Business<storeUserId>" (a missing slash) and
        // "…/Profiles/<storeUserId>.prf" (part of a filename) — and treating those as directory
        // boundaries enumerates the wrong level, handing back a neighbouring folder as if it were
        // the save. The id is unknowable in those shapes, so resolve nothing and let the user pick.
        var wholeSegment =
            (before.Length == 0 || before[^1] is '/' or '\\') &&
            (after.Length == 0 || after[0] is '/' or '\\');
        if (!wholeSegment) return [];

        before = before.TrimEnd('/', '\\');
        after = after.TrimStart('/', '\\');

        if (ResolveToDirectory(before) is not { } parent || !Directory.Exists(parent))
            return [];

        var results = new List<string>();
        foreach (var child in SafeChildDirectories(parent))
        {
            // Only id-shaped children. The parent holds more than accounts — DRAGON QUEST III's
            // "Steam" folder also carries "Config" and "Logs", and returning those hands the user a
            // settings directory labelled as their save. Every store id we care about is numeric.
            var leaf = Path.GetFileName(child);
            if (leaf.Length == 0 || !leaf.All(char.IsDigit)) continue;

            // Rebuild the full template with this concrete id, so the remainder — and any wildcard
            // or filename in it — goes through exactly the same trimming as every other path.
            var rebuilt = after.Length == 0 ? child : Path.Combine(child, after.Replace('/', Path.DirectorySeparatorChar));
            if (ResolveConcrete(rebuilt) is { } dir && Directory.Exists(dir))
                results.Add(dir);
        }

        return results;
    }

    private static IEnumerable<string> SafeChildDirectories(string parent)
    {
        try { return Directory.EnumerateDirectories(parent).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Case-insensitive fallback for a Wine/Proton prefix: walk the manifest-authored remainder of
    /// <paramref name="kept"/> (the segments after <see cref="LongestMatchingRoot"/>'s trusted
    /// anchor) against the REAL directory listing, one segment at a time, rather than retrying only
    /// the last one — a relocated or recreated prefix can case any ONE segment differently than the
    /// manifest expects, not just the deepest. Returns null (never the naive path) when nothing on
    /// disk matches even case-insensitively, so the caller's existing "no such directory" outcome is
    /// unchanged.
    /// </summary>
    private string? ResolveCaseInsensitive(IReadOnlyList<string> kept, string naivePath, bool trailingIsFile)
    {
        if (LongestMatchingRoot(naivePath) is not { } root) return null;

        var rootSegmentCount = root.Split(Path.DirectorySeparatorChar).Length;
        if (rootSegmentCount >= kept.Count) return null; // nothing after the root to correct

        var current = root; // trusted verbatim — never re-walked or re-verified

        for (var i = rootSegmentCount; i < kept.Count; i++)
        {
            var segment = kept[i];

            // The final segment of a file-shaped template (<base>/Save.dat) is never itself
            // checked for existence by ResolveConcrete's own trailing special case either — only
            // its corrected PARENT matters. A bare, never-yet-written filename still yields the
            // right directory; without this, a save that has never been written once (nothing to
            // find, case-insensitively or otherwise) would wrongly fail the whole walk even though
            // an earlier segment's correction was real and found.
            if (trailingIsFile && i == kept.Count - 1)
                return MatchChild(current, segment) ?? current;

            var exact = Path.Combine(current, segment);
            if (Directory.Exists(exact)) { current = exact; continue; } // fast path: already correct

            if (MatchChild(current, segment) is not { } match) return null;
            current = match;
        }

        return current;
    }

    /// <summary>
    /// The single child of <paramref name="parent"/> matching <paramref name="segment"/>
    /// case-insensitively, or the tie-break winner when more than one sibling does.
    /// <para>
    /// Exact-case preference (the policy <c>Backlog.md</c> settled on) falls out of the caller for
    /// free rather than needing its own check here: <see cref="ResolveCaseInsensitive"/> always
    /// tries <c>Directory.Exists</c> on the exact-case path FIRST and only reaches this method on a
    /// miss for that same parent/segment pair — so an exact-case sibling can never appear among the
    /// candidates enumerated below; if one existed, the caller would already have taken it. What's
    /// left to break a tie between is only siblings that are ALL wrong-cased relative to the
    /// manifest, so the newest one wins, on the theory that it is the copy actually being played —
    /// matching the relocation scenario that motivated this fix, where a stale prefix's folder and a
    /// fresh prefix's folder can both survive side by side.
    /// </para>
    /// </summary>
    private static string? MatchChild(string parent, string segment)
    {
        var candidates = SafeChildDirectories(parent)
            .Where(d => Path.GetFileName(d).Equals(segment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => candidates.OrderByDescending(SafeLastWriteTimeUtc).First()
        };
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// Expand placeholders in a template and return the deepest directory prefix
    /// that precedes any wildcard. Returns null if a required token is unknown.
    /// </summary>
    public string? ResolveToDirectory(string template)
    {
        var expanded = template;
        foreach (var (token, value) in _tokens)
        {
            if (expanded.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(value)) return null; // unresolved on this machine
                expanded = expanded.Replace(token, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Any leftover <...> placeholder means we can't resolve it here.
        if (expanded.Contains('<') && expanded.Contains('>'))
            return null;

        return ResolveConcrete(expanded);
    }

    /// <summary>
    /// The half of resolution that operates on an already-expanded path: trim at the first wildcard,
    /// reduce a filename to its directory, and refuse anything not rooted in one of our tokens.
    /// Shared so a &lt;storeUserId&gt; fan-out gets identical treatment to a straight expansion.
    /// </summary>
    private string? ResolveConcrete(string expanded)
    {
        expanded = expanded.Replace('/', Path.DirectorySeparatorChar);

        // Trim at the first wildcard segment so we return a concrete directory.
        var segments = expanded.Split(Path.DirectorySeparatorChar);
        var kept = new List<string>();
        foreach (var seg in segments)
        {
            if (seg.Contains('*') || seg.Contains('?'))
                break;
            kept.Add(seg);
        }

        if (kept.Count == 0) return null;
        var path = string.Join(Path.DirectorySeparatorChar, kept);

        // A template that expands to something not rooted in one of OUR tokens is refused.
        //
        // The community manifest contains malformed entries with no placeholder at all — e.g.
        // "/AppData/LocalLow/Strange Scaffold/.../Savegames" for Space Warlord Organ Trading
        // Simulator, which is missing its <home>. Left alone, Path.GetFullPath anchors that to the
        // CURRENT DRIVE, so the agent can hand back "E:\AppData\..." — a real path outside every
        // prefix and profile we know about, presented to the user as a confident answer. Found by
        // tests/detection.
        if (!StartsWithKnownRoot(path)) return null;

        var trailingIsFile = kept.Count == segments.Length && Path.HasExtension(path);

        // Inside a Wine/Proton prefix, a segment the GAME created (not one of our own tokens) can
        // be cased differently than the manifest expects — a relocated or recreated prefix is the
        // common trigger (Borderlands 2: Wine wrote "My games", the manifest and the original
        // prefix both say "My Games"). Windows never reaches here: _caseInsensitiveFilesystem is
        // false there, and NTFS would have resolved a mis-cased Directory.Exists above anyway.
        if (_caseInsensitiveFilesystem && !Directory.Exists(path) &&
            ResolveCaseInsensitive(kept, path, trailingIsFile) is { } corrected)
            return corrected;

        // If the template pointed at a specific file (last kept segment has an
        // extension and there were no wildcards after it), use its directory.
        if (trailingIsFile && !Directory.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;

        return path;
    }
}
