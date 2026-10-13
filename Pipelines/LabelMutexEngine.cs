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
        // 4. Security vulnerability severity mapping (forward-looking single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.Severity1,
            GitHubLabels.Severity2,
            GitHubLabels.Severity3,
            GitHubLabels.Severity4
        },
        // 5. CVE tracking lifecycle state (forward-looking single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.VulnerabilityDetected,
            GitHubLabels.VulnerabilityFixedPending,
            GitHubLabels.VulnerabilityResolved,
            GitHubLabels.VulnerabilityDismissed,
            GitHubLabels.VulnerabilityPendingVerification
        },
        // 6. Downstream rebuild package count tier (forward-looking single-choice)
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

        // Enforce mutex rules across declared single-choice groups:
        // If multiple conflicting labels within the same group appear in toAdd,
        // the last declared candidate wins (respecting downstream module priority).
        // Any conflicting members in existingSet or toAdd are evicted.
        foreach (var group in _mutexGroups)
        {
            var candidatesInGroup = toAdd.Where(a => group.Contains(a)).ToList();
            if (candidatesInGroup.Count > 0)
            {
                // The last candidate in the list is the winner
                var winner = candidatesInGroup.Last();

                // Evict non-winning candidates from toAdd
                foreach (var loser in candidatesInGroup)
                {
                    if (!string.Equals(loser, winner, StringComparison.OrdinalIgnoreCase))
                    {
                        toAdd.RemoveAll(a => string.Equals(a, loser, StringComparison.OrdinalIgnoreCase));
                    }
                }

                // Schedule eviction of any other group members present in existingSet
                foreach (var other in group)
                {
                    if (!string.Equals(other, winner, StringComparison.OrdinalIgnoreCase))
                    {
                        if (existingSet.Contains(other) && !toRemove.Contains(other, StringComparer.OrdinalIgnoreCase))
                        {
                            toRemove.Add(other);
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
