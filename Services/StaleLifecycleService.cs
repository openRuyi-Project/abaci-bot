using abaci_bot.Options;
using abaci_bot.Pipelines;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background periodic service for stale issue and PR lifecycle management.
/// Uses GitHub search queries to locate inactive items, enforces exemption whitelists,
/// throttles requests to respect GitHub Secondary Rate Limits, and automatically
/// applies Status: Stale or closes expired items.
/// </summary>
public class StaleLifecycleService : BackgroundService
{
    private readonly IGitHubService _gitHub;
    private readonly IOptions<StaleLifecycleOptions> _options;
    private readonly IConfiguration _config;
    private readonly PullRequestLockManager _lockManager;
    private readonly ILogger<StaleLifecycleService> _logger;

    public StaleLifecycleService(
        IGitHubService gitHub,
        IOptions<StaleLifecycleOptions> options,
        IConfiguration config,
        PullRequestLockManager lockManager,
        ILogger<StaleLifecycleService> logger)
    {
        _gitHub = gitHub;
        _options = options;
        _config = config;
        _lockManager = lockManager;
        _logger = logger;
    }

    // Internal delay for application startup, overridable in tests
    internal TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);

    // Internal delay between rate-limit retries, overridable in tests
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("StaleLifecycleService is disabled via configuration.");
            return;
        }

        _logger.LogInformation("StaleLifecycleService started. Periodic interval: {Hours} hours.", options.CheckIntervalHours);

        // Initial delay to let application finish startup
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

        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.CheckIntervalHours)));

        do
        {
            try
            {
                await RunLifecycleSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred during stale lifecycle sweep.");
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunLifecycleSweepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var repositories = GetTargetRepositories();

        if (repositories.Count == 0)
        {
            _logger.LogWarning("No monitored repositories configured for StaleLifecycleService. Skipping sweep to prevent unintended mutations.");
            return;
        }

        foreach (var (owner, repo) in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ProcessRepositoryAsync(owner, repo, options, cancellationToken);
        }
    }

    private async Task ProcessRepositoryAsync(string owner, string repo, StaleLifecycleOptions options, CancellationToken cancellationToken)
    {
        // 1. Inspect rate limit quota before beginning queries. If below watermark, retry with backoff.
        const int maxRateLimitRetries = 3;
        var retryCount = 0;

        while (true)
        {
            var remainingQuota = await _gitHub.GetRemainingRateLimitAsync(cancellationToken);
            if (remainingQuota is null)
            {
                _logger.LogError("Authentication or network error while querying GitHub rate limits. Aborting stale sweep for {Owner}/{Repo}.", owner, repo);
                return;
            }

            if (remainingQuota.Value >= options.MinRateLimitRemaining)
                break;

            retryCount++;
            if (retryCount > maxRateLimitRetries)
            {
                _logger.LogError("GitHub API remaining quota ({Remaining}) is persistently below watermark ({Min}) after {Retries} retries. Skipping sweep for {Owner}/{Repo}.",
                    remainingQuota.Value, options.MinRateLimitRemaining, maxRateLimitRetries, owner, repo);
                return;
            }

            _logger.LogWarning("GitHub API remaining quota ({Remaining}) is below watermark ({Min}). Pausing before retry {Attempt}/{Max}.",
                remainingQuota.Value, options.MinRateLimitRemaining, retryCount, maxRateLimitRetries);

            try
            {
                if (RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var staleThreshold = now.AddDays(-options.StaleDays);
        var closeThreshold = now.AddDays(-options.CloseDays);

        // 2. Fetch candidates to mark as Stale:
        // Open, not draft, without Status: Stale, not updated within StaleDays
        var staleIssues = await _gitHub.SearchStaleCandidatesAsync(
            owner, repo, staleThreshold, isPr: false, labelFilter: $"-label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);
        var stalePrs = await _gitHub.SearchStaleCandidatesAsync(
            owner, repo, staleThreshold, isPr: true, labelFilter: $"-label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);

        var newStaleCandidates = staleIssues.Concat(stalePrs)
            .GroupBy(i => i.Number)
            .Select(g => g.First())
            .ToList();

        foreach (var item in newStaleCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExempt(item, options))
                continue;

            try
            {
                using (await _lockManager.AcquireLockAsync(owner, repo, item.Number, cancellationToken))
                {
                    if (options.DryRun)
                    {
                        _logger.LogInformation("[DryRun] Would mark {Owner}/{Repo}#{Number} as Status: Stale", owner, repo, item.Number);
                        continue;
                    }

                    _logger.LogInformation("Marking {Owner}/{Repo}#{Number} as Status: Stale", owner, repo, item.Number);

                    // Post comment FIRST: anchors updated_at for CloseDays calculation.
                    // If posting the comment fails, do NOT apply the label (preventing grace period collapse in next cycle).
                    var staleComment = options.FormatStaleComment();
                    await _gitHub.CreateCommentAsync(owner, repo, item.Number, staleComment, cancellationToken);

                    await _gitHub.AddLabelsAsync(owner, repo, item.Number, new[] { GitHubLabels.StatusStale }, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to mark {Owner}/{Repo}#{Number} as stale. Skipping item.", owner, repo, item.Number);
            }

            if (options.RequestThrottleMilliseconds > 0)
            {
                await Task.Delay(options.RequestThrottleMilliseconds, cancellationToken);
            }
        }

        // 3. Fetch candidates to auto-close:
        // Open, not draft, with Status: Stale, and not updated within CloseDays (i.e. grace period expired)
        var closeIssues = await _gitHub.SearchStaleCandidatesAsync(
            owner, repo, closeThreshold, isPr: false, labelFilter: $"label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);
        var closePrs = await _gitHub.SearchStaleCandidatesAsync(
            owner, repo, closeThreshold, isPr: true, labelFilter: $"label:\"{GitHubLabels.StatusStale}\"", ct: cancellationToken);

        var closeCandidates = closeIssues.Concat(closePrs)
            .GroupBy(i => i.Number)
            .Select(g => g.First())
            .ToList();

        foreach (var item in closeCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExempt(item, options))
                continue;

            try
            {
                using (await _lockManager.AcquireLockAsync(owner, repo, item.Number, cancellationToken))
                {
                    if (options.DryRun)
                    {
                        _logger.LogInformation("[DryRun] Would auto-close expired stale object {Owner}/{Repo}#{Number}", owner, repo, item.Number);
                        continue;
                    }

                    _logger.LogInformation("Auto-closing expired stale object {Owner}/{Repo}#{Number}", owner, repo, item.Number);

                    // Auto-close object first: prevents infinite comment-posting loops if close succeeds and comment fails
                    await _gitHub.CloseIssueAsync(owner, repo, item.Number, cancellationToken);

                    var closeComment = options.FormatCloseComment();
                    await _gitHub.CreateCommentAsync(owner, repo, item.Number, closeComment, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to auto-close stale object {Owner}/{Repo}#{Number}. Skipping item.", owner, repo, item.Number);
            }

            if (options.RequestThrottleMilliseconds > 0)
            {
                await Task.Delay(options.RequestThrottleMilliseconds, cancellationToken);
            }
        }
    }

    private static bool IsExempt(Octokit.Issue item, StaleLifecycleOptions options)
    {
        // Protection: Check exempt labels
        if (item.Labels.Any(l => options.ExemptLabels.Contains(l.Name, StringComparer.OrdinalIgnoreCase)))
            return true;

        // Protection: Draft PRs and WIP titles should not be marked or closed as stale
        if (item.Title.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.PullRequest?.Draft == true)
            return true;

        return false;
    }

    private List<(string Owner, string Repo)> GetTargetRepositories()
    {
        var options = _options.Value;
        var list = new List<(string Owner, string Repo)>();

        if (options.MonitoredRepositories.Count > 0)
        {
            foreach (var r in options.MonitoredRepositories)
            {
                var parts = r.Split('/');
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    list.Add((parts[0].Trim(), parts[1].Trim()));
                }
                else
                {
                    _logger.LogWarning("Invalid repository entry '{Entry}' in MonitoredRepositories. Expected format 'owner/repo'.", r);
                }
            }
        }
        else
        {
            // Do NOT silently fallback to primary repository; explicit configuration is required.
            _logger.LogWarning("No MonitoredRepositories configured for StaleLifecycleService. Skipping sweep to prevent unintended mutations.");
        }

        // Apply exclusion list
        if (options.ExcludedRepositories.Count > 0)
        {
            list.RemoveAll(r => options.ExcludedRepositories.Any(e =>
                string.Equals(e, $"{r.Owner}/{r.Repo}", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e, r.Repo, StringComparison.OrdinalIgnoreCase)));
        }

        return list;
    }
}
