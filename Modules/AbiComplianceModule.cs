using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Modules;

/// <summary>
/// Governance module assessing binary ABI compatibility changes using Libabigail abidiff.
/// Flags PRs with Impact: ABI and Impact: Breaking Change when ABI breakage is detected.
/// </summary>
public class AbiComplianceModule : IPullRequestModule
{
    private readonly IAbiDiffService _abiDiffService;
    private readonly AbiComplianceOptions _options;
    private readonly IGovernanceCommentFormatter _formatter;
    private readonly ILogger<AbiComplianceModule>? _logger;

    public string ModuleName => nameof(AbiComplianceModule);
    public int Priority => 85;

    public IReadOnlyCollection<string> ManagedLabels => new[]
    {
        GitHubLabels.ImpactAbi,
        GitHubLabels.ImpactBreakingChange
    };

    public AbiComplianceModule(
        IAbiDiffService abiDiffService,
        IOptions<AbiComplianceOptions> options,
        IGovernanceCommentFormatter? formatter = null,
        ILogger<AbiComplianceModule>? logger = null)
    {
        _abiDiffService = abiDiffService;
        _options = options.Value;
        _formatter = formatter ?? new GovernanceCommentFormatter();
        _logger = logger;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        if (!_options.Enabled || context.IsTerminal)
            return false;

        return context.AbiResult != null;
    }

    public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        if (context.AbiResult != null)
        {
            ApplyAbiResult(context, context.AbiResult);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the outcome of an ABI diff analysis directly to the governance context.
    /// </summary>
    public void ApplyAbiResult(PullRequestContext context, AbiDiffResult result)
    {
        if (!result.Succeeded)
        {
            _logger?.LogDebug("ABI analysis was not completed for PR #{PrNumber}: {Error}",
                context.PrNumber, result.ErrorMessage);
            return;
        }

        if (result.HasAbiChange)
        {
            context.LabelsToAdd.Add(GitHubLabels.ImpactAbi);
        }

        if (result.IsBreakingChange)
        {
            context.LabelsToAdd.Add(GitHubLabels.ImpactBreakingChange);

            var report = _formatter.FormatAbiBreakingChange(result.Details);
            context.PendingComments.Add(report);
            _logger?.LogWarning("PR #{PrNumber} flagged with ABI breaking changes.", context.PrNumber);
        }
    }
}
