namespace SaveLocker.Agent;

/// <summary>
/// The folders a standalone emulator's own config and saves are found under — the user's home, Roaming and
/// Local AppData — with the test rig's override. <see cref="EmuDeckRoots.OverrideVariable"/> pins the scan to one
/// fixture <c>Emulation</c> folder and turns standalone lookups off; <see cref="HomeOverrideVariable"/> turns
/// them back on against a fixture home, so <c>testenv emu-fixture</c> can lay out <c>~/.supermodel</c>,
/// <c>scummvm.ini</c> and the like without the test agent ever reading the developer's real ones.
/// </summary>
public static class EmulatorPaths
{
    public const string HomeOverrideVariable = "SAVELOCKER_EMULATOR_HOME";

    public static string? HomeOverride =>
        Environment.GetEnvironmentVariable(HomeOverrideVariable) is { Length: > 0 } p ? p.Trim() : null;

    /// <summary>Whether standalone (non-EmuDeck-folder) locations are looked at at all.</summary>
    public static bool Standalone => EmuDeckRoots.Override is null || HomeOverride is not null;

    public static string Home => HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Roaming AppData; under a fixture home, <c>&lt;home&gt;/AppData/Roaming</c>.</summary>
    public static string AppData => HomeOverride is { } h
        ? Path.Combine(h, "AppData", "Roaming")
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Local AppData on Windows — Qt's <c>ConfigLocation</c> there; under a fixture home, <c>&lt;home&gt;/AppData/Local</c>.</summary>
    public static string LocalAppData => HomeOverride is { } h
        ? Path.Combine(h, "AppData", "Local")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
