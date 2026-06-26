using System.Net.Http.Headers;
using System.Text.Json;
using LocalGameSync.Server.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace LocalGameSync.Server.Services;

/// <summary>
/// Fetches cover/hero/logo/icon artwork for a game from SteamGridDB
/// (https://www.steamgriddb.com/api/v2) and caches the images locally under
/// <c>wwwroot/art/{gameId}/</c>, storing the served relative URLs on the
/// <see cref="Game"/>. Per the design we cache server-side and are polite
/// (fetch on enroll or an explicit refresh).
///
/// Requires a free API key, now managed from the dashboard (<see cref="SettingsService"/>,
/// DB value overriding config <c>SteamGridDb:ApiKey</c> / env <c>SteamGridDb__ApiKey</c>).
/// The key is resolved per call and attached per request, so a dashboard change takes
/// effect immediately without a restart. With no key, refresh is a no-op with an
/// explanatory message.
/// </summary>
public sealed class ArtService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly HttpClient _http;       // api.steamgriddb.com: base address (Bearer added per request)
    private readonly HttpClient _download;   // plain client for CDN image GETs (NO auth header)
    private readonly string _artRoot;        // wwwroot/art
    private string? _apiKey;                 // resolved at the start of each operation

    // Asset kind -> SteamGridDB endpoint (relative to the api/v2 base).
    private static readonly (string kind, string path)[] Assets =
    {
        ("grid", "grids/game/{0}?dimensions=600x900&types=static&limit=1"),
        ("hero", "heroes/game/{0}?limit=1"),
        ("logo", "logos/game/{0}?limit=1"),
        ("icon", "icons/game/{0}?limit=1"),
    };

    public ArtService(AppDbContext db, SettingsService settings, IHttpClientFactory factory, IWebHostEnvironment env)
    {
        _db = db;
        _settings = settings;
        _http = factory.CreateClient("steamgriddb");
        // Asset images live on a separate CDN host that rejects the API bearer token,
        // so download them with a clean client carrying no Authorization header.
        _download = factory.CreateClient();
        _artRoot = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "art");
    }

    private Task<string?> ResolveKeyAsync(CancellationToken ct) =>
        _settings.GetEffectiveAsync(SettingsService.SteamGridDbApiKey, ct);

    /// <summary>Check the configured key is accepted by SteamGridDB (used after a dashboard save).</summary>
    public async Task<(bool ok, string message)> VerifyKeyAsync(CancellationToken ct = default)
    {
        _apiKey = await ResolveKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(_apiKey))
            return (false, "No SteamGridDB API key is configured.");
        try
        {
            using var doc = await GetJsonAsync("search/autocomplete/celeste", ct);
            return doc is not null
                ? (true, "API key verified with SteamGridDB.")
                : (false, "SteamGridDB rejected the key (check that it was pasted correctly).");
        }
        catch (Exception ex)
        {
            return (false, "Could not reach SteamGridDB: " + ex.Message);
        }
    }

    /// <summary>(Re)fetch and cache artwork for a game by name. Returns a status message.</summary>
    public async Task<(bool ok, string message)> RefreshArtAsync(Guid gameId, CancellationToken ct = default)
    {
        _apiKey = await ResolveKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(_apiKey))
            return (false, "SteamGridDB API key not configured — set it in the dashboard (Server settings).");

        var game = await _db.Games.FindAsync(new object?[] { gameId }, ct);
        if (game is null) return (false, "Unknown game.");

        var sgdbId = await FindGameIdAsync(game.Name, ct);
        if (sgdbId is null) return (false, $"No SteamGridDB match for \"{game.Name}\".");

        var found = new List<string>();
        foreach (var (kind, pathTemplate) in Assets)
        {
            var url = await FirstAssetUrlAsync(string.Format(pathTemplate, sgdbId), ct);
            if (url is null) continue;
            var cached = await DownloadAsync(gameId, kind, url, ct);
            if (cached is null) continue;

            switch (kind)
            {
                case "grid": game.GridUrl = cached; break;
                case "hero": game.HeroUrl = cached; break;
                case "logo": game.LogoUrl = cached; break;
                case "icon": game.IconUrl = cached; break;
            }
            found.Add(kind);
        }

        await _db.SaveChangesAsync(ct);
        return found.Count == 0
            ? (false, $"Matched SteamGridDB id {sgdbId} but found no downloadable assets.")
            : (true, $"Updated art: {string.Join(", ", found)}.");
    }

    /// <summary>Best-effort fetch used on enroll; swallows errors so enroll never fails on art.</summary>
    public async Task TryRefreshOnEnrollAsync(Guid gameId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(await ResolveKeyAsync(ct))) return;
        try { await RefreshArtAsync(gameId, ct); } catch { /* art is non-critical */ }
    }

    // ----- SteamGridDB calls -----

    private async Task<int?> FindGameIdAsync(string name, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"search/autocomplete/{Uri.EscapeDataString(name)}", ct);
        if (doc is null) return null;
        var data = doc.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetInt32() : null;
    }

    private async Task<string?> FirstAssetUrlAsync(string path, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(path, ct);
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return null;
        return data[0].TryGetProperty("url", out var u) ? u.GetString() : null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        // Attach the current key per request (it can change at runtime via the dashboard).
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        // SteamGridDB wraps every response in { success, data }.
        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            doc.Dispose();
            return null;
        }
        return doc;
    }

    // Hero images are wide banners; SteamGridDB serves them at full resolution (~9.5 MB
    // at 1920×620). Cap them at this width to keep file sizes reasonable.
    private const int HeroMaxWidth = 920;

    /// <summary>Download an asset into wwwroot/art/{gameId}/{kind}{ext}; return its served URL.</summary>
    private async Task<string?> DownloadAsync(Guid gameId, string kind, string url, CancellationToken ct)
    {
        var dir = Path.Combine(_artRoot, gameId.ToString("N"));
        Directory.CreateDirectory(dir);

        var bytes = await _download.GetByteArrayAsync(url, ct);

        string file;
        if (kind == "hero")
        {
            // Downscale to HeroMaxWidth, preserving aspect ratio, and store as JPEG.
            file = Path.Combine(dir, "hero.jpg");
            await ResizeHeroAsync(bytes, file, ct);
        }
        else
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";
            file = Path.Combine(dir, kind + ext);
            await File.WriteAllBytesAsync(file, bytes, ct);
        }

        // Cache-bust with the write time so the dashboard <img> refreshes after a re-fetch.
        var filename = Path.GetFileName(file);
        return $"/art/{gameId:N}/{filename}?v={DateTime.UtcNow.Ticks}";
    }

    private static async Task ResizeHeroAsync(byte[] bytes, string destPath, CancellationToken ct)
    {
        using var image = Image.Load(bytes);
        if (image.Width > HeroMaxWidth)
            image.Mutate(x => x.Resize(HeroMaxWidth, 0)); // height=0 preserves aspect ratio

        var encoder = new JpegEncoder { Quality = 85 };
        await using var fs = File.Create(destPath);
        await image.SaveAsync(fs, encoder, ct);
    }
}
