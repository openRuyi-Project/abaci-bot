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
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
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
        var entered1 = service.RecordFailure("failing-pkg");
        Assert.False(entered1);
        Assert.False(service.IsInCooldown("failing-pkg"));

        var entered2 = service.RecordFailure("failing-pkg");
        Assert.False(entered2);
        Assert.False(service.IsInCooldown("failing-pkg"));

        // 3rd failure reaches MaxConsecutiveFailures (3) and triggers cooldown
        var entered3 = service.RecordFailure("failing-pkg");
        Assert.True(entered3);
        Assert.True(service.IsInCooldown("failing-pkg"));
    }

    [Fact]
    public async Task RunSweepAsync_DoesNotDeleteBranch_WhenHeadRepoIsForkOrPrefixMismatch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 89);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-10));
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync(
            "openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        // PR from a fork repository: "external-fork/openRuyi"
        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        var forkRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(forkRepo, "external-fork/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, forkRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);

        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 89, Arg.Any<CancellationToken>())
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

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            options);

        await service.RunSweepAsync();

        // CloseIssue is called, but DeleteRefAsync must NOT be called on target repo for a fork's branch!
        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 89, Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_DryRun_DoesNotEnsureLabels()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = true,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            Array.Empty<IUpdateCandidateSource>(),
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            options);

        await service.RunSweepAsync();

        await gitHubMock.DidNotReceive().EnsureLabelsExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<(string, string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publisher_AttachesStatusStaleExempt_WhenPublishingPr()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout.openruyi.cn", false);
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.2.2\nRelease: 1\n", null, true);

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 77);
        typeof(PullRequest).GetProperty(nameof(PullRequest.HtmlUrl))?.SetValue(createdPr, "https://github.com/pr/77");

        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha123");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.CreatePullRequestAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi");

        Assert.True(result.Success);
        // Verify StatusStaleExempt and AutoUpdate were attached
        await gitHubMock.Received(1).AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 77,
            Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdate) && l.Contains(GitHubLabels.StatusStaleExempt)),
            Arg.Any<CancellationToken>());
    }
}
