namespace SaveLocker.Agent;

/// <summary>
/// Whole-file writes that another process can never observe half-finished.
///
/// <c>File.WriteAllText</c> truncates and then writes, so a reader that opens the file in that
/// window sees an empty or partial document. Every one of the agent's state files is read by a
/// second process — the daemon and the Steam launch wrapper share all of them — and every one of
/// them is parsed as JSON, so a torn read is not a glitch: it is a config that fails to
/// deserialize and silently falls back to defaults, losing the machine's API key and game list.
///
/// Writing to a temp file and renaming makes the swap atomic: a reader sees either the whole old
/// file or the whole new one.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, bool restrictPermissions = false)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // The temp name carries the PID: two processes racing to rewrite the same file must not
        // collide on the intermediate, or one truncates the other's half-written temp and renames
        // the result into place.
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");

        try
        {
            if (restrictPermissions && !OperatingSystem.IsWindows())
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };
                using var stream = new FileStream(temp, options);
                using var writer = new StreamWriter(stream);
                writer.Write(contents);
            }
            else
            {
                File.WriteAllText(temp, contents);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }
}
