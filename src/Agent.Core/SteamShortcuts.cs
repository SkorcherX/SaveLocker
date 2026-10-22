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
/// <param name="MoonDeckAppId">
/// The real Steam AppID a MoonDeck (Decky game-streaming plugin) shortcut streams, read out of its
/// <c>LaunchOptions</c> (<c>MOONDECK_STEAM_APP_ID=&lt;id&gt;</c>). A MoonDeck shortcut's <c>Exe</c>
/// launches its streaming client script, not the game — Steam never creates a compatdata prefix for
/// <see cref="AppId"/>, because the game itself runs on a different machine. When the same title is
/// ALSO installed locally under Proton (played both ways, or migrated from local to streamed), this
/// is the id whose prefix the real local save data lives in. Null for an ordinary shortcut.
/// </param>
public sealed record SteamShortcut(
    string AppName,
    string? Exe,
    string? StartDir,
    string? AppId,
    string? MoonDeckAppId = null);

/// <summary>Reads non-Steam shortcuts out of Steam's binary <c>shortcuts.vdf</c>.</summary>
public static class SteamShortcuts
{
    /// <summary>
    /// Steam stores a shortcut's AppID as a <b>signed</b> 32-bit int, but names the
    /// <c>compatdata</c> folder with its <b>unsigned</b> value. Reading the signed form straight
    /// out of the VDF and using it as a directory name makes every prefix lookup silently miss.
    /// </summary>
    public static string CompatDataId(int signedAppId) => unchecked((uint)signedAppId).ToString();

    /// <summary>
    /// The unsigned AppID for a value that may have arrived in either representation — the same trap
    /// as <see cref="CompatDataId(int)"/>, one step later. <c>TrackedGame.SteamAppId</c> is normally
    /// already unsigned because discovery wrote it, but <c>add-game --appid</c> takes whatever a user
    /// types, and a user copying from a tool that prints the signed form would otherwise be handed
    /// back an AppID Steam's own API does not recognise. Null for anything unparseable.
    /// </summary>
    public static uint? UnsignedAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        var text = appId.Trim();
        if (uint.TryParse(text, out var unsigned)) return unsigned;
        if (int.TryParse(text, out var signed)) return unchecked((uint)signed);
        return null;
    }

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
            var launchOptions = entry.String("LaunchOptions") ?? entry.String("launchoptions");

            results.Add(new SteamShortcut(
                AppName: name.Trim(),
                Exe: Unquote(entry.String("Exe") ?? entry.String("exe")),
                StartDir: Unquote(entry.String("StartDir") ?? entry.String("startdir")),
                AppId: appId is null ? null : CompatDataId(appId.Value),
                MoonDeckAppId: ParseMoonDeckAppId(launchOptions)));
        }
        return results;
    }

    /// <summary>
    /// Pull <c>MOONDECK_STEAM_APP_ID=&lt;digits&gt;</c> out of a shortcut's <c>LaunchOptions</c>.
    /// <para>
    /// Not a full tokenizer: other MoonDeck values are single-quoted and may contain spaces
    /// (<c>MOONDECK_LINKED_DISPLAY='LG Electronics LG TV (1600x900 mm)'</c>), which a naive
    /// space-split would break on. A Steam AppID is always a bare digit run, so finding the marker
    /// and reading the digits after it is both sufficient and immune to how neighbouring values are
    /// quoted.
    /// </para>
    /// </summary>
    private static string? ParseMoonDeckAppId(string? launchOptions)
    {
        const string marker = "MOONDECK_STEAM_APP_ID=";
        if (string.IsNullOrEmpty(launchOptions)) return null;

        var idx = launchOptions.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end = start;
        while (end < launchOptions.Length && char.IsAsciiDigit(launchOptions[end])) end++;

        return end > start ? launchOptions[start..end] : null;
    }

    /// <summary>
    /// Every shortcut across every Steam user account under a Steam root.
    ///
    /// <para>
    /// One unreadable account does not lose the others. <c>userdata</c> holds a directory per Steam
    /// account that has signed in on this machine, and on a shared PC those belong to different
    /// Windows users — so an access-denied read there is the normal case, not a corrupt install. It
    /// used to throw out of the entire scan. WA-11.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<SteamShortcut>> ReadAllAsync(
        string steamRoot, CancellationToken ct = default)
    {
        var results = new List<SteamShortcut>();
        var userdata = Path.Combine(steamRoot, "userdata");

        List<string> userDirs;
        try
        {
            if (!Directory.Exists(userdata)) return results;
            // Materialised inside the try: EnumerateDirectories fails lazily, so leaving it as a
            // lazy sequence moves the failure into the loop below where it is harder to contain.
            userDirs = Directory.EnumerateDirectories(userdata).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or System.Security.SecurityException)
        {
            AgentLogger.Log($"Scan: could not list Steam userdata at '{userdata}' ({ex.GetType().Name}). No shortcuts from this root.");
            return results;
        }

        foreach (var userDir in userDirs)
        {
            ct.ThrowIfCancellationRequested();
            var vdf = Path.Combine(userDir, "config", "shortcuts.vdf");
            try
            {
                if (!File.Exists(vdf)) continue;
                results.AddRange(Parse(await File.ReadAllBytesAsync(vdf, ct)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                          or System.Security.SecurityException)
            {
                AgentLogger.Log($"Scan: could not read '{vdf}' ({ex.GetType().Name}). Skipping this Steam account.");
            }
        }
        return results;
    }

    private static string? Unquote(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('"');
}
