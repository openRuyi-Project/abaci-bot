using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background periodic service for locking inactive closed pull requests.
/// Queries for pull requests closed longer than InactiveCloseDays and locks them with TooHeated lock reason.
/// </summary>
public class LockSweepService : BackgroundService
{
    private readonly IConversationGuardService _guardService;
    private readonly IOptions<ConversationGuardOptions> _options;
    private readonly ILogger<LockSweepService> _logger;

    internal TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);

    public LockSweepService(
        IConversationGuardService guardService,
        IOptions<ConversationGuardOptions> options,
        ILogger<LockSweepService> logger)
    {
        _guardService = guardService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("LockSweepService is disabled via configuration.");
            return;
        }

        _logger.LogInformation(
            "LockSweepService started. Periodic interval: {Hours} hours, Inactive close days: {Days} days.",
            options.SweepIntervalHours, options.InactiveCloseDays);

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
                _logger.LogError(ex, "Unexpected error occurred during inactive PR lock sweep.");
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

        var repositories = RepositoryAllowList.ParseRepositories(options.RepositoryAllowList, _logger, "ConversationGuard RepositoryAllowList");
        if (repositories.Count == 0)
        {
            _logger.LogDebug("No repositories configured for LockSweepService. Skipping sweep.");
            return;
        }

        foreach (var (owner, repo) in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation("Running inactive PR lock sweep on {Owner}/{Repo} (Threshold: {Days} days)",
                    owner, repo, options.InactiveCloseDays);

                await _guardService.LockInactivePullRequestsAsync(
                    owner,
                    repo,
                    options.InactiveCloseDays,
                    options.DryRun,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to run inactive PR lock sweep on repository {Owner}/{Repo}", owner, repo);
            }
        }
    }
}
