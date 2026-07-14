using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>Typed HTTP client for the SaveLocker server REST API.</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// The server's TLS public-key fingerprint as observed on the last connection this client made,
    /// or null over plain http (nothing to pin) or before the first request. Enrollment reads this
    /// to record the TOFU pin; see <see cref="ServerTrust"/>.
    /// </summary>
    public string? ObservedPin { get; private set; }

    /// <summary>
    /// The client every part of the agent should use: it carries the machine key and enforces the
    /// TOFU pin recorded at enrollment. Constructing an <see cref="ApiClient"/> directly is for the
    /// pre-enrollment case, where there is no pin yet.
    /// </summary>
    public static ApiClient For(AgentConfig config, string? apiKey = null, bool useConfigKey = true) =>
        new(config.ServerUrl,
            apiKey ?? (useConfigKey ? config.ApiKey : null),
            config.ServerPin,
            observed => ServerTrust.WarnMismatch(config.ServerPin!, observed));

    /// <param name="expectedPin">TOFU pin recorded at enrollment, if any.</param>
    /// <param name="onPinMismatch">Invoked with the observed pin when it differs from the expected one.</param>
    public ApiClient(string baseUrl, string? apiKey, string? expectedPin = null, Action<string>? onPinMismatch = null)
    {
        var handler = new HttpClientHandler();

        // Observe the certificate without weakening validation: the callback still answers with the
        // platform's own verdict. Returning `true` here would disable TLS validation outright, which
        // is the classic way this hook gets misused.
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
        {
            if (ServerTrust.Fingerprint(cert) is { } pin)
            {
                ObservedPin = pin;
                if (!string.IsNullOrEmpty(expectedPin) && pin != expectedPin)
                    onPinMismatch?.Invoke(pin);
            }
            return errors == SslPolicyErrors.None;
        };

        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    /// <summary>
    /// Spend an enrollment token for this machine's real API key. The only call the agent makes
    /// before it has a key.
    /// </summary>
    public async Task<RedeemEnrollmentResponse> EnrollAsync(string token, string? machineName)
    {
        var resp = await _http.PostAsJsonAsync("/api/enroll", new RedeemEnrollmentRequest(token, machineName));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadErrorAsync(resp));
        return (await resp.Content.ReadFromJsonAsync<RedeemEnrollmentResponse>())!;
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

    /// <summary>
    /// The one unauthenticated route. Used as a reachability probe — and, because it completes a
    /// TLS handshake, as the way <c>trust --accept</c> observes the server's current identity.
    /// </summary>
    public async Task GetAdminStatusAsync() =>
        (await _http.GetAsync("/api/admin/status")).EnsureSuccessStatusCode();

    public async Task<List<GameDto>> ListGamesAsync() =>
        await _http.GetFromJsonAsync<List<GameDto>>("/api/games") ?? new();

    /// <summary>Report this machine's resolved save path for a game back to the server.</summary>
    public async Task SetMachinePathAsync(Guid gameId, string path)
    {
        var resp = await _http.PostAsync($"/api/agent/path/{gameId}?value={Uri.EscapeDataString(path)}", null);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Report this machine's health. Piggybacks the existing poll, so it adds no new schedule — and
    /// it is the only way a headless agent can tell anyone anything (Decisions.md §2).
    /// </summary>
    public async Task ReportHealthAsync(AgentHeartbeat beat, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/agent/health", beat, ct);
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
