namespace abaci_bot.Options;

/// <summary>
/// Configuration options for CVE vulnerability severity and lifecycle tracking.
/// </summary>
public class CveSecurityOptions
{
    /// <summary>
    /// Enables or disables the CVE security tracking module.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL for the OSV.dev vulnerability lookup API.
    /// </summary>
    public string OsvApiBaseUrl { get; set; } = "https://api.osv.dev/v1/vulns/";

    /// <summary>
    /// Base URL for the NIST NVD CVE 2.0 API.
    /// </summary>
    public string NvdApiBaseUrl { get; set; } = "https://services.nvd.nist.gov/rest/json/cves/2.0";

    /// <summary>
    /// Optional NVD API key to increase rate limits.
    /// </summary>
    public string? NvdApiKey { get; set; }

    /// <summary>
    /// Duration in hours to cache queried known/confirmed CVE details in memory.
    /// </summary>
    public double CacheExpirationHours { get; set; } = 12;

    /// <summary>
    /// Duration in hours to cache unknown/unindexed (e.g. embargoed) CVE records in memory before re-querying.
    /// Defaults to 1 hour to promptly detect public disclosure.
    /// </summary>
    public double UnknownCacheExpirationHours { get; set; } = 1;

    /// <summary>
    /// HTTP request timeout in seconds for vulnerability data queries.
    /// </summary>
    public double RequestTimeoutSeconds { get; set; } = 15;
}
