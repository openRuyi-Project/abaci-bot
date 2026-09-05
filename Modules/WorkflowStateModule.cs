using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.IssueComment;

namespace abaci_bot.Modules;

/// <summary>
/// PR 工作流主生命周期状态机模块 (Priority: 10)
/// 治理范围：In Dev, Ready For Review, In Review, Blocked, Complete 以及 Commits: Updated 联动。
/// </summary>
public class WorkflowStateModule : IPullRequestModule
{
    public string ModuleName => "WorkflowStateModule";

    public int Priority => 10;

    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType switch
        {
            PullRequestEventType.PullRequest =>
                context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.ReadyForReview ||
                context.PullRequestAction == PullRequestAction.Closed ||
                context.PullRequestAction == PullRequestAction.Labeled ||
                context.PullRequestAction == PullRequestAction.Unlabeled,
            PullRequestEventType.Review => context.PullRequestReviewAction == PullRequestReviewAction.Submitted,
            PullRequestEventType.IssueComment => context.IssueCommentAction == IssueCommentAction.Created,
            _ => false
        };
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        switch (context.EventType)
        {
            case PullRequestEventType.PullRequest:
                await ProcessPullRequestEventAsync(context);
                break;

            case PullRequestEventType.Review:
                await ProcessReviewEventAsync(context);
                break;

            case PullRequestEventType.IssueComment:
                await ProcessIssueCommentEventAsync(context);
                break;
        }
    }

    private Task ProcessPullRequestEventAsync(PullRequestContext context)
    {
        var action = context.PullRequestAction;
        var isBlocked = context.IsBlocked;

        if (action == PullRequestAction.Labeled)
        {
            if (context.PullRequestEvent is PullRequestLabeledEvent labeledEvent &&
                GitHubWebhookProcessor.IsBlockedLabel(labeledEvent.Label?.Name))
            {
                context.LabelsToRemove.Add(GitHubLabels.WorkflowInReview);
                context.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
            }
            return Task.CompletedTask;
        }

        if (action == PullRequestAction.Unlabeled)
        {
            if (context.PullRequestEvent is PullRequestUnlabeledEvent unlabeledEvent &&
                GitHubWebhookProcessor.IsBlockedLabel(unlabeledEvent.Label?.Name))
            {
                context.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
            }
            return Task.CompletedTask;
        }

        if (action == PullRequestAction.Closed)
        {
            if (context.IsMerged == true)
            {
                context.LabelsToAdd.Add(GitHubLabels.WorkflowComplete);
            }
            context.LabelsToRemove.Add(GitHubLabels.WorkflowInDev);
            context.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
            context.LabelsToRemove.Add(GitHubLabels.WorkflowInReview);
            return Task.CompletedTask;
        }

        // Opened, Synchronize, Reopened, ConvertedToDraft, ReadyForReview
        if (action == PullRequestAction.Synchronize && isBlocked)
        {
            context.LabelsToAdd.Add(GitHubLabels.CommitsUpdated);
        }

        if (isBlocked)
        {
            return Task.CompletedTask;
        }

        if (context.IsDraft || context.Title.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
        {
            context.LabelsToAdd.Add(GitHubLabels.WorkflowInDev);
            context.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
        }
        else
        {
            context.LabelsToRemove.Add(GitHubLabels.WorkflowInDev);
            if (!context.ExistingLabels.Contains(GitHubLabels.WorkflowInReview))
            {
                context.LabelsToAdd.Add(GitHubLabels.WorkflowReadyForReview);
            }
        }

        return Task.CompletedTask;
    }

    private async Task ProcessReviewEventAsync(PullRequestContext context)
    {
        var captains = await context.TeamCaptains.Value;
        var sender = context.SenderLogin;
        var author = context.AuthorLogin;

        if (string.IsNullOrWhiteSpace(sender))
            return;

        if (captains.Contains(sender) && !context.IsBlocked)
        {
            context.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
            context.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
        }

        if (captains.Contains(sender) && !GitHubWebhookProcessor.IsSameUser(sender, author))
        {
            context.LabelsToRemove.Add(GitHubLabels.CommitsUpdated);
        }
    }

    private async Task ProcessIssueCommentEventAsync(PullRequestContext context)
    {
        var captains = await context.TeamCaptains.Value;
        var sender = context.SenderLogin;
        var author = context.AuthorLogin;

        if (string.IsNullOrWhiteSpace(sender))
            return;

        var pullRequest = await context.PullRequest.Value;
        var effectiveAuthor = author ?? pullRequest.User?.Login;
        var isDraft = pullRequest.Draft;

        if (captains.Contains(sender) &&
            !context.IsBlocked &&
            !isDraft &&
            !context.Title.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
        {
            context.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
            context.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
        }

        if (captains.Contains(sender) && !GitHubWebhookProcessor.IsSameUser(sender, effectiveAuthor))
        {
            context.LabelsToRemove.Add(GitHubLabels.CommitsUpdated);
        }
    }
}
