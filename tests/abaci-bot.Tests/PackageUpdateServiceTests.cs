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
            Substitute.For<ISpecCatalog>(),
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
            Substitute.For<ISpecCatalog>(),
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
            Substitute.For<ISpecCatalog>(),
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
            Substitute.For<ISpecCatalog>(),
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

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        // Verify StatusStaleExempt and AutoUpdate were attached
        await gitHubMock.Received(1).AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 77,
            Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdate) && l.Contains(GitHubLabels.StatusStaleExempt)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_StabilizationWindow_SkipsRecentCandidates_UnlessSecurityAdvisory()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        // curl: published 10 hours ago, not security -> should be skipped by 72h window
        var routineCandidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout", false,
            PublishedAt: DateTimeOffset.UtcNow.AddHours(-10));
        // openssl: published 10 hours ago, IS security advisory -> should bypass 72h window
        var securityCandidate = new UpdateCandidate("openssl", "3.0.0", "3.0.1", CandidateOrigin.Lookout, "https://lookout", true,
            PublishedAt: DateTimeOffset.UtcNow.AddHours(-10));

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { routineCandidate, securityCandidate });

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriterMock.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "Name: openssl\nVersion: 3.0.1\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 101));

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("openssl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("openssl", "openssl", "Name: openssl\nVersion: 3.0.0\nRelease: 1\n", "SPECS/openssl/openssl.spec"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            CooldownHours = 72,
            RequestThrottleMilliseconds = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options);

        await service.RunSweepAsync();

        // curl was skipped due to 72h cooldown
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            routineCandidate, Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // openssl was published because IsSecurityAdvisory bypassed stabilization cooldown
        await publisherMock.Received(1).PublishUpdateAsync(
            securityCandidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/openssl/openssl.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_UsesSpecCatalog_WhenAvailable()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate = new UpdateCandidate("rust-aho-corasick", "0.7.18", "0.7.20", CandidateOrigin.RustCrate, string.Empty, false);
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        var specContext = new SpecContext("rust-aho-corasick", "rust-aho-corasick-0.7", "Name: rust-aho-corasick\nVersion: 0.7.18\n", "SPECS/rust-aho-corasick-0.7/rust-aho-corasick.spec");
        specCatalogMock.GetSpecAsync("rust-aho-corasick", Arg.Any<CancellationToken>())
            .Returns(specContext);

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(specContext).Returns(true);
        rewriterMock.Rewrite(specContext, "0.7.20")
            .Returns(new SpecRewriteResult(true, "Name: rust-aho-corasick\nVersion: 0.7.20\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(candidate, Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 102));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            CooldownHours = 0,
            RequestThrottleMilliseconds = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options);

        await service.RunSweepAsync();

        await specCatalogMock.Received(1).GetSpecAsync("rust-aho-corasick", Arg.Any<CancellationToken>());
        await publisherMock.Received(1).PublishUpdateAsync(
            candidate, Arg.Any<SpecRewriteResult>(),
            "openRuyi-Project/openRuyi", "main", "SPECS/rust-aho-corasick-0.7/rust-aho-corasick.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ResetCooldown_ClearsCooldownAndFailureCount()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            MaxConsecutiveFailures = 2,
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
            Substitute.For<ISpecCatalog>(),
            options);

        // Record 2 failures to enter cooldown
        service.RecordFailure("test-pkg");
        service.RecordFailure("test-pkg");
        Assert.True(service.IsInCooldown("test-pkg"));

        // Reset cooldown
        service.ResetCooldown("test-pkg");
        Assert.False(service.IsInCooldown("test-pkg"));
    }

    [Fact]
    public async Task RunSweepAsync_DeletesBranch_WhenHeadRepoMatchesTargetRepo()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 95);
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

        var targetRepoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(targetRepoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, targetRepoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);

        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 95, Arg.Any<CancellationToken>())
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
            Substitute.For<ISpecCatalog>(),
            options);

        await service.RunSweepAsync();

        await gitHubMock.Received(1).CloseIssueAsync("openRuyi-Project", "openRuyi", 95, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).DeleteRefAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_ResetsCooldown_WhenTrackingIssueIsClosed()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout", false,
            PublishedAt: DateTimeOffset.UtcNow.AddDays(-5));
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("curl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("curl", "curl", "Name: curl\nVersion: 8.2.1\n", "SPECS/curl/curl.spec"));

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriterMock.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(new SpecRewriteResult(true, "Name: curl\nVersion: 8.2.2\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, PullRequestNumber: 201));

        // Mock issue created when entering cooldown
        var createdIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(createdIssue, 888);
        typeof(Issue).GetProperty(nameof(Issue.State))?.SetValue(createdIssue, new StringEnum<ItemState>(ItemState.Open));

        gitHubMock.CreateIssueAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(createdIssue);
        gitHubMock.GetIssueAsync("openRuyi-Project", "openRuyi", 888, Arg.Any<CancellationToken>())
            .Returns(createdIssue);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConsecutiveFailures = 1,
            IssueOnGivingUp = true,
            CooldownHours = 0,
            RequestThrottleMilliseconds = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options);

        // 1. Record a failure to trigger cooldown and file tracking issue #888
        var stalePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(stalePr, 88);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(stalePr, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(stalePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { stalePr });

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 88, Arg.Any<CancellationToken>()).Returns(fullPr);

        var failedRun = (WorkflowRun)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRun));
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.CreatedAt))?.SetValue(failedRun, DateTimeOffset.UtcNow);
        typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.Conclusion))?.SetValue(failedRun, new StringEnum<WorkflowRunConclusion>(WorkflowRunConclusion.Failure));
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(new[] { failedRun });

        // Trigger failure processing
        await service.RunSweepAsync();
        Assert.True(service.IsInCooldown("curl"));

        // 2. Next sweep: tracking issue #888 is closed by human -> should reset cooldown and publish update
        typeof(Issue).GetProperty(nameof(Issue.State))?.SetValue(createdIssue, new StringEnum<ItemState>(ItemState.Closed));
        await service.RunSweepAsync();

        Assert.False(service.IsInCooldown("curl"));
        await publisherMock.Received(1).PublishUpdateAsync(
            candidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunSweepAsync_ResetsCooldown_WhenManuallyUpdatedOnMain()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        var candidate = new UpdateCandidate("curl", "8.2.1", "8.2.2", CandidateOrigin.Lookout, "https://lookout", false);
        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>()).Returns(new[] { candidate });

        // Spec on main already at 8.2.2 (human manually upgraded!)
        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("curl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("curl", "curl", "Name: curl\nVersion: 8.2.2\nRelease: 1\n", "SPECS/curl/curl.spec"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConsecutiveFailures = 1,
            CooldownHours = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            Array.Empty<ISpecVersionRewriter>(),
            Substitute.For<IPackageUpdatePublisher>(),
            obsVerifier,
            specCatalogMock,
            options);

        // Put package in cooldown
        service.RecordFailure("curl");
        Assert.True(service.IsInCooldown("curl"));

        // Sweep runs and detects spec already at target version -> resets cooldown
        await service.RunSweepAsync();

        Assert.False(service.IsInCooldown("curl"));
    }

    [Fact]
    public async Task RunSweepAsync_AllowsSecurityAdvisories_WhenRoutineConcurrentLimitReached()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRateLimitSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubRateLimitSnapshot(5000, DateTimeOffset.UtcNow.AddHours(1), 30, DateTimeOffset.UtcNow.AddMinutes(1)));

        // 1 open active PR currently running
        var activePr = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(activePr, 101);
        typeof(Issue).GetProperty(nameof(Issue.UpdatedAt))?.SetValue(activePr, (DateTimeOffset?)DateTimeOffset.UtcNow);
        var autoUpdateLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(autoUpdateLabel, GitHubLabels.AutoUpdate);
        typeof(Issue).GetProperty(nameof(Issue.Labels))?.SetValue(activePr, new List<Label> { autoUpdateLabel });

        gitHubMock.SearchStaleCandidatesAsync("openRuyi-Project", "openRuyi", Arg.Any<DateTimeOffset>(), true, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { activePr });

        var fullPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/active-pkg");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(fullPr, headRef);
        gitHubMock.GetPullRequestAsync("openRuyi-Project", "openRuyi", 101, Arg.Any<CancellationToken>())
            .Returns(fullPr);
        gitHubMock.ListWorkflowRunsAsync("openRuyi-Project", "openRuyi", "autoupdate/active-pkg", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkflowRun>()); // Still active/pending, not failed

        // Two candidates: one security advisory, one routine update
        var securityCandidate = new UpdateCandidate("openssl", "3.0.0", "3.0.1", CandidateOrigin.Lookout, "https://lookout", true);
        var routineCandidate = new UpdateCandidate("curl", "8.2.0", "8.2.1", CandidateOrigin.Lookout, "https://lookout", false);

        var sourceMock = Substitute.For<IUpdateCandidateSource>();
        sourceMock.GetCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { routineCandidate, securityCandidate });

        var specCatalogMock = Substitute.For<ISpecCatalog>();
        specCatalogMock.GetSpecAsync("openssl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("openssl", "openssl", "Name: openssl\nVersion: 3.0.0\nRelease: 1\n", "SPECS/openssl/openssl.spec"));
        specCatalogMock.GetSpecAsync("curl", Arg.Any<CancellationToken>())
            .Returns(new SpecContext("curl", "curl", "Name: curl\nVersion: 8.2.0\nRelease: 1\n", "SPECS/curl/curl.spec"));

        var rewriterMock = Substitute.For<ISpecVersionRewriter>();
        rewriterMock.CanHandle(Arg.Any<SpecContext>()).Returns(true);
        rewriterMock.Rewrite(Arg.Any<SpecContext>(), Arg.Any<string>())
            .Returns(callInfo => new SpecRewriteResult(true, $"Name: {callInfo.Arg<SpecContext>().PackageName}\nVersion: {callInfo.Arg<string>()}\nRelease: 1\n", null, true));

        var publisherMock = Substitute.For<IPackageUpdatePublisher>();
        publisherMock.PublishUpdateAsync(Arg.Any<UpdateCandidate>(), Arg.Any<SpecRewriteResult>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResult(true, false, null, 202, "https://pr/202"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            Enabled = true,
            DryRun = false,
            TargetRepository = "openRuyi-Project/openRuyi",
            MaxConcurrentPullRequests = 1, // Only 1 routine PR allowed, which is already occupied by PR #101!
            CooldownHours = 0
        });

        var obsVerifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), options);
        var service = new PackageUpdateService(
            gitHubMock,
            new[] { sourceMock },
            new[] { rewriterMock },
            publisherMock,
            obsVerifier,
            specCatalogMock,
            options);

        await service.RunSweepAsync();

        // Security advisory MUST be published thanks to dedicated concurrency allowance
        await publisherMock.Received(1).PublishUpdateAsync(
            securityCandidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/openssl/openssl.spec", Arg.Any<CancellationToken>());

        // Routine candidate MUST NOT be published as routine concurrent limit (1) was reached
        await publisherMock.DidNotReceive().PublishUpdateAsync(
            routineCandidate, Arg.Any<SpecRewriteResult>(), "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec", Arg.Any<CancellationToken>());
    }
}
