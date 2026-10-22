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
    /// machines and runs. Returns the all-zero hash for a missing dir.
    /// <paramref name="excludeGlobs"/> (e.g. <c>*.log</c>, <c>cache/**</c>) are skipped —
    /// pass the SAME globs used for <see cref="CreateArchive(string, string, IEnumerable{string}?, IEnumerable{string}?)"/>
    /// so the hash matches the archive. <paramref name="includeGlobs"/>, when non-empty, narrows the set
    /// to files matching at least one of them before excludes apply — see <see cref="FilterIncluded"/>.
    /// </summary>
    public static string HashDirectory(string sourceDir, IEnumerable<string>? excludeGlobs = null,
        IEnumerable<string>? includeGlobs = null) =>
        HashDirectory(new[] { SaveRoot.Primary(sourceDir, includeGlobs) }, excludeGlobs);

    /// <summary>
    /// <see cref="HashDirectory(string, IEnumerable{string}?, IEnumerable{string}?)"/> over every save
    /// folder of a game at once: one hash over every file's archive name and bytes, in one Ordinal
    /// order of the final names (see <see cref="ReservedPrefix"/>). Returns the all-zero hash when none
    /// of the folders exists.
    /// </summary>
    public static string HashDirectory(IReadOnlyList<SaveRoot> roots, IEnumerable<string>? excludeGlobs = null) =>
        Digest(roots, excludeGlobs, perFile: false).ContentHash;

    /// <summary>Compute the SHA-256 of an existing archive file on disk.</summary>
    public static string HashFile(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>File count and the newest per-file write time inside a save archive — read
    /// straight from the zip's own entry metadata, never re-extracted or re-hashed. Meant to help
    /// tell two conflicting versions apart beyond a bare size and upload time — by a human in the
    /// console today, and by whatever decides conflicts automatically later.</summary>
    public readonly record struct ArchiveStats(int FileCount, DateTime? NewestFileWriteUtc);

    /// <summary>Read <see cref="ArchiveStats"/> from an archive already on disk. A save folder's
    /// marker (<see cref="MarkerName"/>) is not a file of the save and is not counted.</summary>
    public static ArchiveStats GetArchiveStats(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var count = 0;
        DateTime? newest = null;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry, no content
            if (IsMarker(entry.FullName)) continue;
            count++;
            var mtime = EntryWriteTimeUtc(entry);
            if (newest is null || mtime > newest) newest = mtime;
        }
        return new ArchiveStats(count, newest);
    }

    /// <summary>One save folder's files inside an archive, as the console lists them.</summary>
    /// <param name="Files">Relative to the folder, Ordinal order, at most the cap the caller asked for.</param>
    public sealed record ArchiveFolder(string Key, int FileCount, long TotalBytes,
        IReadOnlyList<(string Path, long Size, DateTime? ModifiedUtc)> Files);

    /// <summary>
    /// The archive's files grouped by save folder (tasks/multiple-save-paths plan §1): the primary
    /// folder's at the root, each extra folder's under its own prefix, names relative to their folder.
    /// An extra folder whose marker is there but which holds nothing is listed empty — this version
    /// holds it, emptied. Read from the zip's directory, never extracted.
    /// </summary>
    public static IReadOnlyList<ArchiveFolder> ListArchiveFolders(string zipPath, int maxFilesPerFolder = 500)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var folders = new SortedDictionary<string, List<(string, long, DateTime?)>>(StringComparer.Ordinal)
        {
            [SaveRoot.PrimaryKey] = new(),
        };
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry, no content
            var name = entry.FullName.Replace('\\', '/');
            if (IsMarker(name))
            {
                var marked = name[KeysPrefix.Length..];
                if (SaveRoot.ValidateExtraKey(marked) is null && !folders.ContainsKey(marked)) folders[marked] = new();
                continue;
            }
            var (key, rel) = TrySplitExtra(name, out var k, out var r) ? (k, r) : (SaveRoot.PrimaryKey, name);
            if (!folders.TryGetValue(key, out var files)) folders[key] = files = new();
            files.Add((rel, entry.Length, EntryWriteTimeUtc(entry)));
        }

        // The primary folder first, then the extra ones by key.
        return folders
            .OrderBy(f => f.Key == SaveRoot.PrimaryKey ? 0 : 1).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new ArchiveFolder(f.Key, f.Value.Count, f.Value.Sum(x => x.Item2),
                f.Value.OrderBy(x => x.Item1, StringComparer.Ordinal).Take(maxFilesPerFolder).ToList()))
            .ToList();
    }

    /// <summary>
    /// Where an entry's real UTC write time lives: its comment, as <c>mtime-utc=</c> + a round-trip
    /// ("o") UTC timestamp.
    /// <para>
    /// Zip's own timestamp is a DOS date/time with no zone — by the spec's convention the writer's
    /// local wall clock — so it cannot say when a file was written unless you also know where. Read
    /// as UTC it came out shifted by the uploader's offset (+3 h from a UTC+3 machine, 2026-09-28).
    /// That field stays the local wall clock, so Explorer, 7-Zip and every older reader show what they
    /// always did; the UTC record rides beside it. Not the "extended timestamp" extra field (0x5455),
    /// which would be the standard home for it: .NET 10's zip API cannot write extra fields, and
    /// hand-assembling zip headers in the one format every save passes through is not worth that.
    /// </para>
    /// <para>
    /// It is per ENTRY, not per archive, because a delta rebuild on the server copies entries out of
    /// older archives into a new one — an archive can hold both kinds.
    /// </para>
    /// </summary>
    private const string UtcWriteTimePrefix = "mtime-utc=";

    /// <summary>
    /// When the entry's file was last written, in UTC. An entry written before the UTC record existed
    /// has only the uploader's wall clock and nothing saying which timezone that was; it reads as it
    /// always has — that wall clock labelled UTC, off by the uploader's offset — rather than a guess.
    /// Deterministic either way: never interpreted in the reading process's own timezone.
    /// </summary>
    public static DateTime EntryWriteTimeUtc(ZipArchiveEntry entry) =>
        TryReadUtcWriteTime(entry.Comment, out var utc)
            ? utc
            : DateTime.SpecifyKind(entry.LastWriteTime.DateTime, DateTimeKind.Utc);

    /// <summary>Stamp both: the local wall clock zip tools expect, and the UTC record beside it.</summary>
    public static void StampWriteTime(ZipArchiveEntry entry, DateTime writtenUtc)
    {
        var utc = DateTime.SpecifyKind(ZipSafeTimestamp(writtenUtc), DateTimeKind.Utc);
        entry.LastWriteTime = ZipSafeTimestamp(utc.ToLocalTime());
        entry.Comment = UtcWriteTimePrefix + utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Carry an entry's write time into a copy of it. Only a well-formed UTC record crosses: the
    /// source may be an agent's upload, and whatever else its comment says is not ours to store.
    /// </summary>
    public static void CopyWriteTime(ZipArchiveEntry from, ZipArchiveEntry to)
    {
        to.LastWriteTime = from.LastWriteTime;
        if (TryReadUtcWriteTime(from.Comment, out var utc))
            to.Comment = UtcWriteTimePrefix + utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryReadUtcWriteTime(string? comment, out DateTime utc)
    {
        utc = default;
        return comment is not null
            && comment.StartsWith(UtcWriteTimePrefix, StringComparison.Ordinal)
            && DateTime.TryParseExact(comment[UtcWriteTimePrefix.Length..], "o",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out utc)
            && utc.Kind == DateTimeKind.Utc;
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
    public static void CreateArchive(string sourceDir, string destinationZip, IEnumerable<string>? excludeGlobs = null,
        IEnumerable<string>? includeGlobs = null) =>
        CreateArchive(new[] { SaveRoot.Primary(sourceDir, includeGlobs) }, destinationZip, excludeGlobs);

    /// <summary>
    /// Zip every save folder of a game into one archive: the primary folder's files at the root, each
    /// extra folder's under its key, plus that folder's marker (<see cref="ReservedPrefix"/>). An extra
    /// folder that does not exist contributes nothing; the primary one must exist.
    /// </summary>
    public static void CreateArchive(IReadOnlyList<SaveRoot> roots, string destinationZip,
        IEnumerable<string>? excludeGlobs = null)
    {
        ValidateRoots(roots);
        var primary = roots.Single(r => r.IsPrimary);
        if (!Directory.Exists(primary.Directory))
            throw new DirectoryNotFoundException($"Save directory not found: {primary.Directory}");

        PrepareDestination(destinationZip);

        // Add files individually (not ZipFile.CreateFromDirectory) so excluded files are skipped.
        using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        foreach (var item in EnumerateItems(roots, excludeGlobs))
            AddItem(zip, item);
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
    public static void CreateArchiveSubset(string sourceDir, string destinationZip, IEnumerable<string> includePaths) =>
        CreateArchiveSubset(new[] { SaveRoot.Primary(sourceDir) }, destinationZip, includePaths);

    /// <summary>
    /// <see cref="CreateArchiveSubset(string, string, IEnumerable{string})"/> across a game's save
    /// folders: each name is mapped back to the folder its prefix names (<see cref="ReservedPrefix"/>)
    /// and contained there. A name for a key this game does not have, or a marker for a folder that
    /// does not exist here, is refused — the server named it, and nothing here declared it.
    /// </summary>
    public static void CreateArchiveSubset(IReadOnlyList<SaveRoot> roots, string destinationZip,
        IEnumerable<string> includePaths)
    {
        ValidateRoots(roots);
        PrepareDestination(destinationZip);

        using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        foreach (var name in includePaths)
        {
            if (IsMarker(name))
            {
                var key = name[KeysPrefix.Length..];
                if (!roots.Any(r => !r.IsPrimary && r.Key == key && Directory.Exists(r.Directory)))
                    throw new UnsafeArchiveException(
                        $"Refusing to archive '{name}': it is not a save folder this game has here.");
                AddItem(zip, new SaveItem(name, null));
                continue;
            }

            if (ResolveName(roots, name) is not var (root, rel))
                throw new UnsafeArchiveException(
                    $"Refusing to archive '{name}': it names no save folder of this game.");

            var rootFull = Path.GetFullPath(root.Directory);
            var full = Path.GetFullPath(
                Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new UnsafeArchiveException(
                    $"Refusing to archive '{name}': it resolves outside the save folder.");
            AddItem(zip, new SaveItem(name, full));
        }
    }

    /// <summary>
    /// Per-file identity of every file <see cref="HashDirectory(string, IEnumerable{string}?, IEnumerable{string}?)"/>/
    /// <see cref="CreateArchive(string, string, IEnumerable{string}?, IEnumerable{string}?)"/> would
    /// act on — the same ordered, exclude-filtered file set, each with its own SHA-256 and size —
    /// together with the aggregate content hash HashDirectory would return for that
    /// same set, chained in the same order out of the SAME pass over the bytes.
    /// <para>
    /// Both answers come from one read of the save folder. Computing them separately read every file
    /// twice, which on the multi-gigabyte saves a per-file delta exists for is the dominant cost of
    /// a push — larger than the transfer the delta saves.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<FileManifestEntry> Files, string ContentHash) ComputeManifest(
        string sourceDir, IEnumerable<string>? excludeGlobs = null, IEnumerable<string>? includeGlobs = null) =>
        ComputeManifest(new[] { SaveRoot.Primary(sourceDir, includeGlobs) }, excludeGlobs);

    /// <summary>
    /// <see cref="ComputeManifest(string, IEnumerable{string}?, IEnumerable{string}?)"/> over every save
    /// folder of a game. Paths are archive names; each existing extra folder's marker is listed as an
    /// empty file, so the server's delta copy-forward carries it like any other entry.
    /// </summary>
    public static (IReadOnlyList<FileManifestEntry> Files, string ContentHash) ComputeManifest(
        IReadOnlyList<SaveRoot> roots, IEnumerable<string>? excludeGlobs = null) =>
        Digest(roots, excludeGlobs, perFile: true);

    private static (IReadOnlyList<FileManifestEntry> Files, string ContentHash) Digest(
        IReadOnlyList<SaveRoot> roots, IEnumerable<string>? excludeGlobs, bool perFile)
    {
        ValidateRoots(roots);
        if (!roots.Any(r => Directory.Exists(r.Directory)))
            return (Array.Empty<FileManifestEntry>(), Convert.ToHexString(new byte[32]).ToLowerInvariant());

        using var aggregate = SHA256.Create();
        var items = EnumerateItems(roots, excludeGlobs);
        var result = new List<FileManifestEntry>(perFile ? items.Count : 0);
        var buffer = new byte[81920];

        foreach (var item in items)
        {
            // Mix in the archive name so renames/moves change the hash. The server's delta rebuild
            // (SyncService.ReconstructDelta) chains name + bytes in this same order.
            var pathBytes = Encoding.UTF8.GetBytes(item.Name + "\n");
            aggregate.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            if (item.FullPath is null)
            {
                if (perFile) result.Add(new FileManifestEntry(item.Name, EmptySha256, 0));
                continue;
            }

            using var perFileSha = perFile ? SHA256.Create() : null;
            using var fs = OpenShared(item.FullPath);

            // Size is counted from the bytes actually read rather than taken from FileInfo, so a
            // file's declared size can never disagree with the hash beside it.
            long size = 0;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                size += read;
                aggregate.TransformBlock(buffer, 0, read, null, 0);
                perFileSha?.TransformBlock(buffer, 0, read, null, 0);
            }
            if (perFileSha is null) continue;
            perFileSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            result.Add(new FileManifestEntry(
                item.Name, Convert.ToHexString(perFileSha.Hash!).ToLowerInvariant(), size));
        }

        aggregate.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (result, Convert.ToHexString(aggregate.Hash!).ToLowerInvariant());
    }

    private static readonly string EmptySha256 =
        Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();

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
    /// <see cref="File.GetLastWriteTimeUtc"/> return 1601-01-01 rather than throwing, and
    /// <c>ZipArchiveEntry.LastWriteTime</c> rejects anything before 1980 with an
    /// ArgumentOutOfRangeException — an exception no caller up the stack expects, standing in for
    /// the FileNotFoundException that would have said what actually happened.
    /// </para>
    /// </summary>
    private static void AddItem(ZipArchive zip, SaveItem item)
    {
        if (item.FullPath is null)
        {
            // A marker carries no bytes; its existence is the whole message.
            var marker = zip.CreateEntry(item.Name, CompressionLevel.NoCompression);
            StampWriteTime(marker, DateTime.UtcNow);
            return;
        }

        // CreateEntryFromFile opens with FileShare.Read, which throws when a game still holds the
        // save open. Read with a permissive share instead — the agent's settle gate is what
        // guarantees the writer has actually finished.
        using var src = OpenShared(item.FullPath);
        var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal);
        StampWriteTime(entry, File.GetLastWriteTimeUtc(item.FullPath));
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

    /// <summary>
    /// Default for <c>Storage:MaxUploadMb</c>, shipped in the server's <c>appsettings.json</c>. The
    /// deployed value always wins; this is only the fallback when the key is absent, and what the
    /// agent's own size backstop assumes. Change the cap in appsettings, never by forking this.
    /// </summary>
    public const int DefaultMaxUploadMb = 500;

    /// <summary>Thrown when an archive is refused before anything is written. Never a partial restore.</summary>
    public sealed class UnsafeArchiveException(string message) : Exception(message);

    /// <summary>
    /// Thrown while listing a game's files when one file falls inside two of its save folders' include
    /// scopes. Unlike a bad folder set (an <see cref="ArgumentException"/> from the moment it is
    /// configured), this can start on any push, the first time a new file matches both, so it has its
    /// own type for a caller to report as "narrow one folder's include patterns".
    /// </summary>
    public sealed class OverlappingSaveFoldersException(string message) : Exception(message);

    /// <summary>
    /// Ceiling on entries in one archive. A restore that needs more than this is not a save folder.
    /// Override with <c>SAVELOCKER_MAX_RESTORE_ENTRIES</c>.
    /// </summary>
    public static int MaxRestoreEntries =>
        ReadLimit("SAVELOCKER_MAX_RESTORE_ENTRIES", 100_000);

    /// <summary>
    /// Ceiling on TOTAL UNCOMPRESSED bytes. The upload cap (500 MB) applies to the compressed body,
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
    public static void RestoreArchive(string archiveZip, string targetDir, string? stagingRoot = null,
        IEnumerable<string>? includeGlobs = null, IEnumerable<string>? excludeGlobs = null) =>
        RestoreArchive(archiveZip, new[] { SaveRoot.Primary(targetDir, includeGlobs) }, stagingRoot, excludeGlobs);

    /// <summary>What a multi-folder restore did: the keys it restored, and the keys the archive
    /// carried that this machine has no folder for (skipped, never written anywhere else).</summary>
    public sealed record RestoreResult(IReadOnlyList<string> RestoredKeys, IReadOnlyList<string> SkippedKeys);

    /// <summary>
    /// <see cref="RestoreArchive(string, string, string?, IEnumerable{string}?, IEnumerable{string}?)"/> into every save
    /// folder of a game, each from its own slice of the archive (<see cref="ReservedPrefix"/>).
    /// <para>
    /// The primary folder is always restored, exactly as a single-folder game's is. An extra folder is
    /// restored — its delete pass included — <b>only when the archive carries its marker</b>: without
    /// one this version simply does not contain that folder (it predates it, or came from a machine
    /// where the folder was missing), and treating that as "every file in it was deleted" would wipe
    /// it on every machine. An empty folder WITH a marker is a real deletion and does propagate.
    /// </para>
    /// <para>
    /// Every slice is checked — nesting depth, links below the root, a file another folder sharing
    /// the same directory also claims — before a single byte is written to any of them, so a bad
    /// slice can never leave the others half-restored. <paramref name="stagingRoot"/> defaults to the
    /// primary folder's parent.
    /// </para>
    /// <para>
    /// A file matching <paramref name="excludeGlobs"/> is this machine's own: a push never carries it, so
    /// the restore neither deletes it nor writes one an older archive still holds (Decisions.md §4).
    /// </para>
    /// </summary>
    public static RestoreResult RestoreArchive(string archiveZip, IReadOnlyList<SaveRoot> roots, string? stagingRoot = null,
        IEnumerable<string>? excludeGlobs = null)
    {
        if (!File.Exists(archiveZip))
            throw new FileNotFoundException($"Archive not found: {archiveZip}");
        ValidateRoots(roots);

        var excluded = IsExcluded(excludeGlobs);
        var primary = roots.Single(r => r.IsPrimary);
        var stageParent = stagingRoot
            ?? Path.GetDirectoryName(Path.GetFullPath(primary.Directory.TrimEnd(Path.DirectorySeparatorChar)))!;
        Directory.CreateDirectory(stageParent);

        var stagingDir = Path.Combine(stageParent, $".lgs-staging-{DateTime.UtcNow.Ticks}");
        try
        {
            Directory.CreateDirectory(stagingDir);
            ExtractChecked(archiveZip, stagingDir);
            var stagingFull = Path.GetFullPath(stagingDir);

            // Sort what arrived into one slice per key. Keys are compared case-insensitively because
            // a case-insensitive filesystem has already merged a hostile archive's spellings in staging.
            var slices = new Dictionary<string, List<StagedFile>>(StringComparer.OrdinalIgnoreCase)
            {
                [SaveRoot.PrimaryKey] = new()
            };
            var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in EnumerateFilesNoFollow(stagingFull))
            {
                var name = Path.GetRelativePath(stagingFull, src).Replace('\\', '/');
                if (IsMarker(name))
                    markers.Add(name[KeysPrefix.Length..]);
                else if (TrySplitExtra(name, out var key, out var rel))
                {
                    if (!slices.TryGetValue(key, out var slice)) slices[key] = slice = new();
                    slice.Add(new StagedFile(rel, src));
                }
                else if (!IsSliceName(name))
                    // Includes any OTHER .savelocker/ subtree — a later format this agent does not know.
                    // It rides in the primary folder byte-for-byte, as everything did on an older agent,
                    // so the next push carries it back and the hash still matches the head.
                    slices[SaveRoot.PrimaryKey].Add(new StagedFile(name, src));
                // A malformed name under paths/ belongs to no folder: never restored.
            }

            var plans = new List<RestorePlan>();
            foreach (var root in roots)
            {
                if (!root.IsPrimary && !markers.Contains(root.Key)) continue;
                var staged = slices.GetValueOrDefault(root.Key) ?? new List<StagedFile>();
                // Another machine may have archived the whole shared folder; its copy of some OTHER
                // game's save must not overwrite this machine's.
                var inScope = InScope(root);
                var prefix = NamePrefix(root);
                plans.Add(new RestorePlan(root,
                    // Resolve the target root through a link before anything else. A user symlinking
                    // their save folder (onto an SD card, say) is legitimate and must keep working — so
                    // the root is FOLLOWED. What must not be followed is any component BELOW it,
                    // because those come from paths the archive chose.
                    ResolveRoot(root.Directory),
                    staged.Where(f => inScope(f.Rel) && !excluded(prefix + f.Rel)).ToList()));
            }
            var skipped = markers.Concat(slices.Keys)
                .Where(k => k != SaveRoot.PrimaryKey &&
                            !roots.Any(r => string.Equals(r.Key, k, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToList();

            foreach (var plan in plans)
                CheckSlice(plan, roots);

            foreach (var plan in plans)
            {
                Directory.CreateDirectory(plan.Root.Directory);
                foreach (var file in plan.Files)
                {
                    var dst = Path.Combine(plan.TargetFull, file.Rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(file.Source, dst, overwrite: true);
                }
            }

            // Remove files in each target that are no longer in its slice.
            //
            // This is the most dangerous loop in the codebase: it DELETES. It must never walk through
            // a symlink, or a link inside a save folder would let it delete files outside that folder
            // entirely (a link to $HOME in a Wine prefix is not hypothetical). Links themselves are
            // skipped, never deleted — we did not archive them, so their absence from the archive
            // must not read as "the user removed this file". Only files in the folder's include scope
            // may go: in a shared emulator saves folder every other game's save is absent from this
            // game's archive. An excluded file is absent from every archive by design, so it stays.
            // The primary folder never touches the other folders' slices.
            foreach (var plan in plans)
            {
                var inSlice = plan.Files.Select(f => f.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var inScope = InScope(plan.Root);
                var prefix = NamePrefix(plan.Root);
                foreach (var tgt in EnumerateFilesNoFollow(plan.TargetFull))
                {
                    var rel = Path.GetRelativePath(plan.TargetFull, tgt).Replace('\\', '/');
                    if (plan.Root.IsPrimary && IsSliceName(rel)) continue;
                    if (inScope(rel) && !inSlice.Contains(rel) && !excluded(prefix + rel))
                        File.Delete(tgt);
                }
            }

            // Prune empty subdirectories left behind by deletions (deepest first, links skipped —
            // deleting a symlinked directory would remove the user's link, which we never created).
            // A scoped folder is shared with other games, and its directories are not ours to remove.
            foreach (var plan in plans.Where(p => !p.Root.HasIncludeScope))
            {
                foreach (var dir in EnumerateDirsNoFollow(plan.TargetFull))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        try { Directory.Delete(dir); } catch { /* best-effort */ }
                }
            }

            return new RestoreResult(plans.Select(p => p.Root.Key).ToList(), skipped);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
        }
    }

    private sealed record StagedFile(string Rel, string Source);

    private sealed record RestorePlan(SaveRoot Root, string TargetFull, List<StagedFile> Files);

    /// <summary>Every refusal a slice can earn, raised before any slice is written.</summary>
    private static void CheckSlice(RestorePlan plan, IReadOnlyList<SaveRoot> roots)
    {
        // Refuse if this save folder is mapped deeper than the one the archive was made from.
        // Nothing else catches it, and the damage is silent.
        if (NestedRestoreDepth(plan.Files.Select(f => f.Rel), plan.TargetFull) is var depth && depth > 0)
        {
            var repeated = string.Join('/', SplitPath(plan.TargetFull)[^depth..]);
            throw new UnsafeArchiveException(
                $"REFUSED the server's save: this machine's save folder is {depth} level(s) deeper " +
                $"than the one this save was archived from — both end in '{repeated}'. Restoring " +
                "would nest that path under itself, and would DELETE the correctly-placed files " +
                "on the way (they are absent from the archive at that depth). Map this game to " +
                $"the folder that CONTAINS '{repeated}', so every machine's save root is the same " +
                "level. Set SAVELOCKER_ALLOW_NESTED_RESTORE=1 only if this really is a save " +
                "folder that legitimately repeats its own name.");
        }

        // The delete pass is no-follow, but a copy is not: if the target already contained a
        // symlinked directory and the archive carried a matching path, File.Copy would write straight
        // THROUGH the link and overwrite a file outside the save folder.
        var checkedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files)
            EnsureNoLinkBelowRoot(plan.TargetFull,
                Path.Combine(plan.TargetFull, file.Rel.Replace('/', Path.DirectorySeparatorChar)), checkedDirs);

        // Two folders of one game may share a directory only with include scopes that keep their
        // files apart; a file both would claim belongs to neither safely.
        foreach (var other in roots)
        {
            if (ReferenceEquals(other, plan.Root) || !SameDirectory(other.Directory, plan.Root.Directory)) continue;
            var claimed = InScope(other);
            if (plan.Files.FirstOrDefault(f => claimed(f.Rel)) is { } clash)
                throw new UnsafeArchiveException(
                    $"Refusing to restore: '{clash.Rel}' would belong to both the '{plan.Root.Key}' and " +
                    $"'{other.Key}' save folders, which share '{plan.Root.Directory}'.");
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
    public static IReadOnlyList<string> ListFiles(string root, IEnumerable<string>? excludeGlobs = null,
        IEnumerable<string>? includeGlobs = null) =>
        ListSaveFiles(new[] { SaveRoot.Primary(root, includeGlobs) }, excludeGlobs).Select(f => f.ArchiveName).ToList();

    /// <summary>One real file of a game's saves: its name in the archive and where it is on disk.</summary>
    public readonly record struct SaveFile(string ArchiveName, string FullPath);

    /// <summary>
    /// The exact files <see cref="HashDirectory(IReadOnlyList{SaveRoot}, IEnumerable{string}?)"/> and
    /// <see cref="CreateArchive(IReadOnlyList{SaveRoot}, string, IEnumerable{string}?)"/> act on across
    /// every save folder of a game, in archive order — markers left out, since they are not files on
    /// disk. For callers that inspect the same files (the settle gate) and must never disagree with
    /// what gets archived.
    /// </summary>
    public static IReadOnlyList<SaveFile> ListSaveFiles(IReadOnlyList<SaveRoot> roots, IEnumerable<string>? excludeGlobs = null)
    {
        ValidateRoots(roots);
        return EnumerateItems(roots, excludeGlobs)
            .Where(i => i.FullPath is not null)
            .Select(i => new SaveFile(i.Name, i.FullPath!))
            .ToList();
    }

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
    private static int NestedRestoreDepth(IEnumerable<string> relEntries, string targetFull)
    {
        if (Environment.GetEnvironmentVariable("SAVELOCKER_ALLOW_NESTED_RESTORE") == "1") return 0;

        var entries = relEntries.Select(SplitPath).ToList();
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

    // ----- Several save folders in one archive (tasks/multiple-save-paths/plan.md §1) -----

    /// <summary>
    /// How a game's save folders are laid out in one archive — the names here are the wire format,
    /// shared with every older agent and with the server's delta rebuild, so they never change.
    /// <list type="bullet">
    /// <item>The <b>primary</b> folder's files sit at the archive root under their relative path, as a
    /// single-folder game's always have: its stored versions, hash and delta baseline are unchanged.</item>
    /// <item>An <b>extra</b> folder's files sit under <c>.savelocker/paths/&lt;key&gt;/</c>.</item>
    /// <item>Each extra folder that exists also writes an empty <b>marker</b>,
    /// <c>.savelocker/keys/&lt;key&gt;</c>, so a restore can tell "this folder is empty" apart from "this
    /// version does not contain this folder".</item>
    /// </list>
    /// <c>.savelocker/paths/</c> and <c>.savelocker/keys/</c> are reserved: the primary folder's own copies
    /// of them are never hashed, archived or deleted. Any other <c>.savelocker/</c> name (a later format's)
    /// is an ordinary file of the primary folder and passes through unchanged. An older agent that knows none of this restores those entries as a
    /// real folder inside its primary save folder and pushes them back byte-for-byte; because every
    /// name is hashed in ONE Ordinal order — never folder by folder — its hash is the same as ours.
    /// Excludes match these archive names; include scopes match each folder's own relative paths.
    /// </summary>
    public const string ReservedPrefix = ".savelocker/";
    private const string PathsPrefix = ReservedPrefix + "paths/";
    private const string KeysPrefix = ReservedPrefix + "keys/";

    /// <summary>The archive name of an extra save folder's marker.</summary>
    public static string MarkerName(string key) => KeysPrefix + key;

    public static bool IsMarker(string archiveName) =>
        archiveName.StartsWith(KeysPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The extra save folders an archive holds — one per marker, so an empty folder counts.</summary>
    public static IReadOnlyList<string> ArchiveKeys(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries
            .Select(e => e.FullName)
            .Where(IsMarker)
            .Select(n => n[KeysPrefix.Length..])
            .Where(k => SaveRoot.ValidateExtraKey(k) is null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Why these folders cannot be one game's (nested, an unscoped shared directory, a bad
    /// key), or null when they can — the check every archive call makes, for a caller that wants to
    /// refuse a mapping before it is saved rather than fail the next sync.</summary>
    public static string? FolderRulesError(IReadOnlyList<SaveRoot> roots)
    {
        try { ValidateRoots(roots); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    /// <summary>
    /// A name in another folder's slice (<c>paths/</c>) or a marker (<c>keys/</c>): what the primary
    /// folder never lists, archives or deletes. Every other <c>.savelocker/</c> name is a plain file of
    /// the primary folder, so a subtree a LATER format adds (registry saves) passes through this agent
    /// unchanged instead of being dropped from the head by its next push.
    /// </summary>
    private static bool IsSliceName(string name) =>
        name.StartsWith(PathsPrefix, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(KeysPrefix, StringComparison.OrdinalIgnoreCase);

    private static string NamePrefix(SaveRoot root) => root.IsPrimary ? "" : PathsPrefix + root.Key + "/";

    /// <summary><c>.savelocker/paths/&lt;key&gt;/&lt;rel&gt;</c> → key and rel.</summary>
    private static bool TrySplitExtra(string name, out string key, out string rel)
    {
        key = rel = "";
        if (!name.StartsWith(PathsPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = name[PathsPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;
        key = rest[..slash];
        rel = rest[(slash + 1)..];
        return true;
    }

    /// <summary>Which folder an archive name belongs to, and its path inside it; null when it names
    /// no folder of this game.</summary>
    private static (SaveRoot Root, string Rel)? ResolveName(IReadOnlyList<SaveRoot> roots, string name)
    {
        if (!IsSliceName(name)) return (roots.Single(r => r.IsPrimary), name);
        if (!TrySplitExtra(name, out var key, out var rel)) return null;
        var root = roots.FirstOrDefault(r => !r.IsPrimary && r.Key == key);
        return root is null ? null : (root, rel);
    }

    /// <summary>One entry of a game's archive: a file, or a marker when <see cref="FullPath"/> is null.</summary>
    private readonly record struct SaveItem(string Name, string? FullPath);

    /// <summary>
    /// Every archive entry a game's save folders produce, in one Ordinal order of the final names —
    /// the single list hashing, the manifest and archiving all walk, so they can never disagree.
    /// </summary>
    private static List<SaveItem> EnumerateItems(IReadOnlyList<SaveRoot> roots, IEnumerable<string>? excludeGlobs)
    {
        var excludes = CleanGlobs(excludeGlobs);
        var owner = new Dictionary<string, string>(PathComparer);
        var items = new List<SaveItem>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Directory)) continue;

            var rootFull = Path.GetFullPath(root.Directory);
            var rels = EnumerateFilesNoFollow(rootFull)
                .Select(f => Path.GetRelativePath(rootFull, f).Replace('\\', '/'));
            if (root.IsPrimary) rels = rels.Where(r => !IsSliceName(r));

            var prefix = NamePrefix(root);
            var named = FilterIncluded(rels, root.IncludeGlobs).Select(r => (Name: prefix + r, Rel: r)).ToList();
            var kept = FilterExcluded(named.Select(n => n.Name), excludes).ToHashSet(StringComparer.Ordinal);

            foreach (var (name, rel) in named)
            {
                if (!kept.Contains(name)) continue;
                var full = Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar));
                // Two folders sharing one directory must keep their files apart; a file in both would
                // be archived twice and restored by whichever slice ran last.
                if (!owner.TryAdd(full, root.Key))
                    throw new OverlappingSaveFoldersException(
                        $"'{full}' belongs to both the '{owner[full]}' and '{root.Key}' save folders of this " +
                        "game. A file can only be synced once — narrow one folder's include patterns.");
                items.Add(new SaveItem(name, full));
            }

            if (!root.IsPrimary) items.Add(new SaveItem(MarkerName(root.Key), null));
        }

        items.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return items;
    }

    /// <summary>
    /// Refuse a set of save folders that cannot be one game's: anything but exactly one primary,
    /// a bad or repeated key, one folder inside another (its files would belong to both, and the
    /// outer folder's delete pass would remove the inner one's restore), or two folders sharing a
    /// directory without include scopes to keep them apart.
    /// </summary>
    private static void ValidateRoots(IReadOnlyList<SaveRoot> roots)
    {
        if (roots.Count(r => r.IsPrimary) != 1)
            throw new ArgumentException("A game's save folders need exactly one primary folder.");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!root.IsPrimary && SaveRoot.ValidateExtraKey(root.Key) is { } why)
                throw new ArgumentException(why);
            if (!keys.Add(root.Key))
                throw new ArgumentException($"Save folder key '{root.Key}' is used twice.");
        }

        for (var i = 0; i < roots.Count; i++)
        for (var j = i + 1; j < roots.Count; j++)
        {
            var (a, b) = (roots[i], roots[j]);
            if (NormalizeDir(a.Directory) is not { } da || NormalizeDir(b.Directory) is not { } db) continue;

            if (PathComparer.Equals(da, db))
            {
                if (!a.HasIncludeScope || !b.HasIncludeScope)
                    throw new ArgumentException(
                        $"The '{a.Key}' and '{b.Key}' save folders are the same directory ({a.Directory}). " +
                        "Two folders may share one only when both have include patterns.");
            }
            else if (IsInside(da, db) || IsInside(db, da))
            {
                throw new ArgumentException(
                    $"The '{a.Key}' and '{b.Key}' save folders are nested ({a.Directory} and {b.Directory}). " +
                    "One folder of a game cannot sit inside another.");
            }
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string? NormalizeDir(string? dir) =>
        string.IsNullOrWhiteSpace(dir)
            ? null
            : Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsInside(string child, string parent) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar,
            PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool SameDirectory(string a, string b) =>
        NormalizeDir(a) is { } da && NormalizeDir(b) is { } db && PathComparer.Equals(da, db);

    /// <summary>A test for "is this relative path one of the folder's own files" — built once per folder.</summary>
    private static Func<string, bool> InScope(SaveRoot root)
    {
        var globs = CleanGlobs(root.IncludeGlobs);
        if (globs.Count == 0) return _ => true;
        var matcher = IncludeMatcher(globs);
        return rel => matcher.Match(new[] { rel }).HasMatches;
    }

    /// <summary>
    /// Which of <paramref name="relativePaths"/> match at least one of <paramref name="includeGlobs"/>;
    /// all of them when there are none. This is how one game owns a single file inside a folder other
    /// games share — RetroArch writes every ROM's <c>&lt;rom&gt;.srm</c> side by side. It cannot be
    /// expressed as excludes: the matcher has no negation, so "exclude everything but X" archives
    /// nothing. Patterns are anchored at the save folder's root (no gitignore-style any-depth
    /// matching), because a save folder's own subfolders can belong to other games too.
    /// </summary>
    public static List<string> FilterIncluded(IEnumerable<string> relativePaths, IEnumerable<string>? includeGlobs)
    {
        var all = relativePaths.ToList();
        var globs = CleanGlobs(includeGlobs);
        if (globs.Count == 0) return all;

        var kept = new HashSet<string>(IncludeMatcher(globs).Match(all).Files.Select(m => m.Path), StringComparer.Ordinal);
        return all.Where(kept.Contains).ToList();
    }

    /// <summary>Null when <paramref name="glob"/> is a usable include pattern, else why it is not.</summary>
    public static string? ValidateIncludeGlob(string glob)
    {
        var g = glob.Trim();
        if (g.Length == 0) return null;
        try { IncludeMatcher(new[] { g }); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    private static Matcher IncludeMatcher(IEnumerable<string> globs)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var g in globs)
        {
            try { matcher.AddInclude(g); }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid include pattern '{g}': {ex.Message}", ex);
            }
        }
        return matcher;
    }

    private static List<string> CleanGlobs(IEnumerable<string>? globs) =>
        globs?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList() ?? new List<string>();

    /// <summary>
    /// Which of <paramref name="relativePaths"/> survive after removing anything matching
    /// <paramref name="excludeGlobs"/> — the same matcher <see cref="ListSaveFiles"/> uses
    /// against a live directory, exposed here so a caller with an existing path list (e.g. the
    /// server previewing a draft exclude pattern against an already-uploaded archive, which has no
    /// filesystem of its own to walk) gets the identical bare-filename-matches-at-any-depth rule
    /// rather than a second, driftable reimplementation of it.
    /// Throws <see cref="ArgumentException"/> naming the offending pattern when one is not a valid
    /// glob (see <see cref="ValidateExcludeGlob"/>) — never a bare matcher exception.
    /// </summary>
    public static List<string> FilterExcluded(IEnumerable<string> relativePaths, IEnumerable<string>? excludeGlobs)
    {
        var all = relativePaths.ToList();
        var globs = excludeGlobs?
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .ToList();
        if (globs is not { Count: > 0 }) return all;

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("**/*");
        foreach (var g in globs)
            AddExclusion(matcher, g);
        var kept = new HashSet<string>(matcher.Match(all).Files.Select(m => m.Path), StringComparer.Ordinal);
        return all.Where(kept.Contains).ToList();
    }

    /// <summary>
    /// Null when <paramref name="glob"/> is usable, else why it is not. The matcher rejects some
    /// shapes outright — a ".." anywhere but the start of a pattern throws — and it does so at the
    /// moment a save folder is hashed, on every agent, for every push of that game. Checking here,
    /// where a pattern is first typed, turns "this game silently stopped syncing" into an error on
    /// the field that caused it.
    /// </summary>
    public static string? ValidateExcludeGlob(string glob)
    {
        var g = glob.Trim();
        if (g.Length == 0) return null;
        try { AddExclusion(new Matcher(StringComparison.OrdinalIgnoreCase), g); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    /// <summary>A test for "does an exclude pattern keep this archive name out of a push" — the matcher
    /// <see cref="FilterExcluded"/> uses, built once.</summary>
    private static Func<string, bool> IsExcluded(IEnumerable<string>? excludeGlobs)
    {
        var globs = CleanGlobs(excludeGlobs);
        if (globs.Count == 0) return _ => false;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("**/*");
        foreach (var g in globs) AddExclusion(matcher, g);
        return name => !matcher.Match(new[] { name }).HasMatches;
    }

    private static void AddExclusion(Matcher matcher, string glob)
    {
        try
        {
            matcher.AddExclude(glob);
            // A bare filename pattern (no '/') should match at any depth, gitignore-style:
            // "*.log" excludes logs in every subfolder, not just the save root. Patterns
            // that already contain '/' are treated as explicit paths anchored at the root.
            if (!glob.Contains('/')) matcher.AddExclude("**/" + glob);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid exclude pattern '{glob}': {ex.Message}", ex);
        }
    }

    /// <summary>Relative paths of every real file entry already in an archive on disk, straight
    /// from the zip's own directory — the same source <see cref="GetArchiveStats"/> reads, never
    /// re-extracted. Markers are not files of the save and are left out.</summary>
    public static IReadOnlyList<string> ListArchiveEntries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = new List<string>();
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry, no content
            if (IsMarker(entry.FullName)) continue;
            names.Add(entry.FullName);
        }
        return names;
    }
}
