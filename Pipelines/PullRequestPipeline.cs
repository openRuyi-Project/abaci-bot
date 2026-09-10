using abaci_bot.Contexts;
using abaci_bot.Services;

namespace abaci_bot.Pipelines;

/// <summary>
/// Orchestrates the execution of registered <see cref="IPullRequestModule"/> instances.
/// Orders modules by priority, applies a fail-fast error policy, and performs an atomic
/// batch write-back of computed label diffs to GitHub upon successful completion.
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

    /// <summary>
    /// Registered modules in priority-sorted execution order.
    /// </summary>
    public IReadOnlyList<IPullRequestModule> Modules => _modules;

    /// <summary>
    /// Executes the pipeline sequentially across all active modules and flushes label changes.
    /// </summary>
    public async Task ExecuteAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        // 1. Run each registered module in ascending priority order
        foreach (var module in _modules)
        {
            if (!module.ShouldProcess(context))
                continue;

            // Fail-fast policy: unhandled exceptions abort the pipeline immediately,
            // preventing partial or corrupted label state from being pushed to GitHub.
            await module.ProcessAsync(context, cancellationToken);
        }

        // 2. Resolve mutual-exclusion conflicts and calculate minimal API diffs
        var diff = _mutexEngine.ComputeDiff(
            context.ExistingLabels,
            context.LabelsToAdd,
            context.LabelsToRemove);

        // 3. Batch-add newly qualified labels in a single call
        if (diff.LabelsToAdd.Count > 0)
        {
            await _gitHubService.AddLabelsAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                diff.LabelsToAdd.ToArray());
        }

        // 4. Remove conflicting or explicitly evicted labels
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
