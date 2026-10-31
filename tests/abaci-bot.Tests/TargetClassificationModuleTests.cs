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
    [InlineData("lts/24.03")]
    [InlineData("lts/2026")]
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

    [Theory]
    [InlineData("lts/24.03", GitHubLabels.TargetLts)]
    [InlineData("lts/2026", GitHubLabels.TargetLts)]
    [InlineData("lts-24.03", GitHubLabels.TargetLts)]
    [InlineData("ltsfoo", GitHubLabels.TargetRolling)]
    [InlineData("main", GitHubLabels.TargetRolling)]
    public async Task ProcessAsync_StandardConfig_RequiresPrefixDelimiter(string branchName, string expectedLabel)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TargetClassificationOptions
        {
            LtsBranchPrefixes = new List<string> { "lts/", "lts-" },
            RollingBranches = new List<string> { "main", "master", "rolling" },
            DefaultTarget = "Rolling"
        });
        var module = new TargetClassificationModule(options);
        var context = CreateContext("openRuyi", branchName);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(expectedLabel, context.LabelsToAdd);
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
    public async Task ProcessAsync_UnknownBranchWithUnrecognizedOrNullDefault_KeepsSilentWithoutTargetLabel(string? defaultTarget)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TargetClassificationOptions
        {
            DefaultTarget = defaultTarget
        });
        var module = new TargetClassificationModule(options);
        var context = CreateContext("openRuyi", "custom-experimental-branch");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.DoesNotContain(GitHubLabels.TargetRolling, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TargetLts, context.LabelsToAdd);
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

    [Fact]
    public void ShouldProcess_PullRequestEdited_ReturnsTrue()
    {
        var module = new TargetClassificationModule();
        var context = CreateContext("openRuyi", "lts/24.03", action: PullRequestAction.Edited);

        Assert.True(module.ShouldProcess(context));
    }

    [Fact]
    public async Task ProcessAsync_RetargetFromMainToLtsSlashBranch_ReconcilesLabelsDirectly()
    {
        var module = new TargetClassificationModule();
        // Retargeting to "lts/24.03" with pre-existing "Target: Rolling" label and Edited action (no new commits)
        var context = CreateContext("openRuyi", "lts/24.03", action: PullRequestAction.Edited, existingLabels: new[] { GitHubLabels.TargetRolling });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetLts, context.LabelsToAdd);
        Assert.Contains(GitHubLabels.TargetRolling, context.LabelsToRemove);
    }

    [Fact]
    public async Task ProcessAsync_RetargetFromLtsToMain_ReconcilesLabelsDirectly()
    {
        var module = new TargetClassificationModule();
        // Retargeting back to "main" with pre-existing "Target: LTS" label and Edited action
        var context = CreateContext("openRuyi", "main", action: PullRequestAction.Edited, existingLabels: new[] { GitHubLabels.TargetLts });

        Assert.True(module.ShouldProcess(context));
        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetRolling, context.LabelsToAdd);
        Assert.Contains(GitHubLabels.TargetLts, context.LabelsToRemove);
    }

    [Theory]
    [InlineData("lts/24.03")]
    [InlineData("lts/milestone-1")]
    [InlineData("lts-24.03")]
    public async Task ProcessAsync_DefaultLtsPrefixes_MatchesBothSlashAndHyphen(string branchName)
    {
        var module = new TargetClassificationModule();
        var context = CreateContext("openRuyi", branchName);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetLts, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TargetRolling, context.LabelsToAdd);
    }

    [Fact]
    public void ManagedTargetLabels_IsReadOnlyAndContainsExpectedLabels()
    {
        Assert.NotNull(TargetClassificationModule.ManagedTargetLabels);
        Assert.Equal(2, TargetClassificationModule.ManagedTargetLabels.Count);
        Assert.Contains(GitHubLabels.TargetRolling, TargetClassificationModule.ManagedTargetLabels);
        Assert.Contains(GitHubLabels.TargetLts, TargetClassificationModule.ManagedTargetLabels);
    }

    [Fact]
    public async Task ProcessAsync_LtsBranchPrefix_MatchesBeforeLtsRepositories()
    {
        var options = new TargetClassificationOptions
        {
            LtsRepositories = new List<string> { "special-repo" },
            LtsBranchPrefixes = new List<string> { "lts/" }
        };
        var module = new TargetClassificationModule(Microsoft.Extensions.Options.Options.Create(options));
        var context = CreateContext("special-repo", "lts/24.03");

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.TargetLts, context.LabelsToAdd);
    }

    [Fact]
    public void DemoConfiguration_ContainsExpectedTargetClassificationSettings()
    {
        // Assert that appsettings.demo.json contract defines expected default LTS prefixes and branches
        var demoConfigPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "appsettings.demo.json");
        Assert.True(File.Exists(demoConfigPath), $"appsettings.demo.json should exist at {demoConfigPath}");

        var json = File.ReadAllText(demoConfigPath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("TargetClassification", out var targetSection));
        Assert.True(targetSection.TryGetProperty("LtsBranchPrefixes", out var ltsPrefixes));
        var prefixes = ltsPrefixes.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("lts/", prefixes);
        Assert.Contains("lts-", prefixes);
    }
}
