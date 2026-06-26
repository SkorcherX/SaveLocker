using LocalGameSync.Server;
using LocalGameSync.Server.Data;
using LocalGameSync.Server.Services;
using LocalGameSync.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var configuredDbPath = builder.Configuration["Storage:DbPath"];
var defaultDbPath = Path.Combine(AppContext.BaseDirectory, "data", "savelocker.db");
var dbPath = configuredDbPath ?? defaultDbPath;

// Migrate the DB file from the old default name on first run after rename.
if (configuredDbPath is null && !File.Exists(dbPath))
{
    var legacyPath = Path.Combine(AppContext.BaseDirectory, "data", "localgamesync.db");
    if (File.Exists(legacyPath))
        File.Move(legacyPath, dbPath);
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<ArchiveStore>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<ArtService>();

// SteamGridDB client (artwork). The Bearer key is attached per request by ArtService
// (resolved from SettingsService) so it can be set/changed from the dashboard at runtime.
builder.Services.AddHttpClient("steamgriddb", c =>
{
    c.BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/");
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Apply EF Core migrations on startup.
// For DBs created before migrations were introduced (existing deployed machines), the
// schema is already fully up-to-date but there is no __EFMigrationsHistory table.
// Detect that case by checking for the history table while the Games table already
// exists, then seed the history table so Migrate() skips the InitialSchema migration
// rather than attempting to recreate tables that are already there.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var historyExists = db.Database
        .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'")
        .Single() > 0;

    if (!historyExists)
    {
        var gamesTableExists = db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='Games'")
            .Single() > 0;

        if (gamesTableExists)
        {
            // Pre-migration DB: schema is already at InitialSchema; just seed the history table.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """);
            db.Database.ExecuteSqlRaw("""
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260624011934_InitialSchema', '9.0.9');
                """);
        }
    }

    db.Database.Migrate();

    // WAL mode: allows concurrent readers alongside the single writer, which prevents
    // "database is locked" 500s when the dashboard fires several parallel API calls.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

    // Additive table: per-machine save paths (not part of the InitialSchema migration).
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "MachineSavePaths" (
            "MachineId" TEXT NOT NULL,
            "GameId"    TEXT NOT NULL,
            "SavePath"  TEXT NOT NULL,
            PRIMARY KEY ("MachineId", "GameId")
        );
        """);

}

// Serve the admin dashboard (wwwroot/index.html) at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { service = "SaveLocker", status = "ok" }));

// ---- Public: machine registration + admin status ----
app.MapPost("/api/machines/register", async (MachineRegisterRequest req, SyncService sync) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Machine name is required.");
    return Results.Ok(await sync.RegisterMachineAsync(req.Name.Trim()));
});

app.MapGet("/api/admin/status", async (SettingsService settings) =>
    Results.Ok(new { passwordRequired = await settings.HasAdminPasswordAsync() }));

// ---- Agent API (requires X-Api-Key: identifies the calling machine) ----
var agent = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();

// Include this machine's stored save path in each game so the agent can use it in reconcile.
agent.MapGet("/games", async (HttpContext http, SyncService sync) =>
{
    var machine = http.CurrentMachine();
    var games = await sync.ListGamesAsync();
    var pathMap = await sync.GetMachinePathMapAsync(machine.Id);
    return Results.Ok(games.Select(g => g.ToDtoWithPath(pathMap.GetValueOrDefault(g.Id))));
});

// ---- Leases (agent) ----
agent.MapPost("/games/{id:guid}/lease", async (Guid id, HttpContext http, SyncService sync) =>
    Results.Ok(await sync.AcquireLeaseAsync(id, http.CurrentMachine().Id)));

agent.MapDelete("/games/{id:guid}/lease", async (Guid id, HttpContext http, SyncService sync) =>
{
    await sync.ReleaseLeaseAsync(id, http.CurrentMachine().Id);
    return Results.NoContent();
});

// ---- Upload / download (agent) ----
agent.MapPost("/games/{id:guid}/upload", async (
    Guid id, HttpContext http, SyncService sync,
    string hash, Guid? parent, bool? force, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(hash))
        return Results.BadRequest("Missing content hash.");

    var machine = http.CurrentMachine();
    var result = await sync.UploadAsync(id, machine.Id, parent, hash, http.Request.Body, force ?? false, ct);
    return Results.Ok(result);
});

agent.MapGet("/games/{id:guid}/download", async (Guid id, HttpContext http, SyncService sync) =>
    StreamVersion(http, await sync.DownloadHeadAsync(id)));

agent.MapGet("/versions/{versionId:guid}/download", async (Guid versionId, HttpContext http, SyncService sync) =>
    StreamVersion(http, await sync.DownloadVersionAsync(versionId)));

// ---- Agent command channel ----
agent.MapGet("/agent/commands", async (HttpContext http, SyncService sync) =>
    Results.Ok((await sync.DequeueCommandsAsync(http.CurrentMachine().Id)).Select(c => c.ToDto())));

agent.MapPost("/agent/commands/{id:guid}/result", async (
    Guid id, CommandResultRequest req, HttpContext http, SyncService sync) =>
    await sync.CompleteCommandAsync(id, http.CurrentMachine().Id, req.Status, req.Result)
        ? Results.Ok() : Results.NotFound());

agent.MapPost("/agent/path/{gameId:guid}", async (Guid gameId, HttpContext http, string? value, SyncService sync) =>
{
    if (!string.IsNullOrWhiteSpace(value))
        await sync.SetMachinePathAsync(http.CurrentMachine().Id, gameId, value.Trim());
    return Results.Ok();
});

// ---- Admin dashboard API (requires X-Admin-Password when one is set) ----
var admin = app.MapGroup("/api").AddEndpointFilter<AdminPasswordFilter>();

admin.MapGet("/machines", async (SyncService sync) =>
    Results.Ok((await sync.ListMachinesAsync()).Select(m => m.ToDto())));

admin.MapDelete("/machines/{id:guid}", async (Guid id, SyncService sync) =>
    await sync.DeleteMachineAsync(id) ? Results.NoContent() : Results.NotFound());

admin.MapPost("/games/{id:guid}/enabled", async (Guid id, bool value, SyncService sync) =>
    await sync.SetGameEnabledAsync(id, value) ? Results.Ok() : Results.NotFound());

admin.MapPost("/games/{id:guid}/save-dir", async (Guid id, string? value, SyncService sync) =>
    await sync.SetSuggestedSaveDirAsync(id, value) ? Results.Ok() : Results.NotFound());

admin.MapPost("/games/{id:guid}/retain", async (Guid id, int? value, SyncService sync) =>
    await sync.SetGameRetentionAsync(id, value) ? Results.Ok() : Results.NotFound());

admin.MapDelete("/games/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, SyncService sync) =>
{
    var (ok, error) = await sync.DeleteVersionAsync(id, versionId);
    return ok ? Results.Ok() : (error == "not_found" ? Results.NotFound() : Results.BadRequest(error));
});

// ---- Per-machine save paths (admin) ----
admin.MapGet("/games/{id:guid}/paths", async (Guid id, SyncService sync) =>
    Results.Ok(await sync.GetGameMachinePathsAsync(id)));

admin.MapPost("/games/{id:guid}/paths/{machineId:guid}", async (Guid id, Guid machineId, string? value, SyncService sync) =>
{
    if (string.IsNullOrWhiteSpace(value))
        await sync.ClearMachinePathAsync(machineId, id);
    else
        await sync.SetMachinePathAsync(machineId, id, value.Trim());
    return Results.Ok();
});

admin.MapDelete("/games/{id:guid}/paths/{machineId:guid}", async (Guid id, Guid machineId, SyncService sync) =>
{
    await sync.ClearMachinePathAsync(machineId, id);
    return Results.NoContent();
});

admin.MapGet("/overview", async (SyncService sync) =>
    Results.Ok(await sync.GetOverviewAsync()));

// ---- Server settings (admin) ----
admin.MapGet("/settings", async (SettingsService settings) =>
    Results.Ok(await settings.GetServerSettingsDtoAsync()));

admin.MapPost("/settings/steamgriddb-key", async (
    SetSteamGridDbKeyRequest req, SettingsService settings, ArtService art) =>
{
    await settings.SetAsync(SettingsService.SteamGridDbApiKey, req.ApiKey);
    if (string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.Ok(new { ok = true, message = "SteamGridDB API key cleared." });
    var (ok, message) = await art.VerifyKeyAsync();
    return Results.Ok(new { ok, message });
});

admin.MapPost("/admin/password", async (SetAdminPasswordRequest req, SettingsService settings) =>
{
    await settings.SetAdminPasswordAsync(req.Password);
    var msg = string.IsNullOrWhiteSpace(req.Password) ? "Admin password cleared." : "Admin password updated.";
    return Results.Ok(new { ok = true, message = msg });
});

admin.MapPost("/games", async (CreateGameRequest req, SyncService sync, ArtService art) =>
{
    var game = await sync.CreateGameAsync(req);
    if (string.IsNullOrEmpty(game.GridUrl)) await art.TryRefreshOnEnrollAsync(game.Id);
    return Results.Ok(game.ToDto());
});

admin.MapPost("/games/{id:guid}/art/refresh", async (Guid id, ArtService art) =>
{
    var (ok, message) = await art.RefreshArtAsync(id);
    return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

admin.MapDelete("/games/{id:guid}", async (Guid id, SyncService sync) =>
    await sync.DeleteGameAsync(id) ? Results.NoContent() : Results.NotFound());

admin.MapGet("/games/{id:guid}/state", async (Guid id, SyncService sync) =>
    await sync.GetGameStateAsync(id) is { } state ? Results.Ok(state) : Results.NotFound());

admin.MapGet("/games/{id:guid}/versions", async (Guid id, SyncService sync) =>
    Results.Ok((await sync.ListVersionsAsync(id)).Select(v => v.ToDto())));

// ---- Admin actions ----
admin.MapGet("/conflicts", async (SyncService sync) =>
    Results.Ok((await sync.ListOpenConflictsAsync()).Select(c => c.ToDto())));

admin.MapPost("/conflicts/{id:guid}/resolve", async (Guid id, Guid version, SyncService sync) =>
    await sync.ResolveConflictAsync(id, version, "admin")
        ? Results.Ok() : Results.BadRequest("Could not resolve conflict."));

admin.MapPost("/games/{id:guid}/rollback", async (Guid id, Guid version, SyncService sync) =>
    await sync.RollbackAsync(id, version, "admin")
        ? Results.Ok() : Results.BadRequest("Could not roll back."));

admin.MapPost("/games/{id:guid}/set-latest", async (Guid id, Guid version, SyncService sync) =>
    await sync.SetAsLatestAsync(id, version, "admin")
        ? Results.Ok() : Results.BadRequest("Could not set latest."));

admin.MapDelete("/games/{id:guid}/lease/force", async (Guid id, SyncService sync) =>
{
    await sync.ForceReleaseLeaseAsync(id);
    return Results.NoContent();
});

// ---- Command channel (admin side: queue + list) ----
admin.MapPost("/commands", async (EnqueueCommandRequest req, SyncService sync) =>
    Results.Ok((await sync.EnqueueCommandAsync(req)).ToDto()));

admin.MapGet("/commands", async (SyncService sync) =>
    Results.Ok((await sync.ListCommandsAsync()).Select(c => c.ToDto())));

admin.MapGet("/audit", async (SyncService sync, int limit = 200) =>
    Results.Ok(await sync.GetAuditLogAsync(Math.Clamp(limit, 1, 1000))));

app.Run();

// Streams a downloaded version, exposing its id and content hash as response
// headers so the agent can record the parent version for its next upload.
static IResult StreamVersion(HttpContext http, (SaveVersion version, Stream content)? dl)
{
    if (dl is null) return Results.NotFound();
    var (version, content) = dl.Value;
    http.Response.Headers["X-Version-Id"] = version.Id.ToString();
    http.Response.Headers["X-Content-Hash"] = version.ContentHash;
    return Results.Stream(content, "application/zip", $"{version.Id:N}.zip",
        enableRangeProcessing: false);
}

public partial class Program { }
