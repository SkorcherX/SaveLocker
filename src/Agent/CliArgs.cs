namespace LocalGameSync.Agent;

/// <summary>
/// Minimal command-line parser: first non-option token is the command, the rest
/// are <c>--key value</c> / <c>--flag</c> options or bare positionals.
/// </summary>
public static class CliArgs
{
    public static (string? command, Dictionary<string, string> opts, List<string> positionals) Parse(string[] args)
    {
        string? command = null;
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    opts[key] = args[++i];
                else
                    opts[key] = "true"; // boolean flag
            }
            else if (command is null)
            {
                command = a.ToLowerInvariant();
            }
            else
            {
                positionals.Add(a);
            }
        }

        return (command, opts, positionals);
    }
}
