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
        Assert.Contains("To pause automated updates for this package, add `#!NoAutoUpdate: <reason>` to the SPEC file header.", body);
        Assert.DoesNotContain("如需停止", body);
    }

    [Fact]
    public async Task PublishUpdateAsync_ReturnsSkipped_WhenCandidateOrRewriteResultInvalid()
    {
        var publisher = new GitHubPackageUpdatePublisher(Substitute.For<IGitHubService>(), Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");

        // 1. Rewrite failed
        var failedRewrite = new SpecRewriteResult(false, "", "error");
        var res1 = await publisher.PublishUpdateAsync(candidate, failedRewrite, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(res1.Success);
        Assert.True(res1.Skipped);

        // 2. Rewrite has no changes
        var unchangedRewrite = new SpecRewriteResult(true, "", null, HasChanged: false);
        var res2 = await publisher.PublishUpdateAsync(candidate, unchangedRewrite, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(res2.Success);
        Assert.True(res2.Skipped);

        // 3. Invalid target repo
        var validRewrite = new SpecRewriteResult(true, "new content", null, HasChanged: true);
        var res3 = await publisher.PublishUpdateAsync(candidate, validRewrite, "invalid_repo_without_slash", "main", "SPECS/curl.spec");
        Assert.False(res3.Success);
        Assert.False(res3.Skipped);
    }

    [Fact]
    public async Task PublishUpdateAsync_FailsClosed_WhenLatestCommitFailsOrNull()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("sha123");

        // Throws exception reading latest commit
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns<Task<(string, string, string, string)?>>(_ => throw new InvalidOperationException("API error"));

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions()));
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(res.Success);
        Assert.Contains("fail-closed", res.SkipReason);

        // Null latest commit
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((ValueTuple<string, string, string, string>?)null);
        var resNull = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");
        Assert.False(resNull.Success);
        Assert.Contains("fail-closed", resNull.SkipReason);
    }

    [Fact]
    public async Task PublishUpdateAsync_CreatesBranchAndNewPullRequest_WhenBranchDoesNotExist()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((string?)null); // Branch does not exist
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns((PullRequest?)null);

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 99);
        typeof(PullRequest).GetProperty(nameof(PullRequest.HtmlUrl))?.SetValue(createdPr, "https://github.com/owner/repo/pull/99");
        gitHubMock.CreatePullRequestAsync("owner", "repo", Arg.Any<string>(), "autoupdate/curl", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false }));
        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.False(res.Skipped);
        Assert.Equal(99, res.PullRequestNumber);

        // Verify branch was created from base SHA
        await gitHubMock.Received(1).CreateOrUpdateRefAsync("owner", "repo", "autoupdate/curl", "baseSha456", force: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_RespectsDryRun_WithoutWritingToGitHub()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = true });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.False(res.Skipped);
        Assert.Contains("DryRun", res.SkipReason);

        await gitHubMock.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_StepsAside_WhenHumanCommitsDetectedOnBranch()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("existingBranchSha123");

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 55);
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(existingPr);

        // Human committer
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(("commitSha", "manual tweak", "dev-human", "human@example.com"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.True(res.Skipped);
        Assert.Contains("Human commits", res.SkipReason);

        // Warning comment posted
        await gitHubMock.Received(1).CreateCommentAsync("owner", "repo", 55, Arg.Is<string>(c => c.Contains("stepped aside")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_Skips_WhenBranchAlreadyAtTargetVersion()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("existingBranchSha123");

        var existingPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(existingPr, 56);
        gitHubMock.FindOpenPullRequestByHeadAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(existingPr);

        // Bot committer
        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(("commitSha", "bump version", "aqua-rium", "bot@openruyi.org"));

        // Spec on branch already at target version 8.22.0
        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("Name: curl\nVersion: 8.22.0\nRelease: 1\n");

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.True(res.Success);
        Assert.True(res.Skipped);
        Assert.Contains("already at target version", res.SkipReason);

        await gitHubMock.DidNotReceive().UpdateFileContentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishUpdateAsync_FailsClosed_WhenBranchSpecReadThrows()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns("existingBranchSha123");

        gitHubMock.GetBranchLatestCommitAsync("owner", "repo", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns(("commitSha", "bump version", "web-flow", "noreply@github.com"));

        gitHubMock.GetFileContentAsync("owner", "repo", "SPECS/curl.spec", "autoupdate/curl", Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new HttpRequestException("Failed to read spec"));

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("curl", "8.21.0", "8.22.0", CandidateOrigin.Lookout, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/curl.spec");

        Assert.False(res.Success);
        Assert.Contains("fail-closed", res.SkipReason);
    }

    [Fact]
    public async Task PublishUpdateAsync_AddsRustLabel_WhenOriginIsRustCrate()
    {
        var gitHubMock = Substitute.For<IGitHubService>();
        gitHubMock.GetRefShaAsync("owner", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("baseSha456");

        var createdPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(createdPr, 77);
        gitHubMock.CreatePullRequestAsync("owner", "repo", Arg.Any<string>(), "autoupdate/rust-anstream", "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(createdPr);

        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions { DryRun = false });
        var publisher = new GitHubPackageUpdatePublisher(gitHubMock, options);

        var candidate = new UpdateCandidate("rust-anstream", "0.6.0", "0.6.1", CandidateOrigin.RustCrate, "https://evidence");
        var rewriteResult = new SpecRewriteResult(true, "new content", null, HasChanged: true);

        var res = await publisher.PublishUpdateAsync(candidate, rewriteResult, "owner/repo", "main", "SPECS/rust-anstream/rust-anstream.spec");

        Assert.True(res.Success);
        Assert.Equal(77, res.PullRequestNumber);

        await gitHubMock.Received(1).AddLabelsAsync(
            "owner", "repo", 77, Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.AutoUpdateRust)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("my-custom-bot", "", true)]
    [InlineData("dependabot[bot]", "", true)]
    [InlineData("web-flow", "", true)]
    [InlineData("developer", "noreply@github.com", true)]
    [InlineData("developer", "dev@example.com", false)]
    public void IsWhitelistedCommitter_EvaluatesCorrectly(string? login, string? email, bool expected)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PackageUpdateOptions
        {
            BotLogin = "my-custom-bot"
        });
        var publisher = new GitHubPackageUpdatePublisher(Substitute.For<IGitHubService>(), options);

        Assert.Equal(expected, publisher.IsWhitelistedCommitter(login!, email!));
    }
}
