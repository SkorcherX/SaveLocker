using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace LocalGameSync.Agent;

/// <summary>
/// Minimal HTTP server that drives the SaveLocker Agent UI (React app in WebView2).
/// Serves the bundled agent-ui/dist/ folder alongside the exe and exposes /api/* routes.
/// </summary>
internal sealed class AgentApiServer : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly AgentConfig _config;
    private readonly SynchronizationContext _ui;
    private readonly Func<Task<IReadOnlyList<ScanCandidate>>> _doScan;
    private readonly Func<IReadOnlyList<ScanCandidate>, int[], Task<(int enrolled, int skipped)>> _enroll;
    private readonly Action? _onRegistered;
    private readonly string _uiRoot;

    private IReadOnlyList<ScanCandidate>? _candidateCache;
    private readonly Dictionary<string, string> _leaseWarnings = new(); // gameName → holderMachine
    private Task? _loop;
    private volatile bool _stopping;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Port { get; }

    public AgentApiServer(
        int port,
        AgentConfig config,
        SynchronizationContext ui,
        Func<Task<IReadOnlyList<ScanCandidate>>> doScan,
        Func<IReadOnlyList<ScanCandidate>, int[], Task<(int enrolled, int skipped)>> enroll,
        Action? onRegistered = null)
    {
        Port = port;
        _config = config;
        _ui = ui;
        _doScan = doScan;
        _enroll = enroll;
        _onRegistered = onRegistered;
        _uiRoot = Path.Combine(AppContext.BaseDirectory, "agent-ui");
        _http.Prefixes.Add($"http://localhost:{port}/");
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
        _http.Start();
        AgentLogger.Log($"AgentApiServer listening on http://localhost:{Port}/ — UI root: {_uiRoot} (exists: {Directory.Exists(_uiRoot)})");
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_stopping)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        try
        {
            var path = req.Url?.LocalPath ?? "/";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                await HandleApiAsync(ctx, req.HttpMethod, path[5..]);
            else
                await ServeStaticAsync(ctx, path);
        }
        catch (Exception ex)
        {
            AgentLogger.Log("AgentApiServer: " + ex.Message);
            try { res.StatusCode = 500; await WriteJsonAsync(res, new { error = ex.Message }); }
            catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    // ─── API routes ─────────────────────────────────────────────────────────────

    private async Task HandleApiAsync(HttpListenerContext ctx, string method, string route)
    {
        var res = ctx.Response;
        var req = ctx.Request;
        var parts = route.TrimEnd('/').Split('/');

        // GET /api/state
        if (route == "state" && method == "GET")
        {
            object[] warnings;
            lock (_leaseWarnings)
                warnings = _leaseWarnings
                    .Select(kv => (object)new { gameName = kv.Key, holderMachine = kv.Value })
                    .ToArray();

            var lastSyncAgo = _config.LastSyncTime.HasValue
                ? FormatAgo(DateTime.UtcNow - _config.LastSyncTime.Value)
                : "—";

            await WriteJsonAsync(res, new
            {
                connected = !string.IsNullOrEmpty(_config.ApiKey),
                machineName = _config.MachineName,
                serverUrl = _config.ServerUrl,
                apiKey = _config.ApiKey ?? "",
                startWithWindows = AutoStart.IsEnabled(),
                gamesTracked = _config.Games.Count,
                savesBacked = _config.TotalSavesPushed,
                lastSyncAgo,
                leaseWarnings = warnings,
            });
            return;
        }

        // POST /api/lease-warnings/dismiss  { gameName }
        if (route == "lease-warnings/dismiss" && method == "POST")
        {
            var body = await ReadJsonAsync<DismissWarningBody>(req);
            if (body?.GameName is not null) ClearLeaseWarning(body.GameName);
            await WriteJsonAsync(res, new { ok = true });
            return;
        }

        // GET /api/candidates  — returns cached scan or triggers one
        if (route == "candidates" && method == "GET")
        {
            var c = _candidateCache ?? await RescanAsync();
            await WriteJsonAsync(res, ToCandidateDtos(c));
            return;
        }

        // POST /api/candidates/rescan
        if (route == "candidates/rescan" && method == "POST")
        {
            var c = await RescanAsync();
            await WriteJsonAsync(res, ToCandidateDtos(c));
            return;
        }

        // POST /api/enroll  { ids: number[] }
        if (route == "enroll" && method == "POST")
        {
            var body = await ReadJsonAsync<EnrollRequest>(req);
            if (body?.Ids is null) { res.StatusCode = 400; return; }
            var candidates = _candidateCache ?? Array.Empty<ScanCandidate>();
            var (enrolled, skipped) = await _enroll(candidates, body.Ids);
            await WriteJsonAsync(res, new { enrolled, skipped });
            return;
        }

        // GET /api/config
        if (route == "config" && method == "GET")
        {
            await WriteJsonAsync(res, new
            {
                serverUrl = _config.ServerUrl,
                machineName = _config.MachineName,
                apiKey = _config.ApiKey ?? "",
                startWithWindows = AutoStart.IsEnabled(),
            });
            return;
        }

        // POST /api/config  { serverUrl?, machineName?, startWithWindows? }
        if (route == "config" && method == "POST")
        {
            var body = await ReadJsonAsync<ConfigRequest>(req);
            if (body is not null)
            {
                if (!string.IsNullOrWhiteSpace(body.ServerUrl))
                    _config.ServerUrl = body.ServerUrl.Trim().TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(body.MachineName))
                    _config.MachineName = body.MachineName.Trim();
                _config.Save();
                if (body.StartWithWindows.HasValue)
                    AutoStart.SetEnabled(body.StartWithWindows.Value);
            }
            await WriteJsonAsync(res, new { ok = true });
            return;
        }

        // POST /api/register
        if (route == "register" && method == "POST")
        {
            try
            {
                var body = await ReadJsonAsync<RegisterRequest>(req);
                var api = new ApiClient(_config.ServerUrl, null);
                var reg = await api.RegisterAsync(_config.MachineName, body?.AdminPassword);
                _config.ApiKey = reg.ApiKey;
                _config.MachineId = reg.MachineId;
                _config.Save();
                _onRegistered?.Invoke();
                await WriteJsonAsync(res, new { apiKey = reg.ApiKey });
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await WriteJsonAsync(res, new { error = ex.Message });
            }
            return;
        }

        // GET /api/games
        if (route == "games" && method == "GET")
        {
            await WriteJsonAsync(res, _config.Games.Select(g => new
            {
                id = g.GameId.ToString(),
                name = g.Name,
                path = g.SaveDirectory,
            }));
            return;
        }

        // POST /api/games/{id}/remove
        if (parts.Length == 3 && parts[0] == "games" && parts[2] == "remove" && method == "POST")
        {
            if (Guid.TryParse(parts[1], out var id))
            {
                _config.Games.RemoveAll(g => g.GameId == id);
                _config.Save();
            }
            await WriteJsonAsync(res, new { ok = true });
            return;
        }

        // POST /api/games/{id}/folder  { path }
        if (parts.Length == 3 && parts[0] == "games" && parts[2] == "folder" && method == "POST")
        {
            var body = await ReadJsonAsync<FolderBody>(req);
            if (Guid.TryParse(parts[1], out var id) && body?.Path is not null)
            {
                var game = _config.Games.FirstOrDefault(g => g.GameId == id);
                if (game is not null) { game.SaveDirectory = body.Path; _config.Save(); }
            }
            await WriteJsonAsync(res, new { ok = true });
            return;
        }

        // POST /api/folder-pick  → open native OS folder dialog on the STA thread
        if (route == "folder-pick" && method == "POST")
        {
            var path = await ShowFolderPickerAsync();
            await WriteJsonAsync(res, new { path });
            return;
        }

        // POST /api/candidates/{id}/folder-pick  → open folder dialog AND update the cache
        if (parts.Length == 3 && parts[0] == "candidates" && parts[2] == "folder-pick" && method == "POST")
        {
            if (!int.TryParse(parts[1], out var candidateId) ||
                _candidateCache is null || candidateId < 0 || candidateId >= _candidateCache.Count)
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = "Invalid candidate id" });
                return;
            }
            var pickedPath = await ShowFolderPickerAsync();
            if (pickedPath is not null)
            {
                // Rebuild the immutable record with the new save dir.
                var old = _candidateCache[candidateId];
                var updated = old with { SuggestedSaveDir = pickedPath };
                var list = _candidateCache.ToList();
                list[candidateId] = updated;
                _candidateCache = list;
            }
            await WriteJsonAsync(res, new { path = pickedPath });
            return;
        }

        res.StatusCode = 404;
        await WriteJsonAsync(res, new { error = "Not found" });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScanCandidate>> RescanAsync()
    {
        var result = await _doScan();
        _candidateCache = result;
        return result;
    }

    private static object[] ToCandidateDtos(IReadOnlyList<ScanCandidate> candidates) =>
        candidates.Select((c, i) => (object)new
        {
            id = i,
            name = c.Name,
            source = c.Source.ToString(),
            hasSteamCloud = c.HasSteamCloud,
            path = c.SuggestedSaveDir ?? "",
        }).ToArray();

    /// <summary>Open a Windows folder-picker on a dedicated STA thread and return the chosen path.</summary>
    private static Task<string?> ShowFolderPickerAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Select save folder",
                    UseDescriptionForTitle = true,
                };
                // Parent to the first open form so the dialog appears in front of the agent window.
                var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
                var chosen = dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
                tcs.SetResult(chosen);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    // ─── Static file server ──────────────────────────────────────────────────────

    private async Task ServeStaticAsync(HttpListenerContext ctx, string urlPath)
    {
        var res = ctx.Response;
        if (urlPath == "/") urlPath = "/index.html";

        var safePart = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var filePath = Path.GetFullPath(Path.Combine(_uiRoot, safePart));

        if (!filePath.StartsWith(_uiRoot, StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 403; return;
        }

        if (!File.Exists(filePath))
            filePath = Path.Combine(_uiRoot, "index.html"); // SPA fallback

        if (!File.Exists(filePath))
        {
            res.StatusCode = 404; return;
        }

        res.ContentType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html"  => "text/html; charset=utf-8",
            ".js"    => "application/javascript; charset=utf-8",
            ".css"   => "text/css; charset=utf-8",
            ".png"   => "image/png",
            ".svg"   => "image/svg+xml",
            ".ico"   => "image/x-icon",
            ".json"  => "application/json; charset=utf-8",
            ".woff2" => "font/woff2",
            _        => "application/octet-stream",
        };

        var bytes = await File.ReadAllBytesAsync(filePath);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static string FormatAgo(TimeSpan ago)
    {
        if (ago.TotalSeconds < 60) return "just now";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        return $"{(int)ago.TotalDays}d ago";
    }

    // ─── JSON utilities ─────────────────────────────────────────────────────────

    private static async Task WriteJsonAsync(HttpListenerResponse res, object data)
    {
        res.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, JsonOpts));
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        var json = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public void Dispose()
    {
        _stopping = true;
        try { _http.Stop(); } catch { }
        try { _loop?.Wait(1000); } catch { }
    }

    // ─── Request body records ────────────────────────────────────────────────────

    private sealed class EnrollRequest { public int[]? Ids { get; set; } }
    private sealed class ConfigRequest
    {
        public string? ServerUrl { get; set; }
        public string? MachineName { get; set; }
        public bool? StartWithWindows { get; set; }
    }
    private sealed class RegisterRequest { public string? AdminPassword { get; set; } }
    private sealed class FolderBody { public string? Path { get; set; } }
    private sealed class DismissWarningBody { public string? GameName { get; set; } }
}
