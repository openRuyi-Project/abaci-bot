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
    private readonly IGovernanceCommentFormatter _formatter;
    private readonly ILogger<SpdxComplianceModule>? _logger;

    public string ModuleName => nameof(SpdxComplianceModule);
    public int Priority => 60;

    public IReadOnlyCollection<string> ManagedLabels => new[] { GitHubLabels.ComplianceLicenseIssue };

    [GeneratedRegex(@"^\s*License\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LicenseTagRegex();

    public SpdxComplianceModule(
        ISpdxLicenseService spdxService,
        IOptions<SpdxComplianceOptions> options,
        IGovernanceCommentFormatter? formatter = null,
        ILogger<SpdxComplianceModule>? logger = null)
    {
        _spdxService = spdxService;
        _options = options.Value;
        _formatter = formatter ?? new GovernanceCommentFormatter();
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
        {
            context.ReconcileLabels(ManagedLabels, _ => false);
            return;
        }

        var licenseIssues = new List<(string File, string Expression, string Details)>();
        var hasUnverifiedSpecFiles = false;
        var verifiedSpecCount = 0;

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
                _logger?.LogWarning(ex, "Failed to retrieve content for spec file {Filename} in PR #{PrNumber}. Skipping file verification.",
                    file.FileName, context.PrNumber);
                hasUnverifiedSpecFiles = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                hasUnverifiedSpecFiles = true;
                continue;
            }

            verifiedSpecCount++;

            var matches = LicenseTagRegex().Matches(content);
            if (matches.Count == 0)
            {
                licenseIssues.Add((file.FileName, "None", "Missing required `License:` field declaration"));
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
                        ? $"Non-standard or unknown identifier(s): `{string.Join("`, `", result.InvalidIdentifiers)}`"
                        : result.ErrorMessage ?? "Invalid expression syntax";

                    licenseIssues.Add((file.FileName, licenseExpr, details));
                }
            }
        }

        if (licenseIssues.Count > 0)
        {
            context.ReconcileLabels(ManagedLabels, GitHubLabels.ComplianceLicenseIssue);
            if (!context.ExistingLabels.Contains(GitHubLabels.ComplianceLicenseIssue))
            {
                var report = _formatter.FormatSpdxLicenseIssue(licenseIssues);
                context.PendingComments.Add(report);
            }
            _logger?.LogInformation("PR #{PrNumber} flagged with License Issue ({Count} issues found).",
                context.PrNumber, licenseIssues.Count);
        }
        else if (!hasUnverifiedSpecFiles && verifiedSpecCount > 0)
        {
            // All spec files have valid licenses: remove any previous license issue label
            context.ReconcileLabels(ManagedLabels, (string?)null);
        }
        else
        {
            _logger?.LogWarning("PR #{PrNumber} contains unverified spec files; skipping license issue label removal to fail safe.",
                context.PrNumber);
        }
    }
}
