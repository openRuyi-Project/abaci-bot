using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Milestone synchronization governance module (Priority: 90).
/// Automatically assigns closed, merged pull requests without an existing milestone to the current month's release milestone (YYYY.MM).
/// Creates the target milestone if enabled and missing, setting dueOn to the configured day of month.
/// </summary>
public class MilestoneSyncModule : IPullRequestModule
{
    private readonly MilestoneSyncOptions _options;
    private readonly IMilestoneResolver _milestoneResolver;
    private readonly ILogger<MilestoneSyncModule>? _logger;

    public string ModuleName => "MilestoneSyncModule";

    public int Priority => 90;

    public MilestoneSyncModule(
        IOptions<MilestoneSyncOptions> options,
        IMilestoneResolver milestoneResolver,
        ILogger<MilestoneSyncModule>? logger = null)
    {
        _options = options.Value;
        _milestoneResolver = milestoneResolver;
        _logger = logger;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        if (!_options.Enabled || !RepositoryAllowList.IsAllowed(context.Owner, context.Repo, _options.RepositoryAllowList))
        {
            return false;
        }

        return context.EventType == PullRequestEventType.PullRequest &&
               context.PullRequestAction == PullRequestAction.Closed;
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var pr = await context.PullRequest.Value;
        if (pr.Merged != true)
        {
            _logger?.LogDebug("Pull request {Owner}/{Repo}#{PrNumber} closed without merge; skipping milestone assignment.",
                context.Owner, context.Repo, context.PrNumber);
            return;
        }

        if (pr.Milestone != null)
        {
            _logger?.LogDebug("Pull request {Owner}/{Repo}#{PrNumber} already has milestone '{MilestoneTitle}'; skipping.",
                context.Owner, context.Repo, context.PrNumber, pr.Milestone.Title);
            return;
        }

        if (_options.DryRun)
        {
            _logger?.LogInformation("[DryRun] Would evaluate and assign milestone for merged pull request {Owner}/{Repo}#{PrNumber}.",
                context.Owner, context.Repo, context.PrNumber);
            return;
        }

        var targetDate = pr.MergedAt ?? DateTimeOffset.UtcNow;
        var milestoneNumber = await _milestoneResolver.ResolveMilestoneAsync(
            context.Owner,
            context.Repo,
            targetDate,
            _options.CreationEnabled,
            _options.DueDayOfMonth,
            _options.TimeZoneId,
            cancellationToken);

        if (milestoneNumber.HasValue)
        {
            context.TargetMilestone = milestoneNumber.Value;
            _logger?.LogInformation(
                "Staged milestone #{MilestoneNumber} assignment for merged pull request {Owner}/{Repo}#{PrNumber}.",
                milestoneNumber.Value, context.Owner, context.Repo, context.PrNumber);
        }
        else
        {
            _logger?.LogWarning(
                "Could not resolve milestone for merged pull request {Owner}/{Repo}#{PrNumber} (CreationEnabled={CreationEnabled}).",
                context.Owner, context.Repo, context.PrNumber, _options.CreationEnabled);
        }
    }
}
