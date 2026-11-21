using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Tests;

public class MilestoneSyncModuleTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IMilestoneResolver _resolver = Substitute.For<IMilestoneResolver>();

    private PullRequestContext CreateContext(
        string owner = "openRuyi-Project",
        string repo = "openRuyi",
        PullRequestAction? prAction = null,
        PullRequestEventType eventType = PullRequestEventType.PullRequest,
        bool? isMerged = true,
        Milestone? milestone = null,
        DateTimeOffset? mergedAt = null)
    {
        var action = prAction ?? PullRequestAction.Closed;
        var octokitPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(octokitPr, 42);
        var mergedTimestamp = (isMerged == true) ? (mergedAt ?? DateTimeOffset.UtcNow) : (DateTimeOffset?)null;
        typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))?.SetValue(octokitPr, mergedTimestamp);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Milestone))?.SetValue(octokitPr, milestone);

        _gitHub.GetPullRequestAsync(owner, repo, 42, Arg.Any<CancellationToken>())
            .Returns(octokitPr);

        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: eventType,
            owner: owner,
            repo: repo,
            prNumber: 42,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: isMerged,
            title: "Add awesome feature",
            body: "description",
            senderLogin: "merger",
            authorLogin: "pr-author",
            action: action.ToString(),
            existingLabels: Array.Empty<string>(),
            prAction: action);
    }

    [Fact]
    public void Properties_CorrectValues()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions());
        var module = new MilestoneSyncModule(options, _resolver);

        Assert.Equal(90, module.Priority);
        Assert.Equal("MilestoneSyncModule", module.ModuleName);
    }

    [Fact]
    public void ShouldProcess_Disabled_ReturnsFalse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = false,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var module = new MilestoneSyncModule(options, _resolver);
        var context = CreateContext();

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_EmptyAllowList_ReturnsFalse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string>()
        });
        var module = new MilestoneSyncModule(options, _resolver);
        var context = CreateContext();

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_RepoNotInAllowList_ReturnsFalse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "other-owner/other-repo" }
        });
        var module = new MilestoneSyncModule(options, _resolver);
        var context = CreateContext();

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_ShortRepoNameWithoutOwner_ReturnsFalse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi" }
        });
        var module = new MilestoneSyncModule(options, _resolver);
        var context = CreateContext();

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_NotClosedAction_ReturnsFalse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var module = new MilestoneSyncModule(options, _resolver);
        var context = CreateContext(prAction: PullRequestAction.Opened);

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_WrongEventType_ReturnsFalse()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var module = new MilestoneSyncModule(options, _resolver);
        var context = CreateContext(eventType: PullRequestEventType.IssueComment);

        Assert.False(module.ShouldProcess(context));
    }

    [Fact]
    public void ShouldProcess_Valid_ReturnsTrue()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var module = new MilestoneSyncModule(options, _resolver);
        var context = CreateContext();

        Assert.True(module.ShouldProcess(context));
    }

    [Fact]
    public async Task ProcessAsync_ClosedWithoutMerge_DoesNotSetMilestone()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = false,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var module = new MilestoneSyncModule(options, _resolver, NullLogger<MilestoneSyncModule>.Instance);
        var context = CreateContext(isMerged: false);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Null(context.TargetMilestone);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveMilestoneAsync(default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_AlreadyHasMilestone_DoesNotOverwrite()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = false,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var existingMs = (Milestone)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Milestone));
        typeof(Milestone).GetProperty(nameof(Milestone.Title))?.SetValue(existingMs, "CustomMilestone");

        var module = new MilestoneSyncModule(options, _resolver, NullLogger<MilestoneSyncModule>.Instance);
        var context = CreateContext(isMerged: true, milestone: existingMs);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Null(context.TargetMilestone);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveMilestoneAsync(default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_DryRun_DoesNotSetMilestone()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = true,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        var module = new MilestoneSyncModule(options, _resolver, NullLogger<MilestoneSyncModule>.Instance);
        var context = CreateContext(isMerged: true);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Null(context.TargetMilestone);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveMilestoneAsync(default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_MergedWithoutMilestone_ResolvesAndSetsTargetMilestone()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = false,
            CreationEnabled = true,
            DueDayOfMonth = 25,
            TimeZoneId = "Asia/Shanghai",
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        _resolver.ResolveMilestoneAsync(
                "openRuyi-Project",
                "openRuyi",
                Arg.Any<DateTimeOffset>(),
                true,
                25,
                "Asia/Shanghai",
                Arg.Any<CancellationToken>())
            .Returns(42);

        var module = new MilestoneSyncModule(options, _resolver, NullLogger<MilestoneSyncModule>.Instance);
        var context = CreateContext(isMerged: true);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Equal(42, context.TargetMilestone);
    }

    [Fact]
    public async Task ProcessAsync_ResolverReturnsNull_LeavesTargetMilestoneNull()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MilestoneSyncOptions
        {
            Enabled = true,
            DryRun = false,
            CreationEnabled = false,
            RepositoryAllowList = new List<string> { "openRuyi-Project/openRuyi" }
        });
        _resolver.ResolveMilestoneAsync(
                "openRuyi-Project",
                "openRuyi",
                Arg.Any<DateTimeOffset>(),
                false,
                25,
                "Asia/Shanghai",
                Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var module = new MilestoneSyncModule(options, _resolver, NullLogger<MilestoneSyncModule>.Instance);
        var context = CreateContext(isMerged: true);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Null(context.TargetMilestone);
    }
}
