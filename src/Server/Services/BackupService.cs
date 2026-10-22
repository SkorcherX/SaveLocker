using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using SaveLocker.Server.Data;
using SaveLocker.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SaveLocker.Server.Services;

/// <summary>Configuration for <see cref="BackupService"/>, built once at startup. <c>Enabled</c>,
/// <c>RetentionCount</c> and <c>HourOfDay</c> are only the fallbacks: the admin's own choice lives in the
/// DB (<see cref="BackupService.GetSettingsAsync"/>) and is re-read on every scheduler loop.</summary>
public sealed class BackupOptions
{
    /// <summary>Absolute (or working-dir-relative) path to the live SQLite database file.</summary>
    public required string DbPath { get; init; }

    /// <summary>Directory the backups are written to (created on demand).</summary>
    public required string BackupRoot { get; init; }

    /// <summary>How many of the most recent backups to keep; older ones are pruned.</summary>
    public int RetentionCount { get; init; } = 7;

    /// <summary>UTC hour of day (0–23) the scheduled backup fires at.</summary>
    public int HourOfDay { get; init; } = 3;

    /// <summary>When false, the scheduler is a no-op (Back up now still works).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>The backup schedule in force: the DB's value for each, else config's. Hour is UTC.</summary>
public sealed record BackupSettings(bool Enabled, int RetentionCount, int HourOfDay, string Frequency, int DayOfWeek)
{
    public TimeSpan Interval => Frequency == BackupService.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
}

/// <summary>
/// Backs up the server: one zip holding a <c>VACUUM INTO</c> copy of the database (the version graph, compressed)
/// and every game's <b>latest</b> save archive, plus a manifest. <c>VACUUM INTO</c> reads a consistent view under
/// a read transaction, so it is safe while the server serves writes (copying the .db file can capture a torn WAL).
/// The zip is written under a <c>.tmp</c> name and renamed on success, so retention never sees a half-written one.
///
/// Older versions are not in the backup (the maintainer's call: smallest backups). A restore brings back the
/// database and each game's latest save; older versions whose archives were pruned since then stay listed but
/// can no longer be downloaded.
///
/// The reason rides in the name after the timestamp (<c>savelocker-20260927-030000-manual.zip</c>, UTC), so the
/// ordinal newest-first sort and the prune pattern need no index. Legacy <c>.db</c> snapshots are still listed,
/// downloadable and restorable (database only).
/// </summary>
public sealed class BackupService
{
    private const string Prefix = "savelocker-";
    private const string ZipExt = ".zip";
    private const string DbExt = ".db";
    private const int StampLength = 15; // yyyyMMdd-HHmmss
    private const string DbEntry = "savelocker.db";
    private const string ArchivesDir = "archives/";
    private const string ManifestEntry = "manifest.json";

    public const string EnabledKey = "Backup:Enabled";
    public const string RetentionCountKey = "Backup:RetentionCount";
    public const string HourOfDayKey = "Backup:HourOfDay";
    public const string FrequencyKey = "Backup:Frequency";
    public const string DayOfWeekKey = "Backup:DayOfWeek";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const int MaxRetention = 365;

    private readonly BackupOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly ArchiveStore _store;
    private readonly ILogger<BackupService> _log;
    private readonly SemaphoreSlim _wake = new(0);
    // One backup or restore at a time: a restore swapping the database under a running backup (or two
    // restores racing) is the one outcome worse than either on its own.
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly object _gate = new();
    private string? _lastError;
    private DateTime? _lastErrorAt;
    private DateTime? _nextRunAt;

    public BackupService(BackupOptions options, IServiceScopeFactory scopes, ArchiveStore store, ILogger<BackupService> log)
    {
        _options = options;
        _scopes = scopes;
        _store = store;
        _log = log;
    }

    public BackupOptions Options => _options;

    /// <summary>When the scheduler will next fire (UTC); null while scheduled backups are off.</summary>
    public DateTime? NextRunAt { get { lock (_gate) return _nextRunAt; } internal set { lock (_gate) _nextRunAt = value; } }

    /// <summary>The most recent failure, kept for the page until a backup succeeds.</summary>
    public (string? Error, DateTime? At) LastError { get { lock (_gate) return (_lastError, _lastErrorAt); } }

    public async Task<BackupSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var enabled = bool.TryParse(await settings.GetEffectiveAsync(EnabledKey, ct), out var e) ? e : _options.Enabled;
        var keep = int.TryParse(await settings.GetEffectiveAsync(RetentionCountKey, ct), out var k) ? k : _options.RetentionCount;
        var hour = int.TryParse(await settings.GetEffectiveAsync(HourOfDayKey, ct), out var h) ? h : _options.HourOfDay;
        var freq = await settings.GetEffectiveAsync(FrequencyKey, ct) is Daily ? Daily : Weekly;
        var day = int.TryParse(await settings.GetEffectiveAsync(DayOfWeekKey, ct), out var d) ? d : 0;
        return new BackupSettings(enabled, Math.Clamp(keep, 1, MaxRetention), Math.Clamp(hour, 0, 23), freq, Math.Clamp(day, 0, 6));
    }

    public async Task SetSettingsAsync(SetBackupSettingsRequest req, CancellationToken ct = default)
    {
        using (var scope = _scopes.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.SetAsync(EnabledKey, req.Enabled ? "true" : "false", ct);
            await settings.SetAsync(RetentionCountKey, req.RetentionCount.ToString(CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(HourOfDayKey, req.HourOfDay.ToString(CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(FrequencyKey, req.Frequency, ct);
            await settings.SetAsync(DayOfWeekKey, req.DayOfWeek.ToString(CultureInfo.InvariantCulture), ct);
        }
        // The scheduler is sleeping until the OLD time; wake it so the new schedule takes effect now.
        _wake.Release();
    }

    /// <summary>Sleeps for <paramref name="delay"/>; true if woken early by a settings change.</summary>
    internal Task<bool> WaitForWakeAsync(TimeSpan delay, CancellationToken ct) => _wake.WaitAsync(delay, ct);

    /// <summary>The next scheduled run after <paramref name="nowUtc"/>, in UTC.</summary>
    public static DateTime NextRun(BackupSettings s, DateTime nowUtc)
    {
        var next = nowUtc.Date.AddHours(s.HourOfDay);
        if (s.Frequency == Weekly)
        {
            next = next.AddDays(((s.DayOfWeek - (int)next.DayOfWeek) + 7) % 7);
            if (next <= nowUtc) next = next.AddDays(7);
        }
        else if (next <= nowUtc) next = next.AddDays(1);
        return next;
    }

    /// <summary>Back up the database and every game's latest save, then prune to the retention count.</summary>
    /// <param name="retention">The count to prune to; null skips pruning (the before-upgrade backup runs before
    /// the DB it would read the setting from has been migrated).</param>
    /// <param name="saves">False for the before-upgrade backup: it runs before migrations, when the schema the
    /// save lookup queries may not exist yet — and the archives are not what a migration can damage.</param>
    public async Task<BackupResult> BackupAsync(BackupReason reason, int? retention, CancellationToken ct = default, bool saves = true)
    {
        await _run.WaitAsync(ct);
        try { return await BackupCoreAsync(reason, retention, saves, ct); }
        finally { _run.Release(); }
    }

    private async Task<BackupResult> BackupCoreAsync(BackupReason reason, int? retention, bool saves, CancellationToken ct)
    {
        string? tempDb = null, tempZip = null;
        try
        {
            Directory.CreateDirectory(_options.BackupRoot);
            // Never reuse a name: a restore's safety backup taken in the same second as the backup being restored
            // (restoring a Before restore one right after it was made) would overwrite its own source.
            var stamp = DateTime.UtcNow;
            string fileName, finalPath;
            do
            {
                fileName = $"{Prefix}{stamp:yyyyMMdd-HHmmss}{Suffix(reason)}{ZipExt}";
                finalPath = Path.Combine(_options.BackupRoot, fileName);
                stamp = stamp.AddSeconds(1);
            } while (File.Exists(finalPath) || Directory.EnumerateFiles(_options.BackupRoot, fileName[..(Prefix.Length + StampLength)] + "*").Any());
            tempZip = finalPath + ".tmp";
            tempDb = Path.Combine(_options.BackupRoot, $".{Guid.NewGuid():N}.db.tmp");
            if (File.Exists(tempZip)) File.Delete(tempZip);

            // A separate connection to the same file; VACUUM INTO never writes the source.
            await using (var conn = new SqliteConnection($"Data Source={_options.DbPath}"))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM main INTO $target";
                cmd.Parameters.AddWithValue("$target", tempDb);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var heads = saves ? await ReadHeadsAsync(tempDb, ct) : new List<ManifestGame>();
            var included = new List<ManifestGame>();
            await using (var fs = new FileStream(tempZip, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tempDb, DbEntry, CompressionLevel.SmallestSize);
                foreach (var g in heads)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_store.Exists(g.ArchivePath)) continue; // pruned between the snapshot and now: nothing to copy
                    // Save archives are zips already; a second compression pass costs time and saves nothing.
                    zip.CreateEntryFromFile(_store.FullPath(g.ArchivePath), ArchivesDir + Normalize(g.ArchivePath), CompressionLevel.NoCompression);
                    included.Add(g);
                }
                var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var ms = manifest.Open();
                await JsonSerializer.SerializeAsync(ms, new Manifest(1, DateTime.UtcNow, reason.ToString(), BuildInfo.Current.Version, included), cancellationToken: ct);
            }

            File.Move(tempZip, finalPath, overwrite: true);

            var info = new BackupInfo(fileName, new FileInfo(finalPath).Length, File.GetLastWriteTimeUtc(finalPath), reason, true);
            var retained = retention is { } keep ? Prune(keep) : ListBackups().Count;
            lock (_gate) { _lastError = null; _lastErrorAt = null; }
            _log.LogInformation("Backup written: {File} ({Size:N0} bytes, {Saves} latest saves); {Count} retained.",
                fileName, info.SizeBytes, included.Count, retained);
            return new BackupResult(true, null, info, retained);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backup failed.");
            lock (_gate) { _lastError = ex.Message; _lastErrorAt = DateTime.UtcNow; }
            return new BackupResult(false, ex.Message, null, ListBackups().Count);
        }
        finally
        {
            if (tempDb is not null) TryDelete(tempDb);
            // A failure or cancel mid-zip leaves a partial file under a fresh name each time — on a full disk,
            // exactly the file that makes it fuller. After a successful move there is nothing here to delete.
            if (tempZip is not null && File.Exists(tempZip)) TryDelete(tempZip);
            SqliteConnection.ClearAllPools(); // release the temp file on Windows
        }
    }

    /// <summary>
    /// Replace the live database with the one in <paramref name="fileName"/> and put back any latest save it holds
    /// that is missing on disk. A safety backup of the current state is taken first, so a restore can itself be
    /// undone. The copy goes through SQLite's online backup API into the live file, so the server keeps running and
    /// every later request reads the restored data. Refuses a backup whose database fails
    /// <c>PRAGMA integrity_check</c> or is not a SaveLocker database — before anything is changed.
    /// </summary>
    public async Task<(BackupRestoreResult? Result, string? Error)> RestoreAsync(string fileName, CancellationToken ct = default)
    {
        if (Find(fileName) is not { } source) return (null, null);
        await _run.WaitAsync(ct);
        var work = Path.Combine(_options.BackupRoot, $".restore-{Guid.NewGuid():N}");
        try
        {
            // ---- Before the swap: cancellable, and every refusal leaves the live database untouched. ----
            Directory.CreateDirectory(work);
            var dbCopy = Path.Combine(work, DbEntry);
            var isZip = source.EndsWith(ZipExt, StringComparison.OrdinalIgnoreCase);
            if (isZip)
            {
                try
                {
                    using var zip = ZipFile.OpenRead(source);
                    var entry = zip.GetEntry(DbEntry);
                    if (entry is null) return (null, "That backup has no database in it.");
                    entry.ExtractToFile(dbCopy);
                }
                catch (InvalidDataException) { return (null, "That backup is not a readable zip."); }
            }
            else File.Copy(source, dbCopy);

            if (Validate(dbCopy) is { } refused) return (null, refused);

            var safety = await BackupCoreAsync(BackupReason.BeforeRestore, retention: null, saves: true, ct);
            if (!safety.Ok) return (null, $"The safety backup failed, so nothing was restored: {safety.Message}");

            SqliteConnection.ClearAllPools();
            if (await CopyIntoLiveAsync(dbCopy, ct) is { } busy) return (null, busy);

            // ---- After the swap: never cancelled. A closed tab or a proxy timeout here would leave the live
            // database on the backup's (possibly older) schema with nothing put back — worse than a slow restore. ----
            try
            {
                using var scope = _scopes.CreateScope();
                await DatabaseSetup.PrepareAsync(scope.ServiceProvider, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Restored the database from {File} but could not bring it up to this build.", fileName);
                throw new RestoreIncompleteException(
                    $"The database was restored from {fileName}, but bringing it up to this build failed ({ex.Message}). " +
                    $"Restart the server to finish it, or restore {safety.Backup!.FileName} to go back.", ex);
            }

            int restored = 0, present = 0;
            string? warning = null;
            if (isZip)
            {
                try { (restored, present) = PutBackSaves(source); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _log.LogError(ex, "Restored the database from {File} but could not put its saves back.", fileName);
                    warning = $"The database was restored, but putting its saves back stopped: {ex.Message}";
                }
            }

            _wake.Release(); // the schedule is a setting, and the restored database may hold a different one
            _log.LogWarning("Restored the database from {File}; safety backup {Safety}; {Restored} saves put back.",
                fileName, safety.Backup!.FileName, restored);
            return (new BackupRestoreResult(Path.GetFileName(source), safety.Backup.FileName, restored, present, warning), null);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // the extracted copy may still be held by a pooled handle
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
            _run.Release();
        }
    }

    /// <summary>
    /// Copy <paramref name="dbCopy"/> page by page into the live file with SQLite's online backup API. A write in
    /// flight (an agent's heartbeat) makes the step fail with BUSY/LOCKED before it changes anything, so retry a few
    /// times. Returns null on success, else why nothing was restored.
    /// </summary>
    private async Task<string?> CopyIntoLiveAsync(string dbCopy, CancellationToken ct)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var from = new SqliteConnection($"Data Source={dbCopy};Mode=ReadOnly;Pooling=False");
                await using var to = new SqliteConnection($"Data Source={_options.DbPath};Pooling=False");
                await from.OpenAsync(ct);
                await to.OpenAsync(ct);
                from.BackupDatabase(to);
                return null;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY, SQLITE_LOCKED
            {
                if (attempt == attempts)
                    return "The database stayed busy with other work, so nothing was restored. Try again in a moment.";
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
        }
    }

    /// <summary>Extract each latest save in the backup that is missing on disk. Returns (put back, already there).</summary>
    private (int Restored, int Present) PutBackSaves(string source)
    {
        int restored = 0, present = 0;
        var root = Path.GetFullPath(_store.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(source);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(ArchivesDir, StringComparison.Ordinal) && e.Length > 0))
        {
            var rel = e.FullName[ArchivesDir.Length..];
            // The same zip-slip rule as a save restore: the entry must land inside the archive root.
            var full = Path.GetFullPath(_store.FullPath(rel));
            if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
            // Archives never change once written, so an existing file IS this one.
            if (File.Exists(full)) { present++; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            e.ExtractToFile(full + ".tmp", overwrite: true);
            File.Move(full + ".tmp", full, overwrite: false);
            restored++;
        }
        return (restored, present);
    }

    // ----- Download tickets -----
    //
    // A backup can be gigabytes, so the console must not fetch it into a blob in memory; a plain <a href> streams
    // it to disk but cannot carry the X-Admin-Session header. So an admin asks for a ticket (session-checked,
    // audited) and the browser follows a link carrying it: random, single-use, about a minute, bound to one file.

    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string File, DateTime Expires)> _tickets = new();

    /// <summary>A single-use ticket for <paramref name="fileName"/>, or null when no such backup is listed.</summary>
    public (string Ticket, DateTime ExpiresAt)? IssueDownloadTicket(string fileName)
    {
        if (Find(fileName) is null) return null;
        var now = DateTime.UtcNow;
        foreach (var (key, t) in _tickets) if (t.Expires <= now) _tickets.TryRemove(key, out _);
        var ticket = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expires = now + TicketLifetime;
        _tickets[ticket] = (fileName, expires);
        return (ticket, expires);
    }

    /// <summary>The full path the ticket was issued for, consuming it; null when unknown, used or expired.</summary>
    public string? RedeemDownloadTicket(string ticket)
    {
        if (!_tickets.TryRemove(ticket, out var t) || t.Expires <= DateTime.UtcNow) return null;
        return Find(t.File);
    }

    private static string? Validate(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            if (cmd.ExecuteScalar() as string != "ok") return "The database in that backup is damaged (integrity check failed).";
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Games','SaveVersions','Machines')";
            if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 3) return "That file is not a SaveLocker database.";
            return null;
        }
        catch (SqliteException ex) { return $"That file is not a readable database: {ex.Message}"; }
    }

    /// <summary>Each game's latest version, read from the snapshot itself so the list matches the database
    /// the backup holds, not whatever moved on since.</summary>
    private static async Task<List<ManifestGame>> ReadHeadsAsync(string dbPath, CancellationToken ct)
    {
        var list = new List<ManifestGame>();
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.Id, g.Name, v.Id, v.ArchivePath, v.Size
            FROM Games g JOIN SaveVersions v ON v.Id = g.HeadVersionId
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ManifestGame(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
        return list;
    }

    private static string Normalize(string archivePath) => archivePath.Replace('\\', '/').TrimStart('/');

    private sealed record ManifestGame(string GameId, string Name, string VersionId, string ArchivePath, long SizeBytes);
    private sealed record Manifest(int Format, DateTime CreatedAt, string Reason, string ServerVersion, List<ManifestGame> Games);

    /// <summary>
    /// Deletes one backup. Taken under the same lock as a backup or restore, so a restore is never left reading a
    /// file that disappears under it — but never WAITED for: a backup that zips every save can hold it for minutes,
    /// and a delete that silently hangs that long is worse than one that says why and can be pressed again.
    /// Returns the deleted file's name; (null, null) when there is no such backup; (null, why) when it could not be
    /// deleted now.
    /// </summary>
    public async Task<(string? Deleted, string? Error)> DeleteAsync(string fileName)
    {
        if (Find(fileName) is null) return (null, null);
        if (!await _run.WaitAsync(TimeSpan.Zero)) return (null, "A backup or restore is running. Try again when it has finished.");
        try
        {
            if (Find(fileName) is not { } path) return (null, null);
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not delete backup {File}.", fileName);
                return (null, $"The file could not be deleted: {ex.Message}");
            }
            return (Path.GetFileName(path), null);
        }
        finally { _run.Release(); }
    }

    /// <summary>Existing backups (zip and legacy db), newest first.</summary>
    public IReadOnlyList<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(_options.BackupRoot)) return Array.Empty<BackupInfo>();
        return EnumerateNewestFirst()
            // A before-upgrade backup runs before migrations, so it never holds saves (BackupAsync's `saves: false`).
            .Select(f => new BackupInfo(f.Name, f.Length, f.LastWriteTimeUtc, ReasonOf(f.Name),
                f.Name.EndsWith(ZipExt, StringComparison.OrdinalIgnoreCase) && ReasonOf(f.Name) != BackupReason.BeforeUpgrade))
            .ToList();
    }

    /// <summary>
    /// The full path of the backup named <paramref name="fileName"/>, or null. The name is matched against the
    /// listing and never joined into a path, so <c>..</c>, an absolute path or a percent-encoded separator can
    /// only ever miss.
    /// </summary>
    public string? Find(string fileName)
    {
        if (!Directory.Exists(_options.BackupRoot)) return null;
        return EnumerateNewestFirst()
            .FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.Ordinal))
            ?.FullName;
    }

    private static string Suffix(BackupReason reason) => reason switch
    {
        BackupReason.Manual => "-manual",
        BackupReason.BeforeUpgrade => "-before-upgrade",
        BackupReason.BeforeRestore => "-before-restore",
        _ => "",
    };

    internal static BackupReason ReasonOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName)[Prefix.Length..];
        return stem.Length > StampLength ? stem[StampLength..] switch
        {
            "-manual" => BackupReason.Manual,
            "-before-upgrade" => BackupReason.BeforeUpgrade,
            "-before-restore" => BackupReason.BeforeRestore,
            _ => BackupReason.Scheduled,
        } : BackupReason.Scheduled;
    }

    // Names start with fixed-width, zero-padded timestamps, so ordinal order == chronological order.
    private IEnumerable<FileInfo> EnumerateNewestFirst() =>
        new DirectoryInfo(_options.BackupRoot)
            .EnumerateFiles(Prefix + "*")
            .Where(f => f.Name.EndsWith(ZipExt, StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(DbExt, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Name[..Math.Min(f.Name.Length, Prefix.Length + StampLength)], StringComparer.Ordinal)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal);

    /// <summary>Keep the newest <paramref name="retention"/> backups — and always the newest <b>Before restore</b>
    /// one, even past the count: it is the only way to undo the last restore, and a small keep plus a scheduled run
    /// soon after a restore would otherwise delete it.</summary>
    private int Prune(int retention)
    {
        var files = EnumerateNewestFirst().ToList();
        var undo = files.FirstOrDefault(f => ReasonOf(f.Name) == BackupReason.BeforeRestore);
        var kept = 0;
        for (var i = 0; i < files.Count; i++)
        {
            if (i < Math.Max(0, retention) || files[i] == undo) { kept++; continue; }
            try { files[i].Delete(); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to prune old backup {File}.", files[i].Name); kept++; }
        }
        return kept;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
}

/// <summary>
/// Fires <see cref="BackupService.BackupAsync"/> on the configured schedule (daily or weekly, at a UTC hour),
/// re-reading the settings every loop and immediately when an admin changes them. When scheduled backups are on at
/// startup it also takes a catch-up backup if the newest one is missing or older than one interval.
/// </summary>
public sealed class BackupScheduler : BackgroundService
{
    private static readonly TimeSpan OffRecheck = TimeSpan.FromHours(1);

    private readonly BackupService _backup;
    private readonly ILogger<BackupScheduler> _log;

    public BackupScheduler(BackupService backup, ILogger<BackupScheduler> log)
    {
        _backup = backup;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Inside the loop's catch, not before it: an exception escaping ExecuteAsync stops the whole host.
            var caughtUp = false;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var s = await _backup.GetSettingsAsync(ct);
                    if (!caughtUp)
                    {
                        caughtUp = true;
                        if (s.Enabled && (MostRecentAge() is not { } age || age > s.Interval))
                        {
                            await _backup.BackupAsync(BackupReason.Scheduled, s.RetentionCount, ct);
                            continue;
                        }
                    }
                    if (!s.Enabled)
                    {
                        _backup.NextRunAt = null;
                        await _backup.WaitForWakeAsync(OffRecheck, ct);
                        continue;
                    }

                    var next = BackupService.NextRun(s, DateTime.UtcNow);
                    _backup.NextRunAt = next;
                    _log.LogInformation("Next backup at {Next:u}.", next);
                    // Clamped: a negative timeout throws, and the retry a minute later would plan the NEXT run and skip this one.
                    var delay = next - DateTime.UtcNow;
                    if (await _backup.WaitForWakeAsync(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct)) continue;

                    // Read again: the retention may have changed while this slept.
                    var now = await _backup.GetSettingsAsync(ct);
                    if (now.Enabled) await _backup.BackupAsync(BackupReason.Scheduled, now.RetentionCount, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Backup scheduler loop failed; retrying in a minute.");
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                }
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }

    private TimeSpan? MostRecentAge()
    {
        var newest = _backup.ListBackups().FirstOrDefault();
        return newest is null ? null : DateTime.UtcNow - newest.CreatedAt;
    }
}

/// <summary>A restore that replaced the live database but could not finish bringing it up to this build. The
/// message says so and what to do; it must never read as "nothing was restored".</summary>
public sealed class RestoreIncompleteException(string message, Exception inner) : Exception(message, inner);
