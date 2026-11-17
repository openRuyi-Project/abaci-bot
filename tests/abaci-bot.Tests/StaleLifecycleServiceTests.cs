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

    private readonly Dictionary<int, Issue> _issuesByNumber = new();

    private static void SetProperty<T, TVal>(T obj, string propertyName, TVal value)
    {
        var prop = typeof(T).GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on {typeof(T).Name}");
        prop.SetValue(obj, value);
    }

    public StaleLifecycleServiceTests()
    {
        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitHubRateLimitSnapshot(5000, null, 30, null)));
        _gitHub.GetIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var num = callInfo.ArgAt<int>(2);
                return _issuesByNumber.TryGetValue(num, out var issue) ? issue : null;
            });
        _gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var num = callInfo.ArgAt<int>(2);
                if (_issuesByNumber.TryGetValue(num, out var issue) && issue.PullRequest != null)
                {
                    var pr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
                    SetProperty(pr, nameof(PullRequest.Number), num);
                    SetProperty(pr, nameof(PullRequest.Draft), issue.PullRequest.Draft);
                    SetProperty(pr, nameof(PullRequest.State), new StringEnum<ItemState>(issue.State.Value));
                    return Task.FromResult<PullRequest?>(pr);
                }
                return Task.FromResult<PullRequest?>(null);
            });
    }

    /// <summary>
    /// Creates a mock Issue representing an Issue as returned by the GitHub Issues API.
    /// </summary>
    private Issue CreateMockIssue(int number, string title, DateTimeOffset updatedAt, params string[] labelNames)
    {
        var issue = (Issue)RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        SetProperty(issue, nameof(Issue.Number), number);
        SetProperty(issue, nameof(Issue.Title), title);
        SetProperty(issue, nameof(Issue.UpdatedAt), updatedAt);
        SetProperty(issue, nameof(Issue.State), new StringEnum<ItemState>(ItemState.Open));

        var labels = labelNames.Select(name =>
        {
            var label = (Label)RuntimeHelpers.GetUninitializedObject(typeof(Label));
            SetProperty(label, nameof(Label.Name), name);
            return label;
        }).ToList();

        SetProperty(issue, nameof(Issue.Labels), labels);
        _issuesByNumber[number] = issue;
        return issue;
    }

    /// <summary>
    /// Creates a mock Issue representing a Pull Request as returned by the GitHub Issues API.
    /// In the GitHub Issues API payload (/repos/{owner}/{repo}/issues/{number}), the embedded
    /// 'pull_request' object contains URLs but omits the 'draft' field entirely.
    /// </summary>
    private Issue CreateMockPullRequestFromIssuesApi(int number, string title, DateTimeOffset updatedAt, params string[] labelNames)
    {
        var issue = CreateMockIssue(number, title, updatedAt, labelNames);
        var embeddedPr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        SetProperty(embeddedPr, nameof(PullRequest.Draft), false);
        SetProperty(issue, nameof(Issue.PullRequest), embeddedPr);
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
            ExemptLabels = new List<string> { GitHubLabels.StatusStaleExempt, "Workflow: Blocked" },
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        var pinnedIssue = CreateMockIssue(103, "Important Pinned Issue", DateTimeOffset.UtcNow.AddDays(-60), GitHubLabels.StatusStaleExempt);
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
    public async Task RunLifecycleSweepAsync_WhenCandidateHasWipTitle_PreFiltersWithoutLock()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            RequestThrottleMilliseconds = 0,
            DryRun = false
        });

        var wipIssue = CreateMockIssue(105, "WIP: Ongoing refactor", DateTimeOffset.UtcNow.AddDays(-60));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { wipIssue });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // Pre-filtered by IsExempt without modifying GitHub or fetching item
        await _gitHub.DidNotReceive().GetIssueAsync(Arg.Any<string>(), Arg.Any<string>(), 105, Arg.Any<CancellationToken>());
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

        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(50, null, 30, null)); // Below 100

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

        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(null, null, 30, null)); // Auth/network failure indicator

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

        await _gitHub.DidNotReceive().GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>());
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

        await _gitHub.DidNotReceive().GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>());
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

        await _gitHub.Received().GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>());
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
        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(10, null, 30, null));

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

        var expiredCandidate = CreateMockIssue(103, "Stale Issue", DateTimeOffset.UtcNow.AddDays(-10), GitHubLabels.StatusStale);

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

        var candidate1 = CreateMockIssue(201, "Stale 1", DateTimeOffset.UtcNow.AddDays(-10), GitHubLabels.StatusStale);
        var candidate2 = CreateMockIssue(202, "Stale 2", DateTimeOffset.UtcNow.AddDays(-10), GitHubLabels.StatusStale);

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

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenItemUpdatedAfterDiscoveryBeforeLock_DoesNotClose()
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

        // Discovered candidate (appears old at search time)
        var candidate = CreateMockIssue(201, "Stale Issue", DateTimeOffset.UtcNow.AddDays(-40), GitHubLabels.StatusStale);

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        // Concurrently updated by a contributor before lock acquisition
        var freshlyActiveIssue = CreateMockIssue(201, "Stale Issue", DateTimeOffset.UtcNow.AddHours(-1), GitHubLabels.StatusStale);
        _issuesByNumber[201] = freshlyActiveIssue;

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // Must NOT close the newly active item
        await _gitHub.DidNotReceive().CloseIssueAsync(Arg.Any<string>(), Arg.Any<string>(), 201, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenExemptionLabelAddedAfterDiscoveryBeforeLock_DoesNotClose()
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

        // Discovered candidate (has only Status: Stale at discovery time)
        var candidate = CreateMockIssue(202, "Stale Issue", DateTimeOffset.UtcNow.AddDays(-40), GitHubLabels.StatusStale);

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        // Maintainer concurrently added exemption label "Status: stale-exempt" before lock acquisition
        var exemptedIssue = CreateMockIssue(202, "Stale Issue", DateTimeOffset.UtcNow.AddDays(-40), GitHubLabels.StatusStale, GitHubLabels.StatusStaleExempt);
        _issuesByNumber[202] = exemptedIssue;

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // Must NOT close the exempted item
        await _gitHub.DidNotReceive().CloseIssueAsync(Arg.Any<string>(), Arg.Any<string>(), 202, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenCandidateUpdatedAfterDiscoveryBeforeLock_DoesNotMarkStale()
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

        // Discovered candidate without Status: Stale
        var candidate = CreateMockIssue(301, "Active Issue", DateTimeOffset.UtcNow.AddDays(-35));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { candidate });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.Contains("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        // Concurrently updated by a contributor before lock acquisition
        var freshlyActive = CreateMockIssue(301, "Active Issue", DateTimeOffset.UtcNow.AddMinutes(-5));
        _issuesByNumber[301] = freshlyActive;

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // Must NOT mark the item stale
        await _gitHub.DidNotReceive().AddLabelsAsync(Arg.Any<string>(), Arg.Any<string>(), 301, Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenSearchQuotaExhaustedWhileCoreQuotaHealthy_SkipsSweepSafely()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        // Core quota is plentiful (5000), but Search API quota is exhausted (0)
        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, null, 0, null));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            RetryDelay = TimeSpan.Zero
        };
        await service.RunLifecycleSweepAsync();

        // Should not execute search when search quota is depleted
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RunLifecycleSweepAsync_WhenSearchQuotaBelowFourCallsWatermark_SkipsSweepSafely(int remainingSearch)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        // Core quota is plentiful (5000), but Search API quota is strictly below MinSearchWatermarkPerSweep (4)
        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, null, remainingSearch, null));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            RetryDelay = TimeSpan.Zero
        };
        await service.RunLifecycleSweepAsync();

        // Should not execute search when search quota cannot satisfy the 4 required queries per sweep
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenSearchThrowsRateLimitExceededException_CatchesSafely()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        var rateLimitEx = (RateLimitExceededException)RuntimeHelpers.GetUninitializedObject(typeof(RateLimitExceededException));
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Issue>>(rateLimitEx));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);

        // Does not throw unhandled exception
        await service.RunLifecycleSweepAsync();
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenRateLimitExceededDuringStaleMarking_AbortsEntireSweepImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100,
            StaleDays = 30,
            CloseDays = 7,
            DryRun = false
        });

        var staleIssue1 = CreateMockIssue(101, "Issue 101", DateTimeOffset.UtcNow.AddDays(-40));
        var staleIssue2 = CreateMockIssue(102, "Issue 102", DateTimeOffset.UtcNow.AddDays(-40));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { staleIssue1, staleIssue2 });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var rateLimitEx = (RateLimitExceededException)RuntimeHelpers.GetUninitializedObject(typeof(RateLimitExceededException));
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueComment>(rateLimitEx)); // RateLimitExceededException during stale marking

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);

        await service.RunLifecycleSweepAsync();

        // 1. Issue 102 should NOT be processed
        await _gitHub.DidNotReceive().GetIssueAsync("openRuyi-Project", "openRuyi", 102, Arg.Any<CancellationToken>());

        // 2. Stage 3 search for close candidates must NOT be executed at all
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenSearchQuotaUnavailable_AbortsSweepSafely()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        // Search quota is null (e.g. rate limit response did not contain search quota or header omitted)
        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, null, null, null));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            RetryDelay = TimeSpan.Zero
        };

        await service.RunLifecycleSweepAsync();

        // Fail-safe: Should abort sweep without calling search
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenServerResetTimeExceedsThreshold_AbortsSweepImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        // Search quota below watermark, and reset is 45 minutes in the future (exceeding 30 min safety cap)
        var futureReset = DateTimeOffset.UtcNow.AddMinutes(45);
        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, null, 1, futureReset));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);

        await service.RunLifecycleSweepAsync();

        // Should abort immediately without waiting or searching
        await _gitHub.Received(1).GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(10)]    // Future reset (10ms)
    [InlineData(-5000)] // Past reset (-5s)
    public async Task RunLifecycleSweepAsync_WhenQuotaLowWithResetTarget_ProbesAgainInsteadOfAborting(int resetOffsetMs)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100
        });

        var resetTime = DateTimeOffset.UtcNow.AddMilliseconds(resetOffsetMs);
        var lowSnapshot = new GitHubRateLimitSnapshot(5000, null, 1, resetTime);
        var healthySnapshot = new GitHubRateLimitSnapshot(5000, null, 30, null);

        var callCount = 0;
        _gitHub.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++callCount == 1 ? lowSnapshot : healthySnapshot));

        _gitHub.SearchStaleCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance)
        {
            RetryDelay = TimeSpan.Zero
        };

        await service.RunLifecycleSweepAsync();

        // Verifies it probed again and then successfully resumed candidate discovery
        await _gitHub.Received(2).GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>());
        await _gitHub.Received().SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    public enum PrEligibilityScenario
    {
        Eligible,
        Draft,
        NotFound,
        Closed
    }

    [Theory]
    [InlineData(PrEligibilityScenario.Eligible)]
    [InlineData(PrEligibilityScenario.Draft)]
    [InlineData(PrEligibilityScenario.NotFound)]
    [InlineData(PrEligibilityScenario.Closed)]
    public async Task RunLifecycleSweepAsync_PullRequestEligibilityVerification_HandlesVariousStates(PrEligibilityScenario scenario)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            DryRun = false
        });

        var prIssue = CreateMockPullRequestFromIssuesApi(101, "Candidate PR", DateTimeOffset.UtcNow.AddDays(-40));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { prIssue });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        PullRequest? prResult = null;
        if (scenario != PrEligibilityScenario.NotFound)
        {
            var fullPr = (PullRequest)RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            SetProperty(fullPr, nameof(PullRequest.Number), 101);
            SetProperty(fullPr, nameof(PullRequest.Draft), scenario == PrEligibilityScenario.Draft);
            SetProperty(fullPr, nameof(PullRequest.State), new StringEnum<ItemState>(scenario == PrEligibilityScenario.Closed ? ItemState.Closed : ItemState.Open));
            prResult = fullPr;
        }

        _gitHub.GetPullRequestAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PullRequest?>(prResult));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        if (scenario == PrEligibilityScenario.Eligible)
        {
            // Positive verification (P2-1): Eligible open non-draft PR is marked stale and commented on
            await _gitHub.Received(1).AddLabelsAsync(
                "openRuyi-Project", "openRuyi", 101, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.StatusStale)), Arg.Any<CancellationToken>());
            await _gitHub.Received(1).CreateCommentAsync(
                "openRuyi-Project", "openRuyi", 101, Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        else
        {
            // Defensive verification: Ineligible states must not be marked stale or commented on
            await _gitHub.DidNotReceive().AddLabelsAsync(
                "openRuyi-Project", "openRuyi", 101, Arg.Any<string[]>(), Arg.Any<CancellationToken>());
            await _gitHub.DidNotReceive().CreateCommentAsync(
                "openRuyi-Project", "openRuyi", 101, Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenPullRequestRecentlyUpdated_RejectsBeforeFetchingRemotePullRequest()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            StaleDays = 30,
            CloseDays = 7,
            DryRun = false
        });

        // Search returned a stale issue, but authoritative GetIssueAsync reveals it was recently updated (5 minutes ago)
        var prIssue = CreateMockPullRequestFromIssuesApi(101, "Candidate PR", DateTimeOffset.UtcNow.AddDays(-40));
        var recentlyUpdatedIssue = CreateMockPullRequestFromIssuesApi(101, "Candidate PR", DateTimeOffset.UtcNow.AddMinutes(-5));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { prIssue });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        _gitHub.GetIssueAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>())
            .Returns(recentlyUpdatedIssue);

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);
        await service.RunLifecycleSweepAsync();

        // Must reject via in-memory UpdatedAt check BEFORE making the network call to GetPullRequestAsync (P3-4)
        await _gitHub.DidNotReceive().GetPullRequestAsync(
            "openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 101, Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, true, 50, 20, null)]
    [InlineData(false, false, 60, 20, 60)]
    [InlineData(false, false, 20, 60, 60)]
    [InlineData(false, false, null, 20, 20)]
    [InlineData(false, false, 60, null, 60)]
    [InlineData(false, true, 60, 20, 60)]
    [InlineData(true, false, 60, 20, 20)]
    public void SelectBackoffTarget_SelectsAppropriateResetWindow(
        bool isCoreHealthy, bool isSearchHealthy, int? coreOffsetSec, int? searchOffsetSec, int? expectedOffsetSec)
    {
        var now = DateTimeOffset.UtcNow;
        var coreReset = coreOffsetSec.HasValue ? now.AddSeconds(coreOffsetSec.Value) : (DateTimeOffset?)null;
        var searchReset = searchOffsetSec.HasValue ? now.AddSeconds(searchOffsetSec.Value) : (DateTimeOffset?)null;
        var snapshot = new GitHubRateLimitSnapshot(100, coreReset, 10, searchReset);

        var result = StaleLifecycleService.SelectBackoffTarget(isCoreHealthy, isSearchHealthy, snapshot);

        if (expectedOffsetSec.HasValue)
        {
            Assert.NotNull(result);
            Assert.Equal(now.AddSeconds(expectedOffsetSec.Value).ToUnixTimeSeconds(), result.Value.ToUnixTimeSeconds());
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Fact]
    public void IsRateLimitOrAbuse_CorrectlyClassifiesExceptions()
    {
        var rateLimitEx = (RateLimitExceededException)RuntimeHelpers.GetUninitializedObject(typeof(RateLimitExceededException));
        Assert.True(StaleLifecycleService.IsRateLimitOrAbuse(rateLimitEx, out _, out var type1));
        Assert.Equal("Core", type1);

        var secondaryEx = (SecondaryRateLimitExceededException)RuntimeHelpers.GetUninitializedObject(typeof(SecondaryRateLimitExceededException));
        Assert.True(StaleLifecycleService.IsRateLimitOrAbuse(secondaryEx, out var delay2, out var type2));
        Assert.Equal("Secondary", type2);
        Assert.Equal(TimeSpan.FromSeconds(60), delay2);

        var abuseEx = (AbuseException)RuntimeHelpers.GetUninitializedObject(typeof(AbuseException));
        Assert.True(StaleLifecycleService.IsRateLimitOrAbuse(abuseEx, out var delay3, out var type3));
        Assert.Equal("Abuse", type3);
        Assert.Equal(TimeSpan.FromSeconds(60), delay3);

        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(System.Net.HttpStatusCode.TooManyRequests);
        var tooManyRequestsEx = new ApiException(response);
        Assert.True(StaleLifecycleService.IsRateLimitOrAbuse(tooManyRequestsEx, out var delay4, out var type4));
        Assert.Equal("HTTP 429", type4);
        Assert.Equal(TimeSpan.FromSeconds(60), delay4);

        var forbiddenEx = (ForbiddenException)RuntimeHelpers.GetUninitializedObject(typeof(ForbiddenException));
        Assert.False(StaleLifecycleService.IsRateLimitOrAbuse(forbiddenEx, out _, out _));

        var genericEx = new InvalidOperationException("Something else");
        Assert.False(StaleLifecycleService.IsRateLimitOrAbuse(genericEx, out _, out _));
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenSecondaryRateLimitExceededDuringStaleMarking_AbortsEntireSweepImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100,
            StaleDays = 30,
            CloseDays = 7,
            DryRun = false
        });

        var staleIssue1 = CreateMockIssue(101, "Issue 101", DateTimeOffset.UtcNow.AddDays(-40));
        var staleIssue2 = CreateMockIssue(102, "Issue 102", DateTimeOffset.UtcNow.AddDays(-40));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { staleIssue1, staleIssue2 });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var secondaryEx = (SecondaryRateLimitExceededException)RuntimeHelpers.GetUninitializedObject(typeof(SecondaryRateLimitExceededException));
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueComment>(secondaryEx));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);

        await service.RunLifecycleSweepAsync();

        // Issue 102 should NOT be processed
        await _gitHub.DidNotReceive().GetIssueAsync("openRuyi-Project", "openRuyi", 102, Arg.Any<CancellationToken>());

        // Stage 3 search for close candidates must NOT be executed
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenAbuseExceptionDuringAutoClosing_AbortsEntireSweepImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100,
            StaleDays = 30,
            CloseDays = 7,
            DryRun = false
        });

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Is<string?>(s => s != null && s.StartsWith("-label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        var closeIssue1 = CreateMockIssue(201, "Issue 201", DateTimeOffset.UtcNow.AddDays(-40), GitHubLabels.StatusStale);
        var closeIssue2 = CreateMockIssue(202, "Issue 202", DateTimeOffset.UtcNow.AddDays(-40), GitHubLabels.StatusStale);

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { closeIssue1, closeIssue2 });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Is<string?>(s => s != null && s.StartsWith("label:")), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        _gitHub.GetIssueAsync("openRuyi-Project", "openRuyi", 201, Arg.Any<CancellationToken>())
            .Returns(closeIssue1);
        _gitHub.GetIssueAsync("openRuyi-Project", "openRuyi", 202, Arg.Any<CancellationToken>())
            .Returns(closeIssue2);

        var abuseEx = (AbuseException)RuntimeHelpers.GetUninitializedObject(typeof(AbuseException));
        _gitHub.CloseIssueAsync("openRuyi-Project", "openRuyi", 201, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(abuseEx));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);

        await service.RunLifecycleSweepAsync();

        // Issue 202 should NOT be closed
        await _gitHub.DidNotReceive().CloseIssueAsync("openRuyi-Project", "openRuyi", 202, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenSecondaryRateLimitExceededInFirstRepository_AbortsRemainingRepositories()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/repoA", "openRuyi-Project/repoB" },
            MinRateLimitRemaining = 100
        });

        var secondaryEx = (SecondaryRateLimitExceededException)RuntimeHelpers.GetUninitializedObject(typeof(SecondaryRateLimitExceededException));
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "repoA", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Issue>>(secondaryEx));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);

        await service.RunLifecycleSweepAsync();

        // repoB should NOT be queried at all
        await _gitHub.DidNotReceive().SearchStaleCandidatesAsync("openRuyi-Project", "repoB", Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleSweepAsync_WhenPlainForbiddenExceptionDuringStaleMarking_ContinuesToNextCandidate()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new StaleLifecycleOptions
        {
            MonitoredRepositories = new List<string> { "openRuyi-Project/openRuyi" },
            MinRateLimitRemaining = 100,
            StaleDays = 30,
            CloseDays = 7,
            DryRun = false
        });

        var staleIssue1 = CreateMockIssue(101, "Issue 101", DateTimeOffset.UtcNow.AddDays(-40));
        var staleIssue2 = CreateMockIssue(102, "Issue 102", DateTimeOffset.UtcNow.AddDays(-40));

        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue> { staleIssue1, staleIssue2 });
        _gitHub.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), isPr: true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Issue>());

        _gitHub.GetIssueAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>())
            .Returns(staleIssue1);
        _gitHub.GetIssueAsync("openRuyi-Project", "openRuyi", 102, Arg.Any<CancellationToken>())
            .Returns(staleIssue2);

        var forbiddenEx = (ForbiddenException)RuntimeHelpers.GetUninitializedObject(typeof(ForbiddenException));
        _gitHub.CreateCommentAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueComment>(forbiddenEx));

        var service = new StaleLifecycleService(_gitHub, options, _config, _lockManager, NullLogger<StaleLifecycleService>.Instance);

        await service.RunLifecycleSweepAsync();

        // Issue 102 SHOULD still be checked and processed despite 101 getting a non-rate-limit 403
        await _gitHub.Received().GetIssueAsync("openRuyi-Project", "openRuyi", 102, Arg.Any<CancellationToken>());
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
