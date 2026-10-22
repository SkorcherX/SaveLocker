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
    public static Task<bool> WaitForQuietAsync(
        string directory,
        IEnumerable<string>? excludeGlobs,
        TimeSpan quietPeriod,
        TimeSpan maxWait,
        Action<string>? log = null,
        CancellationToken ct = default) =>
        WaitForQuietAsync(new[] { SaveRoot.Primary(directory) }, excludeGlobs, quietPeriod, maxWait, log, ct);

    /// <summary>
    /// <see cref="WaitForQuietAsync(string, IEnumerable{string}?, TimeSpan, TimeSpan, Action{string}?, CancellationToken)"/>
    /// over every real folder of a game at once: a save that writes two folders is quiet only when
    /// both are. Pass real folders only — a shadow is the agent's own copy and nothing else writes it.
    /// </summary>
    public static Task<bool> WaitForQuietAsync(
        IReadOnlyList<SaveRoot> roots,
        IEnumerable<string>? excludeGlobs,
        TimeSpan quietPeriod,
        TimeSpan maxWait,
        Action<string>? log = null,
        CancellationToken ct = default) =>
        WaitForQuietAsync(roots, excludeGlobs, quietPeriod, maxWait, FileLockProbe.FirstWriter, log, ct);

    /// <summary>
    /// The gate with its lock probe supplied. Tests pin the probe: on a CI runner an antivirus or
    /// indexer holding a just-written file without sharing reads looks exactly like a writer (see
    /// <see cref="FileLockProbe"/>), which says nothing about whether the fingerprint half works.
    /// </summary>
    internal static async Task<bool> WaitForQuietAsync(
        IReadOnlyList<SaveRoot> roots,
        IEnumerable<string>? excludeGlobs,
        TimeSpan quietPeriod,
        TimeSpan maxWait,
        Func<IReadOnlyList<SaveRoot>, IReadOnlyList<SaveArchive.SaveFile>, FileLockProbe.LockProbeResult> probeWriters,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (quietPeriod <= TimeSpan.Zero || !roots.Any(r => Directory.Exists(r.Directory)))
            return true;

        var globs = excludeGlobs?.ToList();
        var pollMs = Math.Clamp(quietPeriod.TotalMilliseconds / 5, 250, 2000);
        var poll = TimeSpan.FromMilliseconds(pollMs);

        // MONOTONIC, not wall-clock. A Steam Deck suspends constantly, and mid-settle is exactly when
        // it happens — the user finishes a game and puts it to sleep. DateTime.UtcNow counts the hours
        // spent suspended as elapsed, so on resume the max-wait has "expired" and the gate gives up
        // instantly, publishing a save that may still have been mid-flush when the lid closed. It
        // would also see the quiet period as satisfied without ever having observed quiet.
        // Stopwatch runs on the monotonic clock, which does not advance while the machine is asleep,
        // so the gate measures only time it was actually awake and watching.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        string? lastPrint = null;
        var stableSince = TimeSpan.Zero;
        var waited = false;
        var warnedNoLockProbe = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var files = SaveArchive.ListSaveFiles(roots, globs);
            var print = Fingerprint(files);
            var probe = probeWriters(roots, files);
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
                if (clock.Elapsed - stableSince >= quietPeriod)
                {
                    if (waited) log?.Invoke("save files settled.");
                    return true;
                }
            }
            else
            {
                stableSince = clock.Elapsed;
                lastPrint = print;
            }

            if (clock.Elapsed >= maxWait)
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

    /// <summary>Cheap snapshot of the folders' observable state — no file contents read.</summary>
    private static string Fingerprint(IReadOnlyList<SaveArchive.SaveFile> files)
    {
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file.FullPath);
                sb.Append(file.ArchiveName).Append('|')
                  .Append(info.Length).Append('|')
                  .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
            catch (IOException)
            {
                // Vanished mid-scan — a change in itself, so let the next poll see a new print.
                sb.Append(file.ArchiveName).Append("|?\n");
            }
        }
        return sb.ToString();
    }
}
