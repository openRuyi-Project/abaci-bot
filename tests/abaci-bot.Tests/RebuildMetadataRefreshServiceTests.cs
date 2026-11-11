using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace abaci_bot.Tests;

public class RebuildMetadataRefreshServiceTests
{
    private readonly IRpmDependencyService _rpmDependencyService = Substitute.For<IRpmDependencyService>();
    private readonly ILogger<RebuildMetadataRefreshService> _logger = Substitute.For<ILogger<RebuildMetadataRefreshService>>();

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotCallRefresh()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions { Enabled = false });
        var service = new RebuildMetadataRefreshService(_rpmDependencyService, options, _logger)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel immediately

        await service.StartAsync(cts.Token);

        await _rpmDependencyService.DidNotReceive().RefreshMetadataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnabled_CallsRefreshMetadataAsync()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
        {
            Enabled = true,
            RefreshIntervalHours = 0.001 // very fast for testing
        });

        var tcs = new TaskCompletionSource<bool>();
        _rpmDependencyService.RefreshMetadataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        var service = new RebuildMetadataRefreshService(_rpmDependencyService, options, _logger)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();

        await service.StopAsync(CancellationToken.None);

        await _rpmDependencyService.Received().RefreshMetadataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenRefreshThrows_CatchesAndContinues()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
        {
            Enabled = true,
            RefreshIntervalHours = 0.001
        });

        var tcs = new TaskCompletionSource<bool>();
        _rpmDependencyService.RefreshMetadataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(callInfo =>
            {
                tcs.TrySetResult(true);
                throw new HttpRequestException("Network failure");
            });

        var service = new RebuildMetadataRefreshService(_rpmDependencyService, options, _logger)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();

        // Must not throw or crash
        await service.StopAsync(CancellationToken.None);

        await _rpmDependencyService.Received().RefreshMetadataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenRebuildDisabled_ButAbiBaselineEnabled_CallsRefreshWithFallbackUrl()
    {
        var rebuildOptions = Microsoft.Extensions.Options.Options.Create(new RebuildImpactOptions
        {
            Enabled = false,
            PrimaryXmlUrl = null,
            RefreshIntervalHours = 0.001
        });

        var abiOptions = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions
        {
            Enabled = true,
            StableRpmBaseUrl = "https://repo.openeuler.org/OS/x86_64"
        });

        var tcs = new TaskCompletionSource<string?>();
        _rpmDependencyService.RefreshMetadataAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.TrySetResult(callInfo.ArgAt<string?>(0));
                return Task.FromResult(true);
            });

        var service = new RebuildMetadataRefreshService(_rpmDependencyService, rebuildOptions, _logger, abiOptions)
        {
            StartupDelay = TimeSpan.Zero
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        var fallbackUrl = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("https://repo.openeuler.org/OS/x86_64/repodata/repomd.xml", fallbackUrl);
        await _rpmDependencyService.Received().RefreshMetadataAsync(
            "https://repo.openeuler.org/OS/x86_64/repodata/repomd.xml",
            Arg.Any<CancellationToken>());
    }
}
