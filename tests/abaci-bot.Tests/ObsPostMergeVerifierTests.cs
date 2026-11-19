using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class ObsPostMergeVerifierTests
{
    [Fact]
    public async Task VerifyMergedPullRequestsAsync_UpdatesExistingComment_WhenStatusChanges()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        // 1. Mock merged PR
        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        // 2. Mock OBS service returning synchronized revision and Failed status
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        // 3. Mock existing comment showing 'Building' status
        var existingComment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Id))?.SetValue(existingComment, 999L);
        var oldBody = """
            ### OBS Build Verification
            - **Project**: `openruyi`
            - **Package**: `curl`
            - **Repository / Arch**: `riscv64` / `riscv64`
            - **Status**: `Building` (`building`)
            - **Synchronized Commit**: `sha123`
            """;
        typeof(IssueComment).GetProperty(nameof(IssueComment.Body))?.SetValue(existingComment, oldBody);

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(new[] { existingComment });

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // Verify UpdateCommentAsync was invoked on comment 999 with Failed status
        await gitHubMock.Received(1).UpdateCommentAsync(
            "openRuyi-Project", "openRuyi", 999L, Arg.Is<string>(b => b.Contains("`Failed`")), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CreateCommentAsync(
            "openRuyi-Project", "openRuyi", 42, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_SkipsComment_WhenAlreadyUpToDate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");
        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        var existingComment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Id))?.SetValue(existingComment, 999L);
        var matchingBody = """
            ### OBS Build Verification
            - **Project**: `openruyi`
            - **Package**: `curl`
            - **Repository / Arch**: `riscv64` / `riscv64`
            - **Status**: `Succeeded` (`succeeded`)
            - **Synchronized Commit**: `sha123`
            """;
        typeof(IssueComment).GetProperty(nameof(IssueComment.Body))?.SetValue(existingComment, matchingBody);

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(new[] { existingComment });

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        await gitHubMock.DidNotReceive().UpdateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_RemovesBlockedLabel_WhenBuildSucceeds()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");

        var blockedLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(blockedLabel, GitHubLabels.AutoUpdateBlocked);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new[] { blockedLabel });

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        await gitHubMock.Received(1).RemoveLabelAsync("openRuyi-Project", "openRuyi", 42, GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotAddBlockedLabel_WhenStatusIsBuildingOrUnknown()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64",
                RequireObsSuccessAfterMerge = true
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "sha123");

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        // Building status
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Building, "building"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        await gitHubMock.DidNotReceive().AddLabelsAsync(
            "openRuyi-Project", "openRuyi", 42, Arg.Is<string[]>(l => l.Contains(GitHubLabels.AutoUpdateBlocked)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ReclaimsMergedBranch_WhenBuildSucceeded()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 50);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha456");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "headSha456");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("headSha456");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeSha456");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 50, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // Verify DeleteRefAsync was invoked for the merged autoupdate branch
        await gitHubMock.Received(1).DeleteRefAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotReclaimBranch_WhenDryRunOrFork()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        // DryRun = true
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = true,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 51);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha789");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        var forkRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(forkRepo, "external-fork/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, forkRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeSha789");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 51, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // DidNotReceive DeleteRefAsync
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotReclaimBranch_WhenBranchShaMovedForward()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 52);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeShaOriginal");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "originalMergedSha");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        // The branch has moved to a new commit!
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("newerCommitSha");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeShaOriginal");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 52, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // MUST NOT delete branch because branch head moved
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_DoesNotReclaimBranch_WhenOpenPrExistsOnBranch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 53);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeShaOriginal");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "headSha456");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("headSha456");

        // A new in-flight PR is reusing this head ref!
        var inFlightPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(inFlightPr, 54);
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(inFlightPr);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeShaOriginal");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 53, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // MUST NOT delete branch because an open PR is reusing it
        await gitHubMock.DidNotReceive().DeleteRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ReclaimsMergedBranch_WhenObsFailedAndPastTtl()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "openRuyi-Project/openRuyi",
            DryRun = false,
            BranchPrefix = "autoupdate/",
            FailedPrTtlDays = 7,
            Obs = new ObsOptions
            {
                Project = "openruyi",
                TargetRepository = "riscv64",
                TargetArch = "riscv64"
            }
        });

        var pr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 55);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "mergeSha1");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))?.SetValue(pr, DateTimeOffset.UtcNow.AddDays(-10));

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "headSha1");
        var repoObj = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(repoObj, "openRuyi-Project/openRuyi");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, repoObj);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("headSha1");
        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("mergeSha1");
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        gitHubMock.GetIssueCommentsAsync("openRuyi-Project", "openRuyi", 55, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // MUST delete branch because it aged past TTL even though OBS failed
        await gitHubMock.Received(1).DeleteRefAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>());
    }
}
