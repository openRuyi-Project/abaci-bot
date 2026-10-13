using abaci_bot.Contexts;
using Octokit;

namespace abaci_bot.Modules.ChangeTypeStrategies;

/// <summary>
/// Strategy interface for repository-specific change type file-tree classification.
/// Adheres to the Open/Closed Principle: new repository rules can be introduced without modifying the host module.
/// </summary>
public interface IChangeTypeStrategy
{
    /// <summary>
    /// Execution priority for ordering strategies deterministically.
    /// Lower values indicate higher precedence.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Checks whether this strategy applies to the given repository name.
    /// </summary>
    bool AppliesTo(string repositoryName);

    /// <summary>
    /// Evaluates modified file paths and declares appropriate Type:* labels on the context.
    /// </summary>
    Task EvaluateChangeTypesAsync(PullRequestContext context, IReadOnlyList<PullRequestFile> files);
}
