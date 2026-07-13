using System.Text;

namespace SaveLocker.Agent;

/// <summary>
/// Minimal reader for Steam's <b>binary</b> KeyValues format, used by
/// <c>userdata\&lt;id&gt;\config\shortcuts.vdf</c> (the non-Steam game shortcuts).
/// There is no well-maintained NuGet for this, and the format is tiny:
/// <list type="bullet">
///   <item><c>0x00</c> — nested object: NUL-terminated key, then child nodes.</item>
///   <item><c>0x01</c> — string:        NUL-terminated key, then NUL-terminated UTF-8 value.</item>
///   <item><c>0x02</c> — int32:         NUL-terminated key, then 4 bytes little-endian.</item>
///   <item><c>0x08</c> — end of the current object.</item>
/// </list>
/// We ignore the rarer scalar types (float/int64/wide-string) — shortcuts.vdf
/// only ever uses the four above. A node is a <see cref="VdfObject"/> (a
/// case-insensitive map) or a leaf <see cref="string"/>/<see cref="int"/>.
/// </summary>
public static class SteamVdf
{
    /// <summary>A binary-VDF object: a case-insensitive map of keys to child nodes.</summary>
    public sealed class VdfObject
    {
        public Dictionary<string, object> Items { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public VdfObject? Object(string key) => Items.GetValueOrDefault(key) as VdfObject;
        public string? String(string key) => Items.GetValueOrDefault(key) as string;
        public int? Int(string key) => Items.GetValueOrDefault(key) is int i ? i : null;

        /// <summary>Child objects in insertion order (the "0","1",… indexed entries).</summary>
        public IEnumerable<VdfObject> Children => Items.Values.OfType<VdfObject>();
    }

    /// <summary>Parse a binary-VDF byte buffer into its root object.</summary>
    public static VdfObject Parse(byte[] data)
    {
        var pos = 0;
        // The file is a single top-level object node: 0x00, "shortcuts", <children>.
        var type = data[pos++];
        if (type != 0x00)
            throw new InvalidDataException($"Expected a root object (0x00), got 0x{type:X2}.");
        ReadCString(data, ref pos); // root key, e.g. "shortcuts" — discarded.
        return ReadObject(data, ref pos);
    }

    /// <summary>Read child nodes until the 0x08 end marker.</summary>
    private static VdfObject ReadObject(byte[] data, ref int pos)
    {
        var obj = new VdfObject();
        while (true)
        {
            var type = data[pos++];
            if (type == 0x08) break; // end of this object

            var key = ReadCString(data, ref pos);
            obj.Items[key] = type switch
            {
                0x00 => ReadObject(data, ref pos),
                0x01 => ReadCString(data, ref pos),
                0x02 => ReadInt32(data, ref pos),
                _ => throw new InvalidDataException(
                    $"Unsupported VDF node type 0x{type:X2} at offset {pos - 1}.")
            };
        }
        return obj;
    }

    private static string ReadCString(byte[] data, ref int pos)
    {
        var start = pos;
        while (pos < data.Length && data[pos] != 0x00) pos++;
        var s = Encoding.UTF8.GetString(data, start, pos - start);
        pos++; // skip the NUL terminator
        return s;
    }

    private static int ReadInt32(byte[] data, ref int pos)
    {
        var v = BitConverter.ToInt32(data, pos);
        pos += 4;
        return v;
    }
}
