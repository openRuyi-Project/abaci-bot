using System.Globalization;
using System.Text;

namespace abaci_bot.Services;

/// <summary>
/// Default implementation of <see cref="IGovernanceCommentFormatter"/>.
/// Formats pull request comments using GitHub Flavored Markdown (GFM) alerts
/// without emojis or internal task codes, defaulting to English with internationalization support.
/// </summary>
public class GovernanceCommentFormatter : IGovernanceCommentFormatter
{
    public string PreferredLocale { get; set; } = "en";

    private bool IsChinese(string? locale) =>
        string.Equals(locale ?? PreferredLocale, "zh", StringComparison.OrdinalIgnoreCase) ||
        (locale ?? PreferredLocale).StartsWith("zh-", StringComparison.OrdinalIgnoreCase);

    public string FormatConsolidatedHeader(string? locale = null)
    {
        return IsChinese(locale)
            ? "## abaci-bot 门禁与治理检查报告"
            : "## abaci-bot Governance Review";
    }

    public string FormatSpdxLicenseIssue(IReadOnlyList<(string File, string Expression, string Details)> issues, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var sb = new StringBuilder();
        sb.AppendLine("> [!WARNING]");

        if (isZh)
        {
            sb.AppendLine("> **SPDX 许可证合规检查未通过**");
            sb.AppendLine("> 所有软件包 Spec 必须采用符合标准 SPDX 规范的许可证标识符：");
            foreach (var (file, expr, details) in issues)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{file}`: 许可证表达式 `{expr}` 不合规（{details}）。");
            }
            sb.AppendLine(">");
            sb.Append("> 请参考 [SPDX License List](https://spdx.org/licenses/) 修正后再推入更新。");
        }
        else
        {
            sb.AppendLine("> **SPDX License Compliance Check Failed**");
            sb.AppendLine("> All RPM package specifications must declare valid standard SPDX license identifiers:");
            foreach (var (file, expr, details) in issues)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{file}`: Expression `{expr}` is not compliant ({details}).");
            }
            sb.AppendLine(">");
            sb.Append("> Please refer to the [SPDX License List](https://spdx.org/licenses/) to update the license identifiers.");
        }

        return sb.ToString();
    }

    public string FormatCveBreakdown(IReadOnlyList<(string CveId, double? Cvss, string? Severity, string Source)> cves, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var sb = new StringBuilder();
        sb.AppendLine("> [!NOTE]");

        if (isZh)
        {
            sb.AppendLine("> **安全漏洞评估清单**");
            sb.AppendLine("> 检测到本 PR 包含 CVE 漏洞修复或变更关联：");
            foreach (var (cveId, cvss, severity, source) in cves)
            {
                if (cvss.HasValue)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: CVSS **{cvss.Value:F1}** ({severity}, 来源: {source})");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: 未查询到公网 CVSS 评分（请安全团队手动核验）");
                }
            }
        }
        else
        {
            sb.AppendLine("> **Security Vulnerability Assessment**");
            sb.AppendLine("> Detected the following CVE vulnerability references associated with this pull request:");
            foreach (var (cveId, cvss, severity, source) in cves)
            {
                if (cvss.HasValue)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: CVSS **{cvss.Value:F1}** ({severity}, source: {source})");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"> - `{cveId}`: No public CVSS score found (manual security review required; expected if embargoed or unreleased)");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public string FormatRebuildGateBlocked(IReadOnlyList<string> packages, int affectedCount, int threshold, string targetBranch, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var pkgList = string.Join("`, `", packages);
        var sb = new StringBuilder();
        sb.AppendLine("> [!IMPORTANT]");

        if (isZh)
        {
            sb.AppendLine("> **Rebuild 规模准入拦截**");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> 本 PR 涉及基础包变更（`{pkgList}`），在下游反向依赖 DAG 中触发 **{affectedCount}** 个软件包重构（阈值: {threshold}）。");
            sb.AppendLine(">");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> 为防止大规模级联构建影响 {targetBranch} 主干分支稳定性，已自动添加 `Workflow: Blocked` 拦截合并。");
            sb.Append("> **建议处置**：请将 PR 目标分支改选为 `Staging` 分支进行隔离验证。");
        }
        else
        {
            sb.AppendLine("> **Rebuild Impact Gate: Merge Blocked**");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> Changes to base packages (`{pkgList}`) trigger a cascading rebuild of **{affectedCount}** downstream packages in the reverse dependency graph (threshold: {threshold}).");
            sb.AppendLine(">");
            sb.AppendLine(CultureInfo.InvariantCulture, $"> To safeguard the stability of the `{targetBranch}` branch, this pull request has been automatically marked with `Workflow: Blocked` to prevent merging.");
            sb.Append("> **Recommended Action**: Retarget this pull request to the `Staging` branch for isolated build verification.");
        }

        return sb.ToString();
    }

    public string FormatAbiBreakingChange(string diffSnippet, string? locale = null)
    {
        var isZh = IsChinese(locale);
        var snippet = diffSnippet;
        if (snippet.Length > 1500)
        {
            snippet = isZh
                ? snippet[..1500] + "\n... (输出过长已截断)"
                : snippet[..1500] + "\n... (output truncated)";
        }

        var sb = new StringBuilder();
        sb.AppendLine("> [!CAUTION]");

        if (isZh)
        {
            sb.AppendLine("> **ABI 破坏性兼容性变更预警**");
            sb.AppendLine("> 门禁比对检测到二进制共享库存在破坏性 ABI 改变（缺少导出的公共符号或 SONAME 发生变更）：");
            sb.AppendLine(">");
            sb.AppendLine("> ```");
            foreach (var line in snippet.Split('\n'))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> {line.TrimEnd('\r')}");
            }
            sb.AppendLine("> ```");
            sb.AppendLine(">");
            sb.Append("> 下游依赖此动态库的应用可能发生运行时符号解析崩溃，请评估是否属于有意变更。");
        }
        else
        {
            sb.AppendLine("> **ABI Breaking Change Detected**");
            sb.AppendLine("> Binary comparison detected incompatible ABI modifications in shared libraries (e.g. missing exported symbols or SONAME changes):");
            sb.AppendLine(">");
            sb.AppendLine("> ```");
            foreach (var line in snippet.Split('\n'))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> {line.TrimEnd('\r')}");
            }
            sb.AppendLine("> ```");
            sb.AppendLine(">");
            sb.Append("> Downstream applications depending on this library may encounter runtime symbol resolution crashes. Please verify whether this breaking change is intended.");
        }

        return sb.ToString();
    }
}
