namespace SaveLocker.Agent;

/// <summary>
/// Reader for Steam's <b>text</b> KeyValues format, used by
/// <c>steamapps\libraryfolders.vdf</c> and <c>appmanifest_*.acf</c>. The grammar
/// is just quoted-string tokens and <c>{ }</c> blocks:
/// <code>
/// "AppState"
/// {
///     "appid"      "427520"
///     "name"       "Factorio"
///     "installdir" "Factorio"
/// }
/// </code>
/// We parse it into the same <see cref="SteamVdf.VdfObject"/> tree the binary
/// reader produces, so callers can treat both formats uniformly.
/// </summary>
public static class SteamTextVdf
{
    public static SteamVdf.VdfObject Parse(string text)
    {
        var pos = 0;
        var root = new SteamVdf.VdfObject();
        // A text VDF file is a sequence of "key" { … } pairs at the top level.
        while (true)
        {
            var key = NextToken(text, ref pos);
            if (key is null) break;
            var value = NextToken(text, ref pos)
                        ?? throw new InvalidDataException("Unexpected end of VDF after key.");
            root.Items[key] = value == "{" ? ReadObject(text, ref pos) : value;
        }
        return root;
    }

    private static SteamVdf.VdfObject ReadObject(string text, ref int pos)
    {
        var obj = new SteamVdf.VdfObject();
        while (true)
        {
            var key = NextToken(text, ref pos)
                      ?? throw new InvalidDataException("Unterminated VDF object.");
            if (key == "}") break;
            var value = NextToken(text, ref pos)
                        ?? throw new InvalidDataException("Unexpected end of VDF after key.");
            obj.Items[key] = value == "{" ? ReadObject(text, ref pos) : value;
        }
        return obj;
    }

    /// <summary>
    /// Return the next token: a quoted string (contents only), or a bare
    /// <c>{</c>/<c>}</c> brace. Null at end of input. Skips whitespace and
    /// <c>//</c> line comments.
    /// </summary>
    private static string? NextToken(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            if (c == '{' || c == '}') { pos++; return c.ToString(); }
            if (c == '"')
            {
                pos++; // opening quote
                var sb = new System.Text.StringBuilder();
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length)
                    {
                        pos++;
                        sb.Append(text[pos] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            _ => text[pos] // \\ and \" collapse to the literal char
                        });
                    }
                    else sb.Append(text[pos]);
                    pos++;
                }
                pos++; // closing quote
                return sb.ToString();
            }
            // Bare unquoted token (rare in these files) — read to whitespace.
            var start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])
                   && text[pos] != '{' && text[pos] != '}') pos++;
            return text[start..pos];
        }
        return null;
    }
}
