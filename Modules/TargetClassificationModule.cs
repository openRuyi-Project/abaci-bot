using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// PR Target release branch classification module (Priority: 25).
/// Classifies pull requests into Target: Rolling or Target: LTS based on target repository and base branch.
/// Automatically harmonizes with LabelMutexEngine for single-choice Target label mutual exclusion.
/// </summary>
public class TargetClassificationModule : IPullRequestModule
{
    private readonly TargetClassificationOptions _options;

    public string ModuleName => "TargetClassificationModule";

    public int Priority => 25;

    public TargetClassificationModule(IOptions<TargetClassificationOptions>? options = null)
    {
        _options = options?.Value ?? new TargetClassificationOptions();
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ReadyForReview ||
                context.PullRequestAction == PullRequestAction.ConvertedToDraft);
    }

    public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        // 1. Dedicated LTS repositories (e.g. openRuyi-LTS) are always mapped to Target: LTS
        if (_options.LtsRepositories.Any(r => string.Equals(r, context.Repo, StringComparison.OrdinalIgnoreCase)))
        {
            context.LabelsToAdd.Add(GitHubLabels.TargetLts);
            return Task.CompletedTask;
        }

        var baseBranch = context.BaseBranch;
        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            ApplyFallbackTarget(context);
            return Task.CompletedTask;
        }

        // 2. LTS prefix matching (e.g. "lts-24.03")
        if (_options.LtsBranchPrefixes.Any(p => baseBranch.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            context.LabelsToAdd.Add(GitHubLabels.TargetLts);
            return Task.CompletedTask;
        }

        // 3. Rolling standard branches (main, master, rolling)
        if (_options.RollingBranches.Any(b => string.Equals(b, baseBranch, StringComparison.OrdinalIgnoreCase)))
        {
            context.LabelsToAdd.Add(GitHubLabels.TargetRolling);
            return Task.CompletedTask;
        }

        // 4. Default target fallback for custom or unmapped base branches
        ApplyFallbackTarget(context);
        return Task.CompletedTask;
    }

    private void ApplyFallbackTarget(PullRequestContext context)
    {
        if (string.Equals(_options.DefaultTarget, "LTS", StringComparison.OrdinalIgnoreCase))
        {
            context.LabelsToAdd.Add(GitHubLabels.TargetLts);
        }
        else
        {
            // Default to Target: Rolling if unspecified or unrecognized
            context.LabelsToAdd.Add(GitHubLabels.TargetRolling);
        }
    }
}
