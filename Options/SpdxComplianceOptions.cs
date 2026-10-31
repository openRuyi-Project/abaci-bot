namespace abaci_bot.Options;

/// <summary>
/// Configuration options for SPDX license compliance governance.
/// </summary>
public class SpdxComplianceOptions
{
    /// <summary>
    /// Enables or disables the SPDX compliance scanning module.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Remote URL of the official SPDX licenses JSON index.
    /// </summary>
    public string UpdateRemoteUrl { get; set; } = "https://spdx.org/licenses/licenses.json";

    /// <summary>
    /// Remote URL of the official SPDX exceptions JSON index.
    /// </summary>
    public string UpdateExceptionsRemoteUrl { get; set; } = "https://spdx.org/licenses/exceptions.json";

    /// <summary>
    /// Periodic background update interval in hours. Set to 0 to disable remote polling.
    /// </summary>
    public double AutoUpdateIntervalHours { get; set; } = 24;

    /// <summary>
    /// Whether to accept common RPM legacy license identifiers (e.g., GPLv2+, LGPLv2+).
    /// </summary>
    public bool AllowLegacyAliases { get; set; } = true;
}
