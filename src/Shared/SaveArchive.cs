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

    /// <summary>
    /// Zip a save directory into <paramref name="destinationZip"/>, skipping files that
    /// match <paramref name="excludeGlobs"/>. Returns the content hash of the archived
    /// contents (NOT of the zip bytes) — computed over the same filtered file set.
    /// </summary>
    public static string CreateArchive(string sourceDir, string destinationZip, IEnumerable<string>? excludeGlobs = null)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Save directory not found: {sourceDir}");

        var dir = Path.GetDirectoryName(destinationZip);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(destinationZip))
            File.Delete(destinationZip);

        // Add files individually (not ZipFile.CreateFromDirectory) so excluded files are skipped.
        var files = EnumerateRelativeFiles(sourceDir, excludeGlobs);
        using (var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create))
        {
            foreach (var rel in files)
            {
                var full = Path.Combine(sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTime(full);

                // CreateEntryFromFile opens with FileShare.Read, which throws when a game still
                // holds the save open. Read with a permissive share instead — the agent's settle
                // gate is what guarantees the writer has actually finished.
                using var src = OpenShared(full);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        return HashDirectory(sourceDir, excludeGlobs);
    }

    /// <summary>
    /// Open a file for reading while tolerating other processes that hold it open for
    /// writing or pending delete. Without this, a single open handle anywhere in the save
    /// tree fails the whole push.
    /// </summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Restore an archive into <paramref name="targetDir"/>. Staging is done in
    /// <paramref name="stagingRoot"/> when provided (recommended for paths inside
    /// OneDrive or other redirected folders where Directory.Move is blocked by the
    /// filesystem filter driver). Falls back to a temp folder beside the target when
    /// omitted. Files are copied individually so no directory rename ever touches the
    /// target tree; files absent from the archive are deleted from the target.
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
            ZipFile.ExtractToDirectory(archiveZip, stagingDir, overwriteFiles: true);

            Directory.CreateDirectory(targetDir);

            // Copy every file from staging into the target (overwrite existing).
            var stagingFull = Path.GetFullPath(stagingDir);
            foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stagingFull, src);
                var dst = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }

            // Remove files in the target that are no longer in the archive.
            var targetFull = Path.GetFullPath(targetDir);
            var archiveRel = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(stagingFull, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var tgt in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                if (!archiveRel.Contains(Path.GetRelativePath(targetFull, tgt)))
                    File.Delete(tgt);
            }

            // Prune empty subdirectories left behind by deletions.
            foreach (var dir in Directory.EnumerateDirectories(targetDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
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
    /// The exact file set that <see cref="HashDirectory"/> and <see cref="CreateArchive"/> act on —
    /// ordered, forward-slash relative paths, excludes applied. Callers that need to inspect the
    /// same files (e.g. waiting for writes to settle) should use this so they never disagree
    /// with what actually gets archived.
    /// </summary>
    public static IReadOnlyList<string> ListFiles(string root, IEnumerable<string>? excludeGlobs = null) =>
        Directory.Exists(root) ? EnumerateRelativeFiles(root, excludeGlobs) : Array.Empty<string>();

    /// <summary>
    /// Ordered, forward-slash relative paths of the files under <paramref name="root"/>,
    /// minus any matching <paramref name="excludeGlobs"/>. Ordering is stable (Ordinal) so
    /// the hash is reproducible. The same result drives both hashing and archiving.
    /// </summary>
    private static List<string> EnumerateRelativeFiles(string root, IEnumerable<string>? excludeGlobs = null)
    {
        var rootFull = Path.GetFullPath(root);
        var all = Directory
            .EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
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
