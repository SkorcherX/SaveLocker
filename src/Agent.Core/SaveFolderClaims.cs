namespace SaveLocker.Agent;

/// <summary>
/// Which tracked game already syncs a folder's files on this machine. Two games mapped to the same folder with
/// overlapping include patterns push and pull the same files: each pull of one overwrites what the other just
/// pushed. A save kept as its own game by hand ("Daytona USA (Model 2)") has exactly the fleet's "Daytona USA"
/// files, so mapping the fleet's game here as well — from a template another machine reported, or from the
/// console — would undo that choice in silence. Nothing maps a folder automatically while another game claims it.
/// </summary>
public static class SaveFolderClaims
{
    /// <summary>
    /// The other tracked game (not <paramref name="gameId"/>) that maps <paramref name="dir"/> here with files
    /// overlapping <paramref name="scope"/>, or null. Unscoped on either side overlaps everything; two scopes
    /// overlap when they share a pattern (ignoring case, as patterns are matched). Two ROMs' disjoint scopes in
    /// one shared emulator folder do not.
    /// </summary>
    public static TrackedGame? ClaimedBy(AgentConfig config, Guid gameId, string dir, IReadOnlyList<string>? scope)
    {
        if (SavePathGuard.Canonicalize(dir) is not { } want) return null;
        foreach (var g in config.Games)
        {
            if (g.GameId == gameId) continue;
            if (g.IsEnrolledHere && Same(g.SaveDirectory, want) && Overlaps(g.IncludeGlobs, scope)) return g;
            if (g.ExtraPaths.Any(p => p.IsMapped && Same(p.Directory, want) && Overlaps(p.IncludeGlobs, scope))) return g;
        }
        return null;
    }

    private static bool Same(string? dir, string want) =>
        SavePathGuard.Canonicalize(dir) is { } d &&
        string.Equals(d, want, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Overlaps(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is not { Count: > 0 } || b is not { Count: > 0 } || a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();
}
