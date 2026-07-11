using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

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
    /// </summary>
    public static string HashDirectory(string sourceDir)
    {
        using var sha = SHA256.Create();

        if (!Directory.Exists(sourceDir))
            return Convert.ToHexString(new byte[32]).ToLowerInvariant();

        var files = EnumerateRelativeFiles(sourceDir);

        foreach (var rel in files)
        {
            // Mix in the relative path so renames/moves change the hash.
            var pathBytes = Encoding.UTF8.GetBytes(rel + "\n");
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            var full = Path.Combine(sourceDir, rel);
            using var fs = File.OpenRead(full);
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
    /// Zip a save directory into <paramref name="destinationZip"/>. Returns the
    /// content hash of the source directory (NOT of the zip bytes).
    /// </summary>
    public static string CreateArchive(string sourceDir, string destinationZip)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Save directory not found: {sourceDir}");

        var dir = Path.GetDirectoryName(destinationZip);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(destinationZip))
            File.Delete(destinationZip);

        ZipFile.CreateFromDirectory(sourceDir, destinationZip, CompressionLevel.Optimal, includeBaseDirectory: false);
        return HashDirectory(sourceDir);
    }

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

    private static List<string> EnumerateRelativeFiles(string root)
    {
        var rootFull = Path.GetFullPath(root);
        return Directory
            .EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(rootFull, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }
}
