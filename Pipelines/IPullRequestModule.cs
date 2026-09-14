using abaci_bot.Contexts;

namespace abaci_bot.Pipelines;

/// <summary>
/// Defines a pull request governance module in the processing pipeline.
/// Following the Deep Module principle, each module hides its specific evaluation rules
/// and operates purely by inspecting the PullRequestContext and declaring desired label changes.
/// </summary>
public interface IPullRequestModule
{
    /// <summary>
    /// Unique name of the module, used for diagnostics and logging.
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// Execution priority in the pipeline. Modules with lower priority values run earlier.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Determines whether this module should run for the given context.
    /// Allows modules to skip execution early based on event type or action.
    /// </summary>
    bool ShouldProcess(PullRequestContext context) => true;

    /// <summary>
    /// Evaluates domain rules against the context and records desired additions or removals in
    /// <see cref="PullRequestContext.LabelsToAdd"/> and <see cref="PullRequestContext.LabelsToRemove"/>.
    /// </summary>
    Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken = default);
}
