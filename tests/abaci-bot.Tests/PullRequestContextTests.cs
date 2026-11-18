using abaci_bot.Contexts;
using abaci_bot.Services;
using NSubstitute;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class PullRequestContextTests
{
    private static PullRequestContext CreateContext(IEnumerable<string> existingLabels) =>
        new(
            gitHubService: Substitute.For<IGitHubService>(),
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: false,
            title: "pr title",
            body: null,
            senderLogin: "author",
            authorLogin: "author",
            action: PullRequestAction.Opened.ToString(),
            existingLabels: existingLabels,
            prAction: PullRequestAction.Opened);

    [Fact]
    public void EffectiveLabels_AppliesStagedAdditionsAndRemovals()
    {
        var context = CreateContext(new[] { GitHubLabels.WorkflowInDev, GitHubLabels.TargetRolling });
        context.LabelsToRemove.Add(GitHubLabels.WorkflowInDev);
        context.LabelsToAdd.Add(GitHubLabels.WorkflowReadyForReview);

        var effective = context.EffectiveLabels;

        Assert.Contains(GitHubLabels.WorkflowReadyForReview, effective);
        Assert.DoesNotContain(GitHubLabels.WorkflowInDev, effective);
        Assert.Contains(GitHubLabels.TargetRolling, effective);
    }

    [Fact]
    public void EffectiveLabels_AdditionWinsOverRemoval()
    {
        var context = CreateContext(new[] { GitHubLabels.WorkflowInReview });
        context.LabelsToRemove.Add(GitHubLabels.WorkflowInReview);
        context.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);

        Assert.Contains(GitHubLabels.WorkflowInReview, context.EffectiveLabels);
    }

    [Fact]
    public void EffectiveLabels_IsCaseInsensitiveAndLeavesExistingLabelsUntouched()
    {
        var context = CreateContext(new[] { "workflow: in review" });
        context.LabelsToRemove.Add("WORKFLOW: IN REVIEW");

        Assert.DoesNotContain("workflow: in review", context.EffectiveLabels);
        Assert.Contains("workflow: in review", context.ExistingLabels);
    }
}
