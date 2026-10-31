namespace abaci_bot.Options;

/// <summary>
/// Configuration options for binary ABI compliance checking using Libabigail.
/// </summary>
public class AbiComplianceOptions
{
    /// <summary>
    /// Enables or disables the ABI compliance module.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path or command name for the abidiff binary executable.
    /// </summary>
    public string AbidiffExecutablePath { get; set; } = "abidiff";

    /// <summary>
    /// Base URL for downloading baseline/stable RPM packages.
    /// </summary>
    public string? StableRpmBaseUrl { get; set; }

    /// <summary>
    /// Working directory for RPM extraction and temporary artifact caching.
    /// </summary>
    public string? TempExtractionDirectory { get; set; }

    /// <summary>
    /// Process timeout in seconds for abidiff execution. Defaults to 300 seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}
