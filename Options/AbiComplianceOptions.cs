namespace abaci_bot.Options;

/// <summary>
/// Configuration options for binary ABI compliance checking.
/// Supports native Libabigail abipkgdiff with pure managed C# fallback.
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
    /// Enables automatic fallback to the pure managed C# ELF/RPM symbol diff engine
    /// when native abipkgdiff is missing or lacks RPM support.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// When true, requires native abipkgdiff with RPM support and fails startup
    /// if the host environment cannot provide it.
    /// </summary>
    public bool StrictEngine { get; set; }

    /// <summary>
    /// List of workflow names (case-insensitive substring match) that are authorized
    /// to trigger ABI analysis on workflow_run events.
    /// </summary>
    public List<string> WorkflowNames { get; set; } = new() { "build", "package", "rpm" };

    /// <summary>
    /// Base URL for downloading baseline/stable RPM packages.
    /// </summary>
    public string? StableRpmBaseUrl { get; set; }

    /// <summary>
    /// Working directory for RPM extraction and temporary artifact caching.
    /// </summary>
    public string? TempExtractionDirectory { get; set; }

    /// <summary>
    /// Process timeout in seconds for abipkgdiff execution. Defaults to 300 seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum uncompressed total payload bytes processed by the managed ELF/RPM engine (default: 1 GB).
    /// </summary>
    public long MaxTotalPayloadBytes { get; set; } = 1024L * 1024 * 1024;
}
