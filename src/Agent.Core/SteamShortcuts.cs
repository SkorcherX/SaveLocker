namespace SaveLocker.Agent;

/// <summary>
/// A non-Steam game the user added to Steam ("Add a Non-Steam Game"), read from
/// <c>userdata/&lt;id&gt;/config/shortcuts.vdf</c>. These — not Steam-store titles — are the
/// Linux niche: they have no Steam Cloud, and under Proton they write Windows saves.
/// </summary>
/// <param name="AppId">
/// The shortcut's generated AppID in the form Steam uses on disk. This IS the
/// <c>compatdata/&lt;appid&gt;/</c> directory name — see <see cref="SteamShortcuts.CompatDataId"/>.
/// Null for a shortcut that carries no AppID key: harmless on Windows, but on Linux it means
/// the prefix cannot be located, so <c>doctor</c> calls it out rather than failing silently.
/// </param>
public sealed record SteamShortcut(
    string AppName,
    string? Exe,
    string? StartDir,
    string? AppId);

/// <summary>Reads non-Steam shortcuts out of Steam's binary <c>shortcuts.vdf</c>.</summary>
public static class SteamShortcuts
{
    /// <summary>
    /// Steam stores a shortcut's AppID as a <b>signed</b> 32-bit int, but names the
    /// <c>compatdata</c> folder with its <b>unsigned</b> value. Reading the signed form straight
    /// out of the VDF and using it as a directory name makes every prefix lookup silently miss.
    /// </summary>
    public static string CompatDataId(int signedAppId) => unchecked((uint)signedAppId).ToString();

    /// <summary>Parse one shortcuts.vdf. Returns empty for a malformed or empty file.</summary>
    public static IReadOnlyList<SteamShortcut> Parse(byte[] vdf)
    {
        SteamVdf.VdfObject root;
        try { root = SteamVdf.Parse(vdf); }
        catch (InvalidDataException) { return Array.Empty<SteamShortcut>(); }
        catch (IndexOutOfRangeException) { return Array.Empty<SteamShortcut>(); }

        var results = new List<SteamShortcut>();
        foreach (var entry in root.Children)
        {
            var name = entry.String("AppName") ?? entry.String("appname");
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Steam has spelled this key "appid" and "AppID" across versions; the map is
            // case-insensitive, so one lookup covers both.
            var appId = entry.Int("appid");

            results.Add(new SteamShortcut(
                AppName: name.Trim(),
                Exe: Unquote(entry.String("Exe") ?? entry.String("exe")),
                StartDir: Unquote(entry.String("StartDir") ?? entry.String("startdir")),
                AppId: appId is null ? null : CompatDataId(appId.Value)));
        }
        return results;
    }

    /// <summary>Every shortcut across every Steam user account under a Steam root.</summary>
    public static async Task<IReadOnlyList<SteamShortcut>> ReadAllAsync(
        string steamRoot, CancellationToken ct = default)
    {
        var results = new List<SteamShortcut>();
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return results;

        foreach (var userDir in Directory.EnumerateDirectories(userdata))
        {
            var vdf = Path.Combine(userDir, "config", "shortcuts.vdf");
            if (!File.Exists(vdf)) continue;
            results.AddRange(Parse(await File.ReadAllBytesAsync(vdf, ct)));
        }
        return results;
    }

    private static string? Unquote(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('"');
}
