using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// The console save formats tasks/emulator-saves Group C reads, and the one question every surface asks of them:
/// is this file a memory card SHARED by every game of a console? Restoring an old version of one restores every
/// game that saved to it, so a shared card is never a game of its own — it is listed with how to switch the
/// emulator to per-game saves (<see cref="ScanCandidate.NotSyncable"/>) and warned about if mapped by hand
/// (<see cref="SaveDirSanity"/>). Judged by the file itself, never by an emulator setting: a setting says what the
/// emulator will do next time, the file says what is on disk now.
/// </summary>
public static partial class MemoryCards
{
    /// <summary>What every PCSX2 file card (8 to 64 MB) starts with — the PS2's own superblock.</summary>
    public const string Ps2CardMagic = "Sony PS2 Memory Card Format";

    /// <summary>A PCSX2 folder card's root holds this; each save is a subfolder beside it.</summary>
    public const string FolderCardSuperblock = "_pcsx2_superblock";

    /// <summary>A shared card: which file, for which console and emulator, and how to switch to per-game saves.</summary>
    public sealed record SharedCard(string FileName, string Emulator, string Console, string HowToFix);

    public const string Pcsx2Fix =
        "In PCSX2, open Settings > Memory Cards, right-click the card and choose Convert > Folder, then right-click the " +
        "new card and choose Use for Slot 1. Each game's saves then sync on their own.";
    public const string DolphinFix =
        "In Dolphin, open Options > Configuration > GameCube and set Slot A to GCI Folder. Dolphin copies the card's saves into the " +
        "folder the first time it starts a game, and each game's saves then sync on their own.";
    public const string DuckStationFix =
        "In DuckStation, open Settings > Memory Cards and set Card 1 to Separate Card Per Game (Title). The new cards " +
        "start empty: copy each game's saves into its card with Tools > Memory Card Editor.";

    /// <summary>
    /// The shared card <paramref name="file"/> is, or null. A PCSX2 file card by its header, Dolphin's raw card
    /// and DuckStation's shared card by the names those emulators give them, and any PS1 card holding saves of more
    /// than one game by its directory. Never throws.
    /// </summary>
    public static SharedCard? Shared(FileInfo file, string? emulator = null)
    {
        try
        {
            if (file.LinkTarget is not null || !file.Exists) return null;
            var name = file.Name;
            var ext = file.Extension.ToLowerInvariant();
            if (ext == ".ps2" && IsPs2CardFile(file))
                return new SharedCard(name, emulator ?? "PCSX2", "PS2", Pcsx2Fix);
            if (ext == ".raw" && DolphinRawCard().IsMatch(name))
                return new SharedCard(name, emulator ?? "Dolphin", "GameCube", DolphinFix);
            if (ext is ".mcd" or ".mcr")
            {
                if (DuckStationSharedCard().IsMatch(name))
                    return new SharedCard(name, emulator ?? "DuckStation", "PS1", DuckStationFix);
                // A card named after a game on it is that game's own, whatever else it holds: a multi-disc game's
                // later discs, or an earlier game's save copied in for the sequel to import (Suikoden II).
                if (Ps1Directory(file.FullName) is { Codes.Count: > 1 } card &&
                    (Ps1CardTitle(name) is not { } title || OwnCodes(title, card.Codes).Count == 0))
                    return new SharedCard(name, emulator ?? "DuckStation", "PS1", DuckStationFix);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The Add games row for a shared card: listed, never addable (<see cref="ScanCandidate.NotSyncable"/>), saying why
    /// and how to switch the emulator to per-game saves.
    /// </summary>
    public static ScanCandidate Candidate(SharedCard card, string dir, string system, bool viaEmuDeck) =>
        new($"{card.Console} memory card ({card.FileName})", EmuDeckRoots.RealPath(dir), ScanSource.Emulator, HasSteamCloud: false,
            EmulatorName: card.Emulator,
            EmulatorSystem: system,
            EmulatorRom: card.FileName,
            ViaEmuDeck: viaEmuDeck,
            NotSyncable: $"Every {card.Console} game saves to this one card, so restoring one game's save would restore them " +
                         $"all. {card.HowToFix}");

    /// <summary>A PCSX2 file card: it starts with the PS2 superblock's format string.</summary>
    public static bool IsPs2CardFile(FileInfo file)
    {
        if (file.Length < Ps2CardMagic.Length) return false;
        var head = new byte[Ps2CardMagic.Length];
        using var stream = file.OpenRead();
        stream.ReadExactly(head);
        return Encoding.ASCII.GetString(head) == Ps2CardMagic;
    }

    /// <summary>A PCSX2 folder card (<c>Mcd001.ps2/</c> as a folder): it holds the superblock file.</summary>
    public static bool IsFolderCard(string dir) =>
        File.Exists(Path.Combine(dir, FolderCardSuperblock));

    /// <summary>What a raw PS1 card holds: how many saves, and their product codes (<c>SLUS-00067</c>), distinct, in
    /// card order. DuckStation names save states by the same code.</summary>
    public sealed record Ps1Card(int Saves, IReadOnlyList<string> Codes);

    /// <summary>
    /// The directory of a raw PS1 card (<c>.mcd</c>/<c>.mcr</c>, 128 KB): frame 0 is the <c>MC</c> header, frames
    /// 1–15 the directory, each save's first block naming it <c>BASLUS-00067…</c> — region, product code, then the
    /// game's own suffix. Null for anything that is not a PS1 card.
    /// </summary>
    public static Ps1Card? Ps1Directory(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != Ps1CardSize) return null;
            var head = new byte[0x80 * 16];
            using (var stream = info.OpenRead()) stream.ReadExactly(head);
            if (head[0] != (byte)'M' || head[1] != (byte)'C') return null;
            var saves = 0;
            var codes = new List<string>();
            for (var frame = 1; frame < 16; frame++)
            {
                var at = frame * 0x80;
                // 0x51: a save's first block. Later blocks (0x52/0x53) and free ones (0xA0…) name nothing new.
                if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(at)) != 0x51) continue;
                saves++;
                var name = Encoding.ASCII.GetString(head, at + 0x0A, 20).TrimEnd('\0');
                if (Ps1ProductCode(name) is { } code && !codes.Contains(code)) codes.Add(code);
            }
            return new Ps1Card(saves, codes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException) { return null; }
    }

    public const int Ps1CardSize = 128 * 1024;

    /// <summary>The game a per-game PS1 card is named after: <c>Suikoden II_1.mcd</c> → <c>Suikoden II</c>; null for a
    /// name with no slot.</summary>
    public static string? Ps1CardTitle(string fileName) =>
        Ps1PerGameCard().Match(fileName) is { Success: true } m ? m.Groups["title"].Value : null;

    /// <summary>
    /// Which of a card's product codes are the game the card is named after (<paramref name="cardTitle"/>): the code
    /// itself (a card per serial), or one whose disc title is that name, ignoring region, disc and punctuation. Every
    /// disc of a multi-disc game; not a save of another game copied onto the card. Empty when none is.
    /// </summary>
    public static IReadOnlyList<string> OwnCodes(string cardTitle, IReadOnlyList<string> codes)
    {
        var key = TitleKey(cardTitle);
        return codes.Where(code => string.Equals(code, cardTitle, StringComparison.OrdinalIgnoreCase) ||
                                   ConsoleTitles.For("psx", code) is { } t && TitleKey(t.Title) == key).ToList();
    }

    private static string TitleKey(string title) => ManifestLoader.NormalizeName(RomNames.CleanTitle(title));

    /// <summary><c>BASLUS-00067SAVE</c> > <c>SLUS-00067</c>; null when the name carries no product code.</summary>
    public static string? Ps1ProductCode(string saveName) =>
        ConsoleSaveName().Match(saveName) is { Success: true } m ? $"{m.Groups["prefix"].Value}-{m.Groups["number"].Value}" : null;

    /// <summary>A PS1 or PS2 save's name: region (<c>BA</c>, <c>BE</c>, <c>BI</c>…), product code with or without
    /// its hyphen, then whatever the game appends.</summary>
    [GeneratedRegex(@"^B[A-Z](?<prefix>[A-Z]{4})-?(?<number>\d{5})")]
    public static partial Regex ConsoleSaveName();

    // Dolphin's GC_MEMCARDA/B + region: MemoryCardA.USA.raw (and the size-suffixed MemoryCardA.USA.251.raw).
    [GeneratedRegex(@"^MemoryCard[AB]\.", RegexOptions.IgnoreCase)]
    private static partial Regex DolphinRawCard();

    [GeneratedRegex(@"^shared_card_\d+\.mcd$", RegexOptions.IgnoreCase)]
    private static partial Regex DuckStationSharedCard();

    // DuckStation's per-game card, <title>_<slot>.mcd: two card slots (System::GetGameMemoryCardPath).
    [GeneratedRegex(@"^(?<title>.+)_[12]\.(?:mcd|mcr)$", RegexOptions.IgnoreCase)]
    private static partial Regex Ps1PerGameCard();
}

/// <summary>
/// Text a console wrote into a save's own metadata — the title a game gives its save (a PS2 <c>icon.sys</c>, a
/// GameCube comment, a Wii <c>banner.bin</c>). It is inside the save, so it reads the same on every machine.
/// </summary>
public static class ConsoleText
{
    /// <summary>Shift-JIS (code page 932): Japanese games, and every PS2 <c>icon.sys</c>.</summary>
    public static string ShiftJis(ReadOnlySpan<byte> bytes) => Decode(932, bytes);

    /// <summary>Windows-1252: a western GameCube save's comment.</summary>
    public static string Latin(ReadOnlySpan<byte> bytes) => Decode(1252, bytes);

    private static string Decode(int codePage, ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        try
        {
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.Latin1;
            return Tidy(encoding.GetString(bytes));
        }
        catch (ArgumentException) { return Tidy(Encoding.Latin1.GetString(bytes)); }
    }

    /// <summary>
    /// Full-width letters and digits (<c>ＫＩＮＧＤＯＭ</c>, how PS2 titles are usually written) folded to plain ones,
    /// the ideographic space to a space, control characters dropped and whitespace collapsed. Done by hand:
    /// the Linux agent runs with invariant globalization, where Unicode normalization is not to be relied on.
    /// </summary>
    public static string Tidy(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch switch
            {
                >= '！' and <= '～' => (char)(ch - 0xFEE0),
                '　' => ' ',
                _ => ch,
            };
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
