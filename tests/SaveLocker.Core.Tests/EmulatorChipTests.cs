using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// The Add games filter rows under Emulators (tasks/emulator-saves Phase 7 and Group C): one chip per emulator SaveLocker
/// reads, and a Console row labelled by the agent. Phase 7's Verify asked for coverage the Heroic store chips never had:
/// a new emulator shipped without its chip would otherwise only be noticed by someone looking for it.
/// </summary>
public sealed class EmulatorChipTests
{
    /// <summary>Every emulator name a reader gives its rows: each reader's <c>EmulatorName</c>, plus PrimeHack, which
    /// is a second name of the Dolphin reader.</summary>
    private static IReadOnlyList<string> ReaderEmulators() =>
        typeof(RomSaves).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name is "EmulatorName" or "PrimeHackName")
            .Select(f => (string)f.GetRawConstantValue()!)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SaveLocker.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Every_emulator_a_reader_names_has_its_chip_on_add_games()
    {
        var tsx = File.ReadAllText(RepoFile("agent-ui/src/components/AddGamesView.tsx"));
        var start = tsx.IndexOf("const EMULATORS", StringComparison.Ordinal);
        var block = tsx[start..tsx.IndexOf("\n]", start, StringComparison.Ordinal)];
        var chips = Regex.Matches(block, @"id: '([^']+)'").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();

        Assert.Contains("PCSX2", ReaderEmulators());
        Assert.Equal(ReaderEmulators(), chips);
    }

    [Theory]
    [InlineData(Pcsx2Saves.System, "PS2")]
    [InlineData(DuckStationSaves.System, "PS1")]
    [InlineData("gc", "GameCube")]
    [InlineData("wii", "Wii")]
    [InlineData("nds", "DS")]
    [InlineData("model3", "Model 3")]
    public void Each_group_c_console_has_its_label(string system, string label) =>
        Assert.Equal(label, GameSources.SystemLabel(system));
}
