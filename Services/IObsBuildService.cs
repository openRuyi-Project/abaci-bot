namespace abaci_bot.Services;

/// <summary>
/// Status representation of an OBS package build result.
/// </summary>
public enum ObsStatusCode
{
    Succeeded,
    Failed,
    Building,
    Scheduled,
    Unresolvable,
    Disabled,
    Excluded,
    Unknown
}

/// <summary>
/// Detailed result of an OBS query for a specific package build.
/// </summary>
public record ObsPackageBuildResult(
    string Project,
    string Package,
    string Repository,
    string Arch,
    ObsStatusCode Code,
    string RawCode,
    string? Details = null);

/// <summary>
/// Deep module abstraction over Open Build Service (OBS) REST API.
/// Exclusively targets /public endpoints for anonymous query without credential leakage.
/// </summary>
public interface IObsBuildService
{
    /// <summary>
    /// Reads the commit SHA configured in the package's OBS _service definition.
    /// Returns null if _service does not exist or has no revision parameter.
    /// </summary>
    Task<string?> GetPackageServiceRevisionAsync(string project, string package, CancellationToken ct = default);

    /// <summary>
    /// Queries the build status of a package in the specified project, repository, and arch.
    /// </summary>
    Task<ObsPackageBuildResult> GetPackageBuildResultAsync(
        string project, string package, string repository, string arch, CancellationToken ct = default);
}

