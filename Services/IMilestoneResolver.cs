namespace abaci_bot.Services;

/// <summary>
/// Resolves and ensures release milestones for pull requests based on merge dates.
/// </summary>
public interface IMilestoneResolver
{
    /// <summary>
    /// Resolves the milestone number matching the target date under the configured IANA or system timezone (format: YYYY.MM).
    /// Handles UTC offsets and DST; if not found and creation is enabled, creates the milestone and self-heals on concurrent 422 conflicts.
    /// </summary>
    Task<int?> ResolveMilestoneAsync(
        string owner,
        string repo,
        DateTimeOffset targetDate,
        bool creationEnabled = false,
        int dueDayOfMonth = 25,
        string timeZoneId = "Asia/Shanghai",
        CancellationToken ct = default);
}
