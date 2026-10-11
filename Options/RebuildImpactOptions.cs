namespace abaci_bot.Options;

/// <summary>
/// Configuration options for RPM dependency DAG building and rebuild impact gating.
/// </summary>
public class RebuildImpactOptions
{
    /// <summary>
    /// Enables or disables the rebuild impact analysis and gating module.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Remote URL of the repository metadata primary.xml.gz file.
    /// </summary>
    public string? PrimaryXmlUrl { get; set; }

    /// <summary>
    /// Path to a local primary.xml or primary.xml.gz cache or fixture file.
    /// </summary>
    public string? LocalCachePath { get; set; }

    /// <summary>
    /// Background refresh interval in hours for metadata re-download and DAG reconstruction.
    /// </summary>
    public double RefreshIntervalHours { get; set; } = 6;

    /// <summary>
    /// Calculation mode: "Transitive" (transitive closure) or "Direct" (direct dependents only).
    /// </summary>
    public string CalculationMode { get; set; } = "Transitive";

    /// <summary>
    /// Maximum recursive traversal depth for transitive closure calculation (null for unlimited).
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Threshold of affected downstream packages to block pull requests on target branch.
    /// </summary>
    public int BlockThreshold { get; set; } = 1001;

    /// <summary>
    /// Target branch name or classification to apply merge blocking (e.g., "Rolling").
    /// </summary>
    public string TargetBranchToBlock { get; set; } = "Rolling";
}
