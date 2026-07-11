using SaveLocker.Shared;
using Microsoft.Data.Sqlite;

namespace SaveLocker.Server.Services;

/// <summary>Resolved configuration for <see cref="BackupService"/>, built once at startup.</summary>
public sealed class BackupOptions
{
    /// <summary>Absolute (or working-dir-relative) path to the live SQLite database file.</summary>
    public required string DbPath { get; init; }

    /// <summary>Directory the nightly snapshots are written to (created on demand).</summary>
    public required string BackupRoot { get; init; }

    /// <summary>How many of the most recent snapshots to keep; older ones are pruned.</summary>
    public int RetentionCount { get; init; } = 7;

    /// <summary>Local hour of day (0–23) the nightly snapshot fires at.</summary>
    public int HourOfDay { get; init; } = 3;

    /// <summary>When false, the scheduler is a no-op (manual <see cref="BackupService.BackupAsync"/> still works).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Takes point-in-time snapshots of the live SQLite database. The DB <em>is</em> the version
/// graph (archives on disk are meaningless without it), so a corrupt or lost file loses
/// history for every machine — hence a self-contained on-box backup.
///
/// Snapshots are produced with <c>VACUUM INTO</c>, which reads a consistent view under a
/// read transaction and folds any pending WAL content into a fresh, defragmented single-file
/// copy. It is safe to run while the server is serving writes (unlike copying the .db file,
/// which can capture a torn WAL). The copy is written to a <c>.tmp</c> name and renamed on
/// success so retention never sees a half-written file.
/// </summary>
public sealed class BackupService
{
    private const string Prefix = "savelocker-";
    private const string Extension = ".db";
    private const string Pattern = Prefix + "*" + Extension;

    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _log;

    public BackupService(BackupOptions options, ILogger<BackupService> log)
    {
        _options = options;
        _log = log;
    }

    public BackupOptions Options => _options;

    /// <summary>Snapshot the DB, then prune old snapshots down to the retention count.</summary>
    public async Task<BackupResult> BackupAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_options.BackupRoot);

            var fileName = $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmss}{Extension}";
            var finalPath = Path.Combine(_options.BackupRoot, fileName);
            var tempPath = finalPath + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);

            // A separate connection to the same file; coexists with the app's connection.
            // VACUUM INTO never writes the source, so this is a pure read of a consistent snapshot.
            await using (var conn = new SqliteConnection($"Data Source={_options.DbPath}"))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM main INTO $target";
                cmd.Parameters.AddWithValue("$target", tempPath);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            File.Move(tempPath, finalPath, overwrite: true);

            var info = new BackupInfo(fileName, new FileInfo(finalPath).Length, File.GetLastWriteTimeUtc(finalPath));
            var retained = Prune();
            _log.LogInformation(
                "SQLite backup written: {File} ({Size:N0} bytes); {Count} snapshot(s) retained.",
                fileName, info.SizeBytes, retained);
            return new BackupResult(true, null, info, retained);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SQLite backup failed.");
            return new BackupResult(false, ex.Message, null, ListBackups().Count);
        }
    }

    /// <summary>Existing snapshots, newest first.</summary>
    public IReadOnlyList<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(_options.BackupRoot)) return Array.Empty<BackupInfo>();
        return EnumerateNewestFirst()
            .Select(f => new BackupInfo(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    // Names are fixed-width, zero-padded timestamps, so ordinal string order == chronological order.
    private IEnumerable<FileInfo> EnumerateNewestFirst() =>
        new DirectoryInfo(_options.BackupRoot)
            .EnumerateFiles(Pattern)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal);

    private int Prune()
    {
        var files = EnumerateNewestFirst().ToList();
        foreach (var stale in files.Skip(Math.Max(0, _options.RetentionCount)))
        {
            try { stale.Delete(); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to prune old backup {File}.", stale.Name); }
        }
        return Math.Min(files.Count, Math.Max(0, _options.RetentionCount));
    }
}

/// <summary>
/// Fires <see cref="BackupService.BackupAsync"/> nightly at <see cref="BackupOptions.HourOfDay"/>.
/// On startup it also takes a catch-up snapshot if the newest existing one is missing or older
/// than a day (e.g. the box was down over its window), while the age guard keeps frequent
/// redeploys from spamming snapshots.
/// </summary>
public sealed class BackupScheduler : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly BackupService _backup;
    private readonly ILogger<BackupScheduler> _log;

    public BackupScheduler(BackupService backup, ILogger<BackupScheduler> log)
    {
        _backup = backup;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = _backup.Options;
        if (!opts.Enabled)
        {
            _log.LogInformation("SQLite backup scheduler disabled (Backup:Enabled=false).");
            return;
        }

        try
        {
            var age = MostRecentAge();
            if (age is null || age > Interval)
                await _backup.BackupAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                var delay = DelayUntilNextRun(opts.HourOfDay);
                _log.LogInformation("Next SQLite backup in {Hours:0.0} h.", delay.TotalHours);
                await Task.Delay(delay, ct);
                await _backup.BackupAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }

    private TimeSpan? MostRecentAge()
    {
        var newest = _backup.ListBackups().FirstOrDefault();
        return newest is null ? null : DateTime.UtcNow - newest.CreatedAt;
    }

    private static TimeSpan DelayUntilNextRun(int hourOfDay)
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(Math.Clamp(hourOfDay, 0, 23));
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }
}
