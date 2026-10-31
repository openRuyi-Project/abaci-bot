using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background periodic service for refreshing official SPDX license and exception dictionaries.
/// Executes periodic background synchronizations according to <see cref="SpdxComplianceOptions.AutoUpdateIntervalHours"/>.
/// </summary>
public class SpdxMetadataRefreshService : BackgroundService
{
    private readonly ISpdxLicenseService _spdxLicenseService;
    private readonly IOptions<SpdxComplianceOptions> _options;
    private readonly ILogger<SpdxMetadataRefreshService>? _logger;

    public SpdxMetadataRefreshService(
        ISpdxLicenseService spdxLicenseService,
        IOptions<SpdxComplianceOptions> options,
        ILogger<SpdxMetadataRefreshService>? logger = null)
    {
        _spdxLicenseService = spdxLicenseService;
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
        if (!options.Enabled || !double.IsFinite(options.AutoUpdateIntervalHours) || options.AutoUpdateIntervalHours <= 0)
        {
            _logger?.LogInformation("SpdxMetadataRefreshService is disabled via configuration or zero/invalid interval.");
            return;
        }

        _logger?.LogInformation(
            "SpdxMetadataRefreshService started. Periodic interval: {Hours} hours.",
            options.AutoUpdateIntervalHours);

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
                _logger?.LogInformation("Executing SPDX metadata refresh...");
                var success = await _spdxLicenseService.RefreshRemoteMetadataAsync(stoppingToken);
                _logger?.LogInformation("SPDX metadata refresh completed: {Success}", success ? "Success" : "No updates / failed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error occurred during background SPDX metadata refresh.");
            }

            var rawInterval = _options.Value.AutoUpdateIntervalHours;
            var intervalHours = (!double.IsFinite(rawInterval) || rawInterval <= 0) ? 24 : Math.Max(0.1, rawInterval);
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

