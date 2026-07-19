using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

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
    private readonly PathBrowser _browser;
    private readonly Action? _onRegistered;
    private readonly Func<UpdateResult?> _getUpdateResult;
    private readonly string _uiRoot;
    private readonly LocalAuth _auth;
    private readonly Dictionary<string, string> _leaseWarnings = new();

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
        Action? onRegistered = null,
        Func<UpdateResult?>? getUpdateResult = null,
        IEnumerable<string>? browseRoots = null)
    {
        _browser = new PathBrowser(browseRoots);
        Port = port;
        _config = config;
        _doScan = doScan;
        _enroll = enroll;
        _autoStart = autoStart;
        _pickFolder = pickFolder ?? (() => Task.FromResult<string?>(null));
        _onRegistered = onRegistered;
        _getUpdateResult = getUpdateResult ?? (() => null);
        _uiRoot = Path.Combine(AppContext.BaseDirectory, "agent-ui");
        _auth = LocalAuth.LoadOrCreate(config.ConfigPath);
    }

    public void AddLeaseWarning(string gameName, string holderMachine)
    {
        lock (_leaseWarnings) _leaseWarnings[gameName] = holderMachine;
    }

    public void ClearLeaseWarning(string gameName)
    {
        lock (_leaseWarnings) _leaseWarnings.Remove(gameName);
    }

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
        builder.Services.AddOpenApi();

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
            LeaseWarningDto[] warnings;
            lock (_leaseWarnings)
                warnings = _leaseWarnings.Select(kv => new LeaseWarningDto(kv.Key, kv.Value)).ToArray();

            var lastSyncAgo = _config.LastSyncTime.HasValue
                ? FormatAgo(DateTime.UtcNow - _config.LastSyncTime.Value)
                : "—";

            return new AgentStateDto(
                !string.IsNullOrEmpty(_config.ApiKey),
                UpdateChecker.CurrentVersion.ToString(3),
                _config.MachineName,
                _config.ServerUrl,
                _autoStart.IsEnabled(),
                _config.Games.Count,
                _config.TotalSavesPushed,
                lastSyncAgo,
                warnings,
                _config.SettleQuietSeconds);
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

        app.MapPost("/api/config", (ConfigRequest body) =>
        {
            if (!string.IsNullOrWhiteSpace(body.ServerUrl))
                _config.ServerUrl = body.ServerUrl.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(body.MachineName))
                _config.MachineName = body.MachineName.Trim();
            if (body.SettleQuietSeconds.HasValue)
                _config.SettleQuietSeconds = Math.Clamp(body.SettleQuietSeconds.Value, 0, 300);
            _config.Save();
            if (body.StartWithWindows.HasValue)
                _autoStart.SetEnabled(body.StartWithWindows.Value);
            return new OkResponse();
        }).Produces<OkResponse>();

        app.MapPost("/api/register", async Task<Results<Ok<RegisterResponse>, InternalServerError<ErrorResponse>>>
            (RegisterRequest body) =>
        {
            try
            {
                var api = ApiClient.For(_config, useConfigKey: false);
                var reg = await api.RegisterAsync(_config.MachineName, body.AdminPassword);
                _config.ApiKey = reg.ApiKey;
                _config.MachineId = reg.MachineId;
                _config.Save();
                _onRegistered?.Invoke();
                // The key itself is never returned — it is written to config and used from there.
                // Nothing in the UI needs its value, and echoing it only creates a way to exfiltrate it.
                return TypedResults.Ok(new RegisterResponse(_config.MachineName));
            }
            catch (Exception ex)
            {
                return TypedResults.InternalServerError(new ErrorResponse(ex.Message));
            }
        });

        app.MapGet("/api/games", () => _config.Games
            .Select(g => new TrackedGameDto(g.GameId, g.Name, g.SaveDirectory))
            .ToArray()).Produces<TrackedGameDto[]>();

        app.MapPost("/api/games/{id:guid}/remove", (Guid id) =>
        {
            _config.Games.RemoveAll(g => g.GameId == id);
            _config.Save();
            return new OkResponse();
        }).Produces<OkResponse>();

        app.MapPost("/api/games/{id:guid}/folder", (Guid id, FolderRequest body) =>
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == id);
            if (game is not null && body.Path is not null)
            {
                game.SaveDirectory = body.Path;
                _config.Save();
            }
            return new OkResponse();
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

        app.MapGet("/api/agent-version", () =>
        {
            var latest = _getUpdateResult() is UpdateResult.Available available
                ? available.Version
                : null;
            return new AgentVersionDto(
                UpdateChecker.CurrentVersion.ToString(3),
                latest,
                latest is not null);
        }).Produces<AgentVersionDto>();
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
            candidate.SuggestedSaveDir ?? "")).ToArray();

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
    string MachineName,
    string ServerUrl,
    bool StartWithWindows,
    int GamesTracked,
    int SavesBacked,
    string LastSyncAgo,
    LeaseWarningDto[] LeaseWarnings,
    int SettleQuietSeconds);
public sealed record CandidateDto(int Id, string Name, string Source, bool HasSteamCloud, string Path);
public sealed record TrackedGameDto(Guid Id, string Name, string Path);
public sealed record AgentConfigDto(
    string ServerUrl,
    string MachineName,
    bool StartWithWindows,
    int SettleQuietSeconds);
public sealed record AgentVersionDto(string CurrentVersion, string? LatestVersion, bool UpdateAvailable);
public sealed record EnrollRequest(int[]? Ids);
public sealed record ConfigRequest(
    string? ServerUrl,
    string? MachineName,
    bool? StartWithWindows,
    int? SettleQuietSeconds);
public sealed record RegisterRequest(string? AdminPassword = null);
public sealed record FolderRequest(string? Path);
public sealed record DismissWarningRequest(string? GameName);
public sealed record OkResponse(bool Ok = true);
public sealed record ErrorResponse(string Error);
public sealed record EnrollResponse(int Enrolled, int Skipped);
public sealed record RegisterResponse(string MachineName);
public sealed record FolderResponse(string? Path);
public sealed record SuggestedPathDto(string? Path);
