using SaveLocker.Shared;
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
    private readonly Func<IReadOnlyList<ScanCandidate>, int[], IReadOnlyDictionary<int, string[]>?, IReadOnlyDictionary<int, LinkChoice>?, Task<(int enrolled, int skipped, IReadOnlyList<string> notes)>> _enroll;
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
    // POST /api/games/{id}/emptied-hold — SyncEngine.AnswerEmptiedAsync (true: delete everywhere) in whichever engine is current.
    private readonly Func<TrackedGame, bool, CancellationToken, Task<(bool Ok, string Message)>> _answerEmptied;
    /// <summary>Maps one of a game's extra save folders through the engine, which carries its shadow over.</summary>
    private readonly Func<TrackedGame, string, string, KeepSide?, Task<MapFolderResult>> _mapFolder;
    // SyncEngine.ForgetSavePathAsync: dropping an extra folder deletes its shadow, so it runs under the
    // game's lock in whichever engine is current.
    private readonly Func<TrackedGame, string, Task<bool>> _forgetFolder;
    // "Also found" for tracked games (tasks/multiple-save-paths plan §8). Reads the manifest and a few
    // folders per game, so it is computed on request, never on a timer.
    private readonly FolderSuggestions _suggestions;
    // Per-game save-dir resolution for /api/candidates/lookup and /api/manifest/search (Phase 5).
    // Host-agnostic (its own Windows-only checks no-op cleanly on Linux), so it is taken directly
    // rather than through a delegate — unlike _doScan/_enroll, nothing here differs per host.
    private readonly Detection _detection;
    // One sync at a time, however many surfaces are offering the button — see /api/sync.
    private readonly SemaphoreSlim _syncGate = new(1, 1);
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
        Func<IReadOnlyList<ScanCandidate>, int[], IReadOnlyDictionary<int, string[]>?, IReadOnlyDictionary<int, LinkChoice>?, Task<(int enrolled, int skipped, IReadOnlyList<string> notes)>> enroll,
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
        Func<Task<string>>? syncAll = null,
        Func<TrackedGame, bool, CancellationToken, Task<(bool Ok, string Message)>>? answerEmptied = null,
        Func<TrackedGame, string, string, KeepSide?, Task<MapFolderResult>>? mapFolder = null,
        Func<TrackedGame, string, Task<bool>>? forgetFolder = null,
        Func<string, string?>? prefixForAppId = null)
    {
        _forgetFolder = forgetFolder ?? ((_, _) => Task.FromResult(false));
        _suggestions = new FolderSuggestions(config, new Detection(config), prefixForAppId);
        _mapFolder = mapFolder ?? ((_, _, _, _) => Task.FromResult(new MapFolderResult(false, "Not available.")));
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
        _answerEmptied = answerEmptied ?? ((_, _, _) => Task.FromResult((false, "Not available.")));
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
            var alsoSync = body.AlsoSync?
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.SelectMany(c => c.Paths ?? Array.Empty<string>()).ToArray());
            var links = body.Links?
                .Where(l => l.Choice is EnrollLinks.Separate or EnrollLinks.Game)
                .GroupBy(l => l.Id)
                .ToDictionary(g => g.Key, g => new LinkChoice(g.Last().Choice, g.Last().GameId));
            var (enrolled, skipped, notes) = await _enroll(candidates, body.Ids, alsoSync, links);
            return TypedResults.Ok(new EnrollResponse(enrolled, skipped, notes.Count > 0 ? notes.ToArray() : null));
        });

        // Which server game each emulator save on Add games would join, and what else it could (EnrollLinks).
        // One read of the server's games per call, so the page asks once per list, not per row. Never fails
        // the page: an unreachable server answers with no links, and every row is linked automatically.
        app.MapGet("/api/candidates/links", async () =>
        {
            var candidates = _candidateCache ?? await RescanAsync();
            if (!candidates.Any(c => c.Source == ScanSource.Emulator) || string.IsNullOrEmpty(_config.ApiKey))
                return new CandidateLinksResponse(true, []);
            List<GameDto> server;
            try { server = await ApiClient.For(_config).ListGamesAsync(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new CandidateLinksResponse(false, []);
            }
            return new CandidateLinksResponse(true, candidates
                .Select((c, id) => (c, id))
                .Where(x => x.c.Source == ScanSource.Emulator && Enroller.TrackedFor(_config, x.c) is not { IsEnrolledHere: true })
                .Select(x => new CandidateLinksDto(x.id, EnrollLinks.For(server, x.c).ToArray()))
                .Where(l => l.Options.Length > 0)
                .ToArray());
        }).Produces<CandidateLinksResponse>();

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

        // HasSteamCloud is decided once, at enrollment (Enroller.EnrollAsync, from the candidate's
        // own ScanCandidate.HasSteamCloud) - not re-derived here from a name-only manifest lookup.
        // That lookup answers "does a Steam release of this TITLE have Steam Cloud", which is true
        // for plenty of titles this machine tracks a Heroic (Epic/GOG) install of, not "is THIS
        // install a Steam one" - see TrackedGame.HasSteamCloud's own doc comment.
        app.MapGet("/api/games", () => _config.Games
            .Select(g => new TrackedGameDto(
                g.GameId, g.Name, g.SaveDirectory, g.ProcessNames.ToArray(), g.Alias,
                SteamShortcuts.UnsignedAppId(g.ResolveSteamAppId()), g.PullBeforeLaunchEnabled,
                g.HasSteamCloud, SaveFolderDto.Of(g), g.IsEnrolledHere ? g.Source : null))
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

        // The Decky plugin's per-game alias editor: a manual override for name-based matching, for
        // when this agent has not resolved a Steam AppID for the game (see TrackedGame.Alias). Goes
        // through SaveGameAlias rather than a plain mutate + Save() like /processes above — unlike
        // that route, this one can be hit by the Decky plugin's HTTP request on one thread at the
        // same moment a folder watcher on another thread calls its own Save(), and only the re-read-
        // under-lock, single-field write actually survives that race. Silently OK on an unknown id,
        // same as /processes — SaveGameAlias itself no-ops when the id isn't found on disk.
        app.MapPost("/api/games/{id:guid}/alias", (Guid id, AliasRequest body) =>
        {
            _config.SaveGameAlias(id, body.Alias);
            return TypedResults.Ok(new OkResponse());
        }).Produces<OkResponse>();

        // The Decky plugin's per-game Gaming Mode pull toggle. Server-side for the same reason the
        // alias route above is: it is the one place every machine syncing this game (and a Decky
        // plugin reinstall on this one) can agree on the setting, rather than it living only in that
        // plugin's own local settings file. `enabled: null` clears the override.
        app.MapPost("/api/games/{id:guid}/pull-before-launch", (Guid id, PullBeforeLaunchRequest body) =>
        {
            _config.SaveGamePullBeforeLaunch(id, body.Enabled);
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
            if (game is not null && body.Path is not null &&
                body.Key is { Length: > 0 } key && key != SaveRoot.PrimaryKey)
            {
                // One of the game's extra folders (tasks/multiple-save-paths). The same two tiers of
                // checks as the primary folder below; the engine adds the folder rules and carries the
                // shadow's files over, so this is never a bare assignment.
                if (game.ExtraPaths.All(p => p.Key != key))
                    return TypedResults.BadRequest(new ErrorResponse($"'{game.Name}' has no save folder called '{key}'."));
                KeepSide? keep;
                switch (body.Keep?.Trim().ToLowerInvariant())
                {
                    case null or "": keep = null; break;
                    case "local": keep = KeepSide.Local; break;
                    case "cloud": keep = KeepSide.Cloud; break;
                    default:
                        return TypedResults.BadRequest(new ErrorResponse("keep must be 'local' or 'cloud'."));
                }
                var extraCheck = SavePathGuard.CheckFolder(game, key, body.Path, _config.StateDir);
                if (!extraCheck.Ok)
                    return TypedResults.BadRequest(new ErrorResponse($"Can't use that folder: {extraCheck.Reason}"));
                if (!body.Confirm)
                {
                    var problems = SaveDirSanity.Inspect(extraCheck.Canonical, game.ExcludeGlobs,
                        game.ExtraPaths.First(p => p.Key == key).IncludeGlobs);
                    if (problems.Count > 0)
                        return TypedResults.BadRequest(new ErrorResponse(
                            "That folder looks wrong: " + string.Join(" ", problems) +
                            " Re-send with confirm to use it anyway."));
                }

                var mapped = await _mapFolder(game, key, extraCheck.Canonical!, keep);
                if (!mapped.Ok)
                    return TypedResults.BadRequest(new ErrorResponse(mapped.Error ?? "Could not map the folder.",
                        NeedsChoice: mapped.NeedsChoice));
                _onGamesChanged?.Invoke();
                if (!string.IsNullOrEmpty(_config.ApiKey))
                {
                    try
                    {
                        await ApiClient.For(_config).SetMachinePathAsync(id, mapped.Directory!, key);
                        game.ExtraPaths.First(p => p.Key == key).PathUnreported = false;
                        _config.SaveGameFolders(game);
                    }
                    catch (Exception ex) { AgentLogger.LogException("AgentApiServer.SetMachinePath", ex); }
                }
                return TypedResults.Ok(new OkResponse());
            }
            if (game is not null && body.Path is not null)
            {
                // Typed paths and picked paths arrive here alike, and neither was validated before.
                // The hard check has no override — nothing makes C:\Users a save folder. WA-02.
                // CheckFolder adds the rules between the game's folders: nothing nested in another.
                var check = SavePathGuard.CheckFolder(game, SaveRoot.PrimaryKey, body.Path, _config.StateDir);
                if (!check.Ok)
                    return TypedResults.BadRequest(new ErrorResponse($"Can't use that folder: {check.Reason}"));

                // The heuristics DO have false positives (a game whose save folder is genuinely
                // large, or genuinely named 'drive_c'), so they are refused once and accepted on a
                // second, explicit confirmation rather than being silently ignored.
                if (!body.Confirm)
                {
                    var problems = SaveDirSanity.Inspect(check.Canonical, game.ExcludeGlobs, game.IncludeGlobs);
                    if (problems.Count > 0)
                        return TypedResults.BadRequest(new ErrorResponse(
                            "That folder looks wrong: " + string.Join(" ", problems) +
                            " Re-send with confirm to use it anyway."));
                }

                // The canonical form is stored, not the typed text: a relative path or a path with
                // a trailing separator must not reach the server as a different string than the one
                // that was validated.
                // A game set up here by picking its folder, not by a scan: it was added by hand. Moving the
                // folder of one already set up says nothing about how it was found, and stamping it here would
                // keep a scan from ever recording the real answer for a game enrolled before sources were.
                if (!game.IsEnrolledHere) game.Source ??= GameSources.Manual(GameSources.FolderPickedInAgent);
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
                    await GameSources.ReportAsync(ApiClient.For(_config), game);
                }
            }
            return TypedResults.Ok(new OkResponse());
        }).Produces<OkResponse>();

        // "Also found" (tasks/multiple-save-paths plan §8): the manifest's other locations of every
        // tracked game that exist here — or of one, for its page. Nothing is added until the user says so.
        app.MapGet("/api/folder-suggestions", async (Guid? gameId, CancellationToken ct) =>
        {
            IReadOnlyList<FolderSuggestion> found = gameId is { } id
                ? _config.Games.FirstOrDefault(g => g.GameId == id) is { } game
                    ? await _suggestions.ForGameAsync(game, ct)
                    : Array.Empty<FolderSuggestion>()
                : await _suggestions.AllAsync(ct);
            return found.Select(s => new FolderSuggestionDto(s.GameId, s.GameName, s.Key, s.Directory, s.Deferred)).ToArray();
        }).Produces<FolderSuggestionDto[]>();

        app.MapPost("/api/folder-suggestions/answer", Results<Ok<OkResponse>, NotFound, BadRequest<ErrorResponse>>
            (FolderSuggestionAnswerRequest body) =>
        {
            if (_config.Games.All(g => g.GameId != body.GameId)) return TypedResults.NotFound();
            static string[] Clean(string[]? dirs) =>
                (dirs ?? Array.Empty<string>()).Select(SavePathGuard.Canonicalize).OfType<string>().ToArray();
            try { _config.SaveGameFolderChoices(body.GameId, Clean(body.Ignore), Clean(body.Defer)); }
            catch (AgentStateLockException ex) { return TypedResults.BadRequest(new ErrorResponse(ex.Message)); }
            return TypedResults.Ok(new OkResponse());
        }).Produces<OkResponse>();

        // Another save folder for a tracked game, for every machine — what `add-path` does, except that a
        // key the game already has is refused rather than moved here (mustBeNew): this route adds, and
        // moving a folder is /folder's. A refusal a heuristic made is confirmable (NeedsConfirm); different
        // files on both sides ask for a side (NeedsChoice, the folder's Key with it) — the folder exists for
        // the fleet by then, so the answer goes to /folder with that key.
        app.MapPost("/api/games/{id:guid}/paths",
            async Task<Results<Ok<AddFolderResponse>, NotFound, BadRequest<ErrorResponse>>> (Guid id, AddFolderRequest body) =>
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == id);
            if (game is null) return TypedResults.NotFound();
            if (string.IsNullOrWhiteSpace(body.Path))
                return TypedResults.BadRequest(new ErrorResponse("path is required."));
            if (string.IsNullOrEmpty(_config.ApiKey))
                return TypedResults.BadRequest(new ErrorResponse("Register this machine first: a new save folder is defined on the server."));
            if (!game.IsEnrolledHere)
                return TypedResults.BadRequest(new ErrorResponse($"Choose the main save folder of '{game.Name}' first."));
            KeepSide? keep;
            switch (body.Keep?.Trim().ToLowerInvariant())
            {
                case null or "": keep = null; break;
                case "local": keep = KeepSide.Local; break;
                case "cloud": keep = KeepSide.Cloud; break;
                default: return TypedResults.BadRequest(new ErrorResponse("keep must be 'local' or 'cloud'."));
            }
            var named = !string.IsNullOrWhiteSpace(body.Key);
            var taken = new HashSet<string>(game.RemovedPathKeys, StringComparer.Ordinal);
            if (body.FreeKey) taken.UnionWith(game.ExtraPaths.Select(p => p.Key));
            var key = named
                ? body.FreeKey ? FolderSuggestions.KeyFor(body.Key!.Trim(), taken) : body.Key!.Trim()
                : FolderSuggestions.KeyFor(Path.GetFileName(body.Path.TrimEnd('/', '\\')),
                    new HashSet<string>(game.ExtraPaths.Select(p => p.Key).Concat(game.RemovedPathKeys), StringComparer.Ordinal));

            SavePathChange result;
            try
            {
                result = await SavePathEditor.AddAsync(_config, ApiClient.For(_config), _mapFolder, game, key, body.Path,
                    body.IncludeGlobs, body.Label, keep, body.Confirm, pickFreeKey: !named || body.FreeKey, mustBeNew: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return TypedResults.BadRequest(new ErrorResponse($"Could not reach the server: {ex.Message}"));
            }
            catch (AgentStateLockException ex) { return TypedResults.BadRequest(new ErrorResponse(ex.Message)); }
            if (result.Added || result.Ok) _onGamesChanged?.Invoke();
            if (!result.Ok)
                return TypedResults.BadRequest(new ErrorResponse(
                    result.NeedsConfirm ? result.Error + ErrorResponse.ConfirmHint : result.Error ?? "Could not add the folder.",
                    NeedsConfirm: result.NeedsConfirm, NeedsChoice: result.NeedsChoice,
                    Key: result.NeedsChoice ? result.Key : null));
            return TypedResults.Ok(new AddFolderResponse(result.Key!, result.Directory!, result.Template, result.Joined));
        }).Produces<AddFolderResponse>();

        // Stop syncing one of a game's extra folders, on every machine — what `remove-path` does. The
        // folder's files stay where they are.
        app.MapDelete("/api/games/{id:guid}/paths/{key}",
            async Task<Results<Ok<OkResponse>, NotFound, BadRequest<ErrorResponse>>> (Guid id, string key) =>
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == id);
            if (game is null) return TypedResults.NotFound();
            if (string.IsNullOrEmpty(_config.ApiKey))
                return TypedResults.BadRequest(new ErrorResponse("Register this machine first: save folders are defined on the server."));
            SavePathChange result;
            try { result = await SavePathEditor.RemoveAsync(_config, ApiClient.For(_config), _forgetFolder, game, key); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return TypedResults.BadRequest(new ErrorResponse($"Could not reach the server: {ex.Message}"));
            }
            catch (AgentStateLockException ex) { return TypedResults.BadRequest(new ErrorResponse(ex.Message)); }
            if (!result.Ok) return TypedResults.BadRequest(new ErrorResponse(result.Error ?? "Could not remove the folder."));
            _onGamesChanged?.Invoke();
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
        //
        // Single-flight: this route holds its request open for the whole sync, and there are now
        // three buttons wired to it (the browser card, Game Mode, and whatever else asks). Two
        // presses used to start two overlapping SyncAllAsync runs racing each other's leases; the
        // second caller is now told what is already happening instead.
        app.MapPost("/api/sync", async () =>
        {
            if (!await _syncGate.WaitAsync(0))
                return new SyncNowResponse("A sync is already running — watch the activity feed.");
            try { return new SyncNowResponse(await _syncAll()); }
            finally { _syncGate.Release(); }
        }).Produces<SyncNowResponse>();

        // Every game held here because its save folder was emptied, with its two choices.
        app.MapGet("/api/holds/emptied", () => EmptyFolderHoldStore.For(_config).Read()
            .Select(h => _config.Games.FirstOrDefault(g => g.GameId == h.GameId) is { } g
                ? new EmptiedHoldDto(h.GameId, g.Name, g.SaveDirectory, h.LastVersionId, h.HeldAt) : null)
            .OfType<EmptiedHoldDto>().ToArray()).Produces<EmptiedHoldDto[]>();

        // Answer one: delete-everywhere (a forced push of the empty folder) or restore-here (a forced pull). Not tied to
        // the request's lifetime: a closed page must not abort a restore halfway. Shares /api/sync's gate.
        app.MapPost("/api/games/{id:guid}/emptied-hold",
            async Task<Results<Ok<SyncNowResponse>, Conflict<ErrorResponse>, BadRequest<ErrorResponse>, NotFound>>
                (Guid id, EmptiedHoldAnswerRequest body) =>
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == id);
            if (game is null) return TypedResults.NotFound();
            if (body.Action is not (EmptiedHoldActions.DeleteEverywhere or EmptiedHoldActions.RestoreHere))
                return TypedResults.BadRequest(new ErrorResponse(
                    $"action must be {EmptiedHoldActions.DeleteEverywhere} or {EmptiedHoldActions.RestoreHere}."));

            if (!await _syncGate.WaitAsync(0))
                return TypedResults.Conflict(new ErrorResponse("A sync is already running. Try again when it is done."));
            try
            {
                var (ok, message) = await _answerEmptied(game, body.Action == EmptiedHoldActions.DeleteEverywhere, CancellationToken.None);
                return ok ? TypedResults.Ok(new SyncNowResponse(message)) : TypedResults.BadRequest(new ErrorResponse(message));
            }
            catch (Exception ex)
            {
                AgentLogger.LogException("AgentApiServer.emptied-hold", ex);
                return TypedResults.BadRequest(new ErrorResponse(ex.Message));
            }
            finally { _syncGate.Release(); }
        }).Produces<SyncNowResponse>().Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

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
        try
        {
            var filled = GameSources.Backfill(_config, result);
            if (filled.Count > 0 && !string.IsNullOrEmpty(_config.ApiKey))
                foreach (var game in filled) await GameSources.ReportAsync(ApiClient.For(_config), game);
        }
        catch (AgentStateLockException ex) { AgentLogger.LogException("AgentApiServer.BackfillSources", ex); }
        return result;
    }

    private CandidateDto[] ToCandidateDtos(IReadOnlyList<ScanCandidate> candidates) =>
        candidates.Select((candidate, id) => new CandidateDto(
            id,
            candidate.Name,
            candidate.Source.ToString(),
            candidate.HasSteamCloud,
            candidate.SuggestedSaveDir ?? "",
            SaveLocker.Shared.WinePrefix.BrowseStart(candidate.PrefixPath),
            candidate.SuggestedProcessName,
            candidate.Store.ToString(),
            candidate.DuplicateShortcuts is { Count: > 0 } duplicates ? duplicates.ToArray() : null,
            candidate.ExtraSaveDirs is { Count: > 0 } extras
                ? extras.Select(e => new SaveFolderDto(e.Key, null, e.Dir, true,
                    (e.IncludeGlobs ?? Array.Empty<string>()).ToArray())).ToArray()
                : null,
            candidate.AlternateSaveDirs is { Count: > 0 } also
                ? also.Select(a => new SaveFolderDto(a.Key, null, a.Dir, false, Array.Empty<string>())).ToArray()
                : null,
            Enroller.TrackedFor(_config, candidate) is { IsEnrolledHere: true },
            candidate.EmulatorName,
            candidate.EmulatorSystem,
            candidate.EmulatorRom,
            candidate.UntouchedSeed)).ToArray();

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
/// <param name="DuplicateShortcuts">
/// Same-named Steam shortcuts under different AppIDs (<see cref="ScanCandidate.DuplicateShortcuts"/>):
/// one entry per AppID, <see cref="DuplicateShortcut.Tracked"/> on the one the scan uses. Null when not the shape.
/// </param>
public sealed record CandidateDto(
    int Id, string Name, string Source, bool HasSteamCloud, string Path, string? PrefixPath,
    string? ProcessName, string Store, DuplicateShortcut[]? DuplicateShortcuts = null,
    /// <summary>The other save folders discovery found this game keeps (an emulator's save states),
    /// adopted with it on enrollment. Null when there are none.</summary>
    SaveFolderDto[]? ExtraFolders = null,
    /// <summary>The manifest's other locations for this game that exist here ("Also found"): added only
    /// when the enroll request names them in <see cref="EnrollRequest.AlsoSync"/>. Null when there are none.</summary>
    SaveFolderDto[]? AlsoFound = null,
    /// <summary>Already tracked here with a folder, so enrolling it again is skipped (<see cref="Enroller"/>).
    /// A game adopted from the server with no folder on this machine is not enrolled: adding it maps it.</summary>
    bool Enrolled = false,
    // Which emulator and console, for an emulator save (null otherwise) — the Add games filter's keys.
    string? EmulatorName = null, string? EmulatorSystem = null,
    // The save file's own name ("Chrono Trigger (Japan)"): two ROMs can share a title.
    string? EmulatorRom = null,
    /// <summary>EmuDeck's preinstalled save, never played here (<see cref="ScanCandidate.UntouchedSeed"/>): list it
    /// only when <c>GET /api/candidates/links</c> says it joins a server game.</summary>
    bool UntouchedSeed = false);
/// <param name="ProcessNames">
/// Process names (no extension) that mean this game is running. <b>Empty means the Windows agent
/// cannot detect it</b> — no lease, no exit push, and no refusal to pull under a live game — so the
/// UI must say so rather than imply automatic sync is working. WA-08.
/// </param>
/// <param name="Alias">
/// The manual name-match override, or null when none is set — see <see cref="TrackedGame.Alias"/>.
/// </param>
/// <param name="SteamAppId">
/// This game's resolved compatdata-prefix AppID, or null when it has none — the same resolution
/// <see cref="TrackedGame.ResolveSteamAppId"/> and the launch-options rows use. <b>Not</b> a "is this
/// a Steam Store game" signal: a non-Steam shortcut run under Proton gets its own compatdata prefix
/// too, so it resolves a non-null value here despite never having Steam Cloud. Use
/// <paramref name="HasSteamCloud"/> for that question instead.
/// </param>
/// <param name="PullBeforeLaunchEnabled">
/// The Gaming Mode pull-before-launch override, or null when none is set — see
/// <see cref="TrackedGame.PullBeforeLaunchEnabled"/>.
/// </param>
/// <param name="HasSteamCloud">
/// Whether this game was enrolled as a genuinely Steam-sourced install, or <b>null when nothing has
/// established that</b> — see <see cref="TrackedGame.HasSteamCloud"/>, decided once at enrollment,
/// not a live lookup. A Decky plugin needs this, not <paramref name="SteamAppId"/>, to decide
/// whether its own pre-launch pull would race Steam's own Cloud sync — an AppID alone can't tell a
/// real Steam install apart from a non-Steam shortcut run under Proton, which gets a compatdata
/// prefix too. Null means "unknown, use your own heuristic", not "no Steam Cloud".
/// </param>
/// <remarks>
/// <see cref="Id"/> and <see cref="Path"/> keep those names, rather than the GameId/SaveDirectory
/// the config-side properties use, because this record IS the <c>/api/games</c> wire contract and
/// the Decky plugin reading it ships from its own repo on its own update channel. Renaming them
/// would break every already-installed plugin the moment an agent updated ahead of it, for nothing
/// — the four fields after them are additive, so an older reader keeps working untouched.
/// </remarks>
public sealed record TrackedGameDto(
    Guid Id, string Name, string Path, string[] ProcessNames, string? Alias,
    uint? SteamAppId, bool? PullBeforeLaunchEnabled, bool? HasSteamCloud,
    /// <summary>Every save folder of the game, primary (<c>main</c>) first. <see cref="Path"/> stays the
    /// primary folder, for readers that predate this.</summary>
    SaveFolderDto[]? Paths = null,
    /// <summary>How this machine found the game; null when it was enrolled before sources were recorded,
    /// or is not set up here.</summary>
    GameSourceDto? Source = null);

/// <summary>One save folder of a tracked game on this machine.</summary>
/// <param name="Path">This machine's folder, or <c>""</c> when it is not mapped here — an unmapped
/// extra folder keeps syncing through a shadow copy in the agent's own state.</param>
public sealed record SaveFolderDto(string Key, string? Label, string Path, bool Mapped, string[] IncludeGlobs)
{
    public static SaveFolderDto[] Of(TrackedGame g) =>
        new[] { new SaveFolderDto(SaveRoot.PrimaryKey, null, g.SaveDirectory, g.IsEnrolledHere, g.IncludeGlobs.ToArray()) }
            .Concat(g.ExtraPaths.Select(p =>
                new SaveFolderDto(p.Key, p.Label, p.Directory ?? "", p.IsMapped, p.IncludeGlobs.ToArray())))
            .ToArray();
}

public sealed record ProcessNamesRequest(string[]? ProcessNames);
public sealed record AliasRequest(string? Alias);
public sealed record PullBeforeLaunchRequest(bool? Enabled);
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

/// <param name="AlsoSync">Which "Also found" folders (<see cref="CandidateDto.AlsoFound"/>) to add with each
/// candidate. A candidate missing here adds none.</param>
/// <param name="Links">Emulator saves the user linked by hand (Add games' Change). Any other is linked automatically.</param>
public sealed record EnrollRequest(int[]? Ids, EnrollFolderChoice[]? AlsoSync = null, EnrollLinkChoice[]? Links = null);
/// <param name="Choice"><c>auto</c>, <c>separate</c> (keep it as its own game) or <c>game</c> (join <paramref name="GameId"/>).</param>
public sealed record EnrollLinkChoice(int Id, string Choice, Guid? GameId = null);
/// <param name="Reachable">False when the server could not be asked: the page says the link is decided when the game is added.</param>
public sealed record CandidateLinksResponse(bool Reachable, CandidateLinksDto[] Links);
/// <param name="Options">The automatic choice first, then the others (<see cref="EnrollLinks.For"/>).</param>
public sealed record CandidateLinksDto(int Id, LinkOption[] Options);
public sealed record EnrollFolderChoice(int Id, string[]? Paths);

/// <summary>A folder a tracked game could also sync ("Also found"), from the manifest.</summary>
/// <param name="Deferred">"Skip for now" was chosen: listed on the game's page, not asked about at start-up.</param>
public sealed record FolderSuggestionDto(Guid GameId, string GameName, string Key, string Path, bool Deferred);
/// <param name="Ignore">Folders this game does not sync: never suggested again.</param>
/// <param name="Defer">Folders put off ("Skip for now"): no longer asked about at start-up.</param>
public sealed record FolderSuggestionAnswerRequest(Guid GameId, string[]? Ignore = null, string[]? Defer = null);
/// <param name="Key">The folder's key. Null: one is made from the folder's name.</param>
/// <param name="Keep"><c>local</c> or <c>cloud</c>, when both this folder and the fleet's copy hold files.</param>
/// <param name="FreeKey"><paramref name="Key"/> is only a preference (a suggestion's): if the game has or
/// had it, take the next free one instead of refusing.</param>
public sealed record AddFolderRequest(string? Path, string? Key = null, string? Label = null,
    string[]? IncludeGlobs = null, string? Keep = null, bool Confirm = false, bool FreeKey = false);
/// <param name="Joined">The fleet already had this folder (another device added it first, as
/// <paramref name="Key"/>): this device joined it rather than adding it twice.</param>
public sealed record AddFolderResponse(string Key, string Path, string? Template, bool Joined = false);
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
/// <param name="Key">Which of the game's save folders: absent or <c>main</c> for the primary one.</param>
/// <param name="Keep"><c>local</c> or <c>cloud</c>, when a previous attempt answered NeedsChoice.</param>
public sealed record FolderRequest(string? Path, bool Confirm = false, string? Key = null, string? Keep = null);
public sealed record DismissWarningRequest(string? GameName);
public sealed record OkResponse(bool Ok = true);
/// <param name="NeedsChoice">Mapping a save folder met different files on both sides; re-send with
/// <c>keep</c> (<c>local</c> or <c>cloud</c>) to say which wins. Nothing changed.</param>
/// <param name="NeedsConfirm">The request was refused by a heuristic that has false positives, and
/// re-sending it with <c>confirm</c> will be accepted.</param>
/// <param name="Key">With <paramref name="NeedsChoice"/> from adding a folder: the key the folder has now, which
/// the answer (keep local or cloud) is sent for.</param>
public sealed record ErrorResponse(string Error, bool NeedsChoice = false, bool NeedsConfirm = false, string? Key = null)
{
    /// <summary>The sentence a confirmable refusal ends with.</summary>
    public const string ConfirmHint = " Re-send with confirm to use it anyway.";
}
/// <param name="Notes">Why each refused game was not added ("Chrono Trigger: the server's …"), one line each;
/// null when every skip was a game already set up here.</param>
public sealed record EnrollResponse(int Enrolled, int Skipped, string[]? Notes = null);
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

/// <summary>A game held because every save file vanished from its folder here.</summary>
/// <param name="LastVersionId">The last version this machine synced, which still has the files.</param>
public sealed record EmptiedHoldDto(Guid GameId, string GameName, string SaveDirectory, Guid? LastVersionId, DateTimeOffset HeldAt);

/// <param name="Action"><see cref="EmptiedHoldActions.DeleteEverywhere"/> or <see cref="EmptiedHoldActions.RestoreHere"/>.</param>
public sealed record EmptiedHoldAnswerRequest(string Action);

public static class EmptiedHoldActions
{
    public const string DeleteEverywhere = "delete-everywhere";
    public const string RestoreHere = "restore-here";
}
