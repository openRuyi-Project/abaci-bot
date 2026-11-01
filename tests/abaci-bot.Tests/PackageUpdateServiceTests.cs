using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class PackageUpdateServiceTests
{
    [Fact]
    public async Task RunSweepAsync_CleansUpInactiveFailedPrs_PastTtl()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        // An inactive failed PR last updated 10 days ago (TTL is 7 days)
        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 88);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-10));
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, "Auto-Update");
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 88, Arg.Any<CancellationToken>())
            .Returns(fullPr);

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow.AddDays(-9));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new Octokit.StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            FailedPrTtlDays = 7,
            MaxConcurrentPullRequests = 5
        });

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            publisherMock,
            obsVerifier,
            options);

        await service.RunSweepAsync();

        // Verify inactive failed PR #88 was closed
        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 88, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RecordFailure_EntersCooldown_AfterMaxConsecutiveFailures()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            MaxConsecutiveFailures = 3,
            GiveUpIssueCooldownDays = 30
        });

        var gitHubMock = Substitute.For<IGitHubService>();
        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);

        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            options);

        // Initial state: not in cooldown
        Assert.False(service.IsInCooldown("failing-pkg"));

        // Record 1st and 2nd failure
        service.RecordFailure("failing-pkg");
        Assert.False(service.IsInCooldown("failing-pkg"));

        service.RecordFailure("failing-pkg");
        Assert.False(service.IsInCooldown("failing-pkg"));

        // 3rd failure reaches MaxConsecutiveFailures (3) and triggers cooldown
        service.RecordFailure("failing-pkg");
        Assert.True(service.IsInCooldown("failing-pkg"));
    }
}
