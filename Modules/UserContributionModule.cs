using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// 贡献者身份识别治理模块 (Priority: 20)
/// 治理范围：根据 PR 提交者邮箱分类 Student contribution 与社区 Contribution，排除 iscas.ac.cn 官方员工。
/// </summary>
public class UserContributionModule : IPullRequestModule
{
    public string ModuleName => "UserContributionModule";

    public int Priority => 20;

    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.ReadyForReview);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        var userEmail = await context.AuthorEmail.Value;
        var domain = GitHubWebhookProcessor.ExtractEmailDomain(userEmail);
        if (string.IsNullOrWhiteSpace(domain))
            return;

        // Staff are excluded
        if (domain.Equals("iscas.ac.cn", StringComparison.OrdinalIgnoreCase))
            return;

        // For intern, check email pattern
        if (GitHubWebhookProcessor.IsInternEmail(userEmail))
        {
            context.LabelsToAdd.Add("Community: Student contribution");
        }
        else
        {
            // Any other domain counted as community contribution
            context.LabelsToAdd.Add("Community: Contribution");
        }
    }
}
