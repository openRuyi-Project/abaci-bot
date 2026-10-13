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
    /// Determines whether the module should run based on whether the pull request was opened,
    /// synchronized, reopened, or transitioned to/from draft.
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

        var domain = EmailDomainHelper.ExtractEmailDomain(userEmail);
        string? desiredLabel = null;

        // If author email has a valid parseable domain and is not official ISCAS staff,
        // classify as student intern or general community contribution.
        // For staff (iscas.ac.cn) or unknown/unparseable author email (desiredLabel == null),
        // any pre-existing community labels are reconciled and removed.
        if (!string.IsNullOrWhiteSpace(domain) && !domain.Equals("iscas.ac.cn", StringComparison.OrdinalIgnoreCase))
        {
            desiredLabel = EmailDomainHelper.IsInternEmail(userEmail)
                ? GitHubLabels.CommunityStudent
                : GitHubLabels.CommunityContribution;
        }

        // Reconcile managed label set: apply current classification and clear stale classifications.
        context.ReconcileLabels(ManagedLabels, desiredLabel);
    }
}
