using System.Buffers.Binary;
using System.Text;

namespace SaveLocker.Agent;

/// <summary>
/// <c>PARAM.SFO</c>, the one metadata file the PSP, PS3, Vita and PS4 share (tasks/emulator-saves Group D): every save
/// of the PSP and the PS3 carries one, a PS4 save and a Vita game one in <c>sce_sys/param.sfo</c>. Little-endian:
/// <c>\0PSF</c>, version, the key table's and the data table's offsets and the entry count, then 16 bytes per entry —
/// key offset (u16), format (u16: <c>0x0004</c> UTF-8 without a terminator, <c>0x0204</c> UTF-8, <c>0x0404</c> an
/// int32), length, maximum length and data offset (u32 each). Checked against a PS3 save RPCS3 wrote on the
/// maintainer's Deck (2026-10-11).
/// </summary>
public static class ParamSfo
{
    // Saves keep it to a few KB (a PS3 save's is 2.3 KB); anything bigger is no PARAM.SFO.
    private const int MaxBytes = 64 * 1024;

    /// <summary>Its text and number entries by key, or null when <paramref name="path"/> is not a PARAM.SFO.</summary>
    public static IReadOnlyDictionary<string, object>? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;
            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static IReadOnlyDictionary<string, object>? Parse(ReadOnlySpan<byte> sfo)
    {
        if (sfo.Length < 20 || sfo[0] != 0 || sfo[1] != 'P' || sfo[2] != 'S' || sfo[3] != 'F') return null;
        var keyTable = BinaryPrimitives.ReadUInt32LittleEndian(sfo[8..]);
        var dataTable = BinaryPrimitives.ReadUInt32LittleEndian(sfo[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(sfo[16..]);
        if (count > 256 || 20 + count * 16 > sfo.Length || keyTable > sfo.Length || dataTable > sfo.Length) return null;

        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var e = sfo.Slice(20 + i * 16, 16);
            var keyAt = keyTable + BinaryPrimitives.ReadUInt16LittleEndian(e);
            var format = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            var dataAt = dataTable + (long)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (keyAt >= sfo.Length || dataAt + length > sfo.Length) return null;
            var keyEnd = sfo[(int)keyAt..].IndexOf((byte)0);
            if (keyEnd <= 0) return null;
            var key = Encoding.ASCII.GetString(sfo.Slice((int)keyAt, keyEnd));
            var data = sfo.Slice((int)dataAt, (int)length);
            object? value = format switch
            {
                0x0404 when length >= 4 => BinaryPrimitives.ReadInt32LittleEndian(data),
                0x0004 or 0x0204 => Utf8(data),
                _ => null,
            };
            if (value is not null) entries[key] = value;
        }
        return entries;
    }

    /// <summary>The game's title: <c>TITLE</c> (PSP, PS3, Vita), else <c>MAINTITLE</c> (a PS4 save), tidied — a PS3
    /// title often breaks its line in two, and carries its trademark sign ("Heavenly Sword™" on the Deck). Null when
    /// the file has neither.</summary>
    public static string? Title(string path) => Read(path) is { } sfo ? Title(sfo) : null;

    public static string? Title(IReadOnlyDictionary<string, object> sfo)
    {
        foreach (var key in new[] { "TITLE", "MAINTITLE" })
            if (sfo.TryGetValue(key, out var v) && v is string s
                && ConsoleText.Tidy(s.Replace("™", "").Replace("®", "").Replace("©", "")) is { Length: > 0 } title)
                return title;
        return null;
    }

    private static string Utf8(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
    }
}
