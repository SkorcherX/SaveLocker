using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// How this machine found a game, as the console and both agent UIs show it: a kind and which one
/// ("Emulator › RetroArch"), plus tags. Written once, here, so every screen says the same thing.
/// </summary>
public static class GameSources
{
    public const string FolderPickedInAgent = "Folder picked in the agent";
    public const string AddedFromCli = "savelocker add-game";

    public static GameSourceDto From(ScanCandidate c)
    {
        var tags = new List<string>();
        switch (c.Source)
        {
            case ScanSource.Emulator:
                if (SystemLabel(c.EmulatorSystem) is { } system) tags.Add(system);
                // The folder is its real path, so it says Flatpak but never EmuDeck: EmuDeck's saves folders
                // are links into the emulator's own. The scanner says which root it came through.
                var dir = (c.SuggestedSaveDir ?? "").Replace('\\', '/');
                if (dir.Contains("/.var/app/", StringComparison.Ordinal)) tags.Add("Flatpak");
                if (c.ViaEmuDeck) tags.Add("EmuDeck");
                return Make(GameSourceKinds.Emulator, c.EmulatorName ?? "Emulator", tags);
            case ScanSource.SteamInstalled:
                if (c.SteamAppId is { } appId) tags.Add("AppID " + appId);
                if (c.PrefixPath is not null) tags.Add("Proton");
                return Make(GameSourceKinds.Steam, "Installed game", tags);
            case ScanSource.SteamShortcut:
                if (c.PrefixPath is not null) tags.Add("Proton");
                return Make(GameSourceKinds.Steam, "Non-Steam shortcut", tags);
            case ScanSource.Heroic:
                if (c.PrefixPath is not null) tags.Add("Wine prefix");
                return Make(GameSourceKinds.Heroic, StoreLabel(c.Store) ?? "Heroic library", tags);
            default:
                var parent = c.SuggestedSaveDir is { } d ? Path.GetFileName(Path.GetDirectoryName(d.TrimEnd('/', '\\'))) : null;
                return Make(GameSourceKinds.SaveFolder, string.IsNullOrEmpty(parent) ? "Save folder" : parent, tags);
        }
    }

    public static GameSourceDto Manual(string detail) => new(GameSourceKinds.Manual, detail);

    private static GameSourceDto Make(string kind, string detail, List<string> tags) =>
        new(kind, Clip(detail, GameSourceDto.MaxDetailLength),
            tags.Count == 0 ? null : tags.Select(t => Clip(t, GameSourceDto.MaxTagLength)).ToArray());

    // The server refuses longer; a folder name is the only text here not written by this file.
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>The first level as people read it. An unknown kind shows as itself.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        GameSourceKinds.Emulator => "Emulator",
        GameSourceKinds.Steam => "Steam",
        GameSourceKinds.Heroic => "Heroic",
        GameSourceKinds.SaveFolder => "Save-folder scan",
        GameSourceKinds.Manual => "Added by hand",
        _ => kind,
    };

    /// <summary>"Emulator › RetroArch".</summary>
    public static string Describe(GameSourceDto s) => $"{KindLabel(s.Kind)} › {s.Detail}";

    public static string? StoreLabel(GameStore store) => store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic Games",
        GameStore.Gog => "GOG",
        GameStore.Amazon => "Amazon Games",
        GameStore.Sideload => "Sideloaded",
        _ => null,
    };

    /// <summary>EmuDeck/ES-DE's ROM folder name as the console's own name ("snes" → "SNES").</summary>
    public static string? SystemLabel(string? system) =>
        string.IsNullOrWhiteSpace(system) ? null
        : Systems.TryGetValue(system.Trim(), out var label) ? label
        : system.Trim().ToUpperInvariant();

    private static readonly Dictionary<string, string> Systems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nes"] = "NES", ["famicom"] = "Famicom", ["fds"] = "Famicom Disk System",
        ["snes"] = "SNES", ["sfc"] = "Super Famicom", ["n64"] = "N64", ["gc"] = "GameCube",
        ["wii"] = "Wii", ["wiiu"] = "Wii U", ["switch"] = "Switch", ["virtualboy"] = "Virtual Boy",
        ["gb"] = "Game Boy", ["gbc"] = "Game Boy Color", ["gba"] = "GBA", ["nds"] = "DS",
        ["n3ds"] = "3DS", ["3ds"] = "3DS",
        ["xbox360"] = "Xbox 360",
        ["psx"] = "PS1", ["ps2"] = "PS2", ["ps3"] = "PS3", ["ps4"] = "PS4", ["psp"] = "PSP", ["psvita"] = "PS Vita",
        ["genesis"] = "Genesis", ["megadrive"] = "Mega Drive", ["mastersystem"] = "Master System",
        ["gamegear"] = "Game Gear", ["segacd"] = "Sega CD", ["sega32x"] = "32X", ["saturn"] = "Saturn",
        ["dreamcast"] = "Dreamcast", ["pcengine"] = "PC Engine", ["tg16"] = "TurboGrafx-16",
        ["pcenginecd"] = "PC Engine CD", ["neogeo"] = "Neo Geo", ["ngp"] = "Neo Geo Pocket",
        ["ngpc"] = "Neo Geo Pocket Color", ["wonderswan"] = "WonderSwan", ["wonderswancolor"] = "WonderSwan Color",
        ["atari2600"] = "Atari 2600", ["atari7800"] = "Atari 7800", ["lynx"] = "Lynx",
        ["arcade"] = "Arcade", ["mame"] = "Arcade", ["fbneo"] = "Arcade",
        ["model2"] = "Model 2", ["model3"] = "Model 3", ["scummvm"] = "ScummVM",
    };

    /// <summary>
    /// Fill in the source of games enrolled before sources were recorded, from a scan that found them
    /// at the same folder. Returns the games it changed (already saved), for the caller to report.
    /// </summary>
    public static IReadOnlyList<TrackedGame> Backfill(AgentConfig config, IEnumerable<ScanCandidate> candidates)
    {
        var changed = new List<TrackedGame>();
        foreach (var c in candidates)
        {
            if (Enroller.TrackedFor(config, c) is not { IsEnrolledHere: true, Source: null } game) continue;
            if (c.SuggestedSaveDir is null ||
                !string.Equals(SavePathGuard.Canonicalize(c.SuggestedSaveDir), SavePathGuard.Canonicalize(game.SaveDirectory),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;
            var source = From(c);
            game.Source = source;
            config.SaveGameSource(game.GameId, source);
            changed.Add(game);
        }
        return changed;
    }

    /// <summary>
    /// Tell the server how this machine found <paramref name="game"/>. Best effort: the poller re-sends a
    /// source the server does not have. True once the server has answered — stored, or refused (a console
    /// older than this route says 404 to every game, every time) — so a caller knows asking again is
    /// pointless; false when it could not be reached and a later try may succeed.
    /// </summary>
    public static async Task<bool> ReportAsync(ApiClient api, TrackedGame game)
    {
        if (game.Source is not { } source) return true;
        try
        {
            await api.SetGameSourceAsync(game.GameId, source);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status < 500)
        {
            AgentLogger.Log($"The server did not take how '{game.Name}' was found ({(int)status}); " +
                            "not sent again until it changes.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AgentLogger.Log($"Could not report how '{game.Name}' was found yet ({ex.Message}); the next poll does.");
            return false;
        }
    }
}
