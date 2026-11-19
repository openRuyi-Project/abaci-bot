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
}
