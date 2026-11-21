namespace abaci_bot.Services;

/// <summary>
/// Narrow gateway for downloading repository archive snapshots in a single GitHub API request.
/// Keeps <see cref="IGitHubService"/> free of archive transport concerns.
/// </summary>
public interface IRepositoryArchiveGateway
{
    /// <summary>
    /// Downloads the gzipped tarball snapshot for <paramref name="owner"/>/<paramref name="repo"/> at <paramref name="reference"/>.
    /// Returns <see langword="null"/> if the archive cannot be retrieved.
    /// </summary>
    Task<byte[]?> GetRepositoryTarballAsync(string owner, string repo, string reference, CancellationToken ct = default);
}
