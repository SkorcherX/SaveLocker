namespace SaveLocker.Agent;

/// <summary>
/// Every emulator reader both scanners run, one row each, so a new emulator is one line here and not two in
/// the hosts. Each is its own failure domain: a broken emulator folder must not cost the rest of the scan.
/// </summary>
public static class EmulatorSaves
{
    public static readonly IReadOnlyList<(string Name, Func<IReadOnlyList<ScanCandidate>> Scan)> Sources = new (string, Func<IReadOnlyList<ScanCandidate>>)[]
    {
        ("RetroArch saves", RetroArchSaves.Scan),
        ("melonDS saves", MelonDsSaves.Scan),
        ("Supermodel saves", SupermodelSaves.Scan),
        ("Model 2 saves", Model2Saves.Scan),
        ("ScummVM saves", ScummVmSaves.Scan),
        ("PCSX2 saves", Pcsx2Saves.Scan),
        ("DuckStation saves", DuckStationSaves.Scan),
        ("Dolphin and PrimeHack saves", DolphinSaves.Scan),
        ("PPSSPP saves", PpssppSaves.Scan),
        ("RPCS3 saves", Rpcs3Saves.Scan),
        ("Vita3K saves", Vita3KSaves.Scan),
        ("shadPS4 saves", ShadPs4Saves.Scan),
    };
}
