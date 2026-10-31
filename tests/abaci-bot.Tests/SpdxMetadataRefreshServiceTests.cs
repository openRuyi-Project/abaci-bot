using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class SpdxMetadataRefreshServiceTests
{
    private readonly ISpdxLicenseService _spdxLicenseService = Substitute.For<ISpdxLicenseService>();
    private readonly ILogger<SpdxMetadataRefreshService> _logger = Substitute.For<ILogger<SpdxMetadataRefreshService>>();

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotCallRefresh()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions { Enabled = false });
        var service = new SpdxMetadataRefreshService(_spdxLicenseService, options, _logger)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.StartAsync(cts.Token);

        await _spdxLicenseService.DidNotReceive().RefreshRemoteMetadataAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenZeroOrNegativeInterval_DoesNotCallRefresh()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            Enabled = true,
            AutoUpdateIntervalHours = 0
        });
        var service = new SpdxMetadataRefreshService(_spdxLicenseService, options, _logger)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.StartAsync(cts.Token);

        await _spdxLicenseService.DidNotReceive().RefreshRemoteMetadataAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnabled_CallsRefreshRemoteMetadataAsync()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            Enabled = true,
            AutoUpdateIntervalHours = 0.001
        });

        var tcs = new TaskCompletionSource<bool>();
        _spdxLicenseService.RefreshRemoteMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        var service = new SpdxMetadataRefreshService(_spdxLicenseService, options, _logger)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();

        await service.StopAsync(CancellationToken.None);

        await _spdxLicenseService.Received().RefreshRemoteMetadataAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenRefreshThrows_CatchesAndContinues()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SpdxComplianceOptions
        {
            Enabled = true,
            AutoUpdateIntervalHours = 0.001
        });

        var tcs = new TaskCompletionSource<bool>();
        _spdxLicenseService.RefreshRemoteMetadataAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(callInfo =>
            {
                tcs.TrySetResult(true);
                throw new HttpRequestException("SPDX remote index unavailable");
            });

        var service = new SpdxMetadataRefreshService(_spdxLicenseService, options, _logger)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();

        await service.StopAsync(CancellationToken.None);

        await _spdxLicenseService.Received().RefreshRemoteMetadataAsync(Arg.Any<CancellationToken>());
    }
}

