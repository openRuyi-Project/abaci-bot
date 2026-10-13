namespace abaci_bot.Services;

/// <summary>
/// Helper predicates for workflow state checks and GitHub username comparisons.
/// Follows the static helper pattern established by <see cref="BotDetectionHelper"/>.
/// </summary>
public static class WorkflowHelper
{
    /// <summary>
    /// Checks whether the label name corresponds to the "Workflow: Blocked" label.
    /// </summary>
    public static bool IsBlockedLabel(string? labelName)
    {
        return string.Equals(labelName, GitHubLabels.WorkflowBlocked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitively determines whether two GitHub usernames refer to the same user.
    /// </summary>
    public static bool IsSameUser(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
