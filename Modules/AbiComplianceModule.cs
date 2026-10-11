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
        ILogger<AbiComplianceModule>? logger = null)
    {
        _abiDiffService = abiDiffService;
        _options = options.Value;
        _logger = logger;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        if (!_options.Enabled || context.IsTerminal)
            return false;

        // Process if an ABI result is already attached in the context Items,
        // or during PR opened / synchronize events for preliminary evaluation
        return context.Items.ContainsKey("AbiDiffResult");
    }

    public Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        if (context.Items.TryGetValue("AbiDiffResult", out var obj) && obj is AbiDiffResult result)
        {
            ApplyAbiResult(context, result);
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

            var snippet = result.Details;
            if (snippet.Length > 1500)
            {
                snippet = snippet[..1500] + "\n... (输出过长已截断)";
            }

            var report = "#### ⚠️ ABI 破坏性兼容性变更预警 (ABI Breaking Change)\n" +
                         "门禁比对检测到二进制共享库存在破坏性 ABI 改变（缺少导出的公共符号或 SONAME 发生变更）：\n\n" +
                         "```\n" + snippet + "\n```\n\n" +
                         "下游依赖此动态库的应用可能发生运行时符号解析崩溃，请评估是否属于有意变更。";

            context.PendingComments.Add(report);
            _logger?.LogWarning("PR #{PrNumber} flagged with ABI breaking changes.", context.PrNumber);
        }
    }
}
