namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the active CVE vulnerability watch background service.
/// </summary>
public class CveActiveWatchOptions
{
    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    public string TargetRepository { get; set; } = "openRuyi-Project/openRuyi";

    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// Number of package entries per OSV batch query (OSV API supports up to 1000).
    /// </summary>
    public int OsvBatchSize { get; set; } = 1000;

    /// <summary>
    /// Total packages to query per sweep (0 = scan full catalog, which takes ~7 batch calls for 6290 packages).
    /// </summary>
    public int PackagesPerSweep { get; set; }

    /// <summary>
    /// Daily cap on queries dispatched to NVD when OSV lacks CVSS metrics.
    /// </summary>
    public int MaxNvdQueriesPerDay { get; set; } = 200;

    /// <summary>
    /// Inter-request delay for unauthenticated NVD queries to avoid HTTP 429.
    /// </summary>
    public int NvdRequestDelayMilliseconds { get; set; } = 6500;

    /// <summary>
    /// Maximum concurrent HTTP requests dispatched when resolving OSV vulnerability details.
    /// </summary>
    public int MaxConcurrentOsvRequests { get; set; } = 8;

    public string IssueLabel { get; set; } = "Vulnerability: Detected";

    public int CacheExpirationHours { get; set; } = 168; // 7 days
}
