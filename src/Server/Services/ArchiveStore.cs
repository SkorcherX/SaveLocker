namespace LocalGameSync.Server.Services;

/// <summary>
/// Stores save-game archive files on disk (an unRAID share volume in production).
/// Layout: {root}/{gameId}/{versionId}.zip
/// </summary>
public sealed class ArchiveStore
{
    private readonly string _root;

    public ArchiveStore(IConfiguration config)
    {
        _root = config["Storage:ArchiveRoot"]
                ?? Path.Combine(AppContext.BaseDirectory, "data", "archives");
        Directory.CreateDirectory(_root);
    }

    public string RelativePath(Guid gameId, Guid versionId) =>
        Path.Combine(gameId.ToString("N"), $"{versionId:N}.zip");

    public string FullPath(string relativePath) => Path.Combine(_root, relativePath);

    /// <summary>Persist an uploaded archive stream and return its relative store path.</summary>
    public async Task<(string relativePath, long size)> SaveAsync(
        Guid gameId, Guid versionId, Stream content, CancellationToken ct = default)
    {
        var rel = RelativePath(gameId, versionId);
        var full = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await using (var fs = File.Create(full))
            await content.CopyToAsync(fs, ct);

        return (rel, new FileInfo(full).Length);
    }

    public Stream OpenRead(string relativePath) => File.OpenRead(FullPath(relativePath));

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));

    public void Delete(string relativePath)
    {
        var full = FullPath(relativePath);
        if (File.Exists(full)) File.Delete(full);
    }
}
