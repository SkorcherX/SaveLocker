using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Checks the SaveLocker server for a newer agent version and, when requested,
/// downloads the installer and launches it. All network I/O runs off the UI thread;
/// this class never touches WinForms directly.
/// </summary>
public sealed class UpdateChecker
{
    private readonly AgentConfig _config;

    // Shared across all checks in one tray session; avoids creating a new socket pool each time.
    private readonly HttpClient _http;

    /// <summary>
    /// The running agent's version, read from the exe's FileVersion resource.
    /// MinVer overrides AssemblyVersion to 0.0.0.0 when git is inaccessible on CI runners,
    /// but the command-line Version property reliably stamps FileVersion. For single-file
    /// self-contained exes Assembly.Location is empty, so read Environment.ProcessPath.
    /// </summary>
    public static readonly Version CurrentVersion =
        Version.TryParse(
            FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").FileVersion,
            out var v) ? v : new Version(0, 1, 0);

    public UpdateChecker(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.ServerUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
    }

    /// <summary>
    /// Queries the server for the latest available agent version.
    /// Returns one of: <see cref="UpdateResult.UpToDate"/>, <see cref="UpdateResult.Available"/>,
    /// <see cref="UpdateResult.Skipped"/>, or <see cref="UpdateResult.Failed"/>.
    /// </summary>
    public async Task<UpdateResult> CheckAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/api/agent/latest");

            if (resp.StatusCode == HttpStatusCode.NoContent)
                return new UpdateResult.UpToDate();

            resp.EnsureSuccessStatusCode();
            var info = await resp.Content.ReadFromJsonAsync<AgentVersionInfo>();
            if (info is null || string.IsNullOrWhiteSpace(info.LatestVersion))
                return new UpdateResult.UpToDate();

            if (!Version.TryParse(info.LatestVersion, out var latest))
                return new UpdateResult.Failed($"Server returned unparseable version: {info.LatestVersion}");

            if (latest <= CurrentVersion)
                return new UpdateResult.UpToDate();

            if (!string.IsNullOrEmpty(_config.SkipVersion) &&
                Version.TryParse(_config.SkipVersion, out var skip) && skip == latest)
                return new UpdateResult.Skipped();

            return new UpdateResult.Available(info.LatestVersion, info.DownloadUrl);
        }
        catch (Exception ex)
        {
            return new UpdateResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Downloads the installer for <paramref name="downloadUrl"/> to %TEMP% and
    /// returns the local path. Throws on failure.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(string version, string downloadUrl, IProgress<int>? progress = null)
    {
        var dest = Path.Combine(Path.GetTempPath(), $"SaveLockerSetup-{version}.exe");

        using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var fs = File.Create(dest);

        var buf = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read));
            downloaded += read;
            if (progress is not null && total > 0)
                progress.Report((int)(downloaded * 100 / total));
        }

        return dest;
    }
}

/// <summary>Discriminated union result returned by <see cref="UpdateChecker.CheckAsync"/>.</summary>
public abstract record UpdateResult
{
    public sealed record UpToDate : UpdateResult;
    public sealed record Available(string Version, string DownloadUrl) : UpdateResult;
    public sealed record Skipped : UpdateResult;
    public sealed record Failed(string Reason) : UpdateResult;
}
