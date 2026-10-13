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

public class ShinyNewPrModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IOptions<BotDetectionOptions> _botOptions = Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions
    {
        Whitelist = new List<string> { "ci-bot", "custom-bot[bot]" }
    });

    private PullRequestContext CreateContext(
        PullRequestEventType eventType,
        string senderLogin,
        string authorLogin = "pr-author",
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
            prNumber: 42,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "Add awesome feature",
            body: "description",
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
    public async Task ProcessAsync_WhenPrOpened_AddsNewLabel()
    {
        var module = new ShinyNewPrModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "contributor",
            authorLogin: "contributor",
            prAction: PullRequestAction.Opened);

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.New, context.LabelsToAdd);
        Assert.Empty(context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenHumanReviewSubmitted_RemovesNewLabel()
    {
        var module = new ShinyNewPrModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.Review,
            senderLogin: "reviewer-alice",
            authorLogin: "contributor",
            reviewAction: PullRequestReviewAction.Submitted,
            existingLabels: new[] { GitHubLabels.New, GitHubLabels.WorkflowReadyForReview });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.New, context.LabelsToRemove);
        Assert.Empty(context.LabelsToAdd);
    }

    [Fact]
    public async Task ProcessAsync_WhenAuthorCommentsOnOwnPr_DoesNotRemoveNewLabel()
    {
        var module = new ShinyNewPrModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "contributor",
            authorLogin: "contributor",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.New });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenBotCommentsOnPr_DoesNotRemoveNewLabel()
    {
        var module = new ShinyNewPrModule(_botOptions);

        // 1. Check with declared Bot user type
        var botTypeContext = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "some-automation",
            authorLogin: "contributor",
            senderType: "Bot",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.New });

        await module.ProcessAsync(botTypeContext, CancellationToken.None);
        Assert.Empty(botTypeContext.LabelsToRemove);

        // 2. Check with [bot] naming suffix
        var suffixContext = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "github-actions[bot]",
            authorLogin: "contributor",
            senderType: "User",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.New });

        await module.ProcessAsync(suffixContext, CancellationToken.None);
        Assert.Empty(suffixContext.LabelsToRemove);

        // 3. Check with explicit whitelist match
        var whitelistContext = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "ci-bot",
            authorLogin: "contributor",
            senderType: "User",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.New });

        await module.ProcessAsync(whitelistContext, CancellationToken.None);
        Assert.Empty(whitelistContext.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenHumanNonAuthorComments_RemovesNewLabel()
    {
        var module = new ShinyNewPrModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "maintainer-bob",
            authorLogin: "contributor",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.New });

        await module.ProcessAsync(context, CancellationToken.None);
        Assert.Contains(GitHubLabels.New, context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_WhenPrDoesNotHaveNewLabel_DoesNothingOnReview()
    {
        var module = new ShinyNewPrModule(_botOptions);
        var context = CreateContext(
            PullRequestEventType.Review,
            senderLogin: "reviewer-alice",
            authorLogin: "contributor",
            reviewAction: PullRequestReviewAction.Submitted,
            existingLabels: new[] { GitHubLabels.WorkflowInReview });

        await module.ProcessAsync(context, CancellationToken.None);
        Assert.Empty(context.LabelsToRemove);
        Assert.Empty(context.LabelsToAdd);
    }

    [Fact]
    public void ShouldProcess_ReturnsFalseForOtherActions()
    {
        var module = new ShinyNewPrModule(_botOptions);

        var reopenContext = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "contributor",
            prAction: PullRequestAction.Reopened);

        Assert.False(module.ShouldProcess(reopenContext));

        var syncContext = CreateContext(
            PullRequestEventType.PullRequest,
            senderLogin: "contributor",
            prAction: PullRequestAction.Synchronize);

        Assert.False(module.ShouldProcess(syncContext));
    }

    [Fact]
    public void ShouldProcess_WhenReviewOrCommentWithoutNewLabel_ReturnsTrue()
    {
        var module = new ShinyNewPrModule(_botOptions);

        var reviewContext = CreateContext(
            PullRequestEventType.Review,
            senderLogin: "reviewer-alice",
            authorLogin: "contributor",
            reviewAction: PullRequestReviewAction.Submitted,
            existingLabels: new[] { GitHubLabels.WorkflowInReview });

        Assert.True(module.ShouldProcess(reviewContext));

        var commentContext = CreateContext(
            PullRequestEventType.IssueComment,
            senderLogin: "maintainer-bob",
            authorLogin: "contributor",
            commentAction: IssueCommentAction.Created,
            existingLabels: new[] { GitHubLabels.WorkflowInReview });

        Assert.True(module.ShouldProcess(commentContext));
    }

    [Fact]
    public void ShouldProcess_WhenUnknownEventType_ReturnsFalse()
    {
        var module = new ShinyNewPrModule(_botOptions);
        var context = CreateContext((PullRequestEventType)999, senderLogin: "user");

        Assert.False(module.ShouldProcess(context));
    }
}
