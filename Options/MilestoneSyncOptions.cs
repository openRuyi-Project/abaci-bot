namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the automated milestone assignment module and background backfill sweep service.
/// </summary>
public class MilestoneSyncOptions
{
    /// <summary>
    /// Whether automated milestone synchronization on merged pull requests is enabled.
    /// Default: false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true, simulated operations are logged without writing milestone assignments or creating milestones on GitHub.
    /// Default: true.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Repository whitelist for milestone synchronization formatted as 'owner/repo'.
    /// If empty, milestone sync is disabled globally to prevent unintended writes across untargeted repositories.
    /// Default: empty list.
    /// </summary>
    public List<string> RepositoryAllowList { get; set; } = new();

    /// <summary>
    /// Whether the bot is permitted to create missing monthly milestones (YYYY.MM) on the remote repository.
    /// Default: false.
    /// </summary>
    public bool CreationEnabled { get; set; }

    /// <summary>
    /// The default due day of the month used when creating a new monthly milestone (e.g. 25).
    /// Bound to 1..28.
    /// Default: 25.
    /// </summary>
    public int DueDayOfMonth { get; set; } = 25;

    /// <summary>
    /// Historical window in days for the background service to backfill merged pull requests lacking a milestone.
    /// Bound to 1..365.
    /// Default: 30.
    /// </summary>
    public int BackfillDays { get; set; } = 30;

    /// <summary>
    /// Execution interval in hours for the background milestone backfill sweep service.
    /// Bound to 1..168.
    /// Default: 24.
    /// </summary>
    public int SweepIntervalHours { get; set; } = 24;

    /// <summary>
    /// IANA (or system) timezone identifier applied to merge and close timestamps when determining the YYYY.MM
    /// release milestone month. Handles UTC offsets and DST transitions; leap seconds are not modeled by .NET or GitHub.
    /// Empty or unrecognized IDs fail startup validation.
    /// Default: "Asia/Shanghai".
    /// </summary>
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
}
