using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace abaci_bot.Tests;

public class LockSweepServiceTests
{
    private readonly IConversationGuardService _guardService = Substitute.For<IConversationGuardService>();

    [Fact]
    public async Task RunSweepAsync_Disabled_ExitsWithoutAction()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = false,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        var service = new LockSweepService(_guardService, options, NullLogger<LockSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);

        await _guardService.DidNotReceiveWithAnyArgs().LockInactivePullRequestsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RunSweepAsync_EmptyRepositories_ExitsWithoutAction()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string>()
        });
        var service = new LockSweepService(_guardService, options, NullLogger<LockSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);

        await _guardService.DidNotReceiveWithAnyArgs().LockInactivePullRequestsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RunSweepAsync_Valid_CallsLockInactivePullRequests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            DryRun = false,
            InactiveCloseDays = 60,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        var service = new LockSweepService(_guardService, options, NullLogger<LockSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);

        await _guardService.Received(1).LockInactivePullRequestsAsync("owner", "repo", 60, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_ExitsImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = false
        });
        var service = new TestableLockSweepService(_guardService, options);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ExitsCleanly()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        var service = new TestableLockSweepService(_guardService, options)
        {
            StartupDelay = TimeSpan.FromMilliseconds(1)
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.CallExecuteAsync(cts.Token);
    }

    [Fact]
    public async Task RunSweepAsync_InvalidRepoEntry_LogsWarningAndSkips()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationGuardOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "invalid-repo-without-slash" }
        });
        var service = new LockSweepService(_guardService, options, NullLogger<LockSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);
        await _guardService.DidNotReceiveWithAnyArgs().LockInactivePullRequestsAsync(default!, default!, default, default);
    }

    private sealed class TestableLockSweepService : LockSweepService
    {
        public TestableLockSweepService(
            IConversationGuardService guardService,
            IOptions<ConversationGuardOptions> options)
            : base(guardService, options, NullLogger<LockSweepService>.Instance)
        {
        }

        public Task CallExecuteAsync(CancellationToken token) => ExecuteAsync(token);
    }
}
