using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;

namespace SaveLocker.Agent;

/// <summary>
/// ASP.NET Core host that drives the SaveLocker Agent UI. It serves the bundled React app and a
/// typed local API whose OpenAPI document is the source for the UI's generated TypeScript types.
/// </summary>
public sealed class AgentApiServer : IDisposable
{
    private readonly AgentConfig _config;
    private readonly Func<Task<IReadOnlyList<ScanCandidate>>> _doScan;
    private readonly Func<IReadOnlyList<ScanCandidate>, int[], Task<(int enrolled, int skipped)>> _enroll;
    private readonly IAutoStart _autoStart;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<LaunchCommandDto> _launchInfo;
    private readonly Func<DeckyStatusDto> _deckyStatus;
    private readonly PathBrowser _browser;
    private readonly Action? _onConnectionChanged;
    // Invoked after the tracked-game list or a save folder changed AND was durably written, so the
    // host can rebuild its folder watchers. Ordering matters: watchers must be rebuilt from the
    // config that is on disk, never from one a concurrent write is about to supersede.
    private readonly Action? _onGamesChanged;
    private readonly Func<UpdateResult?> _getUpdateResult;
    // "Downloaded, verified and waiting", which is a different state from _getUpdateResult's "the
    // server is offering something newer" — and the only one anything may offer to install now.
    // Injected because Updater lives in Agent.Linux: Windows stages nothing and supplies nothing.
    private readonly Func<StagedUpdateInfo?> _stagedUpdate;
    // Owned by the host so it outlives any single engine rebuild (a settings change replaces the
    // engine; the activity feed a user is watching should not reset because of that).
    private readonly SyncActivityTracker _activity;
    // "Sync all" for the Overview page's button — same operation the tray menu offers, resolved
    // against whichever engine and game list are current at the moment it is clicked.
    private readonly Func<Task<string>> _syncAll;
    private readonly string _uiRoot;
    private readonly LocalAuth _auth;
    // Lease warnings are persisted, not held in memory: the Linux launch wrapper is a separate
    // short-lived process from the daemon, so a warning it raises can only reach a UI through
    // shared state on disk. See LeaseWarningStore.
    private readonly LeaseWarningStore _leaseWarnings;

    private IReadOnlyList<ScanCandidate>? _candidateCache;
    private WebApplication? _app;

    public int Port { get; }

    public AgentApiServer(
        int port,
        AgentConfig config,
        Func<Task<IReadOnlyList<ScanCandidate>>> doScan,
        Func<IReadOnlyList<ScanCandidate>, int[], Task<(int enrolled, int skipped)>> enroll,
        IAutoStart autoStart,
        Func<Task<string?>>? pickFolder = null,
        Action? onConnectionChanged = null,
        Func<UpdateResult?>? getUpdateResult = null,
        IEnumerable<string>? browseRoots = null,
        Func<LaunchCommandDto>? launchInfo = null,
        Action? onGamesChanged = null,
        Func<DeckyStatusDto>? deckyStatus = null,
        Func<StagedUpdateInfo?>? stagedUpdate = null,
        SyncActivityTracker? activity = null,
        Func<Task<string>>? syncAll = null)
    {
        _browser = new PathBrowser(browseRoots);
        Port = port;
        _config = config;
        _doScan = doScan;
        _enroll = enroll;
        _autoStart = autoStart;
        _pickFolder = pickFolder ?? (() => Task.FromResult<string?>(null));
        _launchInfo = launchInfo ?? (() => new LaunchCommandDto(null, null));
        // Default is "not applicable", which is exactly right for the Windows tray: Decky cannot
        // exist there, so the host injects nothing and the UI hides the card.
        _deckyStatus = deckyStatus ?? (() => new DeckyStatusDto(false, false, false, null, ""));
        _onConnectionChanged = onConnectionChanged;
        _onGamesChanged = onGamesChanged;
        _getUpdateResult = getUpdateResult ?? (() => null);
        _stagedUpdate = stagedUpdate ?? (() => null);
        _activity = activity ?? new SyncActivityTracker();
        _syncAll = syncAll ?? (() => Task.FromResult("Not available."));
        _uiRoot = Path.Combine(AppContext.BaseDirectory, "agent-ui");
        _auth = LocalAuth.LoadOrCreate(config.ConfigPath);
        _leaseWarnings = LeaseWarningStore.For(config);
    }

    public void AddLeaseWarning(string gameName, string holderMachine) =>
        _leaseWarnings.Add(gameName, holderMachine);

    public void ClearLeaseWarning(string gameName) => _leaseWarnings.Clear(gameName);

    public void Start()
    {
        if (_app is not null) return;

        var options = new WebApplicationOptions
        {
            ApplicationName = typeof(AgentApiServer).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Directory.Exists(_uiRoot) ? _uiRoot : null,
        };
        var builder = WebApplication.CreateSlimBuilder(options);

        // Loopback only, always. The management API hands out control of this machine, and binding
        // it to a LAN interface would expose that to the whole network — see Decisions.md.
        builder.WebHost.ConfigureKestrel(server => server.ListenLocalhost(Port));

        // No CORS policy on purpose: the bundled UI is same-origin, so nothing legitimate needs
        // one, and the previous AllowAnyOrigin let any web page read this API's responses.
        builder.Services.AddOpenApi(o =>
        {
            // .NET 10 emits OpenAPI 3.1, which hedges `long` as ["integer","string"] for a
            // serializer that might fall back to a string — System.Text.Json never does. Left in,
            // every byte-count field (ActivitySnapshotDto's progress) generates as `number | string`
            // in agent-ui's types for a case that cannot occur. Same fix as the server's own
            // AddOpenApi (Program.cs) — see its comment for the full reasoning.
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

        _app = builder.Build();
        _app.Use(GuardAsync);
        _app.MapOpenApi();
        MapApi(_app);
        MapUi(_app);
        // Task.Run for the same reason as Dispose: this runs on the WinForms UI thread, and awaiting
        // there directly risks the continuation needing the very thread that is blocking. Exceptions
        // still propagate — a server that did not start must not be reported as started.
        Task.Run(() => _app.StartAsync()).GetAwaiter().GetResult();

        AgentLogger.Log($"AgentApiServer listening on http://localhost:{Port}/ — UI root: {_uiRoot} (exists: {Directory.Exists(_uiRoot)})");
    }

    /// <summary>
    /// Every request must come from this machine (loopback Host, no foreign Origin), and every
    /// request except the UI itself must carry the local token. The UI is exempt because it is how
    /// the token is delivered — it has nothing to present yet.
    /// </summary>
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        if (!LocalAuth.IsLoopbackHost(context.Request.Host.Host) ||
            !LocalAuth.IsAllowedOrigin(context.Request.Headers.Origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // /openapi is deliberately not token-gated: it is a static description of the API, holds no
        // machine state and no secrets, and the UI's type generator (openapi-typescript) has no way
        // to send a header. Everything that reads or changes state does need the token.
        if (context.Request.Path.StartsWithSegments("/api") &&
            !_auth.IsValid(context.Request.Headers[LocalAuth.HeaderName]))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Static-file middleware would otherwise hand out index.html verbatim, placeholder and all.
        if (context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            context.Request.Path = "/";

        await next(context);
    }

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/state", () =>
        {
            var warnings = _leaseWarnings.Read()
                .Select(e => new LeaseWarningDto(e.GameName, e.HolderMachine))
                .ToArray();

            var lastSyncAgo = _config.LastSyncTime.HasValue
                ? FormatAgo(DateTime.UtcNow - _config.LastSyncTime.Value)
                : "—";

            return new AgentStateDto(
                !string.IsNullOrEmpty(_config.ApiKey),
                UpdateChecker.CurrentVersionText,
                UpdateChecker.BuildLabel,
                _config.MachineName,
                _config.ServerUrl,
                _autoStart.IsEnabled(),
                _config.Games.Count,
                _config.TotalSavesPushed,
                lastSyncAgo,
                warnings,
                _config.SettleQuietSeconds,
                OperatingSystem.IsWindows() ? "Windows" : "Linux");
        }).Produces<AgentStateDto>();

        app.MapPost("/api/lease-warnings/dismiss", (DismissWarningRequest body) =>
        {
            if (!string.IsNullOrWhiteSpace(body.GameName)) ClearLeaseWarning(body.GameName);
            return new OkResponse();
        }).Produces<OkResponse>();

        app.MapGet("/api/candidates", async () => ToCandidateDtos(
            _candidateCache ?? await RescanAsync())).Produces<CandidateDto[]>();

        app.MapPost("/api/candidates/rescan", async () =>
            ToCandidateDtos(await RescanAsync())).Produces<CandidateDto[]>();

        app.MapPost("/api/enroll", async Task<Results<Ok<EnrollResponse>, BadRequest<ErrorResponse>>>
            (EnrollRequest body) =>
        {
            if (body.Ids is null)
                return TypedResults.BadRequest(new ErrorResponse("ids is required"));
            var candidates = _candidateCache ?? Array.Empty<ScanCandidate>();
            var (enrolled, skipped) = await _enroll(candidates, body.Ids);
            return TypedResults.Ok(new EnrollResponse(enrolled, skipped));
        });

        app.MapGet("/api/config", () => new AgentConfigDto(
            _config.ServerUrl,
            _config.MachineName,
            _autoStart.IsEnabled(),
            _config.SettleQuietSeconds)).Produces<AgentConfigDto>();

        app.MapPost("/api/config", Results<Ok<ConfigChangeResponse>, BadRequest<ErrorResponse>>
            (ConfigRequest body) =>
        {
            // A host caches an ApiClient (base URL + key + pin) inside its SyncEngine. Changing any
            // of that here without telling the host leaves the daemon split-brained: the poller
            // rebuilds its client every tick and talks to the NEW server while watcher pushes and
            // queue drains keep hitting the OLD one. Track the change, then hand it back below.
            // Applied FIRST, and checked. It used to run last and its result was discarded, so a
            // registry write refused by group policy still answered { ok: true } and the UI drew a
            // ticked box for a machine that would not start the agent at login. Doing it here also
            // means a refusal costs nothing: no setting has been mutated yet. WA-10.
            if (body.StartWithWindows.HasValue)
            {
                var auto = _autoStart.SetEnabled(body.StartWithWindows.Value);
                if (!auto.Ok)
                    return TypedResults.BadRequest(new ErrorResponse(
                        auto.Error ?? "Could not change the startup setting."));
            }

            var before = (_config.ServerUrl, _config.MachineName);
            // Everything needed to undo a failed transition. Captured BEFORE any mutation, because
            // the point is that a rejected request leaves a config the agent can still start from.
            var previousIdentity = _config.CaptureIdentity();
            var previousName = _config.MachineName;
            var previousSettle = _config.SettleQuietSeconds;

            var identityCleared = false;
            if (!string.IsNullOrWhiteSpace(body.ServerUrl))
            {
                // Validated BEFORE anything is written. The old code assigned the raw string, saved
                // it, and only then built a client from it — so 'htp://typo' persisted, the client
                // constructor threw, the caller got a 500, and every subsequent start of the agent
                // crashed on the same unusable value. WA-04.
                if (!_config.TrySetServerUrl(body.ServerUrl, out identityCleared))
                    return TypedResults.BadRequest(new ErrorResponse(ServerOrigin.InvalidUrlMessage));
            }
            if (!string.IsNullOrWhiteSpace(body.MachineName))
                _config.MachineName = body.MachineName.Trim();
            if (body.SettleQuietSeconds.HasValue)
                _config.SettleQuietSeconds = Math.Clamp(body.SettleQuietSeconds.Value, 0, 300);

            try
            {
                _config.Save();

                // Rebuild before the response returns, so no request that starts after the caller
                // sees 200 can still be addressed to the previous server.
                if (before != (_config.ServerUrl, _config.MachineName))
                    _onConnectionChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // Roll back every field together. A half-applied transition is the one outcome worse
                // than a rejected one: the agent would hold a new URL with the old credentials.
                _config.RestoreIdentity(previousIdentity);
                _config.MachineName = previousName;
                _config.SettleQuietSeconds = previousSettle;
                try { _config.Save(); } catch { /* the in-memory rollback is what matters */ }
                AgentLogger.LogException("AgentApiServer.Config", ex);
                return TypedResults.BadRequest(new ErrorResponse(
                    "Could not apply the change; the previous settings were kept. " + ex.Message));
            }

            // The UI has to know the machine was un-enrolled, or the user is left looking at a
            // "not connected" agent with no idea why their key stopped working. The effective
            // auto-start state is re-read rather than echoed back, so the toggle renders what the
            // machine will actually do — including a change that was reverted underneath us.
            return TypedResults.Ok(new ConfigChangeResponse(identityCleared, _autoStart.IsEnabled()));
        }).Produces<ConfigChangeResponse>();

        app.MapPost("/api/register", async Task<Results<Ok<RegisterResponse>, InternalServerError<ErrorResponse>>>
            (RegisterRequest body) =>
        {
            var previousIdentity = _config.CaptureIdentity();
            try
            {
                var api = ApiClient.For(_config, useConfigKey: false);
                var reg = await api.RegisterAsync(_config.MachineName, body.AdminPassword);

                // All three together, in one Save. The pin was previously ignored here entirely, so
                // registering against an https server through the UI established an identity with no
                // TLS pin at all — every later connection had nothing to compare against, and the
                // TOFU guarantee enrollment provides simply did not exist on this path. WA-04.
                _config.ApiKey = reg.ApiKey;
                _config.MachineId = reg.MachineId;
                if (api.ObservedPin is { } pin) _config.ServerPin = pin;
                _config.Save();
                _onConnectionChanged?.Invoke();
                // The key itself is never returned — it is written to config and used from there.
                // Nothing in the UI needs its value, and echoing it only creates a way to exfiltrate it.
                return TypedResults.Ok(new RegisterResponse(_config.MachineName));
            }
            catch (Exception ex)
            {
                // A failed registration must not leave a half-built identity — in particular not a
                // pin or key from a partially-completed attempt against the new origin.
                _config.RestoreIdentity(previousIdentity);
                return TypedResults.InternalServerError(new ErrorResponse(ex.Message));
            }
        });

        app.MapGet("/api/games", () => _config.Games
            .Select(g => new TrackedGameDto(
                g.GameId, g.Name, g.SaveDirectory, g.ProcessNames.ToArray()))
            .ToArray()).Produces<TrackedGameDto[]>();

        // Editing the process names is the other half of WA-08: discovery can only know them for a
        // non-Steam shortcut, so for everything else the user needs a way to supply them — and the
        // UI needs to be able to show, honestly, that lifecycle sync is unconfigured until they do.
        app.MapPost("/api/games/{id:guid}/processes", (Guid id, ProcessNamesRequest body) =>
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == id);
            if (game is null) return TypedResults.Ok(new OkResponse());

            // Accepts "foo.exe", "foo", or a comma-separated list, and normalises: the watcher
            // matches on Process.ProcessName, which never carries the extension.
            game.ProcessNames = (body.ProcessNames ?? Array.Empty<string>())
                .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(p => GameActivity.ProcessNameFromExe(p))
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _config.Save();
            _onGamesChanged?.Invoke();
            return TypedResults.Ok(new OkResponse());
        }).Produces<OkResponse>();

        // Untracks the game on THIS machine only. It stays on the server for the rest of the fleet;
        // the opt-out is what stops the poller adopting it straight back.
        app.MapPost("/api/games/{id:guid}/remove", (Guid id) =>
        {
            _config.SetTracked(id, tracked: false);
            _onGamesChanged?.Invoke();
            return new OkResponse();
        }).Produces<OkResponse>();

        app.MapPost("/api/games/{id:guid}/folder", async Task<Results<Ok<OkResponse>, BadRequest<ErrorResponse>>>
            (Guid id, FolderRequest body) =>
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == id);
            if (game is not null && body.Path is not null)
            {
                // Typed paths and picked paths arrive here alike, and neither was validated before.
                // The hard check has no override — nothing makes C:\Users a save folder. WA-02.
                var check = SavePathGuard.Check(body.Path, _config.StateDir);
                if (!check.Ok)
                    return TypedResults.BadRequest(new ErrorResponse($"Can't use that folder: {check.Reason}"));

                // The heuristics DO have false positives (a game whose save folder is genuinely
                // large, or genuinely named 'drive_c'), so they are refused once and accepted on a
                // second, explicit confirmation rather than being silently ignored.
                if (!body.Confirm)
                {
                    var problems = SaveDirSanity.Inspect(check.Canonical, game.ExcludeGlobs);
                    if (problems.Count > 0)
                        return TypedResults.BadRequest(new ErrorResponse(
                            "That folder looks wrong: " + string.Join(" ", problems) +
                            " Re-send with confirm to use it anyway."));
                }

                // The canonical form is stored, not the typed text: a relative path or a path with
                // a trailing separator must not reach the server as a different string than the one
                // that was validated.
                game.SaveDirectory = check.Canonical!;
                // Save first: watchers must be built from the config that is on disk, never from
                // one a concurrent write is about to supersede.
                _config.Save();
                _onGamesChanged?.Invoke();

                // Tell the server now rather than letting the next poll notice. The server's stored
                // path is the highest authority in reconcile, so until it hears about this one it
                // keeps handing back the old value — and on a machine with no in-process poller
                // (the Deck's `savelocker ui`) it never hears about it at all.
                if (!string.IsNullOrEmpty(_config.ApiKey))
                {
                    try { await ApiClient.For(_config).SetMachinePathAsync(id, check.Canonical!); }
                    catch (Exception ex) { AgentLogger.LogException("AgentApiServer.SetMachinePath", ex); }
                }
            }
            return TypedResults.Ok(new OkResponse());
        }).Produces<OkResponse>();

        app.MapPost("/api/folder-pick", async () =>
            new FolderResponse(await _pickFolder())).Produces<FolderResponse>();

        app.MapPost("/api/candidates/{id:int}/folder-pick", async Task<Results<Ok<FolderResponse>, BadRequest<ErrorResponse>>>
            (int id) =>
        {
            if (_candidateCache is null || id < 0 || id >= _candidateCache.Count)
                return TypedResults.BadRequest(new ErrorResponse("Invalid candidate id"));

            var path = await _pickFolder();
            if (path is not null)
            {
                var list = _candidateCache.ToList();
                list[id] = list[id] with { SuggestedSaveDir = path };
                _candidateCache = list;
            }
            return TypedResults.Ok(new FolderResponse(path));
        });

        // Sets a browsed path onto a cached candidate. Mirrors POST /api/games/{id}/folder, but the
        // candidate cache — not the tracked-games list — is what Add Games reads before enrollment.
        app.MapPost("/api/candidates/{id:int}/folder", Results<Ok<OkResponse>, BadRequest<ErrorResponse>>
            (int id, FolderRequest body) =>
        {
            if (_candidateCache is null || id < 0 || id >= _candidateCache.Count)
                return TypedResults.BadRequest(new ErrorResponse("Invalid candidate id"));
            if (body.Path is not null)
            {
                // Refused here as well as in Enroller, so the user is told while they are still
                // choosing rather than by a game silently skipping enrollment later. WA-02.
                var check = SavePathGuard.Check(body.Path, _config.StateDir);
                if (!check.Ok)
                    return TypedResults.BadRequest(new ErrorResponse($"Can't use that folder: {check.Reason}"));

                var list = _candidateCache.ToList();
                list[id] = list[id] with { SuggestedSaveDir = check.Canonical };
                _candidateCache = list;
            }
            return TypedResults.Ok(new OkResponse());
        });

        // The Deck's replacement for a folder dialog. Rooted at $HOME + the host's Steam roots;
        // a path outside them is refused rather than described (see PathBrowser).
        app.MapGet("/api/browse", Results<Ok<BrowseListing>, BadRequest<ErrorResponse>>
            (string? path) =>
        {
            var listing = _browser.List(path);
            return listing is null
                ? TypedResults.BadRequest(new ErrorResponse("That folder is not readable, or is outside the browsable roots."))
                : TypedResults.Ok(listing);
        });

        // Where the browser should open for an unmapped game: the scan's guess, if it has one.
        // Scanning is cached, so clicking "Set save path" does not re-walk the disk every time.
        app.MapGet("/api/games/{id:guid}/suggested-path", async (Guid id) =>
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == id);
            if (game is null) return new SuggestedPathDto(null);

            var candidates = _candidateCache ?? await RescanAsync();
            var match = candidates.FirstOrDefault(c =>
                string.Equals(c.Name, game.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.SuggestedSaveDir));

            // Only offer a path that is actually there — a stale guess sends the browser nowhere.
            var suggested = match?.SuggestedSaveDir;
            return new SuggestedPathDto(
                suggested is not null && Directory.Exists(suggested) ? suggested : null);
        }).Produces<SuggestedPathDto>();

        // The Steam launch-options command, so a Deck user never has to go back to install.sh's
        // one-time banner to find it. Linux resolves the real installed binary path; Windows returns
        // nulls (the tray sets up sync through the installer) and the UI hides the card.
        app.MapGet("/api/launch-command", () => _launchInfo()).Produces<LaunchCommandDto>();

        // The optional Decky plugin's state on this machine, so the agent UI can say "installed,
        // v0.2.0" instead of showing install instructions to someone who already followed them.
        // Local file reads only — no network, so polling it costs nothing.
        app.MapGet("/api/decky", () => _deckyStatus()).Produces<DeckyStatusDto>();

        // ---- Launch options (tasks/DeckyPlugin.md) ----
        //
        // The agent cannot write Steam's launch options: Steam holds localconfig.vdf/shortcuts.vdf in
        // memory and rewrites them on exit, so an agent-side edit is discarded. These three routes
        // are how something that CAN write them (a Decky plugin, whose frontend runs inside Steam's
        // own JS context) does it without holding any SaveLocker knowledge of its own — the rule
        // stays here, in LaunchOptions, testable without Steam or hardware.

        // What each tracked game should carry if it carries nothing yet, plus whatever anyone has
        // reported back about it. Empty on Windows: there is no wrapper there, so _launchInfo()
        // returns a null command and there is nothing any caller could usefully do.
        app.MapGet("/api/launch-options", () => LaunchOptionRows()).Produces<LaunchOptionRowDto[]>();

        // The merge. The caller knows each game's CURRENT options (only Steam does) and the agent
        // knows the rule, so neither can do this alone — which is exactly why this is a round trip
        // and not a string the caller assembles. Batched: a plugin sweeps the whole library at once.
        app.MapPost("/api/launch-options/resolve", (ResolveLaunchOptionsRequest body) =>
        {
            var wrapper = WrapperPath();
            if (wrapper is null) return Array.Empty<ResolvedLaunchOptionDto>();

            return (body.Games ?? []).Select(g =>
            {
                var desired = LaunchOptions.Apply(g.Current, wrapper);
                // Compared against the trimmed original: a caller that only ever writes on `changed`
                // must not be told to rewrite a game whose options differ by whitespace alone.
                return new ResolvedLaunchOptionDto(
                    g.SteamAppId, desired, !string.Equals(desired, g.Current?.Trim(), StringComparison.Ordinal));
            }).ToArray();
        }).Produces<ResolvedLaunchOptionDto[]>();

        // Reported back so `doctor` can say "this game's launch options were never set" instead of
        // the user discovering it as a save that silently never synced.
        app.MapPost("/api/launch-options/applied", (LaunchOptionsAppliedRequest body) =>
        {
            var game = _config.Games.FirstOrDefault(
                g => SteamShortcuts.UnsignedAppId(g.ResolveSteamAppId()) == body.SteamAppId);
            if (game is null) return TypedResults.Ok(new OkResponse());

            game.LaunchOptionsAppliedAt = body.Applied ? DateTime.UtcNow : null;
            game.LaunchOptionsError = string.IsNullOrWhiteSpace(body.Error) ? null : body.Error.Trim();
            _config.Save();
            return TypedResults.Ok(new OkResponse());
        }).Produces<OkResponse>();

        // What the Overview page's activity card polls: what is syncing right now (with byte
        // progress for a push) and a short rolling history of what just happened. Cheap — an
        // in-memory read, no I/O — so the UI can poll it far more often than the 10 s /api/state tick.
        app.MapGet("/api/activity", () =>
        {
            var current = _activity.Current();
            var recent = _activity.Recent()
                .Select(e => new ActivityLogEntryDto(e.TimestampUtc, e.Message))
                .ToArray();
            return new ActivityDto(
                new ActivitySnapshotDto(
                    current.GameName, current.Phase.ToString(), current.BytesDone, current.BytesTotal,
                    current.StartedAtUtc),
                recent);
        }).Produces<ActivityDto>();

        // The Overview page's "Sync now" button: pull then push every tracked game, same as the tray
        // menu's "Sync All". Fire-and-poll from the UI's side — the response is a summary line, and
        // progress for whichever game is mid-sync shows up on the next /api/activity poll regardless.
        app.MapPost("/api/sync", async () => new SyncNowResponse(await _syncAll()))
            .Produces<SyncNowResponse>();

        app.MapGet("/api/agent-version", () =>
        {
            var latest = _getUpdateResult() is UpdateResult.Available available
                ? available.Version
                : null;
            var staged = _stagedUpdate();
            return new AgentVersionDto(
                UpdateChecker.CurrentVersionText,
                latest,
                latest is not null,
                staged?.Version,
                staged?.BlockedReason);
        }).Produces<AgentVersionDto>();
    }

    /// <summary>
    /// The installed wrapper binary's path, recovered from the invocation <see cref="_launchInfo"/>
    /// already builds rather than resolved a second time — two resolvers would eventually disagree,
    /// and the one the user is shown must be the one that gets written. Null where there is no
    /// wrapper at all (Windows).
    /// </summary>
    private string? WrapperPath()
    {
        var command = _launchInfo().Command;
        if (string.IsNullOrEmpty(command)) return null;
        var run = command.IndexOf(" run -- ", StringComparison.Ordinal);
        return run <= 0 ? null : command[..run].Trim('"');
    }

    /// <summary>
    /// One row per tracked game that launches under a Steam AppID. Games without one are skipped
    /// rather than reported: nothing can set launch options for a game Steam does not launch.
    /// </summary>
    private LaunchOptionRowDto[] LaunchOptionRows()
    {
        var wrapper = WrapperPath();
        if (wrapper is null) return [];

        var desired = LaunchOptions.Invocation(wrapper);
        return _config.Games
            .Select(g => (game: g, appId: SteamShortcuts.UnsignedAppId(g.ResolveSteamAppId())))
            .Where(x => x.appId is not null)
            .Select(x => new LaunchOptionRowDto(
                x.appId!.Value, x.game.GameId, x.game.Name, desired,
                x.game.LaunchOptionsAppliedAt, x.game.LaunchOptionsError))
            .ToArray();
    }

    private void MapUi(WebApplication app)
    {
        if (!Directory.Exists(_uiRoot)) return;

        // No UseDefaultFiles: "/" must go through SendIndexAsync so the token is injected. Serving
        // index.html as a plain static file would hand out a UI that cannot call its own API.
        var provider = new PhysicalFileProvider(_uiRoot);
        app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });

        app.MapGet("/", SendIndexAsync);
        app.MapFallback(async (HttpContext context) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/openapi"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await SendIndexAsync(context);
        });
    }

    /// <summary>
    /// Serve the SPA shell with the local token baked in. This is the one place the token crosses
    /// into the browser, and it is safe because the same-origin policy stops any other page from
    /// reading the response — which is exactly why the Guard rejects a non-loopback Host first.
    /// </summary>
    private async Task SendIndexAsync(HttpContext context)
    {
        var index = Path.Combine(_uiRoot, "index.html");
        if (!File.Exists(index))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var html = await File.ReadAllTextAsync(index);
        html = html.Replace(LocalAuth.TokenPlaceholder, _auth.Token);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(html);
    }

    private async Task<IReadOnlyList<ScanCandidate>> RescanAsync()
    {
        var result = await _doScan();
        _candidateCache = result;
        return result;
    }

    private static CandidateDto[] ToCandidateDtos(IReadOnlyList<ScanCandidate> candidates) =>
        candidates.Select((candidate, id) => new CandidateDto(
            id,
            candidate.Name,
            candidate.Source.ToString(),
            candidate.HasSteamCloud,
            candidate.SuggestedSaveDir ?? "",
            SaveLocker.Shared.WinePrefix.BrowseStart(candidate.PrefixPath),
            candidate.SuggestedProcessName,
            candidate.Store.ToString())).ToArray();

    private static string FormatAgo(TimeSpan ago)
    {
        if (ago.TotalSeconds < 60) return "just now";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        return $"{(int)ago.TotalDays}d ago";
    }

    /// <summary>
    /// Shut the host down without deadlocking the caller's thread.
    /// <para>
    /// <b>This must never await on the calling thread.</b> The Windows tray disposes this from the
    /// WinForms UI thread (Exit → <c>Application.ThreadContext.DisposeThreadWindows</c>), where a
    /// <c>SynchronizationContext</c> is installed: blocking there with <c>GetAwaiter().GetResult()</c>
    /// froze the whole agent. Kestrel stopped listening, so the port closed and it looked half-dead,
    /// but the process never exited, the tray menu stuck on screen, and only Task Manager could end
    /// it. A captured stack showed the UI thread parked in <c>TaskAwaiter</c> inside this method
    /// while another thread waited on <c>Control.Invoke</c> for that same UI thread.
    /// </para>
    /// <para>
    /// <c>Task.Run</c> moves the continuations onto the thread pool, which has no
    /// <c>SynchronizationContext</c> to post back to, and the bounded wait means a host that refuses
    /// to stop delays exit instead of preventing it — we are tearing down either way.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null) return;

        try
        {
            Task.Run(async () =>
            {
                try { await app.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch { /* stopping is best-effort; we still have to dispose */ }
                await app.DisposeAsync().ConfigureAwait(false);
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* faulted or timed out — the process is going away regardless */ }
    }
}

public sealed record LeaseWarningDto(string GameName, string HolderMachine);
public sealed record AgentStateDto(
    bool Connected,
    string CurrentVersion,
    /// <summary>Display-only; <see cref="UpdateChecker.BuildLabel"/>. Never compare it.</summary>
    string BuildLabel,
    string MachineName,
    string ServerUrl,
    bool StartWithWindows,
    int GamesTracked,
    int SavesBacked,
    string LastSyncAgo,
    LeaseWarningDto[] LeaseWarnings,
    int SettleQuietSeconds,
    string Platform);
/// <param name="ProcessName">
/// The process discovery is confident means this game is running, or null when it cannot know —
/// which is every source but a non-Steam shortcut. Null tells the UI that enrolling this candidate
/// leaves launch/exit sync unconfigured, so it can say so instead of implying otherwise. WA-08.
/// </param>
/// <param name="Store">
/// The storefront the game came from — <c>Steam</c>, <c>Epic</c>, <c>Gog</c>, <c>Amazon</c>,
/// <c>Sideload</c>, or <c>Unknown</c> when discovery cannot tell (a non-Steam shortcut). Orthogonal
/// to <paramref name="Source"/>: one Heroic source covers four storefronts, and a large install
/// base is only navigable if the UI can narrow to one of them.
/// </param>
public sealed record CandidateDto(
    int Id, string Name, string Source, bool HasSteamCloud, string Path, string? PrefixPath,
    string? ProcessName, string Store);
/// <param name="ProcessNames">
/// Process names (no extension) that mean this game is running. <b>Empty means the Windows agent
/// cannot detect it</b> — no lease, no exit push, and no refusal to pull under a live game — so the
/// UI must say so rather than imply automatic sync is working. WA-08.
/// </param>
public sealed record TrackedGameDto(Guid Id, string Name, string Path, string[] ProcessNames);

public sealed record ProcessNamesRequest(string[]? ProcessNames);
public sealed record AgentConfigDto(
    string ServerUrl,
    string MachineName,
    bool StartWithWindows,
    int SettleQuietSeconds);
/// <param name="UpdateAvailable">
/// The server is offering something newer. That is all it means: nothing is downloaded, and acting
/// on it needs network, a download, a digest check and a smoke test — any of which can fail, and all
/// of which take a while.
/// </param>
/// <param name="StagedVersion">
/// A version already downloaded, verified against the published SHA-256, unpacked and smoke-tested,
/// waiting for the next start. Applying it is a file copy and a restart: it works offline and cannot
/// fail for any of the reasons a download can, which is why this — and never
/// <paramref name="UpdateAvailable"/> — is what an "install now" control may offer.
/// Null when nothing is staged, and always null on Windows, which stages nothing (its updater runs
/// the installer).
/// </param>
/// <param name="StagedBlockedReason">
/// Why restarting right now would install nothing, as a sentence to show verbatim, or null when it
/// would work. Today the only reason is a game running under the launch wrapper: the apply is
/// deferred while one is alive, so a restart in that state is <b>safe but does nothing visible</b> —
/// the worst possible outcome for a button. Phrased here rather than by each caller so the three
/// surfaces that can say "update" cannot word it differently.
/// </param>
public sealed record AgentVersionDto(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? StagedVersion,
    string? StagedBlockedReason);

/// <summary>
/// What the host knows about an update it has already staged. Supplied by the Linux daemon, which
/// owns <c>Updater</c>; the Windows tray injects nothing, because it stages nothing.
/// </summary>
/// <param name="BlockedReason"><inheritdoc cref="AgentVersionDto" path="/param[@name='StagedBlockedReason']"/></param>
public sealed record StagedUpdateInfo(string Version, string? BlockedReason);
public sealed record LaunchCommandDto(string? Command, string? Note);

/// <summary>
/// What this machine knows about the optional Decky plugin, from <b>local files only</b> — no
/// network call, so the agent UI can poll it as freely as anything else.
/// </summary>
/// <param name="Applicable">
/// False wherever Decky cannot exist (Windows). The UI hides the whole card on false rather than
/// rendering "not installed", which would be advice nobody on that platform can act on.
/// </param>
/// <param name="DeckyPresent">Is Decky Loader itself installed? Judged by its plugins directory.</param>
/// <param name="PluginVersion">
/// The installed plugin's version, read from the same <c>package.json</c> Decky reports in its own
/// UI, so the two cannot disagree. Null when the plugin is not installed — or when that file cannot
/// be read, which is itself the signal to reinstall.
/// </param>
/// <param name="InstallUrl">
/// Served rather than hard-coded in the UI so the one-paste URL has a single source. Empty when not
/// <paramref name="Applicable"/>.
/// </param>
public sealed record DeckyStatusDto(
    bool Applicable,
    bool DeckyPresent,
    bool PluginInstalled,
    string? PluginVersion,
    string InstallUrl);

/// <param name="SteamAppId">
/// The <b>unsigned</b> 32-bit AppID, normalised here so no caller has to know the trap: Steam stores
/// a non-Steam shortcut's id signed but exposes it unsigned, and comparing the two representations
/// silently matches nothing — which would be every game this feature exists for.
/// </param>
/// <param name="Desired">What this game should carry if it carries nothing yet. Identical for every
/// game on a device; a game that already has options needs the resolve route instead.</param>
/// <param name="AppliedAt">Null with a null <paramref name="Error"/> means <b>unknown</b>, not
/// broken — most machines have nothing that could report.</param>
public sealed record LaunchOptionRowDto(
    uint SteamAppId, Guid GameId, string Name, string Desired, DateTime? AppliedAt, string? Error);

public sealed record ResolveLaunchOptionsRequest(LaunchOptionCurrentDto[]? Games);
/// <param name="Current">The game's launch options as Steam holds them right now, empty or null if unset.</param>
public sealed record LaunchOptionCurrentDto(uint SteamAppId, string? Current);
/// <param name="Changed">False when the game already carries exactly this — the caller should not write.</param>
public sealed record ResolvedLaunchOptionDto(uint SteamAppId, string Desired, bool Changed);

public sealed record LaunchOptionsAppliedRequest(uint SteamAppId, bool Applied, string? Error);

public sealed record EnrollRequest(int[]? Ids);
/// <param name="IdentityCleared">
/// True when the server URL moved to a different origin, so this machine's key, id and TLS pin were
/// dropped and it must register or enroll again. See <see cref="ServerOrigin"/>.
/// </param>
public sealed record ConfigChangeResponse(bool IdentityCleared, bool StartWithWindows);

public sealed record ConfigRequest(
    string? ServerUrl,
    string? MachineName,
    bool? StartWithWindows,
    int? SettleQuietSeconds);
public sealed record RegisterRequest(string? AdminPassword = null);
/// <param name="Confirm">
/// Accept a path the sanity heuristics flagged. It never overrides <see cref="SavePathGuard"/> —
/// those refusals are absolute.
/// </param>
public sealed record FolderRequest(string? Path, bool Confirm = false);
public sealed record DismissWarningRequest(string? GameName);
public sealed record OkResponse(bool Ok = true);
public sealed record ErrorResponse(string Error);
public sealed record EnrollResponse(int Enrolled, int Skipped);
public sealed record RegisterResponse(string MachineName);
public sealed record FolderResponse(string? Path);
public sealed record SuggestedPathDto(string? Path);

/// <param name="GameName">Null when nothing is syncing right now.</param>
/// <param name="Phase">One of "Idle", "Pulling", "Settling", "Pushing" — a plain string rather than
/// a typed enum so the local API's slim JSON pipeline needs no enum-converter configuration for it,
/// matching every other DTO in this file.</param>
/// <param name="BytesDone">Meaningful only during "Pushing" — see the chunk loop in
/// <see cref="ApiClient.UploadAsync"/>, the only place that knows progress mid-transfer. Zero for
/// every other phase.</param>
public sealed record ActivitySnapshotDto(
    string? GameName, string Phase, long BytesDone, long BytesTotal, DateTime? StartedAtUtc);
public sealed record ActivityLogEntryDto(DateTime TimestampUtc, string Message);
public sealed record ActivityDto(ActivitySnapshotDto Current, ActivityLogEntryDto[] Recent);
public sealed record SyncNowResponse(string Message);
