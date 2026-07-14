using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SaveLocker.Agent;

/// <summary>
/// Trust-on-first-use pinning of the server's TLS identity (Decisions.md §4).
/// <para>
/// The enrollment policy file is unsigned, so the agent's protection against being pointed at a
/// malicious server is: the user obtained the file from their own console, HTTPS, and this pin —
/// recorded at enrollment, checked on every later connection.
/// </para>
/// It <b>warns</b> rather than blocks, deliberately. The pin is over the public key (SPKI), not the
/// certificate, so it survives a routine renewal that reuses the key — but Let's Encrypt rotates
/// the key by default, so a legitimate renewal will trip this. A pin that hard-failed would take
/// the agent offline on a Tuesday for a reason the user cannot diagnose on a headless Deck; a pin
/// that warns still surfaces the one thing that matters, that the server is not who it was.
/// </summary>
public static class ServerTrust
{
    /// <summary>SHA-256 of the certificate's SubjectPublicKeyInfo, base64. Null when there is no cert.</summary>
    public static string? Fingerprint(X509Certificate2? cert)
    {
        if (cert is null) return null;
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(SHA256.HashData(spki));
    }

    /// <summary>
    /// True when this URL can be pinned at all. Plain http:// has no identity to pin — on a trusted
    /// LAN that is a legitimate setup, so it is not an error, but nothing is recorded and the
    /// warning must not fire for it later.
    /// </summary>
    public static bool IsPinnable(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public static string MismatchWarning(string expected, string observed) =>
        "WARNING: the server's TLS identity has CHANGED since this machine enrolled.\n" +
        $"  pinned:   {expected}\n" +
        $"  observed: {observed}\n" +
        "  This is expected after a certificate renewal that rotated the key. If you did not renew\n" +
        "  the certificate, you may be talking to a different server — a pull writes files into your\n" +
        "  save folders, so stop and check before syncing. Re-pin with: savelocker trust --accept";

    private static int _warned;

    /// <summary>
    /// Raise the mismatch once per process, to the log <i>and</i> to stderr. The log alone is not
    /// enough: a user running a one-shot command would never see it, and the daemon would otherwise
    /// repeat it on every 20-second poll.
    /// </summary>
    public static void WarnMismatch(string expected, string observed)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 1) return;
        var warning = MismatchWarning(expected, observed);
        AgentLogger.Log(warning);
        Console.Error.WriteLine(warning);
    }
}
