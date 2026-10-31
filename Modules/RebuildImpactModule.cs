using System.Text.RegularExpressions;
using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Governance module measuring downstream RPM rebuild impact scale using repository dependency DAGs.
/// Assigns Rebuild:* tier labels and enforces gating by applying Workflow: Blocked when impact >= 1001 on Rolling branch.
/// </summary>
public partial class RebuildImpactModule : IPullRequestModule
{
    private readonly IRpmDependencyService _dependencyService;
    private readonly RebuildImpactOptions _options;
    private readonly IGovernanceCommentFormatter _formatter;
    private readonly ILogger<RebuildImpactModule>? _logger;

    public string ModuleName => nameof(RebuildImpactModule);
    public int Priority => 80;

    public IReadOnlyCollection<string> ManagedLabels => new[]
    {
        GitHubLabels.Rebuild0,
        GitHubLabels.Rebuild1To10,
        GitHubLabels.Rebuild11To100,
        GitHubLabels.Rebuild101To500,
        GitHubLabels.Rebuild501To1000,
        GitHubLabels.Rebuild1001To2500,
        GitHubLabels.Rebuild2501To5000,
        GitHubLabels.Rebuild5001Plus
    };

    [GeneratedRegex(@"(?:^|[/\\])SPECS[/\\]([^/\\]+)[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex PackageSpecDirRegex();

    [GeneratedRegex(@"([^/\\]+)\.spec$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecFileRegex();

    public RebuildImpactModule(
        IRpmDependencyService dependencyService,
        IOptions<RebuildImpactOptions> options,
        IGovernanceCommentFormatter? formatter = null,
        ILogger<RebuildImpactModule>? logger = null)
    {
        _dependencyService = dependencyService;
        _options = options.Value;
        _formatter = formatter ?? new GovernanceCommentFormatter();
        _logger = logger;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        if (!_options.Enabled || context.IsTerminal)
            return false;

        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ReadyForReview ||
                context.PullRequestAction == PullRequestAction.Edited);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var files = await context.ChangedFiles.Value;
        var specFiles = files
            .Where(f => f.FileName.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(f.Status, "removed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (specFiles.Count == 0)
        {
            context.ReconcileLabels(ManagedLabels, _ => false);
            return;
        }

        var changedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in specFiles)
        {
            var dirMatch = PackageSpecDirRegex().Match(file.FileName);
            if (dirMatch.Success)
            {
                changedPackages.Add(dirMatch.Groups[1].Value);
            }
            else
            {
                var fileMatch = SpecFileRegex().Match(file.FileName);
                if (fileMatch.Success)
                {
                    changedPackages.Add(fileMatch.Groups[1].Value);
                }
            }
        }

        if (changedPackages.Count == 0)
        {
            context.ReconcileLabels(ManagedLabels, _ => false);
            return;
        }

        var totalAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in changedPackages)
        {
            var affected = _dependencyService.GetAffectedPackages(pkg);
            foreach (var a in affected)
            {
                totalAffected.Add(a);
            }
        }

        var totalCount = totalAffected.Count;
        var rebuildLabel = _dependencyService.MapAffectedCountToRebuildLabel(totalCount);
        context.ReconcileLabels(ManagedLabels, rebuildLabel);

        _logger?.LogInformation(
            "Rebuild impact for PR #{PrNumber}: {PackageCount} packages modified, {AffectedCount} downstream packages affected. Assigned: {Label}",
            context.PrNumber, changedPackages.Count, totalCount, rebuildLabel);

        // Admission gate evaluation: Block if >= BlockThreshold and target branch is configured target
        var targetBranch = _options.TargetBranchToBlock ?? "Rolling";
        var isRollingTarget = context.LabelsToAdd.Contains(GitHubLabels.TargetRolling) ||
                              context.ExistingLabels.Contains(GitHubLabels.TargetRolling) ||
                              string.Equals(context.BaseBranch, targetBranch, StringComparison.OrdinalIgnoreCase) ||
                              (string.Equals(targetBranch, "Rolling", StringComparison.OrdinalIgnoreCase) &&
                               (string.Equals(context.BaseBranch, "main", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(context.BaseBranch, "master", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(context.BaseBranch, "rolling", StringComparison.OrdinalIgnoreCase)));

        if (totalCount >= _options.BlockThreshold && isRollingTarget)
        {
            context.LabelsToAdd.Add(GitHubLabels.WorkflowBlocked);

            if (!context.ExistingLabels.Contains(GitHubLabels.WorkflowBlocked))
            {
                var report = _formatter.FormatRebuildGateBlocked(
                    changedPackages.ToList(),
                    totalCount,
                    _options.BlockThreshold,
                    targetBranch);

                context.PendingComments.Add(report);
            }
            _logger?.LogWarning("PR #{PrNumber} blocked due to excessive rebuild impact ({Count} >= {Threshold}).",
                context.PrNumber, totalCount, _options.BlockThreshold);
        }
        else
        {
            // Only unblock if this PR was previously blocked by this module (i.e. carried a blocking rebuild tier)
            var hadRebuildBlock = context.ExistingLabels.Contains(GitHubLabels.Rebuild1001To2500) ||
                                  context.ExistingLabels.Contains(GitHubLabels.Rebuild2501To5000) ||
                                  context.ExistingLabels.Contains(GitHubLabels.Rebuild5001Plus);

            if (hadRebuildBlock && context.ExistingLabels.Contains(GitHubLabels.WorkflowBlocked))
            {
                context.LabelsToRemove.Add(GitHubLabels.WorkflowBlocked);
            }
        }
    }
}
