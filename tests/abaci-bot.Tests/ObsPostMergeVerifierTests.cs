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

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_Skips_WhenTitleMismatchOrRevisionNotSynchronized()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        // PR with title not matching SPECS bump pattern
        var prNonMatching = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(prNonMatching, 60);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(prNonMatching, "docs: update readme");

        // PR where OBS service revision does NOT match merge commit sha
        var prWaitingObs = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(prWaitingObs, 61);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(prWaitingObs, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(prWaitingObs, "newSha");

        gitHubMock.SearchMergedPullRequestsAsync("openRuyi-Project", "openRuyi", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { prNonMatching, prWaitingObs });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("oldShaDifferentFromNewSha");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);

        await verifier.VerifyMergedPullRequestsAsync("openRuyi-Project/openRuyi");

        // No build query or comment should be created
        await obsServiceMock.DidNotReceive().GetPackageBuildResultAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("invalid_repo")]
    [InlineData("")]
    public async Task VerifyMergedPullRequestsAsync_ReturnsEarly_OnInvalidRepository(string targetRepo)
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var verifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));

        await verifier.VerifyMergedPullRequestsAsync(targetRepo);
        await gitHubMock.DidNotReceive().SearchMergedPullRequestsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_AddsBlockedLabel_OnFailedBuild()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
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
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 70);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaFail");

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("shaFail");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Failed, "failed"));

        gitHubMock.GetIssueCommentsAsync("owner", "repo", 70, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).AddLabelsAsync("owner", "repo", 70, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.AutoUpdateBlocked)), Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 70, Arg.Is<string>(c => c.Contains("`Failed`")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_ReclaimsBranch_AndRemovesBlockedLabel_OnSucceededBuild()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
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
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr, 80);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr, "shaSuccess");

        var headRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef, "shaHeadPr");
        var headRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef, headRepo);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr, headRef);

        var blockedLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(blockedLabel, GitHubLabels.AutoUpdateBlocked);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, new List<Label> { blockedLabel });

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>())
            .Returns("shaSuccess");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", "riscv64", "riscv64", Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "riscv64", "riscv64", ObsStatusCode.Succeeded, "succeeded"));

        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("shaHeadPr");

        gitHubMock.GetIssueCommentsAsync("owner", "repo", 80, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IssueComment>());

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        await gitHubMock.Received(1).RemoveLabelAsync("owner", "repo", 80, GitHubLabels.AutoUpdateBlocked, Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 80, Arg.Is<string>(c => c.Contains("`Succeeded`")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryReclaimMergedBranch_SkipsWhenOpenPrExists_OrHeadShaMoved()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var obsServiceMock = Substitute.For<IObsBuildService>();

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            TargetRepository = "owner/repo",
            DryRun = false,
            BranchPrefix = "autoupdate/"
        });

        // PR 1: open PR is reusing the branch
        var pr1 = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr1, 91);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr1, "SPECS: bump curl to 8.21.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr1, "shaMerged1");

        var headRef1 = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef1, "autoupdate/curl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef1, "shaHead1");
        var headRepo1 = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo1, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef1, headRepo1);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr1, headRef1);

        // PR 2: head branch SHA moved forward
        var pr2 = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(pr2, 92);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(pr2, "SPECS: bump openssl to 3.1.0");
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))?.SetValue(pr2, "shaMerged2");

        var headRef2 = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(headRef2, "autoupdate/openssl");
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(headRef2, "shaHead2_old");
        var headRepo2 = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
        typeof(Repository).GetProperty(nameof(Repository.FullName))?.SetValue(headRepo2, "owner/repo");
        typeof(GitReference).GetProperty(nameof(GitReference.Repository))?.SetValue(headRef2, headRepo2);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(pr2, headRef2);

        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pr1, pr2 });

        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "curl", Arg.Any<CancellationToken>()).Returns("shaMerged1");
        obsServiceMock.GetPackageServiceRevisionAsync("openruyi", "openssl", Arg.Any<CancellationToken>()).Returns("shaMerged2");

        obsServiceMock.GetPackageBuildResultAsync("openruyi", "curl", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "curl", "repo", "arch", ObsStatusCode.Succeeded, "succeeded"));
        obsServiceMock.GetPackageBuildResultAsync("openruyi", "openssl", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObsPackageBuildResult("openruyi", "openssl", "repo", "arch", ObsStatusCode.Succeeded, "succeeded"));

        // PR 1 has an active open PR reusing the branch
        var activeOpenPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(activeOpenPr, 999);
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(activeOpenPr);

        // PR 2 has no open PR, but current branch SHA is different from merged PR head SHA
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/openssl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/openssl", Arg.Any<CancellationToken>())
            .Returns("shaHead2_newMovedAhead");

        var verifier = new ObsPostMergeVerifier(gitHubMock, obsServiceMock, options);
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");

        // Neither branch should be deleted
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>());
        await gitHubMock.DidNotReceive().DeleteRefAsync("owner", "repo", "autoupdate/openssl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyMergedPullRequestsAsync_HandlesExceptionsGracefully()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.SearchMergedPullRequestsAsync("owner", "repo", Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PullRequest>>(_ => throw new HttpRequestException("Network failure"));

        var verifier = new ObsPostMergeVerifier(gitHubMock, Substitute.For<IObsBuildService>(), Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
        await verifier.VerifyMergedPullRequestsAsync("owner/repo");
    }
}
