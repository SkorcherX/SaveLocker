using System.Net;
using System.Net.Http.Json;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>Typed HTTP client for the SaveLocker server REST API.</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(string baseUrl, string? apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public async Task<MachineRegisterResponse> RegisterAsync(string name, string? adminPassword = null)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/machines/register")
        {
            Content = JsonContent.Create(new MachineRegisterRequest(name))
        };
        // Re-registering an existing machine name requires the admin password when the
        // server has one set. First-time registration ignores this header.
        if (!string.IsNullOrEmpty(adminPassword))
            msg.Headers.Add("X-Admin-Password", adminPassword);

        var resp = await _http.SendAsync(msg);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadErrorAsync(resp));
        return (await resp.Content.ReadFromJsonAsync<MachineRegisterResponse>())!;
    }

    /// <summary>Pull a human-readable message out of a failed response's { error } body.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
            if (!string.IsNullOrWhiteSpace(body?.Error)) return body!.Error!;
        }
        catch { /* non-JSON or empty body — fall through to a generic message */ }
        return $"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}.";
    }

    private sealed class ErrorBody { public string? Error { get; set; } }

    public async Task<List<GameDto>> ListGamesAsync() =>
        await _http.GetFromJsonAsync<List<GameDto>>("/api/games") ?? new();

    /// <summary>Report this machine's resolved save path for a game back to the server.</summary>
    public async Task SetMachinePathAsync(Guid gameId, string path)
    {
        var resp = await _http.PostAsync($"/api/agent/path/{gameId}?value={Uri.EscapeDataString(path)}", null);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Agent command channel: claim this machine's pending commands.</summary>
    public async Task<List<AgentCommandDto>> GetAgentCommandsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<AgentCommandDto>>("/api/agent/commands", ct) ?? new();

    /// <summary>Report a command's outcome back to the server.</summary>
    public async Task ReportCommandAsync(Guid commandId, CommandStatus status, string? result, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/agent/commands/{commandId}/result", new CommandResultRequest(status, result), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<GameDto> CreateGameAsync(CreateGameRequest req)
    {
        var resp = await _http.PostAsJsonAsync("/api/agent/games", req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<GameDto>())!;
    }

    public async Task<GameStateDto?> GetStateAsync(Guid gameId)
    {
        var resp = await _http.GetAsync($"/api/games/{gameId}/state");
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<GameStateDto>();
    }

    public async Task<LeaseAcquireResponse> AcquireLeaseAsync(Guid gameId)
    {
        var resp = await _http.PostAsync($"/api/games/{gameId}/lease", null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<LeaseAcquireResponse>())!;
    }

    public async Task ReleaseLeaseAsync(Guid gameId) =>
        (await _http.DeleteAsync($"/api/games/{gameId}/lease")).EnsureSuccessStatusCode();

    public async Task<bool> RenewLeaseAsync(Guid gameId)
    {
        var resp = await _http.PostAsync($"/api/games/{gameId}/lease/renew", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<UploadResult> UploadAsync(
        Guid gameId, string contentHash, Guid? parent, bool force, string archivePath, CancellationToken ct = default)
    {
        var url = $"/api/games/{gameId}/upload?hash={Uri.EscapeDataString(contentHash)}";
        if (parent is { } p) url += $"&parent={p}";
        if (force) url += "&force=true";

        await using var fs = File.OpenRead(archivePath);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new("application/zip");
        var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<UploadResult>(cancellationToken: ct))!;
    }

    /// <summary>
    /// Download the current head archive to <paramref name="destinationPath"/>.
    /// Returns the (versionId, contentHash) from response headers, or null if no head exists.
    /// </summary>
    public async Task<(Guid versionId, string contentHash)?> DownloadHeadAsync(
        Guid gameId, string destinationPath, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/games/{gameId}/download",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var versionId = Guid.Parse(resp.Headers.GetValues("X-Version-Id").First());
        var hash = resp.Headers.GetValues("X-Content-Hash").First();

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using (var fs = File.Create(destinationPath))
            await resp.Content.CopyToAsync(fs, ct);

        return (versionId, hash);
    }
}
