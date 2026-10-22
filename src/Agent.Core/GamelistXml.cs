using System.Xml;

namespace SaveLocker.Agent;

/// <summary>
/// ES-DE's per-system <c>gamelists/&lt;system&gt;/gamelist.xml</c> (<c>&lt;game&gt;&lt;path&gt;./scud.zip&lt;/path&gt;&lt;name&gt;Scud
/// Race&lt;/name&gt;</c>): the title EmulationStation shows for a ROM. Used only where a save's own name is not a
/// title — an arcade set name like <c>scud</c> — and it is machine-local (one machine has scraped it, another
/// has not), which is safe only because an emulator game is found by its files, not its name
/// (<see cref="Enroller.ServerNameFor"/>, tasks/emulator-saves D1).
/// <para>
/// Where it lives (EmuDeck's <c>emuDeckESDE.sh</c>/<c>.ps1</c>): ES-DE 3 keeps it in <c>~/ES-DE/gamelists</c>
/// (older builds <c>~/.emulationstation/gamelists</c>); EmuDeck for Windows links
/// <c>%APPDATA%\EmuDeck\EmulationStation-DE\ES-DE\gamelists</c> to <c>Emulation\storage\es-de\gamelists</c>.
/// </para>
/// </summary>
public sealed class GamelistXml
{
    /// <summary>The ES-DE folders whose <c>&lt;system&gt;/gamelist.xml</c> files name arcade ROMs.</summary>
    public static readonly IReadOnlySet<string> ArcadeSystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "arcade", "mame", "mame-advmame", "mame-mame4all", "fbneo", "fba", "neogeo", "cps", "cps1", "cps2", "cps3",
        "naomi", "naomi2", "naomigd", "atomiswave", "model2", "model3",
    };

    private readonly IReadOnlyList<string> _roots;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public GamelistXml(IEnumerable<string> roots) => _roots = roots.ToList();

    /// <summary>No gamelists at all.</summary>
    public static GamelistXml None { get; } = new(Array.Empty<string>());

    /// <summary>Only the gamelists inside these EmuDeck <c>Emulation</c> folders — never ES-DE's own in the user's
    /// home, so a reader given explicit folders (a test) never reads the developer's real ones.</summary>
    public static GamelistXml In(IEnumerable<string> emuDeckRoots) =>
        new(emuDeckRoots.Select(r => Path.Combine(r, "storage", "es-de", "gamelists")));

    /// <summary>This machine's gamelist folders: inside each EmuDeck <c>Emulation</c> folder, and — unless the
    /// test rig pinned the scan to one fixture — ES-DE's own.</summary>
    public static GamelistXml Find(IEnumerable<string> emuDeckRoots)
    {
        var roots = emuDeckRoots.Select(r => Path.Combine(r, "storage", "es-de", "gamelists")).ToList();
        if (EmulatorPaths.Standalone)
        {
            roots.Add(Path.Combine(EmulatorPaths.Home, "ES-DE", "gamelists"));
            roots.Add(Path.Combine(EmulatorPaths.Home, ".emulationstation", "gamelists"));
            if (OperatingSystem.IsWindows())
            {
                roots.Add(Path.Combine(EmulatorPaths.AppData, "EmuDeck", "EmulationStation-DE", "ES-DE", "gamelists"));
                roots.Add(Path.Combine(EmulatorPaths.Home, "emudeck", "EmulationStation-DE", "ES-DE", "gamelists"));
            }
        }
        return new GamelistXml(roots);
    }

    /// <summary>
    /// The gamelist title of <paramref name="romBase"/> (a ROM file name without its extension) in an arcade
    /// <paramref name="system"/>, or null — for any other system, a ROM no gamelist names, or no gamelist.
    /// </summary>
    public string? ArcadeTitle(string? system, string romBase)
    {
        if (string.IsNullOrWhiteSpace(system) || !ArcadeSystems.Contains(system)) return null;
        if (!_loaded.TryGetValue(system, out var names))
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in _roots)
                foreach (var (rom, name) in Load(Path.Combine(root, system, "gamelist.xml")))
                    merged.TryAdd(rom, name);
            _loaded[system] = names = merged;
        }
        return names.TryGetValue(romBase, out var title) ? title : null;
    }

    /// <summary>
    /// One <c>gamelist.xml</c>: ROM file name without extension → <c>&lt;name&gt;</c>. A missing, unreadable or
    /// malformed file reads as whatever parsed before the fault — never an exception. No DTD, no external entities.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(file)) return result;
            using var reader = XmlReader.Create(file, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true,
                // ES-DE writes an <alternativeEmulator> beside <gameList>: two roots.
                ConformanceLevel = ConformanceLevel.Fragment,
            });
            string? path = null, name = null;
            var depth = -1;
            reader.Read();
            while (!reader.EOF)
            {
                if (depth >= 0 && reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 &&
                    reader.Name is "path" or "name")
                {
                    var isPath = reader.Name == "path";
                    // Leaves the reader on the node after the element, so no Read() this turn.
                    var text = reader.ReadElementContentAsString().Trim();
                    if (isPath) path = text; else name = text;
                    if (path is not null && name is { Length: > 0 })
                    {
                        var rom = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').TrimEnd('/').Split('/')[^1]);
                        if (rom.Length > 0) result.TryAdd(rom, name);
                        depth = -1;
                    }
                    continue;
                }
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "game")
                {
                    (path, name, depth) = (null, null, reader.IsEmptyElement ? -1 : reader.Depth);
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "game") depth = -1;
                reader.Read();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }
        return result;
    }
}
