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
    /// Path or command name for the abipkgdiff binary executable.
    /// </summary>
    public string AbipkgdiffExecutablePath { get; set; } = "abipkgdiff";

    /// <summary>
    /// Backward-compatible alias for <see cref="AbipkgdiffExecutablePath"/>.
    /// </summary>
    public string AbidiffExecutablePath
    {
        get => AbipkgdiffExecutablePath;
        set => AbipkgdiffExecutablePath = value;
    }

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
