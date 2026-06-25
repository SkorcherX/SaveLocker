using LocalGameSync.Server;
using LocalGameSync.Server.Data;
using LocalGameSync.Server.Services;
using LocalGameSync.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["Storage:DbPath"]
             ?? Path.Combine(AppContext.BaseDirectory, "data", "localgamesync.db");
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
}

// Serve the admin dashboard (wwwroot/index.html) at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { service = "LocalGameSync", status = "ok" }));

// ---- Public: machine registration ----
app.MapPost("/api/machines/register", async (MachineRegisterRequest req, SyncService sync) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Machine name is required.");
    return Results.Ok(await sync.RegisterMachineAsync(req.Name.Trim()));
});

// ---- Authenticated API (requires X-Api-Key) ----
var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();

api.MapGet("/games", async (SyncService sync) =>
    Results.Ok((await sync.ListGamesAsync()).Select(g => g.ToDto())));

api.MapGet("/machines", async (SyncService sync) =>
    Results.Ok((await sync.ListMachinesAsync()).Select(m => m.ToDto())));

api.MapDelete("/machines/{id:guid}", async (Guid id, HttpContext http, SyncService sync) =>
{
    // Don't let an admin revoke the key they're currently signed in with.
    if (id == http.CurrentMachine().Id)
        return Results.BadRequest(new { message = "You can't delete the machine whose API key you're signed in with." });
    return await sync.DeleteMachineAsync(id) ? Results.NoContent() : Results.NotFound();
});

api.MapPost("/games/{id:guid}/enabled", async (Guid id, bool value, SyncService sync) =>
    await sync.SetGameEnabledAsync(id, value) ? Results.Ok() : Results.NotFound());

api.MapPost("/games/{id:guid}/save-dir", async (Guid id, string? value, SyncService sync) =>
    await sync.SetSuggestedSaveDirAsync(id, value) ? Results.Ok() : Results.NotFound());

api.MapGet("/overview", async (SyncService sync) =>
    Results.Ok(await sync.GetOverviewAsync()));

// ---- Server settings (dashboard-managed) ----
api.MapGet("/settings", async (SettingsService settings) =>
    Results.Ok(await settings.GetServerSettingsDtoAsync()));

// Set/clear the SteamGridDB API key, then verify it so the admin gets immediate feedback.
api.MapPost("/settings/steamgriddb-key", async (
    SetSteamGridDbKeyRequest req, SettingsService settings, ArtService art) =>
{
    await settings.SetAsync(SettingsService.SteamGridDbApiKey, req.ApiKey);
    if (string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.Ok(new { ok = true, message = "SteamGridDB API key cleared." });
    var (ok, message) = await art.VerifyKeyAsync();
    return Results.Ok(new { ok, message });
});

api.MapPost("/games", async (CreateGameRequest req, SyncService sync, ArtService art) =>
{
    var game = await sync.CreateGameAsync(req);
    // Fetch cover art on first enrollment (best-effort; never blocks enroll on failure).
    if (string.IsNullOrEmpty(game.GridUrl)) await art.TryRefreshOnEnrollAsync(game.Id);
    return Results.Ok(game.ToDto());
});

api.MapPost("/games/{id:guid}/art/refresh", async (Guid id, ArtService art) =>
{
    var (ok, message) = await art.RefreshArtAsync(id);
    return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

api.MapDelete("/games/{id:guid}", async (Guid id, SyncService sync) =>
    await sync.DeleteGameAsync(id) ? Results.NoContent() : Results.NotFound());

api.MapGet("/games/{id:guid}/state", async (Guid id, SyncService sync) =>
    await sync.GetGameStateAsync(id) is { } state ? Results.Ok(state) : Results.NotFound());

api.MapGet("/games/{id:guid}/versions", async (Guid id, SyncService sync) =>
    Results.Ok((await sync.ListVersionsAsync(id)).Select(v => v.ToDto())));

// ---- Leases ----
api.MapPost("/games/{id:guid}/lease", async (Guid id, HttpContext http, SyncService sync) =>
    Results.Ok(await sync.AcquireLeaseAsync(id, http.CurrentMachine().Id)));

api.MapDelete("/games/{id:guid}/lease", async (Guid id, HttpContext http, SyncService sync) =>
{
    await sync.ReleaseLeaseAsync(id, http.CurrentMachine().Id);
    return Results.NoContent();
});

// ---- Upload / download ----
api.MapPost("/games/{id:guid}/upload", async (
    Guid id, HttpContext http, SyncService sync,
    string hash, Guid? parent, bool? force, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(hash))
        return Results.BadRequest("Missing content hash.");

    var machine = http.CurrentMachine();
    var result = await sync.UploadAsync(id, machine.Id, parent, hash, http.Request.Body, force ?? false, ct);
    return Results.Ok(result);
});

api.MapGet("/games/{id:guid}/download", async (Guid id, HttpContext http, SyncService sync) =>
    StreamVersion(http, await sync.DownloadHeadAsync(id)));

api.MapGet("/versions/{versionId:guid}/download", async (Guid versionId, HttpContext http, SyncService sync) =>
    StreamVersion(http, await sync.DownloadVersionAsync(versionId)));

// ---- Admin actions ----
api.MapGet("/conflicts", async (SyncService sync) =>
    Results.Ok((await sync.ListOpenConflictsAsync()).Select(c => c.ToDto())));

api.MapPost("/conflicts/{id:guid}/resolve", async (Guid id, Guid version, HttpContext http, SyncService sync) =>
    await sync.ResolveConflictAsync(id, version, http.CurrentMachine().Name)
        ? Results.Ok() : Results.BadRequest("Could not resolve conflict."));

api.MapPost("/games/{id:guid}/rollback", async (Guid id, Guid version, HttpContext http, SyncService sync) =>
    await sync.RollbackAsync(id, version, http.CurrentMachine().Name)
        ? Results.Ok() : Results.BadRequest("Could not roll back."));

api.MapPost("/games/{id:guid}/set-latest", async (Guid id, Guid version, HttpContext http, SyncService sync) =>
    await sync.SetAsLatestAsync(id, version, http.CurrentMachine().Name)
        ? Results.Ok() : Results.BadRequest("Could not set latest."));

api.MapDelete("/games/{id:guid}/lease/force", async (Guid id, SyncService sync) =>
{
    await sync.ForceReleaseLeaseAsync(id);
    return Results.NoContent();
});

// ---- Agent command channel ----
// Agent: claim my pending commands (marks them Dispatched).
api.MapGet("/agent/commands", async (HttpContext http, SyncService sync) =>
    Results.Ok((await sync.DequeueCommandsAsync(http.CurrentMachine().Id)).Select(c => c.ToDto())));

// Agent: report a command's outcome.
api.MapPost("/agent/commands/{id:guid}/result", async (
    Guid id, CommandResultRequest req, HttpContext http, SyncService sync) =>
    await sync.CompleteCommandAsync(id, http.CurrentMachine().Id, req.Status, req.Result)
        ? Results.Ok() : Results.NotFound());

// Dashboard: queue a command + list recent commands.
api.MapPost("/commands", async (EnqueueCommandRequest req, SyncService sync) =>
    Results.Ok((await sync.EnqueueCommandAsync(req)).ToDto()));

api.MapGet("/commands", async (SyncService sync) =>
    Results.Ok((await sync.ListCommandsAsync()).Select(c => c.ToDto())));

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
