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
    };
}
