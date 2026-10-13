using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit.Webhooks.Events.PullRequest;
using Xunit;

namespace abaci_bot.Tests;

public class TargetClassificationModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();

    private PullRequestContext CreateContext(
        string repo,
        string? baseBranch,
        PullRequestAction? action = null,
        IEnumerable<string>? existingLabels = null)
    {
        var effectiveAction = action ?? PullRequestAction.Opened;
        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: repo,
            prNumber: 10,
            headSha: "sha123",
            baseBranch: baseBranch,
            isDraft: false,
            isMerged: null,
            title: "PR Title",
            body: "PR Body",
            senderLogin: "author",
            authorLogin: "author",
            action: effectiveAction.ToString(),
            existingLabels: existingLabels ?? Array.Empty<string>(),
            prAction: effectiveAction);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("rolling")]
    public async Task ProcessAsync_StandardRollingBranches_AddsTargetRolling(string branchName)
    {
        var module = new TargetClassificationModule();
        var context = CreateContext("openRuyi", branchName);

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetRolling, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TargetLts, context.LabelsToAdd);
    }

    [Theory]
    [InlineData("lts-24.03")]
    [InlineData("lts-22.09")]
    [InlineData("lts-next")]
    public async Task ProcessAsync_LtsPrefixBranches_AddsTargetLts(string branchName)
    {
        var module = new TargetClassificationModule();
        var context = CreateContext("openRuyi", branchName);

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetLts, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TargetRolling, context.LabelsToAdd);
    }

    [Fact]
    public async Task ProcessAsync_LtsDedicatedRepository_AlwaysAddsTargetLts()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TargetClassificationOptions
        {
            LtsRepositories = new List<string> { "openRuyi-LTS" }
        });
        var module = new TargetClassificationModule(options);
        var context = CreateContext("openRuyi-LTS", "main");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetLts, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TargetRolling, context.LabelsToAdd);
    }

    [Fact]
    public async Task ProcessAsync_UnknownBranchWithDefaultRolling_AddsTargetRolling()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TargetClassificationOptions
        {
            DefaultTarget = "Rolling"
        });
        var module = new TargetClassificationModule(options);
        var context = CreateContext("openRuyi", "custom-experimental-branch");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetRolling, context.LabelsToAdd);
    }

    [Fact]
    public async Task ProcessAsync_UnknownBranchWithDefaultLts_AddsTargetLts()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TargetClassificationOptions
        {
            DefaultTarget = "LTS"
        });
        var module = new TargetClassificationModule(options);
        var context = CreateContext("openRuyi", "custom-experimental-branch");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetLts, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TargetRolling, context.LabelsToAdd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UnrecognizedCustomValue")]
    public async Task ProcessAsync_UnknownBranchWithUnrecognizedOrNullDefault_FallsBackToRolling(string? defaultTarget)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TargetClassificationOptions
        {
            DefaultTarget = defaultTarget
        });
        var module = new TargetClassificationModule(options);
        var context = CreateContext("openRuyi", "custom-experimental-branch");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetRolling, context.LabelsToAdd);
    }

    [Fact]
    public async Task ProcessAsync_LabelMutexEngine_SwapsExistingTargetRollingWithLts()
    {
        var module = new TargetClassificationModule();
        var context = CreateContext("openRuyi", "lts-24.03", existingLabels: new[] { GitHubLabels.TargetRolling });

        await module.ProcessAsync(context, CancellationToken.None);

        // Verify LabelMutexEngine atomicity
        var mutexEngine = new LabelMutexEngine();
        var diff = mutexEngine.ComputeDiff(context.ExistingLabels, context.LabelsToAdd, context.LabelsToRemove);

        Assert.Contains(GitHubLabels.TargetLts, diff.LabelsToAdd);
        Assert.Contains(GitHubLabels.TargetRolling, diff.LabelsToRemove);
    }
}
