using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;

namespace SaveLocker.Shared;

/// <summary>
/// Helpers for turning a save-game directory into a deterministic, hashable
/// zip archive and restoring it again. The content hash is computed over the
/// logical contents (relative paths + bytes), independent of zip metadata or
/// file ordering, so the same files always yield the same hash on any machine.
/// </summary>
public static class SaveArchive
{
    /// <summary>
    /// Compute a stable SHA-256 over a directory's contents. Files are ordered
    /// by their normalised relative path so the hash is reproducible across
    /// machines and runs. Returns the all-zero hash for a missing/empty dir.
    /// <paramref name="excludeGlobs"/> (e.g. <c>*.log</c>, <c>cache/**</c>) are skipped —
    /// pass the SAME globs used for <see cref="CreateArchive"/> so the hash matches the archive.
    /// </summary>
    public static string HashDirectory(string sourceDir, IEnumerable<string>? excludeGlobs = null)
    {
        using var sha = SHA256.Create();

        if (!Directory.Exists(sourceDir))
            return Convert.ToHexString(new byte[32]).ToLowerInvariant();

        var files = EnumerateRelativeFiles(sourceDir, excludeGlobs);

        foreach (var rel in files)
        {
            // Mix in the relative path so renames/moves change the hash.
            var pathBytes = Encoding.UTF8.GetBytes(rel + "\n");
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            var full = Path.Combine(sourceDir, rel);
            using var fs = OpenShared(full);
            var buffer = new byte[81920];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Compute the SHA-256 of an existing archive file on disk.</summary>
    public static string HashFile(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>File count and the newest per-file write time inside a save archive — read
    /// straight from the zip's own entry metadata, never re-extracted or re-hashed.
    /// <see cref="CreateArchive"/> has always stamped <c>entry.LastWriteTime</c> from the source
    /// file, so this works on archives written long before this method existed, not just future
    /// ones. Meant to help tell two conflicting versions apart beyond a bare size and upload time —
    /// by a human in the console today, and by whatever decides conflicts automatically later.</summary>
    public readonly record struct ArchiveStats(int FileCount, DateTime? NewestFileWriteUtc);

    /// <summary>Read <see cref="ArchiveStats"/> from an archive already on disk.</summary>
    public static ArchiveStats GetArchiveStats(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var count = 0;
        DateTime? newest = null;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry, no content
            count++;
            // entry.LastWriteTime is a DOS-format zip timestamp with no embedded offset — .UtcDateTime
            // would reinterpret the stored wall-clock value using THIS process's local timezone, which
            // is wrong whenever the server isn't in the same timezone the agent was in at upload time.
            // Take the wall-clock value as written and treat it as UTC instead, so the result is at
            // least deterministic and doesn't additionally depend on the server's configured timezone.
            var mtime = DateTime.SpecifyKind(entry.LastWriteTime.DateTime, DateTimeKind.Utc);
            if (newest is null || mtime > newest) newest = mtime;
        }
        return new ArchiveStats(count, newest);
    }

    /// <summary>
    /// Zip a save directory into <paramref name="destinationZip"/>, skipping files that
    /// match <paramref name="excludeGlobs"/>.
    /// <para>
    /// Returns nothing on purpose. Every caller already holds the content hash before it asks for an
    /// archive — <see cref="ComputeManifest"/> hands back the manifest and the aggregate hash from
    /// one pass over the bytes — so hashing them again here would be a second SHA-256 over the whole
    /// save folder for a value no call site reads.
    /// </para>
    /// </summary>
    public static void CreateArchive(string sourceDir, string destinationZip, IEnumerable<string>? excludeGlobs = null)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Save directory not found: {sourceDir}");

        PrepareDestination(destinationZip);

        // Add files individually (not ZipFile.CreateFromDirectory) so excluded files are skipped.
        using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        foreach (var rel in EnumerateRelativeFiles(sourceDir, excludeGlobs))
            AddEntry(zip, sourceDir, rel);
    }

    /// <summary>
    /// Zip only <paramref name="includePaths"/> (relative, forward-slash paths as returned by
    /// <see cref="ListFiles"/>/<see cref="ComputeManifest"/>) out of <paramref name="sourceDir"/> —
    /// the delta-upload payload, carrying just the files a per-file diff found changed. An empty
    /// list produces a valid, empty zip (e.g. a push whose only change is a deletion).
    /// <para>
    /// Unlike every other archive path here, these paths did NOT come from enumerating the disk —
    /// the SERVER names them. Containment is therefore checked per path: nothing that resolves
    /// outside the save folder may enter an upload, however it got into the list. The caller is
    /// expected to have already refused any path it did not itself declare; this is the floor under
    /// that, so a miss there cannot become an arbitrary file read.
    /// </para>
    /// </summary>
    public static void CreateArchiveSubset(string sourceDir, string destinationZip, IEnumerable<string> includePaths)
    {
        PrepareDestination(destinationZip);

        var rootFull = Path.GetFullPath(sourceDir);
        using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        foreach (var rel in includePaths)
        {
            var full = Path.GetFullPath(
                Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new UnsafeArchiveException(
                    $"Refusing to archive '{rel}': it resolves outside the save folder.");
            AddEntry(zip, sourceDir, rel);
        }
    }

    /// <summary>
    /// Per-file identity of every file <see cref="HashDirectory"/>/<see cref="CreateArchive"/> would
    /// act on — the same ordered, exclude-filtered file set, each with its own SHA-256 and size —
    /// together with the aggregate content hash <see cref="HashDirectory"/> would return for that
    /// same set, chained in the same order out of the SAME pass over the bytes.
    /// <para>
    /// Both answers come from one read of the save folder. Computing them separately read every file
    /// twice, which on the multi-gigabyte saves a per-file delta exists for is the dominant cost of
    /// a push — larger than the transfer the delta saves.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<FileManifestEntry> Files, string ContentHash) ComputeManifest(
        string sourceDir, IEnumerable<string>? excludeGlobs = null)
    {
        if (!Directory.Exists(sourceDir))
            return (Array.Empty<FileManifestEntry>(), Convert.ToHexString(new byte[32]).ToLowerInvariant());

        using var aggregate = SHA256.Create();
        var files = EnumerateRelativeFiles(sourceDir, excludeGlobs);
        var result = new List<FileManifestEntry>(files.Count);
        var buffer = new byte[81920];

        foreach (var rel in files)
        {
            // Mix in the relative path exactly as HashDirectory does, so the two agree.
            var pathBytes = Encoding.UTF8.GetBytes(rel + "\n");
            aggregate.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            var full = Path.Combine(sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
            using var perFile = SHA256.Create();
            using var fs = OpenShared(full);

            // Size is counted from the bytes actually read rather than taken from FileInfo, so a
            // file's declared size can never disagree with the hash beside it.
            long size = 0;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                size += read;
                aggregate.TransformBlock(buffer, 0, read, null, 0);
                perFile.TransformBlock(buffer, 0, read, null, 0);
            }
            perFile.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            result.Add(new FileManifestEntry(
                rel, Convert.ToHexString(perFile.Hash!).ToLowerInvariant(), size));
        }

        aggregate.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (result, Convert.ToHexString(aggregate.Hash!).ToLowerInvariant());
    }

    private static void PrepareDestination(string destinationZip)
    {
        var dir = Path.GetDirectoryName(destinationZip);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(destinationZip))
            File.Delete(destinationZip);
    }

    /// <summary>
    /// Add one file to <paramref name="zip"/> under its relative path.
    /// <para>
    /// The source file is OPENED FIRST, before the entry exists, and the order is load-bearing. A
    /// file that vanished between being listed and being archived makes
    /// <see cref="File.GetLastWriteTime"/> return 1601-01-01 rather than throwing, and
    /// <c>ZipArchiveEntry.LastWriteTime</c> rejects anything before 1980 with an
    /// ArgumentOutOfRangeException — an exception no caller up the stack expects, standing in for
    /// the FileNotFoundException that would have said what actually happened.
    /// </para>
    /// </summary>
    private static void AddEntry(ZipArchive zip, string sourceDir, string rel)
    {
        var full = Path.Combine(sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));

        // CreateEntryFromFile opens with FileShare.Read, which throws when a game still holds the
        // save open. Read with a permissive share instead — the agent's settle gate is what
        // guarantees the writer has actually finished.
        using var src = OpenShared(full);
        var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
        entry.LastWriteTime = ZipSafeTimestamp(File.GetLastWriteTime(full));
        using var dst = entry.Open();
        src.CopyTo(dst);
    }

    /// <summary>
    /// Zip's DOS-era timestamp field cannot represent anything outside 1980..2107, and the setter
    /// throws rather than clamping. Wine prefixes and restored-from-backup trees do carry epoch-era
    /// mtimes, and a save is not worth refusing over the timestamp we would have written beside it.
    /// </summary>
    private static DateTime ZipSafeTimestamp(DateTime value) =>
        value < ZipMinTime ? ZipMinTime : value > ZipMaxTime ? ZipMaxTime : value;

    private static readonly DateTime ZipMinTime = new(1980, 1, 1, 0, 0, 0);
    private static readonly DateTime ZipMaxTime = new(2107, 12, 31, 23, 59, 58);

    /// <summary>
    /// Open a file for reading while tolerating other processes that hold it open for
    /// writing or pending delete. Without this, a single open handle anywhere in the save
    /// tree fails the whole push.
    /// </summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Thrown when an archive is refused before anything is written. Never a partial restore.</summary>
    public sealed class UnsafeArchiveException(string message) : Exception(message);

    /// <summary>
    /// Ceiling on entries in one archive. A restore that needs more than this is not a save folder.
    /// Override with <c>SAVELOCKER_MAX_RESTORE_ENTRIES</c>.
    /// </summary>
    public static int MaxRestoreEntries =>
        ReadLimit("SAVELOCKER_MAX_RESTORE_ENTRIES", 100_000);

    /// <summary>
    /// Ceiling on TOTAL UNCOMPRESSED bytes. The upload cap (200 MB) applies to the compressed body,
    /// so a legitimate archive can expand well past it — but not without bound. A zip bomb expands a
    /// few KB into terabytes and fills the disk of a Deck that has no screen to complain on.
    /// Override with <c>SAVELOCKER_MAX_RESTORE_MB</c>.
    /// </summary>
    public static long MaxRestoreBytes =>
        ReadLimit("SAVELOCKER_MAX_RESTORE_MB", 2048) * 1024L * 1024L;

    private static int ReadLimit(string envVar, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(envVar), out var v) && v > 0 ? v : fallback;

    /// <summary>
    /// Restore an archive into <paramref name="targetDir"/>. Staging is done in
    /// <paramref name="stagingRoot"/> when provided (recommended for paths inside
    /// OneDrive or other redirected folders where Directory.Move is blocked by the
    /// filesystem filter driver). Falls back to a temp folder beside the target when
    /// omitted. Files are copied individually so no directory rename ever touches the
    /// target tree; files absent from the archive are deleted from the target.
    /// <para>
    /// The archive is treated as <b>hostile input</b>: it arrives over the network from a server the
    /// agent may have been pointed at by a forged enrollment file (Decisions.md §4). It is size- and
    /// count-checked before extraction, and no destination path may traverse a symlink.
    /// </para>
    /// </summary>
    public static void RestoreArchive(string archiveZip, string targetDir, string? stagingRoot = null)
    {
        if (!File.Exists(archiveZip))
            throw new FileNotFoundException($"Archive not found: {archiveZip}");

        var stageParent = stagingRoot
            ?? Path.GetDirectoryName(Path.GetFullPath(targetDir.TrimEnd(Path.DirectorySeparatorChar)))!;
        Directory.CreateDirectory(stageParent);

        var stagingDir = Path.Combine(stageParent, $".lgs-staging-{DateTime.UtcNow.Ticks}");
        try
        {
            Directory.CreateDirectory(stagingDir);
            ExtractChecked(archiveZip, stagingDir);

            Directory.CreateDirectory(targetDir);

            // Resolve the target root through a link before anything else. A user symlinking their
            // save folder (onto an SD card, say) is legitimate and must keep working — so the root
            // is FOLLOWED. What must not be followed is any component BELOW it, because those come
            // from paths the archive chose.
            var targetFull = ResolveRoot(targetDir);

            // Copy every file from staging into the target (overwrite existing).
            var stagingFull = Path.GetFullPath(stagingDir);

            // Refuse BEFORE the copy/delete passes if this save folder is mapped deeper than the one
            // the archive was made from. Nothing else catches it, and the damage is silent.
            if (NestedRestoreDepth(stagingFull, targetFull) is var depth && depth > 0)
            {
                var repeated = string.Join('/', SplitPath(targetFull)[^depth..]);
                throw new UnsafeArchiveException(
                    $"REFUSED the server's save: this machine's save folder is {depth} level(s) deeper " +
                    $"than the one this save was archived from — both end in '{repeated}'. Restoring " +
                    "would nest that path under itself, and would DELETE the correctly-placed files " +
                    "on the way (they are absent from the archive at that depth). Map this game to " +
                    $"the folder that CONTAINS '{repeated}', so every machine's save root is the same " +
                    "level. Set SAVELOCKER_ALLOW_NESTED_RESTORE=1 only if this really is a save " +
                    "folder that legitimately repeats its own name.");
            }
            var checkedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stagingFull, src);
                var dst = Path.Combine(targetFull, rel);

                // The delete pass below is no-follow, but this copy pass was not: if the target
                // already contained a symlinked directory and the archive carried a matching path,
                // File.Copy wrote straight THROUGH the link and overwrote a file outside the save
                // folder. Creating the parents here is what made it reachable.
                EnsureNoLinkBelowRoot(targetFull, dst, checkedDirs);

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }

            // Remove files in the target that are no longer in the archive.
            //
            // This is the most dangerous loop in the codebase: it DELETES. It must never walk through
            // a symlink, or a link inside a save folder would let it delete files outside that folder
            // entirely (a link to $HOME in a Wine prefix is not hypothetical). Links themselves are
            // skipped, never deleted — we did not archive them, so their absence from the archive
            // must not read as "the user removed this file".
            var archiveRel = EnumerateFilesNoFollow(stagingFull)
                .Select(f => Path.GetRelativePath(stagingFull, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var tgt in EnumerateFilesNoFollow(targetFull))
            {
                if (!archiveRel.Contains(Path.GetRelativePath(targetFull, tgt)))
                    File.Delete(tgt);
            }

            // Prune empty subdirectories left behind by deletions (deepest first, links skipped —
            // deleting a symlinked directory would remove the user's link, which we never created).
            foreach (var dir in EnumerateDirsNoFollow(targetFull))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    try { Directory.Delete(dir); } catch { /* best-effort */ }
            }
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
        }
    }

    /// <summary>
    /// Extract with the archive treated as hostile: bounded entry count, bounded uncompressed size,
    /// and no path escaping the staging directory.
    /// <para>
    /// The declared sizes in the central directory are checked first because it is cheap and rejects
    /// an obvious bomb before a single byte lands — but <b>they are attacker-controlled and may lie</b>,
    /// so the real cap is enforced against bytes actually written. A zip that understates its size
    /// hits the running total instead.
    /// </para>
    /// </summary>
    private static void ExtractChecked(string archiveZip, string stagingDir)
    {
        var maxEntries = MaxRestoreEntries;
        var maxBytes = MaxRestoreBytes;

        using var zip = ZipFile.OpenRead(archiveZip);

        if (zip.Entries.Count > maxEntries)
            throw new UnsafeArchiveException(
                $"Archive has {zip.Entries.Count:N0} entries, over the {maxEntries:N0} limit. " +
                "Refusing to extract it. If this is genuinely your save, raise SAVELOCKER_MAX_RESTORE_ENTRIES.");

        long declared = 0;
        foreach (var entry in zip.Entries)
        {
            declared += entry.Length;
            if (declared > maxBytes)
                throw new UnsafeArchiveException(
                    $"Archive expands to at least {Mb(declared)}, over the {Mb(maxBytes)} limit. " +
                    "Refusing to extract it. If this is genuinely your save, raise SAVELOCKER_MAX_RESTORE_MB.");
        }

        var stagingFull = Path.GetFullPath(stagingDir);
        long written = 0;

        foreach (var entry in zip.Entries)
        {
            // A directory entry (trailing separator, no name) carries no content.
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var dst = Path.GetFullPath(Path.Combine(stagingFull, entry.FullName));

            // Zip-slip. .NET's ExtractToDirectory also rejects this, but extraction is hand-rolled
            // here for the size cap, so the check has to be hand-rolled with it.
            if (!dst.StartsWith(stagingFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new UnsafeArchiveException(
                    $"Archive entry '{entry.FullName}' resolves outside the target directory. Refusing to extract it.");

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            using var src = entry.Open();
            using var outFile = new FileStream(dst, FileMode.Create, FileAccess.Write);
            var buffer = new byte[81920];
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > maxBytes)
                    throw new UnsafeArchiveException(
                        $"Archive expanded past the {Mb(maxBytes)} limit while extracting " +
                        $"(its declared size understated it). Refusing to continue.");
                outFile.Write(buffer, 0, read);
            }
        }
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.#} MB";

    /// <summary>
    /// Follow a link on the save root itself — the user configured that path, so a save folder
    /// symlinked onto an SD card is legitimate and keeps working. Everything below it is not
    /// user-chosen and is checked by <see cref="EnsureNoLinkBelowRoot"/>.
    /// </summary>
    private static string ResolveRoot(string targetDir)
    {
        var full = Path.GetFullPath(targetDir);
        try
        {
            var info = new DirectoryInfo(full);
            if (info.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null) return Path.GetFullPath(resolved.FullName);
            }
        }
        catch { /* not a link, or unresolvable — treat the path as given */ }
        return full;
    }

    /// <summary>
    /// Refuse a destination whose path crosses a symlink or junction anywhere below the save root.
    ///
    /// The archive picks these relative paths. If the target already holds <c>sub -&gt; /home/user</c>
    /// and the archive carries <c>sub/.bashrc</c>, an unchecked copy overwrites the real
    /// <c>~/.bashrc</c> — outside the save folder entirely, with attacker-chosen bytes. The whole
    /// restore is rejected rather than skipping the file, so a partial restore never masquerades as
    /// a complete one.
    /// </summary>
    private static void EnsureNoLinkBelowRoot(string rootFull, string dstFull, HashSet<string> alreadyChecked)
    {
        var dir = Path.GetDirectoryName(dstFull);
        var pending = new List<string>();

        while (dir is not null &&
               dir.Length > rootFull.Length &&
               dir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if (alreadyChecked.Contains(dir)) break;
            pending.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }

        // Shallowest first, so the error names the outermost link rather than a leaf under it.
        pending.Reverse();
        foreach (var component in pending)
        {
            var info = new DirectoryInfo(component);
            if (info.Exists && IsLink(info))
                throw new UnsafeArchiveException(
                    $"Refusing to restore: '{component}' is a symlink/junction, so writing the archive " +
                    "there would modify files outside the save folder. Remove the link, or point the " +
                    "game's save folder at the real directory.");

            // A file sitting where the archive wants a directory is the same escape in a different
            // shape — Directory.CreateDirectory would fail, but check it while we are here.
            if (File.Exists(component))
                throw new UnsafeArchiveException(
                    $"Refusing to restore: '{component}' is a file, but the archive expects a directory there.");

            alreadyChecked.Add(component);
        }
    }

    /// <summary>
    /// The exact file set that <see cref="HashDirectory"/> and <see cref="CreateArchive"/> act on —
    /// ordered, forward-slash relative paths, excludes applied. Callers that need to inspect the
    /// same files (e.g. waiting for writes to settle) should use this so they never disagree
    /// with what actually gets archived.
    /// </summary>
    public static IReadOnlyList<string> ListFiles(string root, IEnumerable<string>? excludeGlobs = null) =>
        Directory.Exists(root) ? EnumerateRelativeFiles(root, excludeGlobs) : Array.Empty<string>();

    /// <summary>
    /// Raised (best-effort) when a symlink or junction is skipped, so the agent can say so rather
    /// than silently omitting a file the user expected to be synced.
    /// </summary>
    public static Action<string>? OnSymlinkSkipped { get; set; }

    /// <summary>
    /// True for a symlink or junction — an entry whose contents live somewhere else.
    /// <para>
    /// <b>This deliberately does NOT test <see cref="FileAttributes.ReparsePoint"/>.</b> Symlinks are
    /// reparse points, but so are <b>OneDrive files-on-demand placeholders</b>, and a real save file
    /// in a OneDrive folder is an ordinary file we must archive (see Gotchas.md). Skipping every
    /// reparse point would therefore stop syncing OneDrive saves entirely — silently. <c>LinkTarget</c>
    /// is non-null only for the symlink and junction reparse tags, which is exactly the set we mean.
    /// </para>
    /// </summary>
    private static string[] SplitPath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p.Length > 0)
            .ToArray();

    /// <summary>
    /// How many levels too deep the target is, or 0 when it lines up with the archive.
    /// <para>
    /// An archive stores paths relative to <b>the save root of the machine that made it</b>, and
    /// carries no record of what that root was. So if one machine maps a game at <c>X</c> and
    /// another at <c>X/sub</c>, restoring the first's archive into the second recreates <c>sub</c>
    /// beneath itself — and the delete pass then removes the correctly-placed files, because at that
    /// depth they do not appear in the archive at all. Both halves are silent: the pull SUCCEEDS.
    /// </para>
    /// <para>
    /// The signal is exact rather than a name heuristic: <b>every</b> entry in the archive must share
    /// the same leading <c>k</c> segments, and the target's last <c>k</c> segments must equal them.
    /// Deepest match wins, so <c>a/b</c> is reported rather than a coincidental <c>b</c>.
    /// </para>
    /// </summary>
    private static int NestedRestoreDepth(string stagingFull, string targetFull)
    {
        if (Environment.GetEnvironmentVariable("SAVELOCKER_ALLOW_NESTED_RESTORE") == "1") return 0;

        var entries = EnumerateFilesNoFollow(stagingFull)
            .Select(f => SplitPath(Path.GetRelativePath(stagingFull, f)))
            .ToList();
        if (entries.Count == 0) return 0;

        var target = SplitPath(targetFull);
        // A file sits at the end of every entry, so a shared directory prefix stops one before it.
        var maxDepth = Math.Min(entries.Min(e => e.Length) - 1, target.Length);

        for (var k = maxDepth; k >= 1; k--)
        {
            var prefix = entries[0][..k];
            if (!entries.All(e => e[..k].SequenceEqual(prefix, StringComparer.OrdinalIgnoreCase)))
                continue;
            if (target[^k..].SequenceEqual(prefix, StringComparer.OrdinalIgnoreCase))
                return k;
        }
        return 0;
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        try { return entry.LinkTarget is not null; }
        catch { return false; }
    }

    /// <summary>
    /// Walk <paramref name="rootFull"/> WITHOUT following symlinks or junctions, yielding full file
    /// paths. <c>Directory.EnumerateFiles(..., AllDirectories)</c> follows them, and a Wine prefix is
    /// full of them — a save folder containing a link to <c>/etc</c> or <c>$HOME</c> would otherwise be
    /// pulled into the archive, and (far worse) the restore's delete pass would reach through the link
    /// and delete files OUTSIDE the save folder.
    /// <para>The link itself is skipped, not followed: its target is not ours to sync or to delete.</para>
    /// </summary>
    private static IEnumerable<string> EnumerateFilesNoFollow(string rootFull)
    {
        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var entry in entries)
            {
                if (IsLink(entry))
                {
                    OnSymlinkSkipped?.Invoke(entry.FullName);
                    continue;
                }
                if (entry is DirectoryInfo sub) stack.Push(sub.FullName);
                else yield return entry.FullName;
            }
        }
    }

    /// <summary>Directories under <paramref name="rootFull"/>, deepest first, never through a link.</summary>
    private static List<string> EnumerateDirsNoFollow(string rootFull)
    {
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(dir).GetDirectories(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subs)
            {
                if (IsLink(sub)) continue;
                found.Add(sub.FullName);
                stack.Push(sub.FullName);
            }
        }

        found.Sort((a, b) => b.Length.CompareTo(a.Length)); // deepest first
        return found;
    }

    /// <summary>
    /// Ordered, forward-slash relative paths of the files under <paramref name="root"/>,
    /// minus any matching <paramref name="excludeGlobs"/>. Ordering is stable (Ordinal) so
    /// the hash is reproducible. The same result drives both hashing and archiving.
    /// </summary>
    private static List<string> EnumerateRelativeFiles(string root, IEnumerable<string>? excludeGlobs = null)
    {
        var rootFull = Path.GetFullPath(root);
        var all = EnumerateFilesNoFollow(rootFull)
            .Select(f => Path.GetRelativePath(rootFull, f).Replace('\\', '/'))
            .ToList();

        var globs = excludeGlobs?
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .ToList();
        if (globs is { Count: > 0 })
        {
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude("**/*");
            foreach (var g in globs)
            {
                matcher.AddExclude(g);
                // A bare filename pattern (no '/') should match at any depth, gitignore-style:
                // "*.log" excludes logs in every subfolder, not just the save root. Patterns
                // that already contain '/' are treated as explicit paths anchored at the root.
                if (!g.Contains('/')) matcher.AddExclude("**/" + g);
            }
            var kept = new HashSet<string>(matcher.Match(all).Files.Select(m => m.Path), StringComparer.Ordinal);
            all = all.Where(kept.Contains).ToList();
        }

        all.Sort(StringComparer.Ordinal);
        return all;
    }
}
