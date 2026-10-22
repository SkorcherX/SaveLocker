using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>Where a <see cref="ScanCandidate"/> was discovered.</summary>
public enum ScanSource
{
    /// <summary>A non-Steam game added to Steam (read from shortcuts.vdf).</summary>
    SteamShortcut,
    /// <summary>An installed Steam game (read from appmanifest_*.acf).</summary>
    SteamInstalled,
    /// <summary>A folder under a common save root whose name matches the manifest.</summary>
    SaveRoot,
    /// <summary>
    /// A game installed through Heroic Games Launcher (Linux). Discovered from Heroic's own library
    /// files, not from Steam — Heroic runs its games in prefixes it manages itself, so a Heroic game
    /// added to the Steam library has a shortcut but no compatdata prefix behind it.
    /// </summary>
    Heroic
}

/// <summary>
/// Which storefront a candidate came from, when discovery can tell. This is a second axis to
/// <see cref="ScanSource"/>, not a finer one: a source says HOW the game was found, a store says
/// WHO sold it. Heroic is the only source that manages more than one, and its own library files
/// name the runner outright, so the distinction costs nothing to carry and is the only way a user
/// with a large Heroic install base can narrow the list to one store.
/// </summary>
public enum GameStore
{
    /// <summary>Discovery has no store to report — a shortcut, or a save-root match.</summary>
    Unknown,
    Steam,
    Epic,
    Gog,
    Amazon,
    /// <summary>Installed by hand into a launcher that manages it (Heroic's <c>sideload</c>).</summary>
    Sideload
}

/// <summary>
/// A discovered game the user might want to enroll. <see cref="SuggestedSaveDir"/>
/// is our best guess at the local save folder (may be null if we couldn't resolve
/// one yet — the user can fill it in).
/// </summary>
public sealed record ScanCandidate(
    string Name,
    string? SuggestedSaveDir,
    ScanSource Source,
    bool HasSteamCloud,
    string? ManifestKey = null,
    string? InstallDir = null,
    /// <summary>Unsigned Steam AppID for a non-Steam shortcut — the compatdata folder name.</summary>
    string? SteamAppId = null,
    /// <summary>
    /// The game's Wine prefix, when discovery resolved one (Linux only; null on Windows) — Steam's
    /// <c>compatdata/&lt;appid&gt;</c> for a shortcut, or Heroic's own <c>winePrefix</c>. Lets the
    /// path browser open inside the prefix instead of at $HOME when the save-folder guess is null —
    /// the normal case for a game absent from the manifest.
    /// <para>
    /// It is the prefix ROOT, not its <c>drive_c</c>: the two launchers nest it differently, so
    /// anything descending into it must go through <see cref="WinePrefix.Locate"/> rather than
    /// assume Steam's layout.
    /// </para>
    /// </summary>
    string? PrefixPath = null,
    /// <summary>
    /// The process name (no <c>.exe</c>) that means this game is running, when discovery can know
    /// it unambiguously — which in practice means a non-Steam shortcut, where Steam records the
    /// exact executable the user chose. Null for an installed Steam game or a save-root match: the
    /// folder name is not an executable name and guessing would be worse than admitting ignorance.
    /// <para>
    /// On Windows this is what drives the whole process lifecycle — lease, exit-push, and the
    /// running-game pull refusal (WA-01). A game enrolled without it is not merely missing a
    /// nicety; <see cref="ProcessWatcher"/> excludes it entirely. WA-08.
    /// </para>
    /// </summary>
    string? SuggestedProcessName = null,
    /// <summary>Which storefront sold the game, when discovery knows. See <see cref="GameStore"/>.</summary>
    GameStore Store = GameStore.Unknown,
    /// <summary>
    /// Steam has this game under more than one AppID — a launcher re-created the shortcut instead of
    /// updating it (<see cref="SteamShortcuts.DuplicateNameGroups"/>), and Steam only ever launches
    /// one. One entry per distinct AppID; <see cref="DuplicateShortcut.Tracked"/> marks the shortcut
    /// this scan resolved. Null unless the shape was actually seen (and never an empty list).
    /// </summary>
    IReadOnlyList<DuplicateShortcut>? DuplicateShortcuts = null);

/// <summary>
/// One same-named Steam shortcut that carries its own AppID (see
/// <see cref="ScanCandidate.DuplicateShortcuts"/>): <see cref="Name"/> is the shortcut's own vdf
/// name, <see cref="AppId"/> its unsigned AppID (null for a shortcut carrying none), and
/// <see cref="Tracked"/> marks the one the scan will actually follow.
/// </summary>
public sealed record DuplicateShortcut(string? AppId, string Name, bool Tracked);
