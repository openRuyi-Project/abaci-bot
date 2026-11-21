namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the automated upstream package update background service.
/// </summary>
public class PackageUpdateOptions
{
    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    public string TargetRepository { get; set; } = "openRuyi-Project/openRuyi";

    public string BotLogin { get; set; } = "aqua-rium";

    /// <summary>
    /// Optional Personal Access Token for the bot. If empty, falls back to GitHub App authentication.
    /// Strictly scoped to target repository contents and pull requests.
    /// </summary>
    public string BotPat { get; set; } = string.Empty;

    public string BranchPrefix { get; set; } = "autoupdate/";

    public int CheckIntervalHours { get; set; } = 12;

    public LookoutUpdateOptions Lookout { get; set; } = new();

    public RustCratePollerOptions RustCratePoller { get; set; } = new();

    public ObsOptions Obs { get; set; } = new();

    public bool AllowCrossMajorVersion { get; set; }

    public bool SkipPackagesWithPatches { get; set; } = true;

    public int MaxCandidatesPerSweep { get; set; } = 5;

    public int MaxConcurrentPullRequests { get; set; } = 5;

    /// <summary>
    /// Number of days an open PR whose CI failed can remain inactive before being automatically closed to free capacity.
    /// </summary>
    public int FailedPrTtlDays { get; set; } = 7;

    public int CooldownHours { get; set; } = 72;

    public int MaxConsecutiveFailures { get; set; } = 3;

    public bool IssueOnGivingUp { get; set; } = true;

    public int GiveUpIssueCooldownDays { get; set; } = 30;

    public string GiveUpIssueLabel { get; set; } = Services.GitHubLabels.StatusStaleExempt;

    public int MinRateLimitRemaining { get; set; } = 500;

    public int RequestThrottleMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Maximum repository tarball archive size in bytes accepted by <c>GitHubSpecCatalog</c> before falling back to the Git Tree API.
    /// Set to 0 to disable the size cap. Default: 104857600 (100 MiB).
    /// </summary>
    public long MaxArchiveBytes { get; set; } = 100L * 1024 * 1024;
}

public class LookoutUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://lookout.openruyi.cn";

    public int StaleThresholdHours { get; set; } = 48;

    /// <summary>
    /// Optional path to the JSON file used to persist first-observed timestamps for Lookout candidates across restarts.
    /// When empty, defaults to <c>lookout-first-seen.json</c> under the local application data directory.
    /// </summary>
    public string FirstSeenStatePath { get; set; } = string.Empty;
}

public class RustCratePollerOptions
{
    public bool Enabled { get; set; } = true;

    public string IndexBaseUrl { get; set; } = "https://index.crates.io";

    public int MaxCratesPerSweep { get; set; } = 60;

    public int RequestDelayMilliseconds { get; set; } = 300;
}

public class ObsOptions
{
    public string BaseUrl { get; set; } = "https://build.openruyi.cn";

    public string Project { get; set; } = "openruyi";

    public string TargetRepository { get; set; } = "riscv64";

    public string TargetArch { get; set; } = "riscv64";

    public bool RequireObsSuccessAfterMerge { get; set; }
}
