namespace SaveLocker.Agent;

/// <summary>
/// Directory listing for the agent UI's path browser. A Deck has no native folder dialog and no
/// usable terminal in Game Mode, so the UI browses the filesystem over the local API instead
/// (<c>pickFolder</c> is Windows-only — see <see cref="AgentApiServer"/>).
/// <para>
/// It is <b>rooted</b>: every listing is confined to the user's home plus the Steam roots the host
/// supplies, and a path is checked <i>after</i> full canonicalization. The local API already hands
/// out control of this machine, but there is no reason for it to also be a whole-disk reader, and
/// every real save location lives under one of these roots anyway.
/// </para>
/// </summary>
public sealed class PathBrowser
{
    private static readonly StringComparison PathCmp = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly IReadOnlyList<string> _roots;

    /// <param name="extraRoots">
    /// Host-supplied roots outside the home directory. This is what makes an SD card reachable:
    /// a Deck's second Steam library lives at <c>/run/media/…</c>, which no amount of probing
    /// under <c>$HOME</c> will ever find.
    /// </param>
    public PathBrowser(IEnumerable<string>? extraRoots = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var all = new List<string>();
        if (!string.IsNullOrEmpty(home)) all.Add(home);
        if (extraRoots is not null) all.AddRange(extraRoots);

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var candidate in all)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string real;
            try
            {
                if (!Directory.Exists(candidate)) continue;
                real = RealPath(candidate);
            }
            catch { continue; }

            // A root already inside another root is redundant, and listing it twice at the top
            // level would show the user the same tree under two names.
            if (!seen.Add(real)) continue;
            if (roots.Any(r => IsUnder(real, r))) continue;
            roots.RemoveAll(r => IsUnder(r, real));
            roots.Add(real);
        }

        _roots = roots;
    }

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>
    /// Subdirectories of <paramref name="path"/>, or the roots themselves when it is null/empty.
    /// Returns null when the path is outside every root, unreadable, or not a directory — the
    /// caller cannot distinguish those, and deliberately so: probing for the existence of files
    /// outside the roots is exactly what the rooting is for.
    /// </summary>
    public BrowseListing? List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new BrowseListing(
                Path: "",
                Parent: null,
                Entries: _roots.Select(r => new BrowseEntry(DisplayName(r), r)).ToArray());

        string full;
        try { full = RealPath(path); }
        catch { return null; }

        if (!_roots.Any(r => IsUnder(full, r) || PathsEqual(full, r))) return null;
        if (!Directory.Exists(full)) return null;

        List<BrowseEntry> entries;
        try
        {
            entries = new DirectoryInfo(full)
                .EnumerateDirectories()
                // Links are not followed here for the same reason the archive walk does not follow
                // them (Gotchas.md): a Wine prefix is full of links pointing outside the tree, and
                // following one would quietly hand back a listing from outside the roots.
                .Where(d => !IsLink(d))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new BrowseEntry(d.Name, d.FullName))
                .ToList();
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }

        // A root has no parent the user is allowed to see, so the browser stops there.
        var parent = _roots.Any(r => PathsEqual(full, r))
            ? null
            : Directory.GetParent(full)?.FullName;
        if (parent is not null && !_roots.Any(r => IsUnder(parent, r) || PathsEqual(parent, r)))
            parent = null;

        return new BrowseListing(full, parent, entries.ToArray());
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/>.</summary>
    /// <remarks>
    /// The trailing separator is what stops <c>/home/deckard</c> from being accepted as a path under
    /// <c>/home/deck</c> — a plain <c>StartsWith</c> here would be an escape.
    /// </remarks>
    private static bool IsUnder(string path, string root)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathCmp);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            PathCmp);

    /// <summary>
    /// Absolute path with symlinks resolved. Resolving <b>before</b> the containment check is the
    /// whole point: <c>~/shortcut</c> pointing at <c>/etc</c> must be judged as <c>/etc</c>.
    /// </summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var resolved = Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
            if (resolved is not null) return Path.GetFullPath(resolved);
        }
        catch { /* not a link, or a broken one — judge the path as written */ }
        return full;
    }

    /// <summary>See <c>SaveArchive.IsLink</c>: LinkTarget, never FileAttributes.ReparsePoint.</summary>
    private static bool IsLink(FileSystemInfo entry)
    {
        try { return entry.LinkTarget is not null; }
        catch { return false; }
    }

    /// <summary>A root shows its full path — on a Deck "Home" and "primary" mean nothing useful.</summary>
    private static string DisplayName(string root) => root;
}

public sealed record BrowseEntry(string Name, string Path);

public sealed record BrowseListing(string Path, string? Parent, BrowseEntry[] Entries);
