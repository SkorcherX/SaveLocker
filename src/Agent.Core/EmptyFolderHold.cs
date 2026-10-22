using System.Text.Json;

namespace SaveLocker.Agent;

/// <summary>
/// Games held because every save file vanished from this machine at once: an uninstaller, a
/// game resetting its saves, or the wrong folder cleared. Pushing that empty folder would make it the head, and every
/// other machine's next pull would delete its saves, so the game sits out every automatic sync here until someone
/// chooses — <b>Delete everywhere</b> (a forced push) or <b>Restore here</b> (a forced pull) — or the files come back.
/// On disk beside the config: the daemon, the tray and the Linux launch wrapper all sync, and each must see the
/// others' holds.
/// </summary>
public sealed class EmptyFolderHoldStore
{
    public sealed class Entry
    {
        public Guid GameId { get; set; }
        /// <summary>The last version this machine synced, which still has the files.</summary>
        public Guid? LastVersionId { get; set; }
        public DateTimeOffset HeldAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _stateDir;

    public static EmptyFolderHoldStore For(AgentConfig config) => new(Path.Combine(config.StateDir, "emptied-holds.json"));

    public EmptyFolderHoldStore(string path)
    {
        _path = path;
        _stateDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
    }

    public IReadOnlyList<Entry> Read() => ReadDisk();

    public Entry? For(Guid gameId) => ReadDisk().FirstOrDefault(e => e.GameId == gameId);

    public void Hold(Guid gameId, Guid? lastVersionId) => Mutate(list =>
    {
        list.RemoveAll(e => e.GameId == gameId);
        list.Add(new Entry { GameId = gameId, LastVersionId = lastVersionId, HeldAt = DateTimeOffset.UtcNow });
    });

    /// <summary>True when there was a hold to lift.</summary>
    public bool Clear(Guid gameId)
    {
        if (For(gameId) is null) return false;
        Mutate(list => list.RemoveAll(e => e.GameId == gameId));
        return true;
    }

    private void Mutate(Action<List<Entry>> change)
    {
        try
        {
            using var guard = AgentStateLock.Acquire("emptied-holds", _stateDir);
            var list = ReadDisk();
            change(list);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(list, JsonOpts), restrictPermissions: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AgentLogger.Log($"Could not record an emptied-folder hold: {ex.Message}");
        }
    }

    private List<Entry> ReadDisk()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), JsonOpts) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}
