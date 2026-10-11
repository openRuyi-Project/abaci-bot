using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Contributor identification governance module (Priority: 20).
/// Categorizes pull requests based on the commit author's email domain into
/// student intern contributions or general community contributions, excluding official staff.
/// Reconciles its complete managed-label set to clear stale classifications.
/// </summary>
public class UserContributionModule : IPullRequestModule
{
    public string ModuleName => "UserContributionModule";

    public int Priority => 20;

    /// <summary>
    /// Complete set of labels managed by this module for community contributor classification.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ManagedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        GitHubLabels.CommunityStudent,
        GitHubLabels.CommunityContribution
    };

    IReadOnlyCollection<string> IPullRequestModule.ManagedLabels => ManagedLabels;

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

        // If author email lookup failed due to transient API/network error, preserve existing labels without mutating.
        if (context.AuthorEmailLookupFailed)
        {
            return;
        }

        var domain = GitHubWebhookProcessor.ExtractEmailDomain(userEmail);

        // If author email is absent or unparseable, identity is undetermined: do not modify existing community labels.
        if (string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        string? desiredLabel = null;

        // Official ISCAS staff members are excluded from community contribution labels.
        // For non-staff contributors, classify as student intern or general community contribution.
        if (!domain.Equals("iscas.ac.cn", StringComparison.OrdinalIgnoreCase))
        {
            desiredLabel = GitHubWebhookProcessor.IsInternEmail(userEmail)
                ? GitHubLabels.CommunityStudent
                : GitHubLabels.CommunityContribution;
        }

        // Reconcile managed label set: apply current classification and clear stale classifications.
        // For staff (desiredLabel == null), this removes any pre-existing community labels.
        context.ReconcileLabels(ManagedLabels, desiredLabel);
    }
}
