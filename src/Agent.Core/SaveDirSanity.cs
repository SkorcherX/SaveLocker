using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Catches a save path that is <i>plausible but wrong</i> — most often a Wine prefix root instead of
/// the save folder buried inside it.
/// <para>
/// The failure without this is baffling rather than loud: the agent dutifully archives the entire
/// multi-gigabyte prefix, the 200 MB upload cap rejects it, and the user is told their *save* is too
/// big. Naming the actual mistake ("that is the prefix, not the save folder") is the whole point.
/// </para>
/// </summary>
public static class SaveDirSanity
{
    /// <summary>Matches the server's default <c>Storage:MaxUploadMb</c>. Past this a push cannot succeed.</summary>
    public const long UploadCapBytes = 200L * 1024 * 1024;

    /// <summary>Problems with this save path, worst first. Empty means it looks like a save folder.</summary>
    public static IReadOnlyList<string> Inspect(string? saveDir, IEnumerable<string>? excludeGlobs = null)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir)) return problems;

        var full = Path.GetFullPath(saveDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(full);
        var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) &&
            string.Equals(full, Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("this is your HOME directory, not a save folder. Syncing it would archive " +
                         "everything you own — and a pull would overwrite it.");
            return problems; // nothing else worth saying
        }

        // A Wine prefix root: compatdata/<appid>/ holds pfx/, and pfx/ holds drive_c/. Any of those
        // three is the prefix, not a save folder — the save lives deeper, under drive_c/users/steamuser/.
        var isPrefixRoot =
            Directory.Exists(Path.Combine(full, "pfx", "drive_c")) ||
            leaf.Equals("pfx", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("drive_c", StringComparison.OrdinalIgnoreCase) ||
            parent.Equals("compatdata", StringComparison.OrdinalIgnoreCase);

        if (isPrefixRoot)
        {
            problems.Add($"'{leaf}' is a Wine PREFIX, not a save folder. The save lives inside it, " +
                         "typically under pfx/drive_c/users/steamuser/AppData/ or Documents/. " +
                         "Archiving the prefix would upload gigabytes of Windows runtime files.");
        }

        // A repeated tail ("…/76561197960271872/SaveGames/76561197960271872/SaveGames") is the
        // signature of a save folder mapped one or more levels TOO DEEP. Archives store paths
        // relative to the save root, so restoring an archive rooted at X into X/sub reproduces
        // `sub` underneath itself. Nothing else produces that shape, and it is silent: the pull
        // succeeds, the files land, and the game never sees them.
        if (RepeatedTail(full) is { } repeated)
        {
            problems.Add($"this path repeats '{repeated}' — it looks like a save folder mapped too " +
                         "deep, then restored into. An archive stores paths relative to its save root, " +
                         "so pulling into a folder that is already inside that root nests it again. " +
                         "Map the save ROOT (the folder the archive's top-level entries sit in), and " +
                         "delete the duplicated copy.");
        }

        // Size is the backstop: the path may be wrong in a way no name check anticipates.
        var (bytes, count) = Measure(saveDir, excludeGlobs);
        if (bytes > UploadCapBytes)
        {
            problems.Add($"this folder holds {Mb(bytes)} across {count} files, over the {Mb(UploadCapBytes)} " +
                         "upload cap — the push will be rejected. That size usually means the path points at " +
                         "a game install or a prefix rather than its saves.");
        }

        return problems;
    }

    /// <summary>
    /// The repeated trailing run of path segments, or null. <c>a/b/X/Y/X/Y</c> yields <c>X/Y</c>.
    /// </summary>
    /// <remarks>
    /// Longest run first, so the clearer <c>X/Y</c> is reported rather than a coincidental single
    /// segment. A run of one is still checked — <c>saves/saves</c> is the same mistake — but a
    /// legitimately repeating name (a game whose save folder is literally <c>Steam/Steam</c>) would
    /// trip it. That is why this is a warning in <c>doctor</c>, and never a refusal to sync.
    /// </remarks>
    private static string? RepeatedTail(string fullPath)
    {
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p.Length > 0)
            .ToArray();

        for (var run = parts.Length / 2; run >= 1; run--)
        {
            var tail = parts[^run..];
            var before = parts[^(run * 2)..^run];
            if (tail.SequenceEqual(before, StringComparer.OrdinalIgnoreCase))
                return string.Join('/', tail);
        }
        return null;
    }

    /// <summary>Total bytes and file count of exactly what would be archived (excludes + no symlinks).</summary>
    public static (long Bytes, int Files) Measure(string dir, IEnumerable<string>? excludeGlobs = null)
    {
        long bytes = 0;
        var files = SaveArchive.ListFiles(dir, excludeGlobs);
        foreach (var rel in files)
        {
            try { bytes += new FileInfo(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar))).Length; }
            catch { /* vanished or unreadable — not worth failing a diagnostic over */ }
        }
        return (bytes, files.Count);
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
