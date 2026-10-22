using SaveLocker.Server.Data;
using SaveLocker.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace SaveLocker.Server.Services;

/// <summary>
/// Server settings persisted as DB key/value pairs, with fallback to
/// <see cref="IConfiguration"/> (appsettings / env) for back-compat. Lets admins
/// manage things like the SteamGridDB API key from the dashboard instead of
/// editing config files. A DB value always wins over the config value.
/// </summary>
public sealed class SettingsService
{
    /// <summary>Settings key for the SteamGridDB API key (matches the config path).</summary>
    public const string SteamGridDbApiKey = "SteamGridDb:ApiKey";

    /// <summary>Settings key for the admin dashboard password hash.</summary>
    public const string AdminPasswordHash = "Admin:PasswordHash";

    /// <summary>Settings key for the GitHub installer auto-fetch interval. Still the only thing
    /// read when <see cref="AgentUpdateScheduleMode"/> is absent or "hours" — existing deployments
    /// that never touch the newer weekly/monthly modes see no behaviour change.</summary>
    public const string AgentUpdateAutoFetchHours = "AgentUpdate:AutoFetchHours";

    /// <summary>"disabled" | "hours" | "weekly" | "monthly". Absent means "hours", for back-compat
    /// with every deployment that predates weekly/monthly scheduling.</summary>
    public const string AgentUpdateScheduleMode = "AgentUpdate:Schedule:Mode";
    public const string AgentUpdateScheduleDayOfWeek = "AgentUpdate:Schedule:DayOfWeek";
    public const string AgentUpdateScheduleDayOfMonth = "AgentUpdate:Schedule:DayOfMonth";
    public const string AgentUpdateScheduleTimeOfDay = "AgentUpdate:Schedule:TimeOfDay";

    /// <summary>
    /// Settings that are secrets the server must be able to USE (so they cannot be hashed like the admin
    /// password or machine keys): stored encrypted with ASP.NET Data Protection. Its key ring lives in
    /// <c>{data}/keys</c>, outside the database — so the database file and every backup of it carry only
    /// ciphertext. A backup restored onto a different server cannot decrypt them: the key reads as unset.
    /// </summary>
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal) { SteamGridDbApiKey };
    private const string EncryptedPrefix = "enc:v1:";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UndecryptableWarned = new();

    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IDataProtector _protector;
    private readonly ILogger<SettingsService> _log;

    public SettingsService(AppDbContext db, IConfiguration cfg, IDataProtectionProvider protection, ILogger<SettingsService> log)
    {
        _db = db;
        _cfg = cfg;
        _protector = protection.CreateProtector("SaveLocker.Settings.v1");
        _log = log;
    }

    /// <summary>The DB value if set, else the configuration value, else null. Secrets come back decrypted; one
    /// this server cannot decrypt counts as unset, so the configuration's value (if any) applies.</summary>
    public async Task<string?> GetEffectiveAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.Settings.FindAsync(new object?[] { key }, ct);
        if (!string.IsNullOrWhiteSpace(row?.Value)
            && (Secrets.Contains(key) ? Reveal(key, row!.Value) : row!.Value) is { } stored)
            return stored;
        var fromCfg = _cfg[key];
        return string.IsNullOrWhiteSpace(fromCfg) ? null : fromCfg;
    }

    /// <summary>Store (or clear, when null/blank) a setting in the DB.</summary>
    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        value = value?.Trim();
        var row = await _db.Settings.FindAsync(new object?[] { key }, ct);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (row is not null) { _db.Settings.Remove(row); await _db.SaveChangesAsync(ct); }
            return;
        }

        if (Secrets.Contains(key)) value = EncryptedPrefix + _protector.Protect(value);
        if (row is null) _db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync(ct);
    }

    private string? Reveal(string key, string stored)
    {
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return stored; // written before encryption
        try { return _protector.Unprotect(stored[EncryptedPrefix.Length..]); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A database from another server (a restored backup) or a lost key ring: unusable, so unset. Said once
            // per key per process — this is read on every settings load and art fetch.
            if (UndecryptableWarned.TryAdd(key, 0))
                _log.LogWarning("The stored {Key} cannot be decrypted with this server's keys; treating it as unset. Enter it again under Configuration.", key);
            return null;
        }
    }

    /// <summary>Encrypt any secret still stored in plain text (written before encryption existed). Run at startup
    /// and after a restore.</summary>
    public async Task EncryptPlaintextSecretsAsync(CancellationToken ct = default)
    {
        var rows = await _db.Settings.Where(s => Secrets.Contains(s.Key)).ToListAsync(ct);
        var changed = 0;
        foreach (var row in rows.Where(r => r.Value.Length > 0 && !r.Value.StartsWith(EncryptedPrefix, StringComparison.Ordinal)))
        {
            row.Value = EncryptedPrefix + _protector.Protect(row.Value);
            changed++;
        }
        if (changed > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Encrypted {Count} stored secret(s) that predate encryption at rest.", changed);
        }
    }

    public async Task<bool> HasAdminPasswordAsync(CancellationToken ct = default) =>
        !string.IsNullOrEmpty(await GetEffectiveAsync(AdminPasswordHash, ct));

    public async Task SetAdminPasswordAsync(string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            await SetAsync(AdminPasswordHash, null, ct);
        else
            await SetAsync(AdminPasswordHash, Tokens.HashPassword(password), ct);
    }

    public async Task<double> GetAutoFetchHoursAsync(CancellationToken ct = default)
    {
        var value = await GetEffectiveAsync(AgentUpdateAutoFetchHours, ct);
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var hours) &&
               double.IsFinite(hours) && hours >= 0 && hours <= TimeSpan.MaxValue.TotalHours
            ? hours
            : 0;
    }

    public async Task SetAutoFetchHoursAsync(double hours, CancellationToken ct = default)
    {
        if (!double.IsFinite(hours) || hours < 0 || hours > TimeSpan.MaxValue.TotalHours)
            throw new ArgumentOutOfRangeException(nameof(hours), "Hours must be a finite, non-negative value.");

        // Store zero explicitly so an admin can override a positive appsettings/env value to disable polling.
        await SetAsync(AgentUpdateAutoFetchHours,
            hours.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    /// <summary>The full schedule, defaulting Mode to "hours" when never set — the DB carries no
    /// row for it on every deployment that predates weekly/monthly scheduling, and defaulting to
    /// "hours" there means <see cref="GetAutoFetchHoursAsync"/> (and its own config/env fallback)
    /// keeps being the entire story for them, unchanged.</summary>
    public async Task<AutoFetchSchedule> GetAutoFetchScheduleAsync(CancellationToken ct = default)
    {
        var mode = await GetEffectiveAsync(AgentUpdateScheduleMode, ct) ?? "hours";
        var hours = await GetAutoFetchHoursAsync(ct);
        var dayOfWeek = int.TryParse(await GetEffectiveAsync(AgentUpdateScheduleDayOfWeek, ct), out var dw)
            ? Math.Clamp(dw, 0, 6) : 0; // Sunday
        var dayOfMonth = int.TryParse(await GetEffectiveAsync(AgentUpdateScheduleDayOfMonth, ct), out var dm)
            ? Math.Clamp(dm, 1, 31) : 1;
        var timeOfDay = await GetEffectiveAsync(AgentUpdateScheduleTimeOfDay, ct) ?? "03:00";
        return new AutoFetchSchedule(mode, hours, dayOfWeek, dayOfMonth, timeOfDay);
    }

    public async Task SetAutoFetchScheduleAsync(AutoFetchSchedule schedule, CancellationToken ct = default)
    {
        var mode = schedule.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("disabled" or "hours" or "weekly" or "monthly"))
            throw new ArgumentOutOfRangeException(nameof(schedule), "Mode must be disabled, hours, weekly, or monthly.");
        if (schedule.DayOfWeek is < 0 or > 6)
            throw new ArgumentOutOfRangeException(nameof(schedule), "DayOfWeek must be 0-6 (0=Sunday).");
        if (schedule.DayOfMonth is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(schedule), "DayOfMonth must be 1-31.");
        if (!TimeOnly.TryParse(schedule.TimeOfDay, out _))
            throw new ArgumentOutOfRangeException(nameof(schedule), "TimeOfDay must be a time like 03:00.");

        await SetAsync(AgentUpdateScheduleMode, mode, ct);
        // Only touched in hours mode, through its own validated setter — switching to weekly and
        // back to hours later should not have silently zeroed out the interval the admin had.
        if (mode == "hours")
            await SetAutoFetchHoursAsync(schedule.Hours, ct);
        await SetAsync(AgentUpdateScheduleDayOfWeek, schedule.DayOfWeek.ToString(), ct);
        await SetAsync(AgentUpdateScheduleDayOfMonth, schedule.DayOfMonth.ToString(), ct);
        await SetAsync(AgentUpdateScheduleTimeOfDay, schedule.TimeOfDay, ct);
    }

    /// <summary>The dashboard-facing settings snapshot (never includes the raw key).</summary>
    public async Task<ServerSettingsDto> GetServerSettingsDtoAsync(CancellationToken ct = default)
    {
        var inDb = await _db.Settings.AnyAsync(s => s.Key == SteamGridDbApiKey && s.Value != "", ct);
        var key = await GetEffectiveAsync(SteamGridDbApiKey, ct);
        var schedule = await GetAutoFetchScheduleAsync(ct);
        return new ServerSettingsDto(
            SteamGridDbConfigured: !string.IsNullOrWhiteSpace(key),
            SteamGridDbKeyMasked: Mask(key),
            SteamGridDbFromConfig: !inDb && !string.IsNullOrWhiteSpace(key),
            AdminPasswordSet: await HasAdminPasswordAsync(ct),
            DefaultExcludeGlobs: GlobConfig.GlobalDefaults(_cfg),
            AutoFetchHours: schedule.Hours,
            Schedule: schedule,
            NextAutoFetchRunAt: AutoFetchScheduler.ComputeNextRun(schedule, DateTime.UtcNow));
    }

    /// <summary>Show only the last 4 characters so the dashboard can confirm which key is set.</summary>
    private static string? Mask(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Length <= 4) return new string('•', s.Length);
        return new string('•', Math.Min(8, s.Length - 4)) + s[^4..];
    }
}
