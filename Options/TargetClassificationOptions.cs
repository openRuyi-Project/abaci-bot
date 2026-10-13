namespace abaci_bot.Options;

/// <summary>
/// Configuration options for PR target release branch classification.
/// </summary>
public class TargetClassificationOptions
{
    /// <summary>
    /// Repositories dedicated exclusively to LTS releases (e.g. "openRuyi-LTS").
    /// PRs targeted to these repositories are always tagged with Target: LTS.
    /// </summary>
    public List<string> LtsRepositories { get; set; } = new();

    /// <summary>
    /// Base branch name prefixes that indicate LTS tracking (e.g. "lts-", "lts/").
    /// Default matches branches like "lts-24.03".
    /// </summary>
    public List<string> LtsBranchPrefixes { get; set; } = new() { "lts-" };

    /// <summary>
    /// Base branch names that represent the standard Rolling track.
    /// </summary>
    public List<string> RollingBranches { get; set; } = new() { "main", "master", "rolling" };

    /// <summary>
    /// Fallback target classification if base branch does not match rolling or LTS rules.
    /// Can be "Rolling", "LTS", or null to keep silent for unknown branches.
    /// </summary>
    public string? DefaultTarget { get; set; } = "Rolling";
}
