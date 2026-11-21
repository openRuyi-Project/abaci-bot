using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;

namespace abaci_bot.Tests;

public class MilestoneBackfillSweepServiceTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IMilestoneResolver _resolver = Substitute.For<IMilestoneResolver>();

    private static Issue CreateIssue(int number, Milestone? milestone = null, DateTimeOffset? closedAt = null)
    {
        var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, number);
        typeof(Issue).GetProperty(nameof(Issue.Milestone))?.SetValue(issue, milestone);
        typeof(Issue).GetProperty(nameof(Issue.ClosedAt))?.SetValue(issue, closedAt ?? DateTimeOffset.UtcNow);
        return issue;
    }

    [Fact]
    public async Task RunSweepAsync_Disabled_ExitsWithoutAction()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = false,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        var service = new MilestoneBackfillSweepService(_gitHub, _resolver, options, NullLogger<MilestoneBackfillSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().SearchMergedPullRequestsWithoutMilestoneAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunSweepAsync_EmptyRepositories_ExitsWithoutAction()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string>()
        });
        var service = new MilestoneBackfillSweepService(_gitHub, _resolver, options, NullLogger<MilestoneBackfillSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().SearchMergedPullRequestsWithoutMilestoneAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunSweepAsync_DryRun_SearchesButDoesNotAssign()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = true,
            BackfillDays = 30,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        _gitHub.SearchMergedPullRequestsWithoutMilestoneAsync("owner", "repo", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new List<Issue> { CreateIssue(101) }, 1, false));

        var service = new MilestoneBackfillSweepService(_gitHub, _resolver, options, NullLogger<MilestoneBackfillSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().SetIssueMilestoneAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RunSweepAsync_Valid_ResolvesAndAssignsMilestone()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = false,
            BackfillDays = 30,
            CreationEnabled = true,
            DueDayOfMonth = 25,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        var issue1 = CreateIssue(101);
        var existingMs = (Milestone)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Milestone));
        var issue2 = CreateIssue(102, milestone: existingMs);

        _gitHub.SearchMergedPullRequestsWithoutMilestoneAsync("owner", "repo", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new List<Issue> { issue1, issue2 }, 600, true));

        _resolver.ResolveMilestoneAsync("owner", "repo", Arg.Any<DateTimeOffset>(), true, 25, "Asia/Shanghai", Arg.Any<CancellationToken>())
            .Returns(77);

        var service = new MilestoneBackfillSweepService(_gitHub, _resolver, options, NullLogger<MilestoneBackfillSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);

        await _gitHub.Received(1).SetIssueMilestoneAsync("owner", "repo", 101, 77, Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().SetIssueMilestoneAsync("owner", "repo", 102, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_ExitsImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = false
        });
        var service = new TestableMilestoneBackfillSweepService(_gitHub, _resolver, options);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ExitsCleanly()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        var service = new TestableMilestoneBackfillSweepService(_gitHub, _resolver, options)
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
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "invalid-repo-without-slash" }
        });
        var service = new MilestoneBackfillSweepService(_gitHub, _resolver, options, NullLogger<MilestoneBackfillSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        await service.RunSweepAsync(CancellationToken.None);
        await _gitHub.DidNotReceiveWithAnyArgs().SearchMergedPullRequestsWithoutMilestoneAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunSweepAsync_WhenResolverThrows_ContinuesNextIssue()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = false,
            RepositoryAllowList = new List<string> { "owner/repo" }
        });
        var issue1 = CreateIssue(101);
        _gitHub.SearchMergedPullRequestsWithoutMilestoneAsync("owner", "repo", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new List<Issue> { issue1 }, 1, false));

        _resolver.ResolveMilestoneAsync("owner", "repo", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<int?>>(_ => throw new InvalidOperationException("API failure"));

        var service = new MilestoneBackfillSweepService(_gitHub, _resolver, options, NullLogger<MilestoneBackfillSweepService>.Instance)
        {
            StartupDelay = TimeSpan.Zero
        };

        var ex = await Record.ExceptionAsync(() => service.RunSweepAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    private sealed class TestableMilestoneBackfillSweepService : MilestoneBackfillSweepService
    {
        public TestableMilestoneBackfillSweepService(
            IGitHubService gitHub,
            IMilestoneResolver resolver,
            IOptions<MilestoneSyncOptions> options)
            : base(gitHub, resolver, options, NullLogger<MilestoneBackfillSweepService>.Instance)
        {
        }

        public Task CallExecuteAsync(CancellationToken token) => ExecuteAsync(token);
    }
}
