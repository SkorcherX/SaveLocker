using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Checks the SaveLocker server for a newer agent version and, when requested,
/// downloads the installer and launches it. All network I/O runs off the UI thread;
/// this class never touches WinForms directly.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly AgentConfig _config;

    // Shared across all checks in one tray session; avoids creating a new socket pool each time.
    private readonly HttpClient _http;

    /// <summary>
    /// The running agent's version.
    /// <para>
    /// <b>The Windows path is the FileVersion resource</b> on the exe: MinVer overrides
    /// AssemblyVersion to 0.0.0.0 when git is inaccessible on CI runners, but the command-line
    /// <c>Version</c> property reliably stamps FileVersion. For single-file self-contained exes
    /// <c>Assembly.Location</c> is empty, so it reads <see cref="Environment.ProcessPath"/>.
    /// </para>
    /// <para>
    /// <b>That yields NOTHING on Linux.</b> The published <c>savelocker</c> is a native ELF apphost,
    /// which has no Win32 version resource — <c>FileVersionInfo.FileVersion</c> is null, and the agent
    /// used to silently fall back to the hard-coded default. Every Deck therefore reported "0.1.0" to
    /// the console forever, no matter which build it was running (the heartbeat sends this). So fall
    /// back to the managed <c>AssemblyFileVersion</c> attribute, which is the same value the Windows
    /// resource is generated from and is readable on every platform.
    /// </para>
    /// </summary>
    public static readonly Version CurrentVersion = ResolveVersion();

    /// <summary>
    /// The one string every surface reports this version as — heartbeat, doctor, agent API, tray.
    /// <para>
    /// Always <c>Major.Minor.Patch</c>. <see cref="Version.ToString()"/> prints as many components as
    /// the value was parsed from, and the two platforms parse from different places: the Windows PE
    /// resource is always four-part ("0.5.0.0"), the Linux <c>AssemblyFileVersion</c> attribute carries
    /// whatever the build script stamped ("0.5.0"). The same release therefore reported two different
    /// strings, and the console — which compares them literally — read that as a fleet running mixed
    /// versions, which is a real fault with real consequences (divergent exclude globs and save paths).
    /// </para>
    /// </summary>
    public static readonly string CurrentVersionText = Format(CurrentVersion);

    /// <summary>
    /// What a human should be shown this build as. Identical to <see cref="CurrentVersionText"/> for
    /// a release; a build carrying a prerelease suffix adds it and the commit — "0.5.10-test (55ddde9)".
    /// <para>
    /// It is a <b>second</b> string on purpose. <see cref="CurrentVersionText"/> is what the heartbeat
    /// sends and what the console compares literally, so a suffix there reads as fleet version skew —
    /// see the note on it. This one is never compared with anything; it only answers "which build is
    /// actually running", which a numeric version cannot when several builds share one.
    /// </para>
    /// </summary>
    public static readonly string BuildLabel = ResolveBuildLabel();

    /// <summary>Major.Minor.Patch, tolerating a Version with fewer components than that.</summary>
    private static string Format(Version v) =>
        $"{v.Major}.{Math.Max(v.Minor, 0)}.{Math.Max(v.Build, 0)}";

    private static string ResolveBuildLabel()
    {
        // MinVer writes "0.5.10-test+<full sha>" here; the Dockerfile and build-linux.sh pass the same
        // shape explicitly. Absent (Agent.Linux carries no MinVer reference), fall back to the number.
        var informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return CurrentVersionText;

        var plus = informational.IndexOf('+');
        var version = plus < 0 ? informational : informational[..plus];
        // The SDK appends the source revision itself, and joins it with a DOT rather than a second
        // '+' when the version it was handed already carries build metadata — so the raw field can
        // read "0.5.10-test+.55ddde9…". Trimming the separator is what keeps the hash a hash.
        var sha = (plus < 0 ? "" : informational[(plus + 1)..]).TrimStart('.');

        // A release has no prerelease suffix, and decorating it would change every screen that has
        // ever shown a version. Only a build that already announces itself as not-a-release gets more.
        if (!version.Contains('-')) return CurrentVersionText;

        return sha.Length == 0 ? version : $"{version} ({sha[..Math.Min(7, sha.Length)]})";
    }

    private static Version ResolveVersion()
    {
        // Windows: the PE version resource (proven; leave it first so nothing about Windows changes).
        if (Version.TryParse(FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").FileVersion, out var fromResource))
            return fromResource;

        // Linux (and any host without a version resource): the managed attribute.
        var attr = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (Version.TryParse(attr, out var fromAttribute))
            return fromAttribute;

        return new Version(0, 1, 0);
    }

    /// <summary>
    /// Which of the server's agent packages this build is asking for.
    /// <para>
    /// Architecture is assumed x64 rather than read from <c>RuntimeInformation</c>: those are the
    /// only two the release workflow produces, a Deck is x64, and inventing a third value here
    /// would only turn "the server hosts nothing for me" into a 400.
    /// </para>
    /// </summary>
    public static readonly string Platform =
        OperatingSystem.IsWindows() ? AgentPlatform.Windows : AgentPlatform.Linux;

    /// <summary>
    /// What a package is called on disk and what its first bytes must be. The agent's own package
    /// differs per host; the Decky plugin is a zip wherever it is downloaded, because it is not this
    /// machine's code — it is another application's, which only the Linux agent ever fetches.
    /// </summary>
    private static (string Extension, byte[] Magic, string Describe) ShapeOf(PackageKind kind) =>
        kind switch
        {
            PackageKind.DeckyPlugin => (".zip", [(byte)'P', (byte)'K'], "a zip archive"),
            _ when OperatingSystem.IsWindows() => (".exe", [(byte)'M', (byte)'Z'], "a Windows executable"),
            _ => (".tar.gz", [0x1f, 0x8b], "a gzip archive"),
        };

    /// <summary>The origin this checker was built for. A connection change retires it (see TrayApp).</summary>
    public string ServerUrl { get; }

    /// <summary>Refuse a package larger than this. The server caps uploads at 200 MB by default.</summary>
    private const long MaxInstallerBytes = 300L * 1024 * 1024;

    public UpdateChecker(AgentConfig config)
    {
        _config = config;
        ServerUrl = config.ServerUrl;

        // Built through the same TLS/pin policy as every other request the agent makes. It used to
        // construct a bare HttpClient, so the update channel — the one that ends in executing a
        // binary — was the single path that ignored the TOFU pin entirely. WA-05.
        _http = ServerHttp.Create(config, withApiKey: true);
        _http.Timeout = TimeSpan.FromSeconds(30);
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
            var info = await FetchLatestAsync(Platform);
            if (info is null || string.IsNullOrWhiteSpace(info.LatestVersion))
                return new UpdateResult.UpToDate();

            if (!Version.TryParse(info.LatestVersion, out var latest))
                return new UpdateResult.Failed($"Server returned unparseable version: {info.LatestVersion}");

            if (latest <= CurrentVersion)
                return new UpdateResult.UpToDate();

            if (!string.IsNullOrEmpty(_config.SkipVersion) &&
                Version.TryParse(_config.SkipVersion, out var skip) && skip == latest)
                return new UpdateResult.Skipped();

            return new UpdateResult.Available(info.LatestVersion, info.DownloadUrl, info.Sha256);
        }
        catch (Exception ex)
        {
            return new UpdateResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// What the server is offering for <paramref name="platform"/>, or null when it is offering
    /// nothing. No version comparison — the caller knows what it is comparing against, which for the
    /// Decky plugin is not this agent's version at all.
    /// </summary>
    public async Task<AgentVersionInfo?> FetchLatestAsync(string platform)
    {
        // A server from before platform slots ignores the parameter and answers with the Windows
        // installer, which is exactly what it would have answered anyway — so an old server keeps
        // working for a Windows agent, and a Linux agent talking to one is told about a .exe it will
        // refuse at the payload check rather than run. Such a server also has no plugin slot, so it
        // answers the plugin query with the same .exe; the payload check is what stops that too.
        var resp = await _http.GetAsync($"/api/agent/latest?platform={platform}");
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AgentVersionInfo>();
    }

    /// <summary>
    /// Download this platform's agent package and prove it is the artifact the configured server
    /// authorised, or throw. Nothing is left on disk on any failure path.
    ///
    /// <para>
    /// Everything here is one defence, because the transport is not one. SaveLocker ships no
    /// certificates and plain http is the default (Decisions.md), so a downloaded package cannot
    /// be trusted because of <i>how</i> it arrived — only because of what it hashes to.
    /// </para>
    /// <para>
    /// It only ever <b>downloads and verifies</b>. Running the Windows installer is the tray's job
    /// and needs the user's consent; unpacking the Linux tarball is the updater's, and is gated on
    /// its own checks. Keeping that separation is what lets the CLI exercise this whole path
    /// without anything being executed.
    /// </para>
    /// </summary>
    /// <param name="expectedSha256">
    /// The digest from the update metadata. Required for a download that leaves the configured
    /// server's origin; optional (with a warning) for the server's own package endpoint, so a
    /// server that predates digests still updates rather than becoming permanently unupdatable.
    /// </param>
    /// <param name="kind">
    /// Which package shape the bytes must have. Everything else about the download — the origin
    /// rule, the credential rule, the size cap, the digest — is identical, which is why the Decky
    /// plugin comes through here rather than getting a second downloader of its own.
    /// </param>
    public async Task<string> DownloadInstallerAsync(
        string version, string downloadUrl, string? expectedSha256 = null, IProgress<int>? progress = null,
        PackageKind kind = PackageKind.Agent)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"The server offered an update at '{downloadUrl}', which is not a usable http(s) URL.");

        // Is this our own server, or somewhere else? An off-origin URL is legitimate (an admin can
        // point AgentUpdate:DownloadUrl at a GitHub release) but it is a different trust situation:
        // it gets no credential, no pin, and no benefit of the doubt about integrity.
        var sameOrigin = ServerOrigin.Same(url.GetLeftPart(UriPartial.Authority), _config.ServerUrl);

        if (!sameOrigin && string.IsNullOrWhiteSpace(expectedSha256))
            throw new InvalidOperationException(
                $"Refusing to download an update from {url.Host}: it is not this SaveLocker server, " +
                "and the server supplied no SHA-256 to verify it against. Host the package on the " +
                "server itself, or set AgentUpdate:Sha256 alongside AgentUpdate:DownloadUrl.");

        if (string.IsNullOrWhiteSpace(expectedSha256))
            AgentLogger.Log(
                "WARNING: the server supplied no SHA-256 for this update, so the downloaded " +
                "package cannot be verified before it is used. Re-upload it in the console " +
                "to record one.");

        // The machine key is a credential for OUR server. The old code reused one client whose
        // DEFAULT HEADERS carried it, so an arbitrary DownloadUrl received this machine's API key
        // simply for being named in the server's response. WA-05.
        var http = sameOrigin ? _http : ServerHttp.CreateForeign();
        try
        {
            // Unique per attempt and created exclusively: the old fixed name in %TEMP% was
            // predictable and world-writable, so another local user could pre-place or swap the file
            // between download and launch.
            var dest = Path.Combine(
                Path.GetTempPath(), $"SaveLockerSetup-{version}-{Guid.NewGuid():N}{ShapeOf(kind).Extension}");
            try
            {
                var actual = await StreamToFileAsync(http, url, dest, progress);

                if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                    !string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The downloaded package does not match the checksum the server published. " +
                        "It was NOT used and has been deleted.\n" +
                        $"  expected: {expectedSha256.Trim().ToLowerInvariant()}\n" +
                        $"  actual:   {actual}");

                VerifyLooksLikePackage(dest, kind);
                return dest;
            }
            catch
            {
                // Deleted on EVERY failure, not just the recognised ones. A rejected installer left
                // in %TEMP% is an executable that failed verification sitting next to one that
                // passed, distinguishable only by a GUID.
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                throw;
            }
        }
        finally
        {
            if (!sameOrigin) http.Dispose();
        }
    }

    /// <summary>Stream to disk under a hard size cap, returning the SHA-256 of what was written.</summary>
    private static async Task<string> StreamToFileAsync(
        HttpClient http, Uri url, string dest, IProgress<int>? progress)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        // A declared length over the cap is refused before a byte is transferred; the copy is still
        // counted, because Content-Length is a claim by whoever is answering.
        if (resp.Content.Headers.ContentLength is { } declared && declared > MaxInstallerBytes)
            throw new InvalidOperationException(
                $"The update is {declared / (1024 * 1024)} MB, over the " +
                $"{MaxInstallerBytes / (1024 * 1024)} MB limit. Nothing was downloaded.");

        var total = resp.Content.Headers.ContentLength;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var src = await resp.Content.ReadAsStreamAsync();

        // CreateNew, not Create: this must fail rather than overwrite anything already at the path.
        // On Unix the mode is set AS the file is created, not afterwards — there is then no instant
        // at which a package this machine is about to execute is writable by anyone else.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var fs = new FileStream(dest, options);

        var buf = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            downloaded += read;
            if (downloaded > MaxInstallerBytes)
                throw new InvalidOperationException(
                    $"The update exceeded the {MaxInstallerBytes / (1024 * 1024)} MB limit while downloading.");

            sha.AppendData(buf.AsSpan(0, read));
            await fs.WriteAsync(buf.AsMemory(0, read));
            if (progress is not null && total > 0)
                progress.Report((int)(downloaded * 100 / total));
        }

        if (downloaded == 0) throw new InvalidOperationException("The server returned an empty update.");
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Reject anything that is not this platform's package shape — an <c>MZ</c> executable on
    /// Windows, a gzip archive on Linux. Without it the most likely wrong payload (a captive-portal
    /// login page, or a proxy's HTML error page served with 200) reaches the code that runs or
    /// unpacks it.
    /// <para>
    /// It also catches the one cross-platform mistake this channel can make: a Linux agent pointed
    /// at a server that predates platform slots is offered the <b>Windows</b> installer, and an
    /// <c>MZ</c> header is not a gzip header, so it stops here with a sentence naming the problem
    /// rather than failing somewhere inside tar.
    /// </para>
    /// <para>
    /// This is a sanity check, not a security control: the digest is what proves authenticity. It
    /// is here because it produces a comprehensible error instead of a baffling one.
    /// </para>
    /// <para>
    /// TODO: verify the Authenticode signature here once releases are signed. That is the one check
    /// that survives an attacker who can rewrite both the artifact and the digest, because it does
    /// not depend on the channel they were delivered over. Code signing is a separate decision with
    /// a real cost; see Decisions.md.
    /// </para>
    /// </summary>
    private static void VerifyLooksLikePackage(string path, PackageKind kind)
    {
        var (_, magic, describe) = ShapeOf(kind);

        using var fs = File.OpenRead(path);
        var header = new byte[magic.Length];
        if (fs.ReadAtLeast(header, magic.Length, throwOnEndOfStream: false) != magic.Length ||
            !header.AsSpan().SequenceEqual(magic))
            throw new InvalidOperationException(
                $"The downloaded update is not {describe} — the server, or a proxy in the way, " +
                "returned something else. It was NOT used.");
    }
}

/// <summary>Which package a download is, for the shape check that runs on the received bytes.</summary>
public enum PackageKind
{
    /// <summary>The agent itself — a .exe on Windows, a .tar.gz on Linux.</summary>
    Agent,
    /// <summary>The Decky plugin: a zip, on the one host that installs it.</summary>
    DeckyPlugin,
}

/// <summary>Discriminated union result returned by <see cref="UpdateChecker.CheckAsync"/>.</summary>
public abstract record UpdateResult
{
    public sealed record UpToDate : UpdateResult;
    /// <param name="Sha256">Digest the download must match. Null if the server published none.</param>
    public sealed record Available(string Version, string DownloadUrl, string? Sha256 = null) : UpdateResult;
    public sealed record Skipped : UpdateResult;
    public sealed record Failed(string Reason) : UpdateResult;
}
