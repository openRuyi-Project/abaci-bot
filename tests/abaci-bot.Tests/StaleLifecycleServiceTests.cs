using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using System.Runtime.CompilerServices;
using Xunit;

namespace abaci_bot.Tests;

public class StaleLifecycleServiceTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly PullRequestLockManager _lockManager = new();

    private static Issue CreateMockIssue(int number, string title, DateTimeOffset updatedAt, params string[] labelNames)
    {
        return CreateMockIssue(number, title, updatedAt, isDraft: false, labelNames);
    }

    /// <summary>
    /// Creates a mock Issue. Note: in production, Draft PRs are filtered server-side
    /// via the '-is:draft' query in GitHub Search API. The isDraft parameter allows testing
    /// client-side defensive checks.
    /// </summary>
    private static Issue CreateMockIssue(int number, string title, DateTimeOffset updatedAt, bool isDraft, params string[] labelNames)
    {
        var issue = (Issue)RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, number);
        typeof(Issue).GetProperty(nameof(Issue.Title))?.SetValue(issue, title);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(issue, updatedAt);

        if (isDraft)
        {
            var pr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(pr, true);
            typeof(Issue).GetProperty(nameof(Issue.PullRequest))?.SetValue(issue, pr);
        }

        var labels = labelNames.Select(name =>
        {
            var label = (Label)RuntimeHelpers.GetUninitializedObject(typeof(Label));
            typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(label, name);
            return label;
        }).ToList();

        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(issue, labels);
        return issue;
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenCandidateNotStale_AddsStatusStaleAndComment()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        var candidate = CreateMockIssue(101, "Normal Issue", DateTimeOffset.UtcNow.AddDays(-35));
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.Received(1).AddLabelsAsync("openRuyi-Project", "openRuyi", 101, Arg.Is<string[]>(a => a.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("openRuyi-Project", "openRuyi", 101, options.Value.FormatStaleComment(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().CloseIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenStaleCommentTemplateBlank_UsesDefaultCommentToAnchorGracePeriod()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            StaleCommentTemplate = "   ", // Blank/whitespace
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        var candidate = CreateMockIssue(101, "Normal Issue", DateTimeOffset.UtcNow.AddDays(-35));
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // Verifies fallback comment is posted so updated_at is always refreshed
        await _gitHub.Received(1).CreateCommentAsync("openRuyi-Project", "openRuyi", 101, Arg.Is<string>(c => !string.IsNullOrWhiteSpace(c)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_AcquiresExclusiveLockOnProcessedItem()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>()).Returns(5000);

        var candidate = CreateMockIssue(200, "Lock Test Issue", DateTimeOffset.UtcNow.AddDays(-35));
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var lockObserved = false;
        _gitHub.AddLabelsAsync("openRuyi-Project", "openRuyi", 200, Arg.Is<string[]>(a => a.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // While AddLabelsAsync executes inside the using (_lockManager.AcquireLockAsync),
                // the lock must be active in _lockManager
                lockObserved = _lockManager.ActiveLockCount > 0;
                return Task.CompletedTask;
            });

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        Assert.True(lockObserved, "Expected PullRequestLockManager to track the active lock during item processing.");
        Assert.Equal(0, _lockManager.ActiveLockCount);
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenDryRun_DoesNotMutateGitHub()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            DryRun = true
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        var staleCandidate = CreateMockIssue(101, "Normal Issue", DateTimeOffset.UtcNow.AddDays(-35));
        var expiredCandidate = CreateMockIssue(102, "Expired Issue", DateTimeOffset.UtcNow.AddDays(-45), GitHubLabels.StatusStale);

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { staleCandidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { expiredCandidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // In dry run mode, neither labels nor comments nor closures should occur
        await _gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default(string[])!, default);
        await _gitHub.DidNotReceive().CreateCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().CloseIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenCandidateAlreadyStaleAndExpired_ClosesIssue()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        // Updated 40 days ago and already marked as Status: Stale
        var expiredCandidate = CreateMockIssue(102, "Abandoned Issue", DateTimeOffset.UtcNow.AddDays(-40), GitHubLabels.StatusStale);
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { expiredCandidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 102, Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("openRuyi-Project", "openRuyi", 102, options.Value.FormatCloseComment(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenCandidateHasExemptLabel_SkipsStale()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            ExemptLabels = new List<string> { "Status: Pinned", "Workflow: Blocked" },
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        var pinnedIssue = CreateMockIssue(103, "Important Pinned Issue", DateTimeOffset.UtcNow.AddDays(-60), "Status: Pinned");
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { pinnedIssue });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default(string[])!, default);
        await _gitHub.DidNotReceive().CloseIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenCandidateIsDraftPrOrWipTitle_SkipsStale()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        var draftPr = CreateMockIssue(104, "Draft PR feature", DateTimeOffset.UtcNow.AddDays(-60), isDraft: true);
        var wipIssue = CreateMockIssue(105, "WIP: Ongoing refactor", DateTimeOffset.UtcNow.AddDays(-60), isDraft: false);

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { wipIssue });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { draftPr });

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default(string[])!, default);
        await _gitHub.DidNotReceive().CloseIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenRateLimitLow_PausesAndSkipsSearch()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(50); // Below 100

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync(cts.Token);

        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenRateLimitApiReturnsNull_AbortsSweep()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null); // Auth/network failure indicator

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenMonitoredRepositoriesEmpty_SkipsSweepToPreventAccidentalMutations()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string>(), // Empty
            MinRateLimitRemaining = 100
        });

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenMonitoredRepositoriesHasMalformedEntry_SkipsMalformedEntry()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "invalidrepo", "valid-org/valid-repo" },
            MinRateLimitRemaining = 100
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>()).Returns(5000);
        _gitHub.SearchStaleCandidatesAsync("valid-org", "valid-repo", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.Received().SearchStaleCandidatesAsync("valid-org", "valid-repo", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(Arg.Is<string>(s => s == "invalidrepo"), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenExcludedRepositoriesConfigured_ExcludesMatchingRepositories()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "org/repo1", "org/repo2", "org/repo3" },
            ExcludedRepositories = new List<string> { "org/repo1", "repo3" },
            MinRateLimitRemaining = 100
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>()).Returns(5000);
        _gitHub.SearchStaleCandidatesAsync("org", "repo2", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // Only org/repo2 should be queried (4 search calls: stale issues, stale PRs, close issues, close PRs)
        await _gitHub.Received(4).SearchStaleCandidatesAsync("org", "repo2", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync("org", "repo1", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync("org", "repo3", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WithRequestThrottle_DelaysBetweenRequests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 1,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        var candidate1 = CreateMockIssue(101, "Issue 1", DateTimeOffset.UtcNow.AddDays(-35));
        var candidate2 = CreateMockIssue(102, "Issue 2", DateTimeOffset.UtcNow.AddDays(-35));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate1, candidate2 });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.Received(1).AddLabelsAsync("openRuyi-Project", "openRuyi", 101, Arg.Is<string[]>(a => a.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).AddLabelsAsync("openRuyi-Project", "openRuyi", 102, Arg.Is<string[]>(a => a.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_ReturnsImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            Enabled = false
        });

        var service = new TestableStaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);

        await _gitHub.DidNotReceive().GetRemainingRateLimitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenStartupDelayCancelled_ExitsGracefully()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            Enabled = true
        });

        var service = new TestableStaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            StartupDelay = TimeSpan.FromSeconds(30)
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately during delay

        await service.CallExecuteAsync(cts.Token);

        await _gitHub.DidNotReceive().GetRemainingRateLimitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnabledAndCancelled_StopsCleanly()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            Enabled = true,
            CheckIntervalHours = 1
        });

        var service = new TestableStaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            StartupDelay = TimeSpan.FromMilliseconds(1)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await service.StartAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunning_PerformsSweepAndHandlesExceptions()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            Enabled = true,
            CheckIntervalHours = 1,
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        _gitHub.SearchStaleCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new TestableStaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            StartupDelay = TimeSpan.FromMilliseconds(5)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await service.StartAsync(cts.Token);

        // Wait brief moment for the initial sweep to execute
        await Task.Delay(30);

        await _gitHub.Received().GetRemainingRateLimitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenSweepThrows_LogsErrorAndRecovers()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            Enabled = true,
            CheckIntervalHours = 1,
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        _gitHub.SearchStaleCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Issue>>(_ => throw new InvalidOperationException("Simulated unexpected sweep failure"));

        var logger = Substitute.For<ILogger<StaleLifecycleService>>();
        var service = new TestableStaleLifecycleService(_gitHub, options, _config, _lockManager, logger)
        {
            StartupDelay = TimeSpan.FromMilliseconds(5)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await service.StartAsync(cts.Token);

        await Task.Delay(30);

        // Verify the exception was logged as error
        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenRateLimitPersistentFailure_AbortsAfterRetries()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        // Always low quota
        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>()).Returns(10);

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            RetryDelay = TimeSpan.Zero
        };

        // Executes all 3 retries immediately and aborts without throwing
        await service.RunLifecycleSweepAsync();

        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WithRequestThrottle_AppliesDelayOnCloseCandidate()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            RequestThrottleMilliseconds = 1,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>())
            .Returns(5000);

        var expiredCandidate = CreateMockIssue(103, "Stale Issue", DateTimeOffset.UtcNow.AddDays(-10), isDraft: false, GitHubLabels.StatusStale);

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { expiredCandidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        await _gitHub.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 103, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenCommentFailsOnMarking_DoesNotApplyStatusStaleAndContinuesNextCandidate()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>()).Returns(5000);

        var candidate1 = CreateMockIssue(101, "Candidate 1", DateTimeOffset.UtcNow.AddDays(-35));
        var candidate2 = CreateMockIssue(102, "Candidate 2", DateTimeOffset.UtcNow.AddDays(-35));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate1, candidate2 });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        // Comment fails on candidate 1
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("GitHub comment API transient failure")));

        var logger = Substitute.For<ILogger<StaleLifecycleService>>();
        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, logger);
        await service.RunLifecycleSweepAsync();

        // Candidate 1: comment failed -> label MUST NOT be added (prevents grace period collapse)
        await _gitHub.DidNotReceive().AddLabelsAsync("openRuyi-Project", "openRuyi", 101, Arg.Is<string[]>(a => a.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>());

        // Candidate 2: isolated from candidate 1 failure -> succeeds completely
        await _gitHub.Received(1).CreateCommentAsync("openRuyi-Project", "openRuyi", 102, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).AddLabelsAsync("openRuyi-Project", "openRuyi", 102, Arg.Is<string[]>(a => a.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenCloseFailsOnClosing_ContinuesNextCandidate()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>()).Returns(5000);

        var candidate1 = CreateMockIssue(201, "Stale 1", DateTimeOffset.UtcNow.AddDays(-10), isDraft: false, GitHubLabels.StatusStale);
        var candidate2 = CreateMockIssue(202, "Stale 2", DateTimeOffset.UtcNow.AddDays(-10), isDraft: false, GitHubLabels.StatusStale);

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate1, candidate2 });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        // Close fails on candidate 1
        _gitHub.CloseIssueAsync("openRuyi-Project", "openRuyi", 201, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("GitHub close API failure")));

        var logger = Substitute.For<ILogger<StaleLifecycleService>>();
        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, logger);
        await service.RunLifecycleSweepAsync();

        // Candidate 1: close failed -> close comment not posted
        await _gitHub.DidNotReceive().CreateCommentAsync("openRuyi-Project", "openRuyi", 201, Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Candidate 2: close succeeded and comment posted
        await _gitHub.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 202, Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("openRuyi-Project", "openRuyi", 202, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenMarking_PostsCommentBeforeAddingLabel()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            MinRateLimitRemaining = 100,
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        _gitHub.GetRemainingRateLimitAsync(Arg.Any<CancellationToken>()).Returns(5000);

        var candidate = CreateMockIssue(101, "Candidate", DateTimeOffset.UtcNow.AddDays(-35));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        Received.InOrder(() =>
        {
            _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<string>(), Arg.Any<CancellationToken>());
            _gitHub.AddLabelsAsync("openRuyi-Project", "openRuyi", 101, Arg.Is<string[]>(a => a.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>());
        });
    }

    private class TestableStaleLifecycleService : StaleLifecycleService
    {
        public TestableStaleLifecycleService(
            IGitHubService gitHub,
            IOptions<StaleLifecycleOptions> options,
            IConfiguration config,
            PullRequestLockManager lockManager,
            ILogger<StaleLifecycleService> logger)
            : base(gitHub, options, config, lockManager, logger)
        {
        }

        public Task CallExecuteAsync(CancellationToken ct) => ExecuteAsync(ct);
    }
}
