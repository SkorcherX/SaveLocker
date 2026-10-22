namespace SaveLocker.Agent;

/// <summary>
/// The hard floor under every save-folder mapping: paths that can <b>never</b> be a save folder,
/// however they arrived.
///
/// <para>
/// This is deliberately separate from <see cref="SaveDirSanity"/>. Sanity checks are heuristics —
/// "that looks like a Wine prefix", "that is suspiciously large" — and they have false positives, so
/// they warn and can be overridden. These are absolutes: nobody's save folder is <c>C:\</c>, their
/// user profile, <c>C:\Windows</c>, or the agent's own state directory. A mapping like that is
/// catastrophic in both directions — a push archives the user's whole world and a force-pull
/// <i>replaces</i> it.
/// </para>
///
/// <para>
/// Paths reach a game from five places: the folder picker, the typed local API, enrollment, the CLI,
/// and server <c>MachineSavePath</c> reconciliation. Each of those validates, and
/// <see cref="SyncEngine"/> validates again immediately before it archives or restores — because
/// configuration-time validation cannot defend against a later hand-edit of config.json, a hostile
/// or mistaken server path update, or a junction that is re-pointed after the mapping was accepted.
/// That last case is why <see cref="Canonicalize"/> resolves reparse points rather than trusting the
/// literal string.
/// </para>
/// </summary>
public static class SavePathGuard
{
    public readonly record struct Result(bool Ok, string? Reason, string? Canonical)
    {
        public static Result Allow(string canonical) => new(true, null, canonical);
        public static Result Refuse(string reason) => new(false, reason, null);
    }

    /// <summary>
    /// Absolute path with reparse points (junctions, symlinks) followed to their real target.
    /// Returns null if the text is not a usable absolute path at all.
    /// <para>
    /// Following links is the point: a junction named <c>C:\Games\MySave</c> that targets
    /// <c>C:\Users\me</c> passes every check made against the literal string, and the checks below
    /// are the only thing standing between a force-pull and the user's profile.
    /// </para>
    /// </summary>
    public static string? Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
            if (!Path.IsPathRooted(full)) return null;
        }
        catch { return null; } // invalid characters, a bare drive letter, a too-long path

        try
        {
            // returnFinalTarget walks a chain of links rather than one hop.
            if (Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName is { } target)
                full = Path.GetFullPath(target);
        }
        catch { /* not a link, does not exist yet, or a broken chain — judge the literal path */ }

        return Trim(full);
    }

    /// <summary>
    /// <see cref="Check"/>, plus the rules between one game's folders: none may sit inside another,
    /// and two may share a directory only when both have include patterns (tasks/multiple-save-paths
    /// plan §7). <paramref name="key"/> is the folder being set — <c>main</c> for the primary one.
    /// </summary>
    public static Result CheckFolder(TrackedGame game, string key, string? path, string stateDir)
    {
        var check = Check(path, stateDir);
        if (!check.Ok) return check;

        var roots = game.Roots(stateDir)
            .Select(r => r.Key == key ? r with { Directory = check.Canonical! } : r)
            // An unmapped primary folder has no place in the rules yet; Path.GetFullPath("") throws.
            .Where(r => !string.IsNullOrWhiteSpace(r.Directory))
            .ToList();
        if (roots.All(r => !r.IsPrimary)) return check;
        return SaveLocker.Shared.SaveArchive.FolderRulesError(roots) is { } why ? Result.Refuse(why) : check;
    }

    /// <summary>
    /// Is this an acceptable save folder? The reason is written for the user, not the log: it has to
    /// survive being shown in a toast, a CLI line, and a command result in the console.
    /// </summary>
    /// <param name="stateDir">The agent's own state directory, from <c>AgentConfig.StateDir</c>.</param>
    public static Result Check(string? path, string? stateDir = null)
    {
        var full = Canonicalize(path);
        if (full is null)
            return Result.Refuse("that is not a usable absolute folder path.");

        // A drive root, or / on Linux. Path.GetPathRoot of "C:\x" is "C:\", so equality catches it.
        var root = Trim(Path.GetPathRoot(full) ?? "");
        if (full.Length == 0 || string.Equals(full, root, Cmp))
            return Result.Refuse(
                $"'{path}' is a drive root. Syncing it would archive the entire drive, and a " +
                "force-pull would replace it. Map the game's own save folder.");

        foreach (var (protectedPath, label) in ProtectedPaths(stateDir))
        {
            if (protectedPath is null) continue;

            if (string.Equals(full, protectedPath, Cmp))
                return Result.Refuse(
                    $"'{path}' is {label}. It is not a save folder, and a force-pull would " +
                    "overwrite it. Map the game's own save folder instead.");

            // An ANCESTOR is just as dangerous as the thing itself: mapping C:\Users archives every
            // profile on the box, and restoring into it replaces them.
            if (IsInside(protectedPath, full))
                return Result.Refuse(
                    $"'{path}' contains {label}. Syncing it would archive far more than a save, and " +
                    "a force-pull would overwrite it. Map the game's own save folder instead.");
        }

        foreach (var (tree, label) in ProtectedTrees(stateDir))
        {
            if (tree is not null && (string.Equals(full, tree, Cmp) || IsInside(full, tree)))
                return Result.Refuse(
                    $"'{path}' is inside {label}. Saves do not live there, and a force-pull would " +
                    "overwrite installed files. Map the game's own save folder instead.");
        }

        return Result.Allow(full);
    }

    /// <summary>
    /// Paths that must never be synced, nor contained by a synced folder. Anything null (a folder
    /// this OS does not have) is skipped by the caller.
    /// </summary>
    private static IEnumerable<(string? Path, string Label)> ProtectedPaths(string? stateDir)
    {
        yield return (Special(Environment.SpecialFolder.UserProfile), "your user profile");
        yield return (Special(Environment.SpecialFolder.CommonApplicationData), "the ProgramData root");
        yield return (Special(Environment.SpecialFolder.Windows), "the Windows directory");
        yield return (Special(Environment.SpecialFolder.System), "the Windows system directory");
        yield return (Special(Environment.SpecialFolder.ProgramFiles), "the Program Files directory");
        yield return (Special(Environment.SpecialFolder.ProgramFilesX86), "the 32-bit Program Files directory");

        // The parent of the profile — C:\Users, /home — holds every account on the machine.
        if (Special(Environment.SpecialFolder.UserProfile) is { } home &&
            Path.GetDirectoryName(home) is { Length: > 0 } homes)
            yield return (Trim(homes), "the folder holding every user profile");

        // SaveLocker's own state. config.json is the machine's server credential and api-token grants
        // the local management API: archiving them uploads both to the server, and restoring over
        // them hands this machine another machine's identity. Note this is same-or-ancestor only —
        // a save folder nested somewhere BELOW the state dir exposes nothing, and the test suites
        // legitimately do exactly that.
        if (stateDir is not null) yield return (Canonicalize(stateDir), "the SaveLocker state directory");

        yield return (Trim(AppContext.BaseDirectory), "the SaveLocker install directory");

        if (!OperatingSystem.IsWindows())
            foreach (var p in new[] { "/home", "/etc", "/usr", "/var", "/boot", "/bin", "/sbin", "/lib", "/opt" })
                yield return (Directory.Exists(p) ? p : null, $"the system directory {p}");
    }

    /// <summary>Trees nothing inside of can be a save folder.</summary>
    private static IEnumerable<(string? Path, string Label)> ProtectedTrees(string? stateDir)
    {
        yield return (Special(Environment.SpecialFolder.Windows), "the Windows directory");
        yield return (Special(Environment.SpecialFolder.ProgramFiles), "Program Files");
        yield return (Special(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)");
        yield return (Trim(AppContext.BaseDirectory), "the SaveLocker install directory");
    }

    private static string? Special(Environment.SpecialFolder f)
    {
        var p = Environment.GetFolderPath(f);
        return string.IsNullOrEmpty(p) ? null : Trim(Path.GetFullPath(p));
    }

    /// <summary>True if <paramref name="child"/> sits strictly underneath <paramref name="parent"/>.</summary>
    private static bool IsInside(string child, string parent) =>
        child.Length > parent.Length &&
        child.StartsWith(parent, Cmp) &&
        (child[parent.Length] == Path.DirectorySeparatorChar ||
         child[parent.Length] == Path.AltDirectorySeparatorChar);

    private static string Trim(string p) =>
        p.Length > 1 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;

    // Windows paths are case-insensitive; Linux paths are not. Getting this backwards would either
    // let 'c:\windows' through or refuse two genuinely different Linux folders as one.
    private static StringComparison Cmp =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
