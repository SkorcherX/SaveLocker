namespace SaveLocker.Agent;

/// <summary>How big a folder is, by plain enumeration — the agent UI's "save size here" and the Deck's rows.</summary>
public static class FolderSize
{
    /// <summary>Bytes under <paramref name="dir"/> (symlinks not followed, unreadable entries skipped); 0 for a
    /// missing or unreadable one. No hashing, so it is cheap next to a sync-status — but it is still a walk,
    /// so callers ask on opening a page, never per frame or on a timer.</summary>
    public static long Of(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            return new DirectoryInfo(dir).EnumerateFiles("*", opts).Sum(f => f.Length);
        }
        catch { return 0; }
    }
}
