using System.Diagnostics.CodeAnalysis;

namespace abaci_bot.Options;

/// <summary>
/// A titled external reference link rendered in manual rebuild verification guidance comments.
/// </summary>
public sealed record VerificationReferenceLink
{
    /// <summary>
    /// Display title used for English and default locales (1..120 characters, single line).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional Chinese display title used in Chinese locales; falls back to <see cref="Title"/> when null or blank.
    /// </summary>
    public string? TitleZh { get; set; }

    /// <summary>
    /// Absolute HTTP or HTTPS destination URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Validates and normalizes <paramref name="rawUrl"/> as an absolute HTTP/HTTPS URI with no whitespace or control characters.
    /// </summary>
    internal static bool TryNormalizeHttpUrl(string? rawUrl, [NotNullWhen(true)] out Uri? normalizedUri)
    {
        normalizedUri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalizedUri = candidate;
        return true;
    }
}

/// <summary>
/// Configuration options for RPM dependency DAG building and rebuild impact gating.
/// </summary>
public class RebuildImpactOptions
{
    /// <summary>
    /// Enables or disables the rebuild impact analysis and gating module.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Remote URL of the repository metadata primary.xml.gz file.
    /// </summary>
    public string? PrimaryXmlUrl { get; set; }

    /// <summary>
    /// Path to a local primary.xml or primary.xml.gz cache or fixture file.
    /// </summary>
    public string? LocalCachePath { get; set; }

    /// <summary>
    /// Background refresh interval in hours for metadata re-download and DAG reconstruction.
    /// </summary>
    public double RefreshIntervalHours { get; set; } = 6;

    /// <summary>
    /// Calculation mode: "Transitive" (transitive closure) or "Direct" (direct dependents only).
    /// </summary>
    public string CalculationMode { get; set; } = "Transitive";

    /// <summary>
    /// Maximum recursive traversal depth for transitive closure calculation (null for unlimited).
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Threshold of affected downstream packages to block pull requests on target branch.
    /// </summary>
    public int BlockThreshold { get; set; } = 1001;

    /// <summary>
    /// Target branch name or classification to apply merge blocking (e.g., "Rolling").
    /// </summary>
    public string TargetBranchToBlock { get; set; } = "Rolling";

    /// <summary>
    /// Optional "See also" reference links appended to manual rebuild verification guidance comments (max 5 unique URLs).
    /// Empty by default so deployments are not coupled to distribution-specific or unpublished documentation URLs.
    /// Default: empty list.
    /// </summary>
    public List<VerificationReferenceLink> VerificationGuideLinks { get; set; } = new();
}
