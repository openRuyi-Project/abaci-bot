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

public class AbiComplianceModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IAbiDiffService _abiService = Substitute.For<IAbiDiffService>();

    private PullRequestContext CreateContext()
    {
        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "openRuyi-Project",
            repo: "openRuyi",
            prNumber: 88,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "Update libfoo",
            body: "PR body",
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "synchronize",
            existingLabels: Array.Empty<string>(),
            prAction: PullRequestAction.Synchronize);
    }

    [Fact]
    public async Task ProcessAsync_CompatibleAbiChange_AddsImpactAbiOnly()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var module = new AbiComplianceModule(_abiService, options);

        var context = CreateContext();
        var diffResult = new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: true,
            IsBreakingChange: false,
            Details: "1 function with some changes");
        context.AbiResult = diffResult;

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.ImpactAbi, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.ImpactBreakingChange, context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Fact]
    public async Task ProcessAsync_BreakingAbiChange_AddsBothImpactLabelsAndComment()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var module = new AbiComplianceModule(_abiService, options);

        var context = CreateContext();
        var diffResult = new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: true,
            IsBreakingChange: true,
            Details: "SONAME changed from libfoo.so.1 to libfoo.so.2; 3 removed functions");
        context.AbiResult = diffResult;

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.ImpactAbi, context.LabelsToAdd);
        Assert.Contains(GitHubLabels.ImpactBreakingChange, context.LabelsToAdd);
        Assert.NotEmpty(context.PendingComments);
        Assert.Contains("ABI 破坏性兼容性变更预警", context.PendingComments[0]);
        Assert.Contains("SONAME changed", context.PendingComments[0]);
    }

    [Fact]
    public async Task ProcessAsync_AbidiffFailedOrSkipped_GracefullyDoesNotAddLabels()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var module = new AbiComplianceModule(_abiService, options);

        var context = CreateContext();
        var diffResult = new AbiDiffResult(
            Succeeded: false,
            HasAbiChange: false,
            IsBreakingChange: false,
            Details: string.Empty,
            ErrorMessage: "abidiff binary not available");
        context.AbiResult = diffResult;

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToAdd);
        Assert.Empty(context.PendingComments);
    }

    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(4, true, true, false)]   // ABIDIFF_ABI_CHANGE
    [InlineData(8, true, true, true)]    // ABIDIFF_ABI_INCOMPATIBLE_CHANGE
    [InlineData(12, true, true, true)]   // 4 | 8
    [InlineData(16, true, true, false)]  // ABIDIFF_ARCHITECTURE_CHANGE
    [InlineData(24, true, true, true)]   // 8 | 16
    public void ProcessAbiDiffRunner_InterpretExitCode_ValidBitmasks(int exitCode, bool expectedSuccess, bool expectedHasChange, bool expectedIsBreaking)
    {
        var result = ProcessAbiDiffRunner.InterpretExitCode(exitCode, "abi summary details", "");

        Assert.Equal(expectedSuccess, result.Succeeded);
        Assert.Equal(expectedHasChange, result.HasAbiChange);
        Assert.Equal(expectedIsBreaking, result.IsBreakingChange);
    }

    [Theory]
    [InlineData(1)] // ABIDIFF_ERROR
    [InlineData(2)] // ABIDIFF_USAGE_ERROR
    [InlineData(3)] // 1 | 2
    [InlineData(5)] // 1 | 4
    public void ProcessAbiDiffRunner_InterpretExitCode_ErrorBitmasks_MarkedAsFailed(int exitCode)
    {
        var result = ProcessAbiDiffRunner.InterpretExitCode(exitCode, "", "tool error");

        Assert.False(result.Succeeded);
        Assert.False(result.HasAbiChange);
        Assert.False(result.IsBreakingChange);
        Assert.Contains($"exit code: {exitCode}", result.ErrorMessage);
    }

    [Fact]
    public async Task ProcessWorkflowRun_FullPipelineExecution_AppliesAbiLabelsAndIgnoresPrOnlyModules()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AbiComplianceOptions());
        var abiModule = new AbiComplianceModule(_abiService, options);

        var pipeline = new PullRequestPipeline(new IPullRequestModule[] { abiModule }, new LabelMutexEngine(), _gitHub);

        var pr = (Octokit.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.PullRequest));
        typeof(Octokit.PullRequest).GetProperty(nameof(Octokit.PullRequest.Number))?.SetValue(pr, 42);
        typeof(Octokit.PullRequest).GetProperty(nameof(Octokit.PullRequest.Labels))?.SetValue(pr, Array.Empty<Octokit.Label>());

        _gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(pr);

        var diffResult = new AbiDiffResult(
            Succeeded: true,
            HasAbiChange: true,
            IsBreakingChange: true,
            Details: "SONAME changed; function symbol removed");

        var context = PullRequestContext.FromWorkflowRun(_gitHub, "captains", "owner", "repo", pr, diffResult);

        await pipeline.ExecuteAsync(context);

        await _gitHub.Received().AddLabelsAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string[]>(labels => labels.Contains(GitHubLabels.ImpactAbi) && labels.Contains(GitHubLabels.ImpactBreakingChange)),
            Arg.Any<CancellationToken>());

        await _gitHub.Received().CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(c => c.Contains("ABI 破坏性兼容性变更预警")),
            Arg.Any<CancellationToken>());
    }
}


