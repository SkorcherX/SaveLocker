using System.Security.Cryptography;

namespace LocalGameSync.Server.Services;

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
}
