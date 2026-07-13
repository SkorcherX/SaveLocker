namespace SaveLocker.Agent;

/// <summary>
/// Launch-on-login toggle. Windows implements it with the HKCU Run key; a Linux
/// agent will implement it with a systemd --user unit. Core only needs the toggle.
/// </summary>
public interface IAutoStart
{
    bool IsEnabled();

    /// <summary>Add or remove the login entry. False if the platform could not apply it.</summary>
    bool SetEnabled(bool enabled);
}

/// <summary>
/// Local game discovery. The sources are entirely platform-specific (Windows reads the
/// registry, Steam libraries and known folders; Linux reads shortcuts.vdf and Proton
/// prefixes), so Core consumes only the resulting candidates.
/// </summary>
public interface IGameScanner
{
    Task<IReadOnlyList<ScanCandidate>> ScanAsync(CancellationToken ct = default);
}
