using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Xunit;

namespace abaci_bot.Tests;

public class StaleUnstaleModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IOptions<BotDetectionOptions> _botOptions = Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions
    {
        Whitelist = new List<string> { "ci-bot", "dependabot[bot]" }
    });

    private PullRequestContext CreateContext(
        PullRequestEventType eventType,
        string senderLogin,
        string authorLogin = "author",
        string? senderType = "User",
        PullRequestAction? prAction = null,
        PullRequestReviewAction? reviewAction = null,
        IssueCommentAction? commentAction = null,
        IEnumerable<string>? existingLabels = null)
    {
        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: eventType,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 55,
            headSha: "head55",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "PR Title",
            body: "PR Body",
            senderLogin: senderLogin,
            authorLogin: authorLogin,
            action: prAction?.ToString() ?? reviewAction?.ToString() ?? commentAction?.ToString() ?? string.Empty,
            existingLabels: existingLabels ?? Array.Empty<string>(),
            prAction: prAction,
            reviewAction: reviewAction,
            commentAction: commentAction,
            senderType: senderType);
    }

    [Fact]
    public async Task ProcessAsync_WhenNewCommitPushedOnStalePr_RemovesStatusStale()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "author",
            prAction: PullRequestAction.Synchronize,
            existingLabels: new[] { GitHubLabels.StatusStale });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.StatusStale, context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenPrReopened_RemovesStatusStale()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "author",
            prAction: PullRequestAction.Reopened,
            existingLabels: new[] { GitHubLabels.StatusStale });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.StatusStale, context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenHumanCommentsOnStalePr_RemovesStatusStale()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "author",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.StatusStale });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.StatusStale, context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenReviewerSubmitsReviewOnStalePr_RemovesStatusStale()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.Review,
            senderLogin: "captain-bob",
            reviewAction: PullRequestReviewAction.Submitted,
            existingLabels: new[] { GitHubLabels.StatusStale });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.StatusStale, context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenBotCommentsOnStalePr_DoesNotRemoveStatusStale()
    {
        var module = new StaleUnstaleModule(_botOptions);

        var botContext = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "ci-bot",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.StatusStale });

        await module.ProcessAsync(botContext, CancellationToken.None);

        Assert.Empty(botContext.LabelsToRemove);
    }

    [Fact]
    public void ShouldProcess_ReturnsTrueRegardlessOfExistingLabels_ForEligibleEvents()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "author",
            prAction: PullRequestAction.Synchronize,
            existingLabels: new[] { GitHubLabels.WorkflowInReview });

        Assert.True(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_ReturnsFalseForUnhandledEventType()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "author",
            prAction: PullRequestAction.Assigned,
            existingLabels: new[] { GitHubLabels.StatusStale });

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public async Task ProcessAsync_WhenPrDoesNotHaveStaleLabel_DoesNothing()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "author",
            prAction: PullRequestAction.Synchronize,
            existingLabels: Array.Empty<string>());

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToRemove);
    }

    [Fact]
    public void ShouldProcess_WhenUnknownEventType_ReturnsFalse()
    {
        var module = new StaleUnstaleModule(_botOptions);
        var context = CreateContext(
            (PullRequestEventType)999,
            senderLogin: "author",
            existingLabels: new[] { GitHubLabels.StatusStale });

        Assert.False(module.ShouldProcess(context));
    }
}
