using System.Security.Cryptography;
using System.Text;

namespace SaveLocker.Agent;

/// <summary>
/// Authentication for the agent's own local API (<see cref="AgentApiServer"/>).
///
/// That API manages this machine — it rewrites config, enrolls games, and re-registers against the
/// server — so it cannot be open just because it listens on loopback. Two things reach it that we
/// do not control: any other process running as this user, and any web page the user has open (a
/// page can POST to http://localhost:5178 or rebind DNS to it).
///
/// The defence is a high-entropy bearer token the page cannot guess and cannot read cross-origin,
/// plus a Host check. The token lives in a 0600 file next to config.json and is handed to the
/// bundled UI by injecting it into index.html, which is only served to a loopback Host.
/// </summary>
public sealed class LocalAuth
{
    public const string HeaderName = "X-SaveLocker-Token";
    /// <summary>Placeholder in agent-ui's index.html, replaced with the live token when served.</summary>
    public const string TokenPlaceholder = "__SAVELOCKER_TOKEN__";

    private readonly byte[] _tokenBytes;

    public string Token { get; }
    public string TokenPath { get; }

    private LocalAuth(string token, string tokenPath)
    {
        Token = token;
        TokenPath = tokenPath;
        _tokenBytes = Encoding.UTF8.GetBytes(token);
    }

    /// <summary>
    /// Load the token beside the given config file, minting one on first run. The token is per
    /// machine-install, not per process: the Windows tray and a CLI-started daemon must agree, and
    /// a restart must not invalidate a browser tab the user already has open.
    /// </summary>
    public static LocalAuth LoadOrCreate(string configPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var path = Path.Combine(dir, "api-token");

        Directory.CreateDirectory(dir);
        RestrictDirectory(dir);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
            {
                RestrictFile(path);
                return new LocalAuth(existing, path);
            }
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        // Create with restrictive permissions from the start — writing then chmod'ing leaves a
        // window where the token is world-readable.
        WritePrivate(path, token);
        return new LocalAuth(token, path);
    }

    /// <summary>Fixed-time comparison, so a caller cannot recover the token byte by byte.</summary>
    public bool IsValid(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, _tokenBytes);
    }

    /// <summary>
    /// True for a Host header that names this machine's loopback interface. A DNS-rebinding page
    /// resolves its OWN name to 127.0.0.1, so the socket is loopback but the Host header still
    /// carries the attacker's domain — which is what this rejects.
    /// </summary>
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var bare = host.Trim('[', ']');
        return bare.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || bare == "::1"
            || (System.Net.IPAddress.TryParse(bare, out var ip) && System.Net.IPAddress.IsLoopback(ip));
    }

    /// <summary>
    /// True when an Origin header is absent (same-origin GET, or a non-browser caller) or names a
    /// loopback origin. Anything else is a web page on another site talking to us.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host);
    }

    private static void WritePrivate(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) { AgentLogger.Log($"could not restrict permissions on {path}: {ex.Message}"); }
    }

    private static void RestrictDirectory(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) { AgentLogger.Log($"could not restrict permissions on {dir}: {ex.Message}"); }
    }
}
