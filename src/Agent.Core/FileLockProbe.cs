namespace SaveLocker.Agent;

/// <summary>
/// "Is another process still writing into this directory?" — the half of the settle gate that
/// catches a game which is flushing but has, for the moment, stopped changing file sizes.
///
/// The two platforms answer this in completely different ways:
/// <list type="bullet">
///   <item><b>Windows</b> — ask the kernel to deny writers (<c>FileShare.Read</c>). If a writer
///   holds the file, the open fails.</item>
///   <item><b>Linux</b> — <c>FileShare</c> is <b>not enforced</b>: the open always succeeds, so
///   that probe would report "nothing is locked" every single time. We instead walk
///   <c>/proc/*/fd</c> for descriptors pointing into the directory and check whether any was
///   opened for writing — which is what <c>lsof</c> does.</item>
/// </list>
/// If a platform cannot answer, it says so (<see cref="LockProbeResult.Unavailable"/>) rather than
/// claiming the directory is quiet — a false "quiet" would archive a half-written save.
/// </summary>
public static class FileLockProbe
{
    /// <summary>Outcome of one probe. <c>Unavailable</c> is NOT the same as "nothing locked".</summary>
    public readonly record struct LockProbeResult(bool Supported, string? LockedFile)
    {
        public static LockProbeResult Unavailable => new(false, null);
        public static LockProbeResult Quiet => new(true, null);
        public static LockProbeResult Locked(string file) => new(true, file);
    }

    /// <summary>The first file another process still has open for writing under <paramref name="directory"/>.</summary>
    public static LockProbeResult FirstWriter(string directory, IEnumerable<string> relativeFiles) =>
        OperatingSystem.IsWindows()
            ? WindowsProbe(directory, relativeFiles)
            : ProcFsProbe(directory);

    // ─── Windows: FileShare.Read denies writers, so a failed open means someone holds one ───

    private static LockProbeResult WindowsProbe(string directory, IEnumerable<string> relativeFiles)
    {
        foreach (var rel in relativeFiles)
        {
            var full = Path.Combine(directory, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                using var _ = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (IOException)
            {
                return LockProbeResult.Locked(rel);
            }
            catch (UnauthorizedAccessException)
            {
                // Permissions, not a writer. Not something waiting will fix.
            }
        }
        return LockProbeResult.Quiet;
    }

    // ─── Linux: walk /proc/<pid>/fd for write handles into the directory ───

    private static LockProbeResult ProcFsProbe(string directory)
    {
        if (!Directory.Exists("/proc")) return LockProbeResult.Unavailable;

        string root;
        try { root = Path.GetFullPath(directory).TrimEnd('/') + "/"; }
        catch { return LockProbeResult.Unavailable; }

        var sawAnyProcess = false;

        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(procDir);
            if (!int.TryParse(pid, out _)) continue;

            string[] fds;
            try { fds = Directory.GetFiles($"/proc/{pid}/fd"); }
            catch (DirectoryNotFoundException) { continue; }  // process exited mid-walk
            catch (UnauthorizedAccessException) { continue; } // another user's process
            catch (IOException) { continue; }

            sawAnyProcess = true;

            foreach (var fd in fds)
            {
                string? target;
                try { target = File.ResolveLinkTarget(fd, returnFinalTarget: false)?.FullName; }
                catch { continue; }

                if (target is null || !target.StartsWith(root, StringComparison.Ordinal))
                    continue;

                if (IsWritable($"/proc/{pid}/fdinfo/{Path.GetFileName(fd)}"))
                    return LockProbeResult.Locked(target[root.Length..]);
            }
        }

        // /proc exists but we could not read a single process's descriptors (hardened kernel with
        // hidepid, or a sandbox). Report that, rather than a fictitious all-clear.
        return sawAnyProcess ? LockProbeResult.Quiet : LockProbeResult.Unavailable;
    }

    /// <summary>
    /// Read the open flags from <c>/proc/&lt;pid&gt;/fdinfo/&lt;fd&gt;</c>. The value is
    /// <b>octal</b>, and the low two bits are the access mode: O_RDONLY=0, O_WRONLY=1, O_RDWR=2.
    /// A read-only descriptor into the save dir is not a writer and must not hold the gate.
    /// </summary>
    private static bool IsWritable(string fdinfoPath)
    {
        try
        {
            foreach (var line in File.ReadLines(fdinfoPath))
            {
                if (!line.StartsWith("flags:", StringComparison.Ordinal)) continue;
                var octal = line["flags:".Length..].Trim();
                var flags = Convert.ToInt32(octal, 8);
                return (flags & 3) != 0;
            }
        }
        catch (FormatException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
}
