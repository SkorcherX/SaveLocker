using LocalGameSync.Server.Data;
using LocalGameSync.Shared;
using Microsoft.EntityFrameworkCore;

namespace LocalGameSync.Server.Services;

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

    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public SettingsService(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    /// <summary>The DB value if set, else the configuration value, else null.</summary>
    public async Task<string?> GetEffectiveAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.Settings.FindAsync(new object?[] { key }, ct);
        if (!string.IsNullOrWhiteSpace(row?.Value)) return row!.Value;
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

        if (row is null) _db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync(ct);
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

    /// <summary>The dashboard-facing settings snapshot (never includes the raw key).</summary>
    public async Task<ServerSettingsDto> GetServerSettingsDtoAsync(CancellationToken ct = default)
    {
        var inDb = await _db.Settings.AnyAsync(s => s.Key == SteamGridDbApiKey && s.Value != "", ct);
        var key = await GetEffectiveAsync(SteamGridDbApiKey, ct);
        return new ServerSettingsDto(
            SteamGridDbConfigured: !string.IsNullOrWhiteSpace(key),
            SteamGridDbKeyMasked: Mask(key),
            SteamGridDbFromConfig: !inDb && !string.IsNullOrWhiteSpace(key),
            AdminPasswordSet: await HasAdminPasswordAsync(ct));
    }

    /// <summary>Show only the last 4 characters so the dashboard can confirm which key is set.</summary>
    private static string? Mask(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Length <= 4) return new string('•', s.Length);
        return new string('•', Math.Min(8, s.Length - 4)) + s[^4..];
    }
}
