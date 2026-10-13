using System.Globalization;

namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the automated stale issues and pull requests lifecycle worker.
/// </summary>
public class StaleLifecycleOptions
{
    /// <summary>
    /// Master toggle for the background stale management worker.
    /// Default is false to prevent unexpected destructive lifecycle operations upon deployment.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true, logs candidates that would be marked as stale or closed without mutating GitHub.
    /// Default is true for safety. Must be explicitly set to false in configuration to execute real actions.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Specific repositories monitored for stale cleanup in "owner/repo" format.
    /// Requires explicit configuration; no repositories are processed if left empty.
    /// </summary>
    public List<string> MonitoredRepositories { get; set; } = new();

    /// <summary>
    /// Repositories explicitly excluded from stale management (e.g. archived or template repos).
    /// </summary>
    public List<string> ExcludedRepositories { get; set; } = new();

    /// <summary>
    /// Number of inactive days before an issue or PR is marked with Status: Stale.
    /// Default is 30 days.
    /// </summary>
    public int StaleDays { get; set; } = 30;

    /// <summary>
    /// Grace period in days after being marked as Status: Stale (and notified via comment)
    /// before the issue or PR is eligible for automatic closure.
    /// Default is 7 days.
    /// </summary>
    public int CloseDays { get; set; } = 7;

    /// <summary>
    /// Labels that protect an issue or PR from ever being marked as stale.
    /// </summary>
    public List<string> ExemptLabels { get; set; } = new()
    {
        Services.GitHubLabels.StatusPinned,
        Services.GitHubLabels.WorkflowBlocked,
        "Security",
        "Epic"
    };

    public const string DefaultStaleCommentTemplate =
        "This issue or pull request has been automatically marked as stale because it has not had recent activity " +
        "within the last {StaleDays} days. It will be closed in {CloseDays} days if no further activity occurs. " +
        "If this is still relevant, please comment or push a commit to keep it active. Thank you for your contributions!";

    public const string DefaultCloseCommentTemplate =
        "This issue or pull request was closed because it has been stalled for over {CloseDays} days with no activity after being marked as stale. " +
        "Feel free to reopen it or file a new one if you would like to continue the work.";

    /// <summary>
    /// Friendly message commented on an issue or PR when it is marked as Stale.
    /// Posting this comment also refreshes the item's update timestamp on GitHub to anchor the grace period.
    /// Supports {StaleDays} and {CloseDays} placeholders.
    /// If left empty or whitespace, a safe formatted default notification will still be posted.
    /// </summary>
    public string StaleCommentTemplate { get; set; } = DefaultStaleCommentTemplate;

    /// <summary>
    /// Friendly message commented when an object is automatically closed due to stale expiration.
    /// Supports {CloseDays} placeholder.
    /// </summary>
    public string CloseCommentTemplate { get; set; } = DefaultCloseCommentTemplate;

    /// <summary>
    /// Formats the stale notification comment by replacing {StaleDays} and {CloseDays} placeholders.
    /// </summary>
    public string FormatStaleComment()
    {
        var template = !string.IsNullOrWhiteSpace(StaleCommentTemplate)
            ? StaleCommentTemplate
            : DefaultStaleCommentTemplate;

        return template
            .Replace("{StaleDays}", StaleDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{CloseDays}", CloseDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Formats the close notification comment by replacing {CloseDays} placeholder.
    /// </summary>
    public string FormatCloseComment()
    {
        var template = !string.IsNullOrWhiteSpace(CloseCommentTemplate)
            ? CloseCommentTemplate
            : DefaultCloseCommentTemplate;

        return template.Replace("{CloseDays}", CloseDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Frequency interval in hours between stale scan executions.
    /// Default is 24 hours.
    /// </summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// Delay between processing individual candidates (in milliseconds) to prevent API burst throttling.
    /// Default is 500 ms.
    /// </summary>
    public int RequestThrottleMilliseconds { get; set; } = 500;

    /// <summary>
    /// GitHub API remaining quota watermark. When quota drops below this, the service backs off with retries before aborting the current cycle.
    /// </summary>
    public int MinRateLimitRemaining { get; set; } = 100;
}
