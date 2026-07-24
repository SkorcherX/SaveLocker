using SaveLocker.Server;
using SaveLocker.Server.Data;
using SaveLocker.Server.Services;
using SaveLocker.Shared;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

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
builder.Services.AddHostedService<AgentInstallerPollerService>();

builder.Services.AddSingleton<ArchiveStore>();
builder.Services.AddSingleton<AgentInstallerService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<ArtService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<HealthService>();

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
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info = new() { Title = "SaveLocker API", Version = "v1" };
        return Task.CompletedTask;
    });

    // .NET 10 emits OpenAPI 3.1, which describes numeric types as a union with string:
    // `long` becomes ["integer","string"] (an int64 can exceed JavaScript's safe-integer range)
    // and `double` becomes ["number","string"] (JSON cannot represent NaN/Infinity). Both hedge
    // for a serializer that *might* fall back to a string. Ours never does — System.Text.Json
    // writes these as JSON numbers, always, and throws rather than emitting "NaN".
    //
    // Leaving the hedge in propagates `number | string` into the generated TS types for every
    // size, byte-count and interval field, which then has to be coerced away at ~10 call sites in
    // the dashboard to handle a case that cannot occur. Describe what the server actually sends.
    o.AddSchemaTransformer((schema, _, _) =>
    {
        if (schema.Type is { } t
            && t.HasFlag(JsonSchemaType.String)
            && (t.HasFlag(JsonSchemaType.Integer) || t.HasFlag(JsonSchemaType.Number)))
        {
            schema.Type = t & ~JsonSchemaType.String;
            schema.Pattern = null;
        }
        return Task.CompletedTask;
    });
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

// Unauthenticated on purpose: this is the reachability probe, and the build identity has to be
// readable before you can authenticate — a wrong admin password is one of the things you would be
// diagnosing. It does disclose the exact version and commit to anyone who can reach the port; for a
// self-hosted LAN service that is the accepted trade for keeping the probe open.
app.MapGet("/api/admin/status", async (SettingsService settings) =>
    Results.Ok(new AdminStatus(await settings.HasAdminPasswordAsync(), BuildInfo.Current)))
    .Produces<AdminStatus>();

// ---- Public: enrollment redeem ----
// No auth filter, because the token IS the credential: a fresh agent has nothing else. It is
// single-use and short-lived, so an intercepted policy file is worth far less than the API key
// it replaces (Decisions.md §4). Every failure — unknown, expired, spent — answers 401.
app.MapPost("/api/enroll", async (RedeemEnrollmentRequest req, EnrollmentService enrollment) =>
{
    var (result, error) = await enrollment.RedeemAsync(req);
    return result is null
        ? Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(result);
}).Produces<RedeemEnrollmentResponse>();

// ---- Agent API (requires X-Api-Key: identifies the calling machine) ----
var agent = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();

// Include this machine's stored save path in each game so the agent can use it in reconcile.
agent.MapGet("/games", async (HttpContext http, SyncService sync, IConfiguration cfg) =>
{
    var machine = http.CurrentMachine();
    var games = await sync.ListGamesAsync();
    var pathMap = await sync.GetMachinePathMapAsync(machine.Id);
    // Agents receive the effective exclude set (global defaults ∪ per-game) to apply.
    return Results.Ok(games.Select(g => g.ToDtoWithPath(pathMap.GetValueOrDefault(g.Id))
        with { ExcludeGlobs = GlobConfig.Effective(cfg, g.ExcludeGlobs) }));
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
    Guid id, HttpContext http, SyncService sync, IConfiguration cfg,
    string hash, Guid? parent, bool? force, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(hash))
        return Results.BadRequest("Missing content hash.");

    // Lift Kestrel's 30 MB default to the configured save-upload cap (default 200 MB).
    var sizeCap = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeCap is { IsReadOnly: false })
        sizeCap.MaxRequestBodySize = (long)(cfg.GetValue<int?>("Storage:MaxUploadMb") ?? 200) * 1024 * 1024;

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

// The same game state the console reads, but reachable with a MACHINE key.
//
// `savelocker status` used to call the admin-filtered /games/{id}/state with only X-Api-Key, so it
// worked exactly until an admin password was set on the server and then returned 401 forever —
// on the one command a headless Deck user runs to ask "is my save safe?". Everything else the agent
// does was already on this group; this was the single stray.
agent.MapGet("/agent/games/{id:guid}/state", async (Guid id, SyncService sync) =>
    await sync.GetGameStateAsync(id) is { } state ? Results.Ok(state) : Results.NotFound())
    .Produces<GameStateDto>();

// An agent describing a game's save location GENERICALLY, so every other machine can expand it for
// itself instead of inheriting a literal path that means nothing on their filesystem. Only fills an
// empty value, and only accepts a template — see TrySetSaveTemplateAsync.
agent.MapPost("/agent/games/{id:guid}/template", async (Guid id, string? value, SyncService sync) =>
{
    if (string.IsNullOrWhiteSpace(value)) return Results.BadRequest("A template is required.");
    return await sync.TrySetSaveTemplateAsync(id, value.Trim()) ? Results.Ok() : Results.NoContent();
});

// ---- Agent health (agent) ----
// Piggybacks the existing ~20 s poll, so it costs no new schedule. This is the channel that makes a
// headless spoke visible at all: the Deck cannot toast, so it tells the server and the console shows it.
agent.MapPost("/agent/health", async (AgentHeartbeat beat, HttpContext http, HealthService health) =>
{
    await health.RecordHeartbeatAsync(http.CurrentMachine().Id, beat);
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

admin.MapPost("/games/{id:guid}/excludes", async (Guid id, string[] patterns, SyncService sync) =>
    await sync.SetExcludeGlobsAsync(id, patterns) ? Results.Ok() : Results.NotFound());

admin.MapPost("/games/{id:guid}/conflict-policy", async (
    Guid id, SetConflictPolicyRequest req, SyncService sync) =>
    await sync.SetConflictPolicyAsync(id, req.Policy, req.PreferredMachineId)
        ? Results.Ok() : Results.NotFound());

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

// Unconfirmed guesses each machine's scan reported for this game. Separate from /paths because
// they are offers, not settings — applying one is a POST to /paths above.
admin.MapGet("/games/{id:guid}/path-candidates", async (Guid id, SyncService sync) =>
    Results.Ok(await sync.GetGameScanCandidatesAsync(id)))
    .Produces<List<MachineScanCandidateDto>>();

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

admin.MapPost("/settings/agent-update-auto-fetch", async (
    SetAutoFetchHoursRequest req, SettingsService settings, CancellationToken ct) =>
{
    try
    {
        await settings.SetAutoFetchHoursAsync(req.Hours, ct);
        return Results.Ok(new { autoFetchHours = req.Hours });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(ex.Message);
    }
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

// Download one version from the CONSOLE. The agent group already has this route, but only with a
// machine key — so an admin could not take a copy of a save before doing something destructive to
// it, and "back it up first" was not offerable as a UI step. That gap is what made every recovery
// action during the 2026-07-22 incident feel one-way.
admin.MapGet("/games/{id:guid}/versions/{versionId:guid}/download", async (
    Guid id, Guid versionId, HttpContext http, SyncService sync) =>
{
    var dl = await sync.DownloadVersionAsync(versionId);
    // Checked, not assumed: DownloadVersionAsync resolves by version id alone, so without this a
    // caller could pull any game's archive through any game's URL.
    if (dl is null || dl.Value.version.GameId != id) return Results.NotFound();
    return StreamVersion(http, dl);
});

// Apply retention immediately, instead of only as a side effect of the next upload.
admin.MapPost("/games/{id:guid}/prune", async (Guid id, SyncService sync) =>
    Results.Ok(new PruneResult(await sync.PruneNowAsync(id))));

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

// ---- Agent health (admin) ----
admin.MapGet("/admin/health", async (HealthService health) =>
    Results.Ok(await health.ListAsync()))
    .Produces<List<AgentHealthDto>>();

admin.MapGet("/admin/health/events", async (HealthService health) =>
    Results.Ok(await health.ListOpenEventsAsync()))
    .Produces<List<AgentEventDto>>();

// Dismissing does not fix the condition. If it is still true, the agent's next report reopens it —
// which is the honest behaviour, and the reason this is not called "resolve".
admin.MapPost("/admin/health/events/{id:guid}/dismiss", async (Guid id, HealthService health) =>
    await health.DismissAsync(id) ? Results.NoContent() : Results.NotFound());

// ---- Enrollment tokens (admin) ----
// Minting returns the policy file, raw token included. That token is not stored and cannot be
// shown again — the console hands the file to the user once, or not at all.
admin.MapPost("/admin/enrollments", async (
    CreateEnrollmentRequest req, HttpContext http, EnrollmentService enrollment) =>
{
    // The URL the admin reached the console on is the one that demonstrably works, so it is the
    // default. It is wrong exactly when the console is on the LAN and the agent needs the public
    // tunnel — hence the override on the request.
    var serverUrl = $"{http.Request.Scheme}://{http.Request.Host}";
    return Results.Ok(await enrollment.CreateAsync(req, serverUrl));
}).Produces<CreateEnrollmentResponse>();

admin.MapGet("/admin/enrollments", async (EnrollmentService enrollment) =>
    Results.Ok(await enrollment.ListAsync()))
    .Produces<List<EnrollmentDto>>();

admin.MapDelete("/admin/enrollments/{id:guid}", async (Guid id, EnrollmentService enrollment) =>
    await enrollment.RevokeAsync(id) ? Results.NoContent() : Results.NotFound());

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

// Pulls the latest release installer straight from GitHub instead of a manual upload.
admin.MapPost("/admin/agent-installer/fetch-github", async (
    AgentInstallerService installer, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    try
    {
        var info = await installer.FetchLatestFromGitHubAsync(httpFactory.CreateClient(), ct);
        return Results.Ok(info);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).Produces<AgentInstallerStatus>();

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
