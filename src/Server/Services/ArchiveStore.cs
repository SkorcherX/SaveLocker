namespace SaveLocker.Server.Services;

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

    /// <summary>
    /// The store path persisted in <c>SaveVersion.ArchivePath</c>. Always '/'-separated, never
    /// Path.Combine: this string goes into the DATABASE, so it outlives the OS that wrote it.
    /// A Windows-hosted server writing "gameid\versionid.zip" produces rows that the Linux
    /// (Docker) server cannot resolve — a backslash is a legal filename character there, not a
    /// separator — and every archive silently becomes undownloadable, which the agent reports
    /// as the very convincing lie "server has no saves yet".
    /// </summary>
    public string RelativePath(Guid gameId, Guid versionId) =>
        $"{gameId:N}/{versionId:N}.zip";

    /// <summary>
    /// Resolve a stored path against the archive root, accepting either separator: rows written
    /// by an older Windows-hosted server still carry backslashes.
    /// </summary>
    public string FullPath(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));

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
