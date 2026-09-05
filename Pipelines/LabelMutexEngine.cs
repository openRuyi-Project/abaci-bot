using abaci_bot.Services;

namespace abaci_bot.Pipelines;

public record LabelDiffResult(IReadOnlyList<string> LabelsToAdd, IReadOnlyList<string> LabelsToRemove);

/// <summary>
/// 声明式标签互斥与差量计算引擎（下沉复杂度至框架）
/// </summary>
public class LabelMutexEngine
{
    private readonly List<HashSet<string>> _mutexGroups = new()
    {
        // 1. Workflow 主状态单选互斥组
        new(StringComparer.OrdinalIgnoreCase)
        {
            GitHubLabels.WorkflowInDev,
            GitHubLabels.WorkflowReadyForReview,
            GitHubLabels.WorkflowInReview,
            GitHubLabels.WorkflowBlocked,
            GitHubLabels.WorkflowComplete
        },
        // 2. Community 贡献者身份单选互斥组
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Community: Student contribution",
            "Community: Contribution"
        },
        // 3. Target 目标分支单选互斥组
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Target: Rolling",
            "Target: LTS"
        },
        // 4. Severity 安全漏洞严重等级单选互斥组
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Severity: 1",
            "Severity: 2",
            "Severity: 3",
            "Severity: 4"
        },
        // 5. Vulnerability 缺陷生命周期单选互斥组
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Vulnerability: Detected",
            "Vulnerability: Fixed-Pending",
            "Vulnerability: Resolved",
            "Vulnerability: Dismissed",
            "Vulnerability: Pending Verification"
        },
        // 6. Rebuild 影响包规模单选互斥组
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

    public LabelDiffResult ComputeDiff(
        IEnumerable<string> existingLabels,
        IEnumerable<string> labelsToAdd,
        IEnumerable<string> labelsToRemove)
    {
        var existingSet = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<string>(labelsToAdd.Distinct(StringComparer.OrdinalIgnoreCase));
        var toRemove = new List<string>(labelsToRemove.Distinct(StringComparer.OrdinalIgnoreCase));

        // 互斥规范化：对于每个待添加标签，若其属于某互斥组，则该组中的其他标签应被加入待移除列表
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
                            // 若现有标签或已声明中含有该冲突标签，则标记移除
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

        // 冲突消解：若一个标签既在 toAdd 又在 toRemove，以 toAdd 优先
        toRemove.RemoveAll(r => toAdd.Contains(r, StringComparer.OrdinalIgnoreCase));

        return new LabelDiffResult(toAdd, toRemove);
    }
}
