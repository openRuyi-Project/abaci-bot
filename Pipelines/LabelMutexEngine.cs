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
            "Community: Student contribution",
            "Community: Contribution"
        },
        // 3. Target release branch classification (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Target: Rolling",
            "Target: LTS"
        },
        // 4. Security vulnerability severity mapping (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Severity: 1",
            "Severity: 2",
            "Severity: 3",
            "Severity: 4"
        },
        // 5. CVE tracking lifecycle state (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Vulnerability: Detected",
            "Vulnerability: Fixed-Pending",
            "Vulnerability: Resolved",
            "Vulnerability: Dismissed",
            "Vulnerability: Pending Verification"
        },
        // 6. Downstream rebuild package count tier (single-choice)
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Rebuild: 0",
            "Rebuild: 1-10",
            "Rebuild: 11-100",
            "Rebuild: 101-500",
            "Rebuild: 501-1000",
            "Rebuild: 1001-2500",
            "Rebuild: 2501-5000",
            "Rebuild: 5001+"
        }
    };

    /// <summary>
    /// Computes the net label additions and removals required to reflect module intents
    /// while enforcing mutual-exclusion rules across known label groups.
    /// </summary>
    public LabelDiffResult ComputeDiff(
        IEnumerable<string> existingLabels,
        IEnumerable<string> labelsToAdd,
        IEnumerable<string> labelsToRemove)
    {
        var existingSet = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<string>(labelsToAdd.Distinct(StringComparer.OrdinalIgnoreCase));
        var toRemove = new List<string>(labelsToRemove.Distinct(StringComparer.OrdinalIgnoreCase));

        // Enforce mutex rules: adding a label in a single-choice group requires evicting any other labels in that group.
        foreach (var adding in toAdd.ToList())
        {
            foreach (var group in _mutexGroups)
            {
                if (group.Contains(adding))
                {
                    foreach (var other in group)
                    {
                        if (!string.Equals(other, adding, StringComparison.OrdinalIgnoreCase))
                        {
                            toAdd.Remove(other);
                            // Mark other conflicting label for removal if it already exists or was scheduled for eviction.
                            if (existingSet.Contains(other) || toRemove.Contains(other, StringComparer.OrdinalIgnoreCase))
                            {
                                if (!toRemove.Contains(other, StringComparer.OrdinalIgnoreCase))
                                    toRemove.Add(other);
                            }
                        }
                    }
                }
            }
        }

        // Conflict resolution: if a label appears in both toAdd and toRemove, addition wins.
        toRemove.RemoveAll(r => toAdd.Contains(r, StringComparer.OrdinalIgnoreCase));

        return new LabelDiffResult(toAdd, toRemove);
    }
}
