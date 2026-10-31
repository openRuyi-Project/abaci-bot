namespace abaci_bot.Services;

/// <summary>
/// Detailed report of a queried CVE vulnerability.
/// </summary>
public record CveReport(
    string CveId,
    double? CvssScore,
    string? SeverityLabel,
    string Source,
    string? Summary = null)
{
    public const string SourceQueryFailed = "QueryFailed";
    public const string SourceUnknown = "Unknown";
}

/// <summary>
/// Service abstraction for retrieving vulnerability metadata and CVSS scores from external security databases.
/// </summary>
public interface ICveSecurityService
{
    /// <summary>
    /// Queries vulnerability severity details for a single CVE identifier.
    /// </summary>
    Task<CveReport> QueryCveAsync(string cveId, CancellationToken ct = default);

    /// <summary>
    /// Queries vulnerability severity details for multiple CVE identifiers concurrently.
    /// </summary>
    Task<IReadOnlyList<CveReport>> QueryMultipleCvesAsync(IEnumerable<string> cveIds, CancellationToken ct = default);
}
