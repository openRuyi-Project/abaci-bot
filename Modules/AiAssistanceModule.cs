using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// AI 协助申明检测治理模块 (Priority: 40)
/// 治理范围：根据 PR Description 中是否有勾选 AI-Assisted Policy 复选框同步添加或移除 AI Assistance 标签。
/// </summary>
public class AiAssistanceModule : IPullRequestModule
{
    public string ModuleName => "AiAssistanceModule";

    public int Priority => 40;

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
