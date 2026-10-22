using System.Buffers.Binary;
using System.Text;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// Byte-accurate console save files for the Group C readers, written from each format's layout (the same offsets the
/// readers document) — and, for <c>banner.bin</c> and the PCSX2 file card, checked against the files on the
/// maintainer's Deck (2026-10-07).
/// </summary>
internal static class ConsoleSaveFixtures
{
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>A PCSX2 file card: the PS2 superblock's format string, then nothing — enough to be recognised.</summary>
    public static byte[] Ps2FileCard()
    {
        var bytes = new byte[64 * 1024];
        Encoding.ASCII.GetBytes(MemoryCards.Ps2CardMagic + " 1.2.0.0").CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>A PS2 save's <c>icon.sys</c> whose title is <paramref name="line1"/> then <paramref name="line2"/>,
    /// Shift-JIS, written full-width as PS2 games do when <paramref name="fullWidth"/>.</summary>
    public static byte[] IconSys(string line1, string line2 = "", bool fullWidth = true)
    {
        var sjis = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
        string Wide(string s) => fullWidth
            ? new string(s.Select(c => c == ' ' ? '　' : c is > ' ' and <= '~' ? (char)(c + 0xFEE0) : c).ToArray())
            : s;
        var first = sjis.GetBytes(Wide(line1));
        var second = sjis.GetBytes(Wide(line2));
        var bytes = new byte[964];
        Encoding.ASCII.GetBytes("PS2D").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)first.Length);
        first.CopyTo(bytes, 0xC0);
        second.Take(68 - first.Length).ToArray().CopyTo(bytes, 0xC0 + first.Length);
        return bytes;
    }

    /// <summary>A raw 128 KB PS1 card holding one save per name (<c>BASLUS-00067GAME</c>).</summary>
    public static byte[] Ps1Card(params string[] saveNames)
    {
        var bytes = new byte[MemoryCards.Ps1CardSize];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'C';
        for (var i = 0; i < 15; i++)
        {
            var at = (i + 1) * 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), i < saveNames.Length ? 0x51u : 0xA0u);
            if (i < saveNames.Length) Encoding.ASCII.GetBytes(saveNames[i]).CopyTo(bytes, at + 0x0A);
        }
        return bytes;
    }

    /// <summary>A one-block GameCube save (<c>.gci</c>) of game <paramref name="code"/> by <paramref name="maker"/>, its
    /// comment naming the game <paramref name="title"/> (Shift-JIS when the code ends in J).</summary>
    public static byte[] Gci(string code, string maker, string title, string fileName = "save")
    {
        var bytes = new byte[0x40 + 0x2000];
        Encoding.ASCII.GetBytes(code).CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes(maker).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes(fileName).CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x38), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x3C), 0x100);
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(code.EndsWith('J') ? 932 : 1252)!;
        encoding.GetBytes(title).CopyTo(bytes, 0x40 + 0x100);
        Encoding.ASCII.GetBytes("Saved data").CopyTo(bytes, 0x40 + 0x100 + 32);
        return bytes;
    }

    /// <summary>A Wii save's <c>banner.bin</c>, laid out as the Deck's: <c>WIBN</c>, title UTF-16BE at 0x20, subtitle at 0x60.</summary>
    public static byte[] Banner(string title, string subtitle = "")
    {
        var bytes = new byte[0x60 + 0x40 + 0x200];
        Encoding.ASCII.GetBytes("WIBN").CopyTo(bytes, 0);
        Encoding.BigEndianUnicode.GetBytes(title).CopyTo(bytes, 0x20);
        Encoding.BigEndianUnicode.GetBytes(subtitle).CopyTo(bytes, 0x60);
        return bytes;
    }
}
