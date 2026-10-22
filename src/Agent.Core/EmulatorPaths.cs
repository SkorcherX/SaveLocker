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

    /// <summary>Documents (where PCSX2, DuckStation and Dolphin keep their folders on Windows by default, wherever
    /// the user moved it); under a fixture home, <c>&lt;home&gt;/Documents</c>.</summary>
    public static string Documents => HomeOverride is { } h
        ? Path.Combine(h, "Documents")
        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary><c>$XDG_CONFIG_HOME</c>, else <c>~/.config</c> — never the real one under a fixture home.</summary>
    public static string XdgConfig =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && HomeOverride is null
            ? x : Path.Combine(Home, ".config");

    /// <summary><c>$XDG_DATA_HOME</c>, else <c>~/.local/share</c> — never the real one under a fixture home.</summary>
    public static string XdgData =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x && HomeOverride is null
            ? x : Path.Combine(Home, ".local", "share");

    /// <summary>A Flatpak app's own home: <c>~/.var/app/&lt;id&gt;</c>.</summary>
    public static string Flatpak(string appId) => Path.Combine(Home, ".var", "app", appId);

    /// <summary>An emulator's INI file as section → key → value (keys ignore case), or empty when it cannot be read.</summary>
    public static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        try { return File.Exists(path) ? ScummVmSaves.ParseIni(File.ReadAllText(path)) : new(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>A folder an emulator's INI names: absolute as given, else under <paramref name="baseDir"/>; null when unset.</summary>
    public static string? IniFolder(string? value, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().Trim('"');
        if (v == "~" || v.StartsWith("~/", StringComparison.Ordinal)) v = Path.Combine(Home, v.Length > 2 ? v[2..] : "");
        try { return Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(baseDir, v)); }
        catch { return null; }
    }
}
