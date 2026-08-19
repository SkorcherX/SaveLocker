namespace SaveLocker.Server.Services;

/// <summary>
/// Thrown when an upload exceeds the configured byte cap. Counted while copying rather than trusted
/// from request metadata: a chunked upload declares no length, so the only honest place to find out
/// is the copy itself.
/// </summary>
public sealed class ArchiveTooLargeException(long limitBytes)
    : Exception($"Archive exceeds the {limitBytes / (1024 * 1024)} MB upload limit.")
{
    public long LimitBytes { get; } = limitBytes;
}

/// <summary>The chunked-upload session named does not exist — it completed, was aborted, or this
/// process restarted (sessions are in-memory only; see <see cref="ArchiveStore"/>).</summary>
public sealed class UnknownUploadSessionException(Guid sessionId)
    : Exception($"No upload session '{sessionId}' (it may have finished, been abandoned, or the " +
                "server restarted since it began — the agent starts a fresh one automatically).");

/// <summary>
/// A chunk arrived at an offset that does not match what the session has actually received.
/// <see cref="ArchiveStore.AppendChunkAsync"/> treats an offset BEHIND the session as a harmless
/// replay (the previous attempt's bytes landed even though its response did not) and only raises
/// this for a gap AHEAD of it, which chunks sent strictly in order should never produce.
/// </summary>
public sealed class UploadOffsetMismatchException(Guid sessionId, long expected, long actual)
    : Exception($"Upload session '{sessionId}' has {actual} byte(s); a chunk arrived expecting {expected}.");

/// <summary>
/// Stores save-game archive files on disk (an unRAID share volume in production).
/// Layout: {root}/{gameId}/{versionId}.zip
/// <para>
/// Nothing appears at its final path until it is complete. Uploads are staged in
/// <see cref="IncomingDirName"/> on the same volume and published with a rename, so a caller that
/// sees the file sees all of it — an aborted upload used to leave a truncated archive sitting at the
/// exact path a <c>SaveVersion</c> row would name.
/// </para>
/// </summary>
public sealed class ArchiveStore
{
    /// <summary>
    /// Staging directory, deliberately inside the archive root: a rename is only atomic within one
    /// volume, and the whole point of staging is that publishing cannot half-happen.
    /// </summary>
    public const string IncomingDirName = ".incoming";

    private readonly string _root;
    private readonly ILogger<ArchiveStore> _log;

    public ArchiveStore(IConfiguration config, ILogger<ArchiveStore> log)
    {
        _log = log;
        _root = config["Storage:ArchiveRoot"]
                ?? Path.Combine(AppContext.BaseDirectory, "data", "archives");
        Directory.CreateDirectory(_root);
    }

    private string IncomingDir => Path.Combine(_root, IncomingDirName);

    // ----- Chunked upload sessions -----
    //
    // A single-request upload works until the archive is big and the link is slow enough that the
    // request outlives whatever sits between the agent and this server — a proxied Cloudflare edge
    // kills any request/response cycle past ~100s, and a well-loved Cyberpunk 2077 save (dozens of
    // autosaves, each several MB) over a modest home upload routinely takes minutes. Splitting the
    // transfer into several short requests keeps every individual one comfortably under that limit.
    // Sessions live in memory only (this is a Singleton): a server restart drops them, but that is no
    // different from the crash-mid-upload case the startup SweepIncoming() call already exists for —
    // the orphaned staging file gets swept, and the agent's next attempt just begins a new session.

    private sealed class UploadSession
    {
        public required Guid GameId { get; init; }
        public required Guid MachineId { get; init; }
        public required Guid VersionId { get; init; }
        public required string ContentHash { get; init; }
        public required Guid? ParentVersionId { get; init; }
        public required bool Force { get; init; }
        public required string StagedPath { get; init; }
        public long BytesReceived;
        public DateTime LastActivityUtc;
        /// <summary>Serializes writes to the staged file. The agent only ever has one chunk for a
        /// session in flight at a time, but a retried chunk racing a slow-to-fail original must not
        /// interleave with it.</summary>
        public readonly SemaphoreSlim WriteLock = new(1, 1);
    }

    /// <summary>Read-only view of a session handed back across the ArchiveStore/SyncService boundary
    /// — SyncService owns auth (does this machine own this session?) and the DB-facing metadata this
    /// carries, ArchiveStore owns only the bytes.</summary>
    public sealed record ChunkedUploadInfo(
        Guid GameId, Guid MachineId, Guid VersionId, long BytesReceived,
        string ContentHash, Guid? ParentVersionId, bool Force);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, UploadSession> _sessions = new();

    /// <summary>How long an inactive session is allowed to sit before a later Begin call sweeps it.
    /// Generous on purpose: sized for a full, very slow (~50 KB/s) 200 MB upload with several
    /// chunk-level retries, not for the common case.</summary>
    private static readonly TimeSpan SessionIdleLimit = TimeSpan.FromMinutes(45);

    /// <summary>Start a chunked upload: stage an empty file and remember the metadata the eventual
    /// <see cref="CompleteSession"/> will need, so later requests need only name the session.</summary>
    public Guid BeginSession(
        Guid gameId, Guid machineId, Guid versionId, string contentHash, Guid? parentVersionId, bool force)
    {
        SweepExpiredSessions();

        Directory.CreateDirectory(IncomingDir);
        var sessionId = Guid.NewGuid();
        var staged = Path.Combine(IncomingDir, $"{versionId:N}-{sessionId:N}.part");
        using (File.Create(staged)) { }

        _sessions[sessionId] = new UploadSession
        {
            GameId = gameId,
            MachineId = machineId,
            VersionId = versionId,
            ContentHash = contentHash,
            ParentVersionId = parentVersionId,
            Force = force,
            StagedPath = staged,
            LastActivityUtc = DateTime.UtcNow
        };
        return sessionId;
    }

    public bool TryGetSession(Guid sessionId, out ChunkedUploadInfo info)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            info = new ChunkedUploadInfo(
                s.GameId, s.MachineId, s.VersionId, Interlocked.Read(ref s.BytesReceived),
                s.ContentHash, s.ParentVersionId, s.Force);
            return true;
        }
        info = default!;
        return false;
    }

    /// <summary>
    /// Append one chunk at the offset the caller believes the session is at.
    /// <para>
    /// Idempotent against a retry: if <paramref name="expectedOffset"/> is behind what has already
    /// landed, the request is assumed to be a replay of one that actually succeeded even though its
    /// response was lost — exactly the failure this protocol exists to survive — and this is a no-op
    /// that reports the current total. An offset AHEAD of what has landed is rejected: chunks are
    /// sent strictly in order, so a gap can only mean an earlier one never arrived.
    /// </para>
    /// </summary>
    public async Task<long> AppendChunkAsync(
        Guid sessionId, long expectedOffset, Stream content, long maxBytes, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var s))
            throw new UnknownUploadSessionException(sessionId);

        await s.WriteLock.WaitAsync(ct);
        try
        {
            if (expectedOffset != s.BytesReceived)
            {
                if (expectedOffset < s.BytesReceived) return s.BytesReceived;
                throw new UploadOffsetMismatchException(sessionId, expectedOffset, s.BytesReceived);
            }

            await using (var fs = new FileStream(
                s.StagedPath, FileMode.Append, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    if (maxBytes > 0 && s.BytesReceived + read > maxBytes)
                        throw new ArchiveTooLargeException(maxBytes);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    s.BytesReceived += read;
                }
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }
            s.LastActivityUtc = DateTime.UtcNow;
            return s.BytesReceived;
        }
        finally { s.WriteLock.Release(); }
    }

    /// <summary>Finish a session: publish its staged file to the archive's final path (same atomic
    /// rename the single-shot path uses) and forget the session. The database work this enables is
    /// the caller's — ArchiveStore only ever knows about bytes.</summary>
    public (string relativePath, long size) CompleteSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var s))
            throw new UnknownUploadSessionException(sessionId);

        var rel = RelativePath(s.GameId, s.VersionId);
        var full = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Move(s.StagedPath, full, overwrite: true);
        return (rel, s.BytesReceived);
    }

    /// <summary>Abandon a session without publishing. Used when the content turned out to already be
    /// on the server (another machine raced ahead while chunks were in flight) — the staged bytes are
    /// deleted immediately rather than waiting for the next-startup sweep.</summary>
    public void AbortSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var s)) TryDeleteFile(s.StagedPath);
    }

    private void SweepExpiredSessions()
    {
        var cutoff = DateTime.UtcNow - SessionIdleLimit;
        foreach (var (id, s) in _sessions)
        {
            if (s.LastActivityUtc < cutoff) AbortSession(id);
        }
    }

    /// <summary>
    /// The store path persisted in <c>SaveVersion.ArchivePath</c>. Always '/'-separated, never
    /// Path.Combine: this string goes into the DATABASE, so it outlives the OS that wrote it.
    /// A Windows-hosted server writing "gameid\versionid.zip" produces rows that the Linux
    /// (Docker) server cannot resolve — a backslash is a legal filename character there, not a
    /// separator — and every archive silently becomes undownloadable, which the agent reports
    /// as the very convincing lie "server has no saves yet".
    /// </summary>
    public string RelativePath(Guid gameId, Guid versionId) =>
        $"{gameId:N}/{versionId:N}.zip";

    /// <summary>
    /// Resolve a stored path against the archive root, accepting either separator: rows written
    /// by an older Windows-hosted server still carry backslashes.
    /// </summary>
    public string FullPath(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Persist an uploaded archive stream and return its relative store path.
    /// <para>
    /// Staged, size-checked, then published atomically. If the copy is cancelled, the client
    /// disconnects, or the cap is exceeded, the partial file is removed and nothing is published —
    /// so the caller can never commit a database row pointing at half an archive.
    /// </para>
    /// </summary>
    /// <param name="maxBytes">Hard cap; exceeded uploads raise <see cref="ArchiveTooLargeException"/>.
    /// Non-positive disables the check.</param>
    public async Task<(string relativePath, long size)> SaveAsync(
        Guid gameId, Guid versionId, Stream content, long maxBytes, CancellationToken ct = default)
    {
        var rel = RelativePath(gameId, versionId);
        var full = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Directory.CreateDirectory(IncomingDir);

        // The random suffix keeps two attempts at the same version id from sharing a staging file.
        var staged = Path.Combine(IncomingDir, $"{versionId:N}-{Guid.NewGuid():N}.part");
        long written = 0;

        try
        {
            await using (var fs = new FileStream(
                staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (maxBytes > 0 && written > maxBytes) throw new ArchiveTooLargeException(maxBytes);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                // Durable before it is visible: publishing a name that outlives the bytes is the
                // failure this whole method exists to prevent.
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }

            File.Move(staged, full, overwrite: true);
            return (rel, written);
        }
        catch
        {
            TryDeleteFile(staged);
            throw;
        }
    }

    public Stream OpenRead(string relativePath) => File.OpenRead(FullPath(relativePath));

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));

    /// <summary>
    /// Delete a stored archive. Returns false if the file is still there afterwards, so a caller
    /// that has already committed the database can record the cleanup debt instead of assuming.
    /// </summary>
    public bool TryDelete(string relativePath)
    {
        var full = FullPath(relativePath);
        try
        {
            if (File.Exists(full)) File.Delete(full);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not delete archive {Path}; it is now orphaned on disk.", relativePath);
            return false;
        }
    }

    /// <summary>
    /// Remove staging files older than <paramref name="olderThan"/>. Anything left in staging is by
    /// definition an upload that never completed — but only once it is old enough that it cannot be
    /// an upload still in flight.
    /// </summary>
    public int SweepIncoming(TimeSpan olderThan)
    {
        if (!Directory.Exists(IncomingDir)) return 0;

        var cutoff = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(IncomingDir, "*.part"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not sweep stale upload {Path}.", file);
            }
        }
        if (removed > 0) _log.LogInformation("Swept {Count} stale upload file(s) from staging.", removed);
        return removed;
    }

    private void TryDeleteFile(string fullPath)
    {
        try { if (File.Exists(fullPath)) File.Delete(fullPath); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not remove staging file {Path}.", fullPath); }
    }
}
