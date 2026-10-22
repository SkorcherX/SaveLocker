using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Which server game an emulator save joins when it is added, and what else the user may pick instead
/// (tasks/emulator-saves Phase 16, "Linking by hand", version 2 of the mockup). The automatic choice is
/// <see cref="Enroller.ServerNameFor"/>'s. By hand the user may keep the save as its own game, or join
/// another server game that keeps exactly the same files. A same-titled game that keeps OTHER files (another
/// dump, another emulator) is listed but cannot be picked: it needs each machine to keep its own file names
/// for the game, which is Phase 17.
/// </summary>
public static class EnrollLinks
{
    public const string Auto = "auto";
    public const string Separate = "separate";
    public const string Game = "game";

    /// <summary>The options for one emulator candidate, the automatic one first. Empty for any other candidate.</summary>
    public static IReadOnlyList<LinkOption> For(IReadOnlyList<GameDto> server, ScanCandidate c)
    {
        if (c.Source != ScanSource.Emulator || c.IncludeGlobs is not { Count: > 0 }) return [];
        var extras = c.ExtraSaveDirs ?? Array.Empty<DeclaredSavePath>();
        var options = new List<LinkOption>();
        var auto = Enroller.ServerNameFor(server, c, extras);
        var joined = auto is null ? null : server.FirstOrDefault(g => Is(g, auto) && Enroller.SameFiles(g, c));
        var file = c.IncludeGlobs[0];

        if (c.UntouchedSeed && joined is null)
            options.Add(new LinkOption(Auto, LinkKind.Blocked, c.Name, null, SeedDetail, "Not played"));
        else if (auto is null)
            options.Add(new LinkOption(Auto, LinkKind.Blocked, c.Name, null,
                "Every name this save could take on the server is already another game’s.", "Not available"));
        else if (joined is not null)
            options.Add(new LinkOption(Auto, LinkKind.Join, joined.Name, joined.Id,
                $"The server’s “{joined.Name}” keeps {Files(joined)}: the same save files.", "Same save files"));
        else
            options.Add(new LinkOption(Auto, LinkKind.New, auto, null,
                $"No server game keeps {file} yet.", "New"));

        if (joined is not null && !c.UntouchedSeed && SeparateName(server, c) is { } own)
            options.Add(new LinkOption(Separate, LinkKind.New, own, null,
                $"Creates “{own}”. It never syncs with “{joined.Name}”.", "New"));

        foreach (var g in server.Where(g => g.Id != joined?.Id && Enroller.SameFiles(g, c))
                     .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            options.Add(new LinkOption(Game, LinkKind.Join, g.Name, g.Id,
                $"Also keeps {Files(g)}: the same save files.", "Same save files"));

        // Same title, other files: shown so the user sees why it is not linked, never pickable here.
        var names = Enroller.NamesFor(c);
        foreach (var g in server.Where(g => !Enroller.SameFiles(g, c) && SameTitle(g, c, names))
                     .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            options.Add(new LinkOption(Game, LinkKind.Blocked, g.Name, g.Id,
                g.IncludeGlobs is { Length: > 0 }
                    ? $"Keeps {Files(g)}, not {file}. Linking saves with different file names comes in a later update."
                    : "A game that keeps a whole save folder, like a PC game. Only another save of this game can be linked.",
                g.IncludeGlobs is { Length: > 0 } ? "Different file names" : "Whole folder"));
        return options;
    }

    /// <summary>The name a save kept as its own game takes: the first of <see cref="Enroller.NamesFor"/> no
    /// server game has. Null when every one is taken.</summary>
    public static string? SeparateName(IReadOnlyList<GameDto> server, ScanCandidate c) =>
        Enroller.NamesFor(c).FirstOrDefault(n => !server.Any(g => Is(g, n)));

    /// <summary>
    /// The server name a candidate takes under the user's <paramref name="choice"/>, or the reason it cannot
    /// be added. A pick is checked again here, against the server as it is now: the list the user chose from
    /// may be minutes old. EmuDeck's untouched preinstalled file (<see cref="ScanCandidate.UntouchedSeed"/>) only
    /// ever joins a game that keeps it: as a game of its own it would publish a save nobody made.
    /// </summary>
    public static (string? Name, string? Refusal) Resolve(IReadOnlyList<GameDto> server, ScanCandidate c,
        IReadOnlyList<DeclaredSavePath> extras, LinkChoice? choice)
    {
        switch (choice?.Choice)
        {
            case Separate when c.UntouchedSeed:
                return (null, SeedRefusal);
            case Separate:
                return SeparateName(server, c) is { } own
                    ? (own, null)
                    : (null, "every name it could take as its own game is already another game’s.");
            case Game:
                if (server.FirstOrDefault(g => g.Id == choice.GameId) is not { } picked)
                    return (null, "the server game you picked is no longer on the server.");
                return Enroller.SameFiles(picked, c)
                    ? (picked.Name, null)
                    : (null, $"the server’s '{picked.Name}' keeps different save files, so it cannot be linked yet.");
            default:
                if (Enroller.ServerNameFor(server, c, extras) is not { } named)
                    return (null, "every name it could take on the server is already another game's.");
                return !c.UntouchedSeed || server.Any(g => Is(g, named) && Enroller.SameFiles(g, c))
                    ? (named, null)
                    : (null, SeedRefusal);
        }
    }

    private const string SeedDetail =
        "EmuDeck’s preinstalled file, never played here. It can only join a server game that keeps it.";
    private const string SeedRefusal =
        "it is EmuDeck's preinstalled file, never played here: it can only join a server game that keeps it.";

    private static bool Is(GameDto g, string name) => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Chrono Trigger" is the same title as "Chrono Trigger", "Chrono Trigger (RetroArch)" or
    /// "Chrono Trigger (Japan) (melonDS)": the name, or the name followed by a bracket.</summary>
    private static bool SameTitle(GameDto g, ScanCandidate c, IReadOnlyList<string> names) =>
        names.Any(n => Is(g, n)) ||
        (g.Name.StartsWith(c.Name + " (", StringComparison.OrdinalIgnoreCase));

    private static string Files(GameDto g)
    {
        var all = (g.IncludeGlobs ?? []).Concat((g.ExtraPaths ?? []).SelectMany(p => p.IncludeGlobs ?? [])).ToList();
        return all.Count switch
        {
            0 => "its whole save folder",
            1 => all[0],
            _ => $"{all[0]} and {all.Count - 1} more",
        };
    }
}

public static class LinkKind
{
    public const string Join = "join";
    public const string New = "new";
    public const string Blocked = "blocked";
}

/// <summary>One choice on an Add games row. <paramref name="Choice"/> is what the enroll request sends back
/// (<see cref="EnrollLinks.Auto"/>, <see cref="EnrollLinks.Separate"/>, <see cref="EnrollLinks.Game"/> with
/// <paramref name="GameId"/>); <paramref name="Kind"/> says what it does (<see cref="LinkKind"/>).</summary>
public sealed record LinkOption(string Choice, string Kind, string Name, Guid? GameId, string Detail, string Badge);

/// <summary>The user's pick for one candidate, from the enroll request.</summary>
public sealed record LinkChoice(string Choice, Guid? GameId = null);
