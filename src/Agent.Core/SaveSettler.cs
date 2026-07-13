using System.Text;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Holds an automatic push back until the game has actually finished writing.
/// A process-exit event does not mean the save is on disk: many games flush for
/// several seconds after the window closes, so archiving on the exit event alone
/// can capture a half-written save and publish it as a version.
///
/// The gate waits until BOTH hold for a quiet period:
///   • the directory fingerprint (file set + sizes + write times) stops changing, and
///   • no file in it is still open for writing by another process (<see cref="FileLockProbe"/>).
///
/// A writer that opened its save with FileShare.ReadWrite is invisible to the lock probe —
/// the fingerprint is what catches that case, which is why both run together. Where the lock
/// probe cannot answer at all, the gate says so in the log and settles on the fingerprint.
/// </summary>
public static class SaveSettler
{
    /// <summary>
    /// Wait for <paramref name="directory"/> to go quiet. Returns true if it settled,
    /// false if <paramref name="maxWait"/> elapsed first — the caller still pushes in that
    /// case, because a stale-but-complete version beats no version at all.
    /// </summary>
    public static async Task<bool> WaitForQuietAsync(
        string directory,
        IEnumerable<string>? excludeGlobs,
        TimeSpan quietPeriod,
        TimeSpan maxWait,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (quietPeriod <= TimeSpan.Zero || !Directory.Exists(directory))
            return true;

        var globs = excludeGlobs?.ToList();
        var pollMs = Math.Clamp(quietPeriod.TotalMilliseconds / 5, 250, 2000);
        var poll = TimeSpan.FromMilliseconds(pollMs);
        var deadline = DateTime.UtcNow + maxWait;

        string? lastPrint = null;
        var stableSince = DateTime.UtcNow;
        var waited = false;
        var warnedNoLockProbe = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var print = Fingerprint(directory, globs);
            var probe = FileLockProbe.FirstWriter(directory, SaveArchive.ListFiles(directory, globs));
            var locked = probe.LockedFile;

            // A probe that cannot answer must not read as "quiet" — say so once, then lean on the
            // fingerprint alone (the load-bearing half) rather than pretending we checked.
            if (!probe.Supported && !warnedNoLockProbe)
            {
                warnedNoLockProbe = true;
                log?.Invoke("open-file detection unavailable on this platform — " +
                            "settling on the file fingerprint alone.");
            }

            if (locked is null && print == lastPrint)
            {
                if (DateTime.UtcNow - stableSince >= quietPeriod)
                {
                    if (waited) log?.Invoke("save files settled.");
                    return true;
                }
            }
            else
            {
                stableSince = DateTime.UtcNow;
                lastPrint = print;
            }

            if (DateTime.UtcNow >= deadline)
            {
                log?.Invoke(locked is not null
                    ? $"still writing after {maxWait.TotalSeconds:0}s (locked: {locked}) — pushing anyway."
                    : $"still writing after {maxWait.TotalSeconds:0}s — pushing anyway.");
                return false;
            }

            waited = true;
            await Task.Delay(poll, ct);
        }
    }

    /// <summary>Cheap snapshot of the directory's observable state — no file contents read.</summary>
    private static string Fingerprint(string directory, IEnumerable<string>? excludeGlobs)
    {
        var sb = new StringBuilder();
        foreach (var rel in SaveArchive.ListFiles(directory, excludeGlobs))
        {
            var full = Path.Combine(directory, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var info = new FileInfo(full);
                sb.Append(rel).Append('|')
                  .Append(info.Length).Append('|')
                  .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
            catch (IOException)
            {
                // Vanished mid-scan — a change in itself, so let the next poll see a new print.
                sb.Append(rel).Append("|?\n");
            }
        }
        return sb.ToString();
    }
}
