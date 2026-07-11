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

    /// <summary>PBKDF2-SHA256 hash of a user-chosen password, suitable for storage.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(32);
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
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(pbkdf2.GetBytes(32), expected);
        }
        catch { return false; }
    }
}
