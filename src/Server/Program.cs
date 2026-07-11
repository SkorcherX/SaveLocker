using SaveLocker.Server;
using SaveLocker.Server.Data;
using SaveLocker.Server.Services;
using SaveLocker.Shared;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var configuredDbPath = builder.Configuration["Storage:DbPath"];
var defaultDbPath = Path.Combine(AppContext.BaseDirectory, "data", "savelocker.db");
var dbPath = configuredDbPath ?? defaultDbPath;

// Migrate the DB file from the old default name on first run after rename.
if (configuredDbPath is null && !File.Exists(dbPath))
{
    var legacyPath = Path.Combine(AppContext.BaseDirectory, "data", "SaveLocker.db");
    if (File.Exists(legacyPath))
        File.Move(legacyPath, dbPath);
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));

// Nightly SQLite snapshots (VACUUM INTO). The DB is the version graph; archives are
// useless without it, so keep a self-contained on-box backup with simple retention.
var backupRoot = builder.Configuration["Storage:BackupRoot"]
    ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
builder.Services.AddSingleton(new BackupOptions
{
    DbPath = dbPath,
    BackupRoot = backupRoot,
    RetentionCount = builder.Configuration.GetValue<int?>("Backup:RetentionCount") ?? 7,
    HourOfDay = builder.Configuration.GetValue<int?>("Backup:HourOfDay") ?? 3,
    Enabled = builder.Configuration.GetValue<bool?>("Backup:Enabled") ?? true,
});
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupScheduler>();
builder.Services.AddHostedService<LeaseSweeperService>();

builder.Services.AddSingleton<ArchiveStore>();
builder.Services.AddSingleton<AgentInstallerService>();
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

// OpenAPI document (/openapi/v1.json) — the single source of truth for the REST
// contract. The web dashboard's TS types are generated from it (openapi-typescript),
// so hand-written types can no longer drift from the server. Endpoint response
// schemas come from the .Produces<T>() annotations on the routes below.
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info = new() { Title = "SaveLocker API", Version = "v1" };
    return Task.CompletedTask;
}));
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
            historyExists = true;

            // If RetainVersions was already added by the pre-migration manual workaround,
            // stamp the migration as applied so EF doesn't attempt the ALTER TABLE again.
            var hasRetainVersions = db.Database
                .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Games')")
                .ToList()
                .Contains("RetainVersions");
            if (hasRetainVersions)
                db.Database.ExecuteSqlRaw("""
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260626031438_AddGameRetainVersions', '9.0.9');
                    """);
        }
    }

    // MachineSavePaths used to be created out-of-band via CREATE TABLE IF NOT EXISTS
    // (it predates being an EF entity). On any DB where that table already exists,
    // stamp the AddMachineSavePaths migration as applied so Migrate() doesn't try to
    // recreate it (which would throw "table already exists"). Only meaningful once a
    // history table is present — a fresh DB has neither and gets the table from Migrate().
    if (historyExists)
    {
        var machineSavePathsExists = db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='MachineSavePaths'")
            .Single() > 0;
        if (machineSavePathsExists)
            db.Database.ExecuteSqlRaw("""
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260706022305_AddMachineSavePaths', '9.0.9');
                """);
    }

    db.Database.Migrate();

    // WAL mode: allows concurrent readers alongside the single writer, which prevents
    // "database is locked" 500s when the dashboard fires several parallel API calls.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

// OpenAPI JSON at /openapi/v1.json + a Swagger UI explorer at /swagger.
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "SaveLocker API v1"));

// Serve the admin dashboard (wwwroot/index.html) at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

// In production, art lives on the persistent volume (Storage:ArtRoot = /data/art).
// Serve it under /art so existing URLs (/art/{gameId}/{kind}.jpg) keep working.
var artRootPath = app.Configuration["Storage:ArtRoot"];
if (!string.IsNullOrWhiteSpace(artRootPath))
{
    Directory.CreateDirectory(artRootPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(artRootPath),
        RequestPath = "/art"
    });
}

app.MapGet("/health", () => Results.Ok(new { service = "SaveLocker", status = "ok" }));

// ---- Public: machine registration + admin status ----
// First-time registration is open so a new agent can enroll on a trusted LAN.
// Re-registering an EXISTING machine name rotates that machine's API key — which
// would let anyone able to reach the server hijack its identity (the real agent
// gets locked out). So once an admin password is set, re-registration must carry
// it in the X-Admin-Password header. With no password configured the endpoint
// stays fully open (first-run behaviour, matching AdminPasswordFilter).
app.MapPost("/api/machines/register", async (
    MachineRegisterRequest req, HttpContext http, SyncService sync, SettingsService settings) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Machine name is required.");
    var name = req.Name.Trim();

    if (await sync.MachineExistsAsync(name))
    {
        var storedHash = await settings.GetEffectiveAsync(SettingsService.AdminPasswordHash);
        if (!string.IsNullOrEmpty(storedHash))
        {
            var provided = http.Request.Headers["X-Admin-Password"].FirstOrDefault();
            if (string.IsNullOrEmpty(provided) || !Tokens.VerifyPassword(provided, storedHash))
                return Results.Json(
                    new { error = "This machine name is already registered. Re-registering rotates its key and requires the admin password." },
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    return Results.Ok(await sync.RegisterMachineAsync(name));
}).Produces<MachineRegisterResponse>();

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
}).Produces<List<GameDto>>();

// ---- Leases (agent) ----
agent.MapPost("/games/{id:guid}/lease", async (Guid id, HttpContext http, SyncService sync) =>
    Results.Ok(await sync.AcquireLeaseAsync(id, http.CurrentMachine().Id)))
    .Produces<LeaseAcquireResponse>();

agent.MapDelete("/games/{id:guid}/lease", async (Guid id, HttpContext http, SyncService sync) =>
{
    await sync.ReleaseLeaseAsync(id, http.CurrentMachine().Id);
    return Results.NoContent();
});

agent.MapPost("/games/{id:guid}/lease/renew", async (Guid id, HttpContext http, SyncService sync) =>
{
    var ok = await sync.RenewLeaseAsync(id, http.CurrentMachine().Id);
    return ok ? Results.Ok() : Results.Conflict("Lease not held by this machine.");
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
}).Produces<UploadResult>();

agent.MapGet("/games/{id:guid}/download", async (Guid id, HttpContext http, SyncService sync) =>
    StreamVersion(http, await sync.DownloadHeadAsync(id)));

agent.MapGet("/versions/{versionId:guid}/download", async (Guid versionId, HttpContext http, SyncService sync) =>
    StreamVersion(http, await sync.DownloadVersionAsync(versionId)));

// ---- Game creation (agent enrollment) ----
// Agents create games during enrollment using their API key.
// The admin POST /api/games route (below) handles dashboard-side game creation.
agent.MapPost("/agent/games", async (HttpContext http, CreateGameRequest req, SyncService sync, ArtService art) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Game name is required.");
    var game = await sync.CreateGameAsync(req);
    if (string.IsNullOrEmpty(game.GridUrl)) await art.TryRefreshOnEnrollAsync(game.Id);
    return Results.Ok(game.ToDto());
}).Produces<GameDto>();

// ---- Agent command channel ----
agent.MapGet("/agent/commands", async (HttpContext http, SyncService sync) =>
    Results.Ok((await sync.DequeueCommandsAsync(http.CurrentMachine().Id)).Select(c => c.ToDto())))
    .Produces<List<AgentCommandDto>>();

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

agent.MapGet("/agent/latest", (IConfiguration cfg, AgentInstallerService installer, HttpContext ctx) =>
{
    // Locally hosted installer takes precedence over the config-based URL.
    var info = installer.GetInfo();
    if (info is not null)
    {
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        return Results.Ok(new AgentVersionInfo(info.Version, $"{baseUrl}/api/agent/installer/download"));
    }

    // Fall back to the static config (backward-compat with the original design).
    var ver = cfg["AgentUpdate:LatestVersion"];
    var url = cfg["AgentUpdate:DownloadUrl"];
    return string.IsNullOrWhiteSpace(ver)
        ? Results.NoContent()
        : Results.Ok(new AgentVersionInfo(ver, url ?? ""));
}).Produces<AgentVersionInfo>();

// Serves the hosted installer to agents. Public so the admin can also download it directly.
app.MapGet("/api/agent/installer/download", (AgentInstallerService installer) =>
{
    var path = installer.GetInstallerPath();
    if (path is null) return Results.NotFound();
    var stream = File.OpenRead(path);
    return Results.Stream(stream, "application/octet-stream", Path.GetFileName(path));
});

// ---- Admin dashboard API (requires X-Admin-Password when one is set) ----
var admin = app.MapGroup("/api").AddEndpointFilter<AdminPasswordFilter>();

admin.MapGet("/machines", async (SyncService sync) =>
    Results.Ok((await sync.ListMachinesAsync()).Select(m => m.ToDto())))
    .Produces<List<MachineDto>>();

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
    Results.Ok(await sync.GetGameMachinePathsAsync(id)))
    .Produces<List<MachineSavePathDto>>();

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
    Results.Ok(await sync.GetOverviewAsync()))
    .Produces<List<GameStateDto>>();

// ---- Server settings (admin) ----
admin.MapGet("/settings", async (SettingsService settings) =>
    Results.Ok(await settings.GetServerSettingsDtoAsync()))
    .Produces<ServerSettingsDto>();

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
}).Produces<GameDto>();

admin.MapPost("/games/{id:guid}/art/refresh", async (Guid id, ArtService art) =>
{
    var (ok, message) = await art.RefreshArtAsync(id);
    return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

admin.MapDelete("/games/{id:guid}", async (Guid id, SyncService sync) =>
    await sync.DeleteGameAsync(id) ? Results.NoContent() : Results.NotFound());

admin.MapGet("/games/{id:guid}/state", async (Guid id, SyncService sync) =>
    await sync.GetGameStateAsync(id) is { } state ? Results.Ok(state) : Results.NotFound())
    .Produces<GameStateDto>();

admin.MapGet("/games/{id:guid}/versions", async (Guid id, SyncService sync) =>
    Results.Ok((await sync.ListVersionsAsync(id)).Select(v => v.ToDto())))
    .Produces<List<SaveVersionDto>>();

// ---- Admin actions ----
admin.MapGet("/conflicts", async (SyncService sync) =>
    Results.Ok((await sync.ListOpenConflictsAsync()).Select(c => c.ToDto())))
    .Produces<List<ConflictDto>>();

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
    Results.Ok((await sync.EnqueueCommandAsync(req)).ToDto()))
    .Produces<AgentCommandDto>();

admin.MapGet("/commands", async (SyncService sync) =>
    Results.Ok((await sync.ListCommandsAsync()).Select(c => c.ToDto())))
    .Produces<List<AgentCommandDto>>();

admin.MapGet("/audit", async (SyncService sync, int limit = 200) =>
    Results.Ok(await sync.GetAuditLogAsync(Math.Clamp(limit, 1, 1000))))
    .Produces<List<AuditEntryDto>>();

// ---- Server backups (admin) ----
admin.MapGet("/admin/backups", (BackupService backup) =>
    Results.Ok(backup.ListBackups()))
    .Produces<List<BackupInfo>>();

admin.MapPost("/admin/backup", async (BackupService backup, CancellationToken ct) =>
    Results.Ok(await backup.BackupAsync(ct)))
    .Produces<BackupResult>();

// ---- Agent installer management (admin) ----
admin.MapGet("/admin/agent-installer", (AgentInstallerService installer) =>
{
    var info = installer.GetInfo();
    return info is null ? Results.NoContent() : Results.Ok(info);
}).Produces<AgentInstallerStatus>();

admin.MapPost("/admin/agent-installer", async (
    HttpRequest req, AgentInstallerService installer, CancellationToken ct) =>
{
    // Kestrel's default body limit is 30 MB; installers are ~43 MB.
    // Must be set before ReadFormAsync begins reading the body.
    var sizeCap = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeCap is { IsReadOnly: false }) sizeCap.MaxRequestBodySize = null;

    var version = req.Query["version"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(version))
        return Results.BadRequest("version query parameter is required.");

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null)
        return Results.BadRequest("file field is required.");

    await using var stream = file.OpenReadStream();
    var info = await installer.SaveAsync(stream, version, file.FileName, ct);
    return Results.Ok(info);
}).Produces<AgentInstallerStatus>();

admin.MapDelete("/admin/agent-installer", (AgentInstallerService installer) =>
{
    installer.Delete();
    return Results.NoContent();
});

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
