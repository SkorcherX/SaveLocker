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

    /// <summary>A five-digit id whose serial (<paramref name="prefix"/> + id) the bundled Redump table does not
    /// know, so naming falls back to the save's own title — what the Group C/D tests assert. A plain random id
    /// eventually collides with a real release: CI rolled SLUS-21640, which is Rugby 08.</summary>
    public static string UnknownSerial(string system, string prefix, int min = 10000, int max = 99999)
    {
        while (true)
        {
            var id = Random.Shared.Next(min, max).ToString();
            if (ConsoleTitles.For(system, prefix + id) is null) return id;
        }
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

    /// <summary>
    /// A <c>PARAM.SFO</c> holding <paramref name="entries"/> — a string is UTF-8 (<c>0x0204</c>, NUL-terminated, padded
    /// to 4), an int an int32 (<c>0x0404</c>) — laid out as the PS3 save RPCS3 wrote on the maintainer's Deck: header,
    /// 16-byte index entries, key table (padded to 4), data table.
    /// </summary>
    public static byte[] ParamSfo(params (string Key, object Value)[] entries)
    {
        var keys = new List<byte>();
        var data = new List<byte>();
        var index = new List<byte>();
        foreach (var (key, value) in entries)
        {
            var bytes = value is int n ? BitConverter.GetBytes(n) : Encoding.UTF8.GetBytes((string)value + "\0");
            var max = (bytes.Length + 3) / 4 * 4;
            var entry = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)keys.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), (ushort)(value is int ? 0x0404 : 0x0204));
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)max);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), (uint)data.Count);
            index.AddRange(entry);
            keys.AddRange(Encoding.ASCII.GetBytes(key + "\0"));
            data.AddRange(bytes);
            data.AddRange(new byte[max - bytes.Length]);
        }
        while (keys.Count % 4 != 0) keys.Add(0);
        var keyTable = 20 + index.Count;
        var header = new byte[20];
        Encoding.ASCII.GetBytes("\0PSF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x101);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(keyTable + keys.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)entries.Length);
        return [.. header, .. index, .. keys, .. data];
    }
    /// <summary>A 3DS cartridge image (<c>.3ds</c>/<c>.cci</c>) with just its headers: <c>NCSD</c>, the first partition at
    /// 0x4000 (as on the Deck's ROMs), and that partition's <c>NCCH</c> with its program ID and product code.</summary>
    public static byte[] Ncsd(ulong programId, string productCode)
    {
        var bytes = new byte[0x4000 + 0x200];
        Encoding.ASCII.GetBytes("NCSD").CopyTo(bytes, 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x108), programId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x120), 0x4000 / 0x200);
        Ncch(programId, productCode).CopyTo(bytes, 0x4000);
        return bytes;
    }

    /// <summary>An NCCH header (a <c>.cxi</c>, or an installed game's <c>content/*.app</c>).</summary>
    public static byte[] Ncch(ulong programId, string productCode)
    {
        var bytes = new byte[0x200];
        Encoding.ASCII.GetBytes("NCCH").CopyTo(bytes, 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x108), programId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x118), programId);
        Encoding.ASCII.GetBytes(productCode).CopyTo(bytes, 0x150);
        return bytes;
    }
}
