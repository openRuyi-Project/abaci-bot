using Octokit;

namespace abaci_bot.Modules.ChangeTypeStrategies;

/// <summary>
/// Strategy interface for repository-specific change type file-tree classification.
/// Adheres to the Open/Closed Principle: new repository rules can be introduced without modifying the host module.
/// A strategy is a pure classifier: it returns the labels implied by the file list and never mutates the
/// context, which leaves reconciliation of the managed label set to the hosting module.
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
    /// Classifies the changed files and returns the <c>Type:</c> labels they imply.
    /// An empty file list yields an empty result.
    /// </summary>
    Task<IReadOnlyCollection<string>> ClassifyAsync(IReadOnlyList<PullRequestFile> files);
}
