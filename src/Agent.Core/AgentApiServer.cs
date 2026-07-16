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
    private readonly Action? _onRegistered;
    private readonly Func<UpdateResult?> _getUpdateResult;
    private readonly string _uiRoot;
    private readonly bool _listenOnAllInterfaces;
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
        bool listenOnAllInterfaces = false)
    {
        Port = port;
        _config = config;
        _doScan = doScan;
        _enroll = enroll;
        _autoStart = autoStart;
        _pickFolder = pickFolder ?? (() => Task.FromResult<string?>(null));
        _onRegistered = onRegistered;
        _getUpdateResult = getUpdateResult ?? (() => null);
        _uiRoot = Path.Combine(AppContext.BaseDirectory, "agent-ui");
        _listenOnAllInterfaces = listenOnAllInterfaces;
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
        builder.WebHost.ConfigureKestrel(server =>
        {
            if (_listenOnAllInterfaces) server.ListenAnyIP(Port);
            else server.ListenLocalhost(Port);
        });
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
        builder.Services.AddOpenApi();

        _app = builder.Build();
        _app.UseCors();
        _app.MapOpenApi();
        MapApi(_app);
        MapUi(_app);
        _app.StartAsync().GetAwaiter().GetResult();

        var host = _listenOnAllInterfaces ? "0.0.0.0" : "localhost";
        AgentLogger.Log($"AgentApiServer listening on http://{host}:{Port}/ — UI root: {_uiRoot} (exists: {Directory.Exists(_uiRoot)})");
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
                _config.ApiKey ?? "",
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
            _config.ApiKey ?? "",
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
                return TypedResults.Ok(new RegisterResponse(reg.ApiKey));
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

        var provider = new PhysicalFileProvider(_uiRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/openapi"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var index = Path.Combine(_uiRoot, "index.html");
            if (!File.Exists(index))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(index);
        });
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

    public void Dispose()
    {
        if (_app is null) return;
        try { _app.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); }
        catch { }
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _app = null;
    }
}

public sealed record LeaseWarningDto(string GameName, string HolderMachine);
public sealed record AgentStateDto(
    bool Connected,
    string CurrentVersion,
    string MachineName,
    string ServerUrl,
    string ApiKey,
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
    string ApiKey,
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
public sealed record RegisterResponse(string ApiKey);
public sealed record FolderResponse(string? Path);
