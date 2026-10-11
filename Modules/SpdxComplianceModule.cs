using System.Text.RegularExpressions;
using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit.Webhooks.Events.PullRequest;

namespace abaci_bot.Modules;

/// <summary>
/// Governance module scanning RPM spec files for SPDX license compliance.
/// Flags non-standard or missing license tags with Compliance: License Issue and warns in comments.
/// </summary>
public partial class SpdxComplianceModule : IPullRequestModule
{
    private readonly ISpdxLicenseService _spdxService;
    private readonly SpdxComplianceOptions _options;
    private readonly ILogger<SpdxComplianceModule>? _logger;

    public string ModuleName => nameof(SpdxComplianceModule);
    public int Priority => 60;

    public IReadOnlyCollection<string> ManagedLabels => new[] { GitHubLabels.ComplianceLicenseIssue };

    [GeneratedRegex(@"^\s*License\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LicenseTagRegex();

    public SpdxComplianceModule(
        ISpdxLicenseService spdxService,
        IOptions<SpdxComplianceOptions> options,
        ILogger<SpdxComplianceModule>? logger = null)
    {
        _spdxService = spdxService;
        _options = options.Value;
        _logger = logger;
    }

    public bool ShouldProcess(PullRequestContext context)
    {
        if (!_options.Enabled || context.IsTerminal)
            return false;

        return context.EventType == PullRequestEventType.PullRequest &&
               (context.PullRequestAction == PullRequestAction.Opened ||
                context.PullRequestAction == PullRequestAction.Synchronize ||
                context.PullRequestAction == PullRequestAction.Reopened ||
                context.PullRequestAction == PullRequestAction.ReadyForReview);
    }

    public async Task ProcessAsync(PullRequestContext context, CancellationToken cancellationToken)
    {
        var files = await context.ChangedFiles.Value;
        var specFiles = files
            .Where(f => f.FileName.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(f.Status, "removed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (specFiles.Count == 0)
            return;

        var licenseIssues = new List<string>();

        foreach (var file in specFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string content;
            try
            {
                content = await context.GetFileContentAsync(file.FileName, context.HeadSha, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to retrieve content for spec file {Filename} in PR #{PrNumber}",
                    file.FileName, context.PrNumber);
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
                continue;

            var matches = LicenseTagRegex().Matches(content);
            if (matches.Count == 0)
            {
                licenseIssues.Add($"- `{file.FileName}`: 缺失必需的 `License:` 字段声明。");
                continue;
            }

            var seenExprs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in matches)
            {
                var licenseExpr = match.Groups[1].Value.Trim();
                if (!seenExprs.Add(licenseExpr))
                    continue;

                var result = _spdxService.ValidateExpression(licenseExpr);
                if (!result.IsValid)
                {
                    var details = result.InvalidIdentifiers.Count > 0
                        ? $"非标准或未知标识符: `{string.Join("`, `", result.InvalidIdentifiers)}`"
                        : result.ErrorMessage ?? "表达式语法无效";

                    licenseIssues.Add($"- `{file.FileName}`: 许可证表达式 `{licenseExpr}` 不合规（{details}）。");
                }
            }
        }

        if (licenseIssues.Count > 0)
        {
            context.LabelsToAdd.Add(GitHubLabels.ComplianceLicenseIssue);
            var report = "#### ⚠️ SPDX 许可证合规检查未通过\n" +
                         "openRuyi 要求所有软件包 Spec 必须采用符合标准 SPDX 规范的许可证标识符：\n" +
                         string.Join("\n", licenseIssues) +
                         "\n\n请参考 [SPDX License List](https://spdx.org/licenses/) 修正后再推入更新。";

            context.PendingComments.Add(report);
            _logger?.LogInformation("PR #{PrNumber} flagged with License Issue ({Count} issues found).",
                context.PrNumber, licenseIssues.Count);
        }
        else
        {
            // All spec files have valid licenses: remove any previous license issue label
            context.LabelsToRemove.Add(GitHubLabels.ComplianceLicenseIssue);
        }
    }
}
