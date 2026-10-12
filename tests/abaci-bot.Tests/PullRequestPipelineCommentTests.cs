using abaci_bot.Contexts;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class PullRequestPipelineCommentTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly LabelMutexEngine _mutexEngine = new();

    private PullRequestContext CreateContext()
    {
        var pr = new PullRequest();
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(pr, Array.Empty<Label>());
        _gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 77,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "Test PR",
            body: "PR body",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "opened",
            existingLabels: Array.Empty<string>(),
            prAction: PullRequestAction.Opened);
    }

    private class TestCommentModule : IPullRequestModule
    {
        private readonly string _comment;

        public TestCommentModule(string comment, int priority)
        {
            _comment = comment;
            Priority = priority;
            ModuleName = $"TestModule_{priority}";
        }

        public string ModuleName { get; }
        public int Priority { get; }
        public bool ShouldProcess(PullRequestContext context) => true;

        public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
        {
            context.PendingComments.Add(_comment);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConsolidatesMultipleModuleComments_IntoSingleGitHubComment()
    {
        var module1 = new TestCommentModule("#### Module 1 Warning: Check SPDX", 10);
        var module2 = new TestCommentModule("#### Module 2 Notice: CVE detected", 20);

        var pipeline = new PullRequestPipeline(new[] { module1, module2 }, _mutexEngine, _gitHub);
        var context = CreateContext();

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync(
            "openRuyi-Project",
            "openRuyi",
            77,
            Arg.Is<string>(c =>
                c.Contains("abaci-bot 门禁与治理检查报告") &&
                c.Contains("Module 1 Warning") &&
                c.Contains("Module 2 Notice")),
            Arg.Any<CancellationToken>());

        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ExecuteAsync_NoPendingComments_DoesNotCallCreateComment()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);
        var context = CreateContext();

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        await _gitHub.DidNotReceive().CreateCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CommentApiThrows_DoesNotCrashPipeline()
    {
        var module = new TestCommentModule("Some warning", 10);
        var pipeline = new PullRequestPipeline(new[] { module }, _mutexEngine, _gitHub);
        var context = CreateContext();

        _gitHub.CreateCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ApiException("Rate limit or network down", System.Net.HttpStatusCode.BadGateway));

        // Must complete without throwing unhandled exception
        await pipeline.ExecuteAsync(context, CancellationToken.None);
    }
}
