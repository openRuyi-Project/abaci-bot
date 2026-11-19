namespace abaci_bot.Services;

/// <summary>
/// Deep module encapsulating CI artifact retrieval, safe zip decompression,
/// unified diff extraction, and commit generation for #!RemoteAsset checksum backfilling.
/// </summary>
public interface IRemoteAssetBackfiller
{
    /// <summary>
    /// Attempts to retrieve remoteasset CI artifacts from a failed run, extracts checksum changes,
    /// and pushes backfill commits directly to the pull request branch.
    /// </summary>
    Task<bool> TryBackfillAsync(
        string owner,
        string repo,
        string branchName,
        int prNumber,
        long runId,
        CancellationToken ct = default);
}
