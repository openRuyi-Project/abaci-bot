using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;

namespace abaci_bot.Modules;

/// <summary>
/// Shiny New Pull Request governance module (Priority: 15).
/// Tags newly opened PRs with the "new" label to highlight them for reviewer triage.
/// Automatically schedules removal of the "new" label as soon as the first valid human review
/// or comment (from someone other than the author and bots) is submitted.
/// </summary>
public class ShinyNewPrModule : IPullRequestModule
{
    private readonly BotDetectionOptions _botOptions;

    public string ModuleName => "ShinyNewPrModule";

    public int Priority => 15;

    public ShinyNewPrModule(IOptions<BotDetectionOptions>? botOptions = null)
    {
        _botOptions = botOptions?.Value ?? new BotDetectionOptions();
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType switch
        {
            PullRequestEventType.PullRequest =>
                context.PullRequestAction == PullRequestAction.Opened,

            PullRequestEventType.Review =>
                context.PullRequestReviewAction == PullRequestReviewAction.Submitted,

            PullRequestEventType.IssueComment =>
                context.IssueCommentAction == IssueCommentAction.Created,

            _ => false
        };
    }

    public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        // 1. Tag newly opened PRs with the "new" indicator
        if (context.EventType == PullRequestEventType.PullRequest &&
            context.PullRequestAction == PullRequestAction.Opened)
        {
            context.LabelsToAdd.Add(GitHubLabels.New);
            return Task.CompletedTask;
        }

        // 2. On review or comment: if the PR currently carries "new", remove it upon first human non-author engagement
        if (context.ExistingLabels.Contains(GitHubLabels.New))
        {
            if (!context.IsSenderAuthorOrBot(_botOptions.Whitelist))
            {
                context.LabelsToRemove.Add(GitHubLabels.New);
            }
        }

        return Task.CompletedTask;
    }
}
