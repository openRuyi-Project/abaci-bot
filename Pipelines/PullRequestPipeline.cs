using abaci_bot.Contexts;
using abaci_bot.Services;

namespace abaci_bot.Pipelines;

/// <summary>
/// PR 治理流水线执行器。负责模块有序装配、Fail-Fast 异常控制与声明式原子写回。
/// </summary>
public class PullRequestPipeline
{
    private readonly IReadOnlyList<IPullRequestModule> _modules;
    private readonly LabelMutexEngine _mutexEngine;
    private readonly IGitHubService _gitHubService;

    public PullRequestPipeline(
        IEnumerable<IPullRequestModule> modules,
        LabelMutexEngine mutexEngine,
        IGitHubService gitHubService)
    {
        _modules = modules.OrderBy(m => m.Priority).ToList();
        _mutexEngine = mutexEngine;
        _gitHubService = gitHubService;
    }

    public IReadOnlyList<IPullRequestModule> Modules => _modules;

    public async Task ExecuteAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        // 1. 按 Priority 升序顺序执行已注册模块
        foreach (var module in _modules)
        {
            if (!module.ShouldProcess(context))
                continue;

            // Fail-Fast: 任一模块未捕获异常立即终止流水线向上抛出，不刷入脏状态
            await module.ProcessAsync(context, cancellationToken);
        }

        // 2. 流水线所有模块成功后，执行原子差量互斥计算
        var diff = _mutexEngine.ComputeDiff(
            context.ExistingLabels,
            context.LabelsToAdd,
            context.LabelsToRemove);

        // 3. 批量写回需添加的标签
        if (diff.LabelsToAdd.Count > 0)
        {
            await _gitHubService.AddLabelsAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                diff.LabelsToAdd.ToArray());
        }

        // 4. 写回需移除的标签
        foreach (var label in diff.LabelsToRemove)
        {
            await _gitHubService.RemoveLabelAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                label);
        }
    }
}
