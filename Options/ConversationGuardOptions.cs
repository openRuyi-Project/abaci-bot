namespace abaci_bot.Options;

/// <summary>
/// Configuration options for conversation locking, slash command moderation, and inactive PR sweep.
/// </summary>
public class ConversationGuardOptions
{
    /// <summary>
    /// Whether conversation guard slash command moderation (/lock, /unlock, /hide, /archive) is enabled.
    /// Default: false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true, moderation actions are logged without applying lock, minimize, or archive on GitHub.
    /// Default: true.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Repository whitelist for conversation guard moderation formatted as 'owner/repo'.
    /// If empty, conversation moderation is disabled globally.
    /// Default: empty list.
    /// </summary>
    public List<string> RepositoryAllowList { get; set; } = new();

    /// <summary>
    /// Independent safety switch for pull request level archiving (/archive).
    /// When false, /archive commands are rejected to prevent unintended 404 access restrictions.
    /// Default: false.
    /// </summary>
    public bool ArchiveEnabled { get; set; }

    /// <summary>
    /// Inactivity threshold in days based on closed_at after which closed PRs are automatically locked to prevent post-closure harassment.
    /// Bound to 7..365.
    /// Default: 60.
    /// </summary>
    public int InactiveCloseDays { get; set; } = 60;

    /// <summary>
    /// Execution interval in hours for the background closed PR lock sweep service.
    /// Bound to 1..72.
    /// Default: 12.
    /// </summary>
    public int SweepIntervalHours { get; set; } = 12;

    /// <summary>
    /// Time window in minutes for the PR author to reply with `/lock confirm` (or `/archive confirm`) before the confirmation request expires.
    /// Bound to 1..60.
    /// Default: 15.
    /// </summary>
    public int AuthorLockConfirmTimeoutMinutes { get; set; } = 15;
}
