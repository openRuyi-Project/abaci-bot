namespace abaci_bot.Services;

/// <summary>
/// Origin source of the discovered update candidate.
/// </summary>
public enum CandidateOrigin
{
    Lookout,
    RustCrate
}

/// <summary>
/// Authoritative representation of an upstream version update candidate.
/// </summary>
/// <param name="PackageName">Name of the RPM package (e.g. "curl", "rust-anstream-0.6").</param>
/// <param name="CurrentVersion">Current version as stated in the package's SPEC.</param>
/// <param name="TargetVersion">Newer upstream version discovered.</param>
/// <param name="Origin">Data source producing this candidate.</param>
/// <param name="EvidenceUrl">Traceable reference link for maintainer review.</param>
/// <param name="IsSecurityAdvisory">Whether this upgrade resolves an upstream security bulletin.</param>
/// <param name="PublishedAt">Timestamp of version release (used for cooldown enforcement if available).</param>
public record UpdateCandidate(
    string PackageName,
    string CurrentVersion,
    string TargetVersion,
    CandidateOrigin Origin,
    string EvidenceUrl,
    bool IsSecurityAdvisory = false,
    DateTimeOffset? PublishedAt = null);

/// <summary>
/// Deep module abstraction providing update candidates from upstream tracking services.
/// </summary>
public interface IUpdateCandidateSource
{
    string SourceName { get; }

    Task<IReadOnlyList<UpdateCandidate>> GetCandidatesAsync(CancellationToken ct = default);
}

