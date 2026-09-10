namespace abaci_bot.Services;

/// <summary>
/// Defines standard label constants used across the repository for workflow, contribution, and compliance tracking.
/// </summary>
public static class GitHubLabels
{
    public const string WorkflowBlocked = "Workflow: Blocked";
    public const string WorkflowInDev = "Workflow: In Dev";
    public const string WorkflowReadyForReview = "Workflow: Ready For Review";
    public const string WorkflowInReview = "Workflow: In Review";
    public const string WorkflowComplete = "Workflow: Complete";
    public const string CommitsUpdated = "Commits: Updated";
    public const string AiAssistance = "AI Assistance";
}
