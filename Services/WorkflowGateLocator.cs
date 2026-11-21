using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Deep module abstracting workflow gate run selection and matching.
/// Locates exact workflow runs (such as remoteasset) by matching workflow filename/path and Git commit SHA.
/// </summary>
public static class WorkflowGateLocator
{
    /// <summary>
    /// Finds the most recent workflow run for the remoteasset workflow on a specific branch,
    /// matching against PR head commit SHA if available.
    /// </summary>
    public static WorkflowRun? FindRemoteAssetRun(IEnumerable<WorkflowRun> runs, string? headSha = null)
    {
        if (runs == null)
            return null;

        return runs
            .Where(r => (r.Name?.Contains("RemoteAsset", StringComparison.OrdinalIgnoreCase) == true ||
                         r.Path?.Contains("remoteasset", StringComparison.OrdinalIgnoreCase) == true) &&
                        (string.IsNullOrWhiteSpace(headSha) || string.Equals(r.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();
    }
}
