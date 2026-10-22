using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>Typed HTTP client for the SaveLocker server REST API.</summary>
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
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
        // Shared with UpdateChecker via ServerHttp, so the TLS policy cannot drift between the two.
        var handler = ServerHttp.CreateHandler(
            expectedPin, onObserved: pin => ObservedPin = pin, onMismatch: onPinMismatch);

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

    /// <summary>
    /// GET a route and report the status code, or null if the connection itself failed. For
    /// diagnostics that must tell "cannot reach the server" apart from "reached it and was
    /// refused" — an unenrolled agent's 401 is a correct answer, not a network fault, and
    /// <c>doctor</c> reported it as one.
    /// </summary>
    public async Task<HttpStatusCode?> ProbeAsync(string path)
    {
        try { return (await _http.GetAsync(path)).StatusCode; }
        catch { return null; }
    }

    /// <summary>
    /// Offer a generic template for a game the server has no save location for. Best-effort: the
    /// server declines (204) when one already exists, so losing this race is normal and harmless.
    /// </summary>
    public async Task<bool> TrySetSaveTemplateAsync(Guid gameId, string template)
    {
        var resp = await _http.PostAsync(
            $"/api/agent/games/{gameId}/template?value={Uri.EscapeDataString(template)}", null);
        return resp.StatusCode == HttpStatusCode.OK;
    }

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
    public async Task<AgentHeartbeatResponse> ReportHealthAsync(
        AgentHeartbeat beat, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/agent/health", beat, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            return new AgentHeartbeatResponse(Array.Empty<ConflictEscalationDto>());
        return JsonSerializer.Deserialize<AgentHeartbeatResponse>(payload, WebJson)
               ?? new AgentHeartbeatResponse(Array.Empty<ConflictEscalationDto>());
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
        // The /api/agent/ one: the bare /api/games/{id}/state is admin-filtered, so this 401'd for
        // every agent the moment the server had an admin password set.
        var resp = await _http.GetAsync($"/api/agent/games/{gameId}/state");
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

    /// <summary>
    /// Bytes per request in the chunked upload protocol. Sized well under Cloudflare's fixed ~100s
    /// proxied-edge timeout at the slowest realistic home upload speed this was diagnosed against
    /// (~200 KB/s): 4 MiB is ~20s there, five times under the limit, and stays under it down to
    /// roughly 40 KB/s. See Gotchas.md → "Cloudflare's 100s edge timeout".
    /// </summary>
    private const int UploadChunkBytes = 4 * 1024 * 1024;

    /// <summary>Attempts for a single chunk before the whole push gives up and falls through to the
    /// caller's own retry (queued for the offline drainer). A transient reset is exactly what this
    /// protocol exists to survive — this is the layer that survives it without redoing the rest of
    /// the archive.</summary>
    private const int UploadChunkMaxAttempts = 4;

    /// <summary>
    /// Upload an archive via the chunked protocol: Begin (which can short-circuit with NoChange
    /// before a single byte moves), then one small request per <see cref="UploadChunkBytes"/> slice
    /// — each individually retried — then Complete. A single request carrying the whole archive is
    /// what a proxied edge with a fixed timeout kills once the file is large and the link slow enough;
    /// this never asks one request to carry more than a few seconds' worth of upload.
    /// <para>
    /// Falls back to the old single-shot route on a 404 from Begin — a server an agent has updated
    /// ahead of (the two ship and get redeployed separately) simply does not have it yet. Without
    /// this, updating the agent first would turn every upload from "sometimes works" into "always
    /// 404s" until the server catches up, which is a worse rollout than doing nothing.
    /// </para>
    /// </summary>
    /// <param name="onProgress">
    /// Called with (bytes sent so far, total archive bytes) after every chunk, so a UI can show a
    /// real progress bar for the direction actually slow enough to want one — a push, not a pull.
    /// Best-effort only: the single-shot fallback can only report 0/total and total/total, since it
    /// has no chunk boundaries to report progress from in between.
    /// </param>
    public async Task<UploadResult> UploadAsync(
        Guid gameId, string contentHash, Guid? parent, bool force, string archivePath,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        var begin = await BeginUploadAsync(gameId, contentHash, parent, force, files: null, ct);
        if (begin is null)
            return await UploadSingleShotAsync(gameId, contentHash, parent, force, archivePath, onProgress, ct);
        if (begin.NoChange is { } noChange) return noChange;

        return await UploadSessionPayloadAsync(gameId, begin.SessionId!.Value, archivePath, onProgress, ct);
    }

    /// <summary>
    /// Open an upload session, optionally declaring the agent's full per-file manifest so the server
    /// can answer with the subset it actually needs fresh bytes for.
    /// <para>
    /// Returns <c>null</c> — rather than throwing — when the server has no chunked routes at all, so
    /// the caller can fall back to <see cref="UploadAsync"/>'s single-shot path. This is deliberately
    /// only the Begin half: what to build and send afterwards is a sync-policy decision, and it lives
    /// with the rest of that policy in <c>SyncEngine</c>, not in the HTTP client.
    /// </para>
    /// </summary>
    public async Task<BeginUploadResponse?> BeginUploadAsync(
        Guid gameId, string contentHash, Guid? parent, bool force, FileManifestEntry[]? files,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/games/{gameId}/upload/begin",
            new BeginUploadRequest(contentHash, parent, force, files), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<BeginUploadResponse>(cancellationToken: ct))!;
    }

    /// <summary>Send one already-built payload against an already-Begun session and finish it.
    /// Whether that payload is a full archive or a per-file delta is the caller's business.</summary>
    public async Task<UploadResult> UploadSessionPayloadAsync(
        Guid gameId, Guid sessionId, string payloadPath,
        Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        await StreamChunksAsync(gameId, sessionId, payloadPath, onProgress, ct);
        return await CompleteChunkedUploadAsync(gameId, sessionId, ct);
    }

    /// <summary>Stream one archive's bytes through the chunk protocol against an already-Begun
    /// session — shared by the full-archive and delta-payload upload paths, which differ only in
    /// which zip they hand this.</summary>
    private async Task StreamChunksAsync(
        Guid gameId, Guid sessionId, string archivePath, Action<long, long>? onProgress, CancellationToken ct)
    {
        var totalBytes = new FileInfo(archivePath).Length;
        onProgress?.Invoke(0, totalBytes);
        await using var fs = File.OpenRead(archivePath);
        var buffer = new byte[UploadChunkBytes];
        long offset = 0;
        int read;
        while ((read = await ReadFullyAsync(fs, buffer, ct)) > 0)
        {
            await PutChunkWithRetryAsync(gameId, sessionId, offset, buffer, read, ct);
            offset += read;
            onProgress?.Invoke(offset, totalBytes);
        }
    }

    private async Task<UploadResult> CompleteChunkedUploadAsync(Guid gameId, Guid sessionId, CancellationToken ct)
    {
        var completeResp = await _http.PostAsync($"/api/games/{gameId}/upload/{sessionId}/complete", null, ct);
        completeResp.EnsureSuccessStatusCode();
        return (await completeResp.Content.ReadFromJsonAsync<UploadResult>(cancellationToken: ct))!;
    }

    /// <summary>The pre-chunking upload: the whole archive as one request body. Kept only for a
    /// server too old to have the chunked routes — see the fallback in <see cref="UploadAsync"/>.</summary>
    private async Task<UploadResult> UploadSingleShotAsync(
        Guid gameId, string contentHash, Guid? parent, bool force, string archivePath,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        var url = $"/api/games/{gameId}/upload?hash={Uri.EscapeDataString(contentHash)}";
        if (parent is { } p) url += $"&parent={p}";
        if (force) url += "&force=true";

        var totalBytes = new FileInfo(archivePath).Length;
        onProgress?.Invoke(0, totalBytes);
        await using var fs = File.OpenRead(archivePath);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new("application/zip");
        var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        onProgress?.Invoke(totalBytes, totalBytes);
        return (await resp.Content.ReadFromJsonAsync<UploadResult>(cancellationToken: ct))!;
    }

    /// <summary>
    /// PUT one chunk, retrying a network-level failure a few times before letting it propagate. The
    /// offset makes a retry safe even when the previous attempt's bytes actually landed and only its
    /// response was lost — the server treats an offset behind its own count as a no-op replay rather
    /// than double-appending.
    /// </summary>
    private async Task PutChunkWithRetryAsync(
        Guid gameId, Guid sessionId, long offset, byte[] buffer, int count, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var last = attempt >= UploadChunkMaxAttempts;
            HttpResponseMessage resp;
            try
            {
                using var content = new ByteArrayContent(buffer, 0, count);
                content.Headers.ContentType = new("application/octet-stream");
                resp = await _http.PutAsync(
                    $"/api/games/{gameId}/upload/{sessionId}/chunk?offset={offset}", content, ct);
            }
            // OperationCanceledException as well as HttpRequestException: ServerHttp sets no
            // Timeout, so a chunk that stalls mid-body trips HttpClient's own 100s default and
            // surfaces as a CANCELLATION, not as a request exception — which is precisely the
            // transient stall this retry layer exists for, and it used to fall straight through
            // it. A cancellation the caller actually asked for is still fatal: the filter checks.
            catch (Exception ex) when (!last && IsTransientTransportFailure(ex, ct))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }

            // The status is judged here rather than by EnsureSuccessStatusCode inside the try, so
            // that a refusal the server will repeat verbatim is not slept on three times first and
            // cannot be mistaken for a transport hiccup by the filter above. A 409 (offset
            // mismatch) or 413 (over the cap) is the server's settled answer about this session.
            using (resp)
            {
                if (resp.IsSuccessStatusCode) return;
                if (last || !IsRetryableStatus(resp.StatusCode)) resp.EnsureSuccessStatusCode();
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
    }

    /// <summary>A failure to get any answer at all, worth another attempt. A cancellation the
    /// CALLER requested is excluded — that is a shutdown, not a hiccup.</summary>
    private static bool IsTransientTransportFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        (ex is HttpRequestException || ex is OperationCanceledException || ex is IOException);

    /// <summary>Worth another attempt with the same bytes at the same offset. Everything else the
    /// server answers about a chunk is a decision, not a hiccup.</summary>
    private static bool IsRetryableStatus(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.RequestTimeout;

    /// <summary>
    /// Fill <paramref name="buffer"/> as full as the stream allows before returning. A single
    /// <c>ReadAsync</c> is allowed to return fewer bytes than asked for even mid-file, and a short
    /// chunk here would desync the byte offset both sides are counting from that point on.
    /// </summary>
    private static async Task<int> ReadFullyAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await s.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
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
