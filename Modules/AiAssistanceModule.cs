using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// AI-assisted contribution declaration module (Priority: 40).
/// Synchronizes the "AI Assistance" label based on whether the contributor
/// checked the AI-assisted contribution policy checkbox in the pull request description.
/// </summary>
public class AiAssistanceModule : IPullRequestModule
{
    public string ModuleName => "AiAssistanceModule";

    public int Priority => 40;

    /// <summary>
    /// Processes pull request creation, commit updates, and description edits.
    /// </summary>
    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.ReadyForReview ||
                context.PullRequestAction == PullRequestAction.Edited);
    }

    public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        // The pull request description is the source of truth for AI assistance.
        // When checked, apply the label; otherwise, ensure it is removed.
        if (GitHubWebhookProcessor.IsAiAssistedPullRequest(context.Body))
        {
            context.LabelsToAdd.Add(GitHubLabels.AiAssistance);
            context.LabelsToRemove.Remove(GitHubLabels.AiAssistance);
        }
        else
        {
            context.LabelsToRemove.Add(GitHubLabels.AiAssistance);
            context.LabelsToAdd.Remove(GitHubLabels.AiAssistance);
        }

        return Task.CompletedTask;
    }
}
