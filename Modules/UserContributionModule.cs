using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Contributor identification governance module (Priority: 20).
/// Categorizes pull requests based on the commit author's email domain into
/// student intern contributions or general community contributions, excluding official staff.
/// </summary>
public class UserContributionModule : IPullRequestModule
{
    public string ModuleName => "UserContributionModule";

    public int Priority => 20;

    /// <summary>
    /// Processes opened, updated, or reopened pull requests to classify contributor identity.
    /// </summary>
    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft ||
                context.PullRequestAction == PullRequestAction.ReadyForReview);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var userEmail = await context.AuthorEmail.Value;
        var domain = GitHubWebhookProcessor.ExtractEmailDomain(userEmail);
        if (string.IsNullOrWhiteSpace(domain))
            return;

        // Official institute staff members are excluded from community labeling
        if (domain.Equals("iscas.ac.cn", StringComparison.OrdinalIgnoreCase))
            return;

        // Intern email addresses match designated subdomains
        if (GitHubWebhookProcessor.IsInternEmail(userEmail))
        {
            context.LabelsToAdd.Add("Community: Student contribution");
        }
        else
        {
            // All other non-staff domains are counted as general community contributions
            context.LabelsToAdd.Add("Community: Contribution");
        }
    }
}
