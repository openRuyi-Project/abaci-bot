using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class GitHubPackageUpdatePublisherTests
{
    [Fact]
    public async Task PublishUpdateAsync_BranchAlreadyAtTargetVersion_SkipsWithoutPushing()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Latest commit by bot
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(("commit_sha_123", "bump curl", "aqua-rium", "bot@openruyi.org"));

        // File on branch is ALREADY at target version 8.22.0
        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\n");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.Contains("already at target version", result.SkipReason);

        // Verify zero writes / zero updates were executed
        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_HumanCommitsOnBranch_StepsAsideAndComments()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Latest commit by human maintainer 'misaka00251'
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(("commit_sha_123", "manual fix", "misaka00251", "maintainer@openruyi.org"));

        var fakePr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(fakePr, 42);

        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(fakePr);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.23.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.23.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.Contains("Human commits by misaka00251 detected", result.SkipReason);

        // Verify warning comment was posted on PR
        await gitHubMock.Received(1).CreateCommentAsync(
            "openRuyi-Project", "openRuyi", 42, Arg.Is<string>(c => c.Contains("contains human commits by @misaka00251")), Arg.Any<CancellationToken>());

        // Verify no overwrite happened
        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_WebFlowAndBotCommits_AreAllowedToUpdate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("commit_sha_123");

        // Merge commit from web-flow (e.g. Update branch button clicked on GitHub)
        gitHubMock.GetBranchLatestCommitAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(("commit_sha_123", "Merge branch 'main'", "web-flow", "noreply@github.com"));

        gitHubMock.GetFileContentAsync("openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.21.0\n");

        var fakePr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(fakePr, 42);

        gitHubMock.FindOpenPullRequestByHeadAsync("openRuyi-Project", "openRuyi", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(fakePr);

        gitHubMock.UpdatePullRequestAsync("openRuyi-Project", "openRuyi", 42, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(fakePr);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            DryRun = false,
            BotLogin = "aqua-rium",
            BranchPrefix = "autoupdate/"
        });

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        var result = await publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", "SPECS/curl/curl.spec");

        Assert.True(result.Success);
        Assert.False(result.Skipped);

        // Assert file update was dispatched
        await gitHubMock.Received(1).UpdateFileContentAsync(
            "openRuyi-Project", "openRuyi", "SPECS/curl/curl.spec", Arg.Any<string>(), rewriteResult.UpdatedContent, "autoupdate/curl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_ThrowsArgumentException_WhenSpecPathIsNullOrWhiteSpace()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions());
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "Name: curl\nVersion: 8.22.0\n", null, HasChanged: true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            publisher.PublishUpdateAsync(candidate, rewriteResult, "openRuyi-Project/openRuyi", "main", ""));
    }

    [Fact]
    public void GeneratePullRequestBody_CompliesWithUpstreamPullRequestTemplate()
    {
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://lookout.openruyi.cn/package/curl");
        var body = GitHubPackageUpdatePublisher.GeneratePullRequestBody(candidate);

        Assert.Contains("## Summary", body);
        Assert.Contains("Automatic version bump for `curl` from `8.21.0` to `8.22.0`", body);
        Assert.Contains("## AI-Assisted Contribution", body);
        Assert.Contains("- [ ] I have read the [AI-Assisted Contribution Policy]", body);
        Assert.Contains("## Checklist", body);
        Assert.Contains("- [x] Package version updated in spec file", body);
        Assert.Contains("如需停止本包的自动更新，请在 SPEC 文件头部加入 `#!NoAutoUpdate: <reason>`", body);
    }
}
