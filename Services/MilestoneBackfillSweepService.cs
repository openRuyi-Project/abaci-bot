using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background periodic service for backfilling missing release milestones on merged pull requests.
/// Queries GitHub search for merged pull requests without milestones within the backfill window,
/// resolves the corresponding monthly milestone (YYYY.MM), and updates them.
/// </summary>
public class MilestoneBackfillSweepService : BackgroundService
{
    private readonly IGitHubService _gitHub;
    private readonly IMilestoneResolver _milestoneResolver;
    private readonly IOptions<MilestoneSyncOptions> _options;
    private readonly ILogger<MilestoneBackfillSweepService> _logger;

    internal TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);

    public MilestoneBackfillSweepService(
        IGitHubService gitHub,
        IMilestoneResolver milestoneResolver,
        IOptions<MilestoneSyncOptions> options,
        ILogger<MilestoneBackfillSweepService> logger)
    {
        _gitHub = gitHub;
        _milestoneResolver = milestoneResolver;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("MilestoneBackfillSweepService is disabled via configuration.");
            return;
        }

        _logger.LogInformation(
            "MilestoneBackfillSweepService started. Periodic interval: {Hours} hours, Backfill window: {Days} days.",
            options.SweepIntervalHours, options.BackfillDays);

        try
        {
            if (StartupDelay > TimeSpan.Zero)
            {
                await Task.Delay(StartupDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.SweepIntervalHours)));

        do
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred during milestone backfill sweep.");
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunSweepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return;
        }

        var repositories = RepositoryAllowList.ParseRepositories(options.RepositoryAllowList, _logger, "MilestoneSync RepositoryAllowList");
        if (repositories.Count == 0)
        {
            _logger.LogDebug("No repositories configured for MilestoneBackfillSweepService. Skipping sweep.");
            return;
        }

        var mergedAfter = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.BackfillDays));

        foreach (var (owner, repo) in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation("Running milestone backfill sweep on {Owner}/{Repo} for PRs merged after {MergedAfter:u}",
                    owner, repo, mergedAfter);

                var searchResult = await _gitHub.SearchMergedPullRequestsWithoutMilestoneAsync(
                    owner, repo, mergedAfter, cancellationToken);

                if (searchResult.IsTruncated)
                {
                    _logger.LogWarning(
                        "Milestone backfill search truncated in {Owner}/{Repo}: retrieved {Retrieved} of {Total} pull requests; remaining items deferred to next sweep.",
                        owner, repo, searchResult.Items.Count, searchResult.TotalCount);
                }

                _logger.LogInformation("Found {Count} merged pull requests without milestone in {Owner}/{Repo}",
                    searchResult.Items.Count, owner, repo);

                foreach (var pr in searchResult.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (pr.Milestone != null)
                    {
                        continue;
                    }

                    var targetDate = pr.ClosedAt ?? pr.CreatedAt;

                    if (options.DryRun)
                    {
                        _logger.LogInformation(
                            "[DryRun] Would assign milestone for merged PR {Owner}/{Repo}#{PrNumber} (closed: {ClosedAt:u})",
                            owner, repo, pr.Number, targetDate);
                        continue;
                    }

                    try
                    {
                        var milestoneNumber = await _milestoneResolver.ResolveMilestoneAsync(
                            owner,
                            repo,
                            targetDate,
                            options.CreationEnabled,
                            options.DueDayOfMonth,
                            options.TimeZoneId,
                            cancellationToken);

                        if (milestoneNumber.HasValue)
                        {
                            await _gitHub.SetIssueMilestoneAsync(
                                owner,
                                repo,
                                pr.Number,
                                milestoneNumber.Value,
                                cancellationToken);

                            _logger.LogInformation(
                                "Assigned milestone #{MilestoneNumber} to merged PR {Owner}/{Repo}#{PrNumber}",
                                milestoneNumber.Value, owner, repo, pr.Number);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to backfill milestone for PR {Owner}/{Repo}#{PrNumber}",
                            owner, repo, pr.Number);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to execute milestone backfill sweep on repository {Owner}/{Repo}",
                    owner, repo);
            }
        }
    }
}
