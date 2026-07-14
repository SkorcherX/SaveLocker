using System.Security.Cryptography;

namespace SaveLocker.Server.Services;

/// <summary>Generates per-machine API keys and hashes them for storage.</summary>
public static class Tokens
{
    /// <summary>Create a new random API key (URL-safe base64, 256 bits).</summary>
    public static string NewApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Stable hash of an API key for storage/lookup.</summary>
    public static string Hash(string apiKey)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // PBKDF2 parameters. These are part of the ON-DISK format ("v1:salt:hash") — changing any of
    // them invalidates every stored password. Bump the version tag if they ever have to move.
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>
    /// PBKDF2-SHA256 hash of a user-chosen password, suitable for storage.
    /// Uses the static <c>Pbkdf2</c> API — the <c>Rfc2898DeriveBytes</c> constructors are obsolete
    /// (SYSLIB0060). Same algorithm, same inputs, same bytes: hashes written by the old constructor
    /// still verify, which <c>tests/verify-password-compat.ps1</c> proves end to end.
    /// </summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashBytes);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>Returns true when <paramref name="password"/> matches a hash produced by <see cref="HashPassword"/>.</summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}
