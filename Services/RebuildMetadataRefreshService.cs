using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background periodic service for refreshing RPM repository metadata and rebuilding the reverse dependency DAG.
/// Automatically executes an initial refresh upon application startup, followed by periodic refreshes based on
/// <see cref="RebuildImpactOptions.RefreshIntervalHours"/>.
/// </summary>
public class RebuildMetadataRefreshService : BackgroundService
{
    private readonly IRpmDependencyService _rpmDependencyService;
    private readonly IOptions<RebuildImpactOptions> _options;
    private readonly ILogger<RebuildMetadataRefreshService>? _logger;

    public RebuildMetadataRefreshService(
        IRpmDependencyService rpmDependencyService,
        IOptions<RebuildImpactOptions> options,
        ILogger<RebuildMetadataRefreshService>? logger = null)
    {
        _rpmDependencyService = rpmDependencyService;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Initial delay before the first metadata refresh runs after application start.
    /// Internal to allow unit tests to execute synchronously or with zero delay.
    /// </summary>
    internal TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger?.LogInformation("RebuildMetadataRefreshService is disabled via configuration.");
            return;
        }

        _logger?.LogInformation(
            "RebuildMetadataRefreshService started. Periodic interval: {Hours} hours.",
            options.RefreshIntervalHours);

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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger?.LogInformation("Executing RPM metadata refresh...");
                var success = await _rpmDependencyService.RefreshMetadataAsync(stoppingToken);
                _logger?.LogInformation("RPM metadata refresh completed: {Success}", success ? "Success" : "No updates / failed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error occurred during background RPM metadata refresh.");
            }

            var intervalHours = Math.Max(0.1, _options.Value.RefreshIntervalHours);
            var delay = TimeSpan.FromHours(intervalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
