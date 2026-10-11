using abaci_bot.Services;

namespace abaci_bot.Pipelines;

/// <summary>
/// Encapsulates the minimal label additions and removals computed by <see cref="LabelMutexEngine"/>.
/// </summary>
public record LabelDiffResult(IReadOnlyList<string> LabelsToAdd, IReadOnlyList<string> LabelsToRemove);

/// <summary>
/// Declarative label mutual-exclusion and diff calculation engine.
/// Pulls complexity downward by managing single-choice label groups centrally.
/// Modules only express what label they want; this engine automatically evicts
/// conflicting labels in the same group and computes the minimal set of changes.
/// </summary>
public class LabelMutexEngine
{
    private readonly List<HashSet<string>> _mutexGroups = new()
    {
        // 1. Workflow primary lifecycle state (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.WorkflowInDev,
            GitHubLabels.WorkflowReadyForReview,
            GitHubLabels.WorkflowInReview,
            GitHubLabels.WorkflowBlocked,
            GitHubLabels.WorkflowComplete
        },
        // 2. Community contributor classification (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.CommunityStudent,
            GitHubLabels.CommunityContribution
        },
        // 3. Target release branch classification (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.TargetRolling,
            GitHubLabels.TargetLts
        },
        // 4. Security vulnerability severity mapping (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.Severity1,
            GitHubLabels.Severity2,
            GitHubLabels.Severity3,
            GitHubLabels.Severity4
        },
        // 5. CVE tracking lifecycle state (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.VulnerabilityDetected,
            GitHubLabels.VulnerabilityFixedPending,
            GitHubLabels.VulnerabilityResolved,
            GitHubLabels.VulnerabilityDismissed,
            GitHubLabels.VulnerabilityPendingVerification
        },
        // 6. Downstream rebuild package count tier (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.Rebuild0,
            GitHubLabels.Rebuild1To10,
            GitHubLabels.Rebuild11To100,
            GitHubLabels.Rebuild101To500,
            GitHubLabels.Rebuild501To1000,
            GitHubLabels.Rebuild1001To2500,
            GitHubLabels.Rebuild2501To5000,
            GitHubLabels.Rebuild5001Plus
        }
    };

    /// <summary>
    /// Computes the net label additions and removals required to reflect module intents
    /// while enforcing mutual-exclusion rules across known label groups.
    /// In case of competing additions within the same mutex group, the last scheduled addition
    /// wins deterministically (respecting execution/declaration order), and all losers are evicted.
    /// Redundant writes (adding labels already present, removing labels not present) are filtered out.
    /// </summary>
    public LabelDiffResult ComputeDiff(
        IEnumerable<string> existingLabels,
        IEnumerable<string> labelsToAdd,
        IEnumerable<string> labelsToRemove)
    {
        var existingSet = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<string>(labelsToAdd.Distinct(StringComparer.OrdinalIgnoreCase));
        var toRemove = new List<string>(labelsToRemove.Distinct(StringComparer.OrdinalIgnoreCase));

        // Enforce mutex rules:
        // 1. If multiple labels in toAdd belong to the same mutex group, the last addition wins.
        foreach (var group in _mutexGroups)
        {
            var candidates = toAdd.Where(group.Contains).ToList();
            if (candidates.Count > 1)
            {
                var winner = candidates.Last();
                foreach (var loser in candidates.Take(candidates.Count - 1))
                {
                    toAdd.RemoveAll(l => string.Equals(l, loser, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 2. For the winning/single addition in this group, evict all other group members from existing labels.
            var activeAdditions = toAdd.Where(group.Contains).ToList();
            foreach (var adding in activeAdditions)
            {
                foreach (var other in group)
                {
                    if (!string.Equals(other, adding, StringComparison.OrdinalIgnoreCase))
                    {
                        toAdd.RemoveAll(l => string.Equals(l, other, StringComparison.OrdinalIgnoreCase));
                        if (existingSet.Contains(other))
                        {
                            if (!toRemove.Contains(other, StringComparer.OrdinalIgnoreCase))
                            {
                                toRemove.Add(other);
                            }
                        }
                    }
                }
            }
        }

        // Conflict resolution: if a label appears in both toAdd and toRemove, addition wins.
        toRemove.RemoveAll(r => toAdd.Contains(r, StringComparer.OrdinalIgnoreCase));

        // Redundant add filtering: avoid duplicate API additions if already present.
        toAdd.RemoveAll(a => existingSet.Contains(a));

        // Redundant remove filtering: avoid duplicate API removals if not currently present.
        toRemove.RemoveAll(r => !existingSet.Contains(r));

        return new LabelDiffResult(toAdd, toRemove);
    }
}
