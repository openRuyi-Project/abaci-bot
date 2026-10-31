namespace abaci_bot.Services;

/// <summary>
/// Service abstraction for formatting pull request governance reports, warnings, and gating advisories
/// using GitHub Flavored Markdown (GFM) blockquote alerts without emojis or internal task codes.
/// </summary>
public interface IGovernanceCommentFormatter
{
    /// <summary>
    /// Gets or sets the preferred default locale code (e.g., "en", "zh-CN"). Defaults to "en".
    /// </summary>
    string PreferredLocale { get; set; }

    /// <summary>
    /// Formats the top-level consolidated governance comment header.
    /// </summary>
    string FormatConsolidatedHeader(string? locale = null);

    /// <summary>
    /// Formats an SPDX license compliance check failure notice using GFM [!WARNING].
    /// </summary>
    string FormatSpdxLicenseIssue(IReadOnlyList<(string File, string Expression, string Details)> issues, string? locale = null);

    /// <summary>
    /// Formats a CVE security vulnerability assessment breakdown using GFM [!NOTE].
    /// </summary>
    string FormatCveBreakdown(IReadOnlyList<(string CveId, double? Cvss, string? Severity, string Source)> cves, string? locale = null);

    /// <summary>
    /// Formats a cascading rebuild impact threshold blockage alert using GFM [!IMPORTANT].
    /// </summary>
    string FormatRebuildGateBlocked(IReadOnlyList<string> packages, int affectedCount, int threshold, string targetBranch, string? locale = null);

    /// <summary>
    /// Formats an ABI breaking change warning using GFM [!CAUTION].
    /// </summary>
    string FormatAbiBreakingChange(string diffSnippet, string? locale = null);
}
