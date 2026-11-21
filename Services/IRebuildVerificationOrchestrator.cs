namespace abaci_bot.Services;

/// <summary>
/// Input describing a pull request change whose downstream rebuild blast radius exceeded
/// the configured admission threshold and requires isolated reverse-dependency verification.
/// </summary>
public sealed record RebuildVerificationRequest(
    string Owner,
    string Repo,
    int PrNumber,
    string HeadSha,
    string TargetBranch,
    IReadOnlyList<string> ChangedPackages,
    int AffectedCount,
    int Threshold,
    string? Locale = null);

/// <summary>
/// Domain lifecycle states for rebuild impact verification orchestration.
/// </summary>
public enum RebuildVerificationState
{
    /// <summary>
    /// Contributors must complete isolated verification manually; keeps <c>Workflow: Blocked</c>
    /// and suppresses duplicate advisory comments when the block label is already present.
    /// </summary>
    ManualVerificationRequired,

    /// <summary>
    /// Bot-initiated asynchronous OBS verification is running; keeps <c>Workflow: Blocked</c>
    /// and posts <see cref="RebuildVerificationResult.AdvisoryComment"/> whenever non-null.
    /// </summary>
    Pending,

    /// <summary>
    /// Verification succeeded; clears rebuild-applied <c>Workflow: Blocked</c>.
    /// </summary>
    Passed,

    /// <summary>
    /// Verification failed; keeps <c>Workflow: Blocked</c> and posts failure details whenever
    /// <see cref="RebuildVerificationResult.AdvisoryComment"/> is non-null.
    /// </summary>
    Failed
}

/// <summary>
/// Decision returned by <see cref="IRebuildVerificationOrchestrator"/> representing the current verification state,
/// optional governance comment, and optional external tracking link.
/// </summary>
public sealed record RebuildVerificationResult(
    RebuildVerificationState State,
    string? AdvisoryComment = null,
    string? TrackingUrl = null)
{
    /// <summary>
    /// Indicates whether <c>Workflow: Blocked</c> remains active. Only <see cref="RebuildVerificationState.Passed"/> clears the gate.
    /// </summary>
    public bool ShouldBlockMerge => State != RebuildVerificationState.Passed;
}

/// <summary>
/// Active extension seam for high-impact rebuild verification workflows.
/// <see cref="Modules.RebuildImpactModule"/> is intentionally stateless with respect to verification lifecycle:
/// persistence, TTLs, restart recovery, retry policies, and manual overrides belong exclusively inside concrete
/// orchestrator implementations (e.g., future OBS automation). Return <c>null</c> for
/// <see cref="RebuildVerificationResult.AdvisoryComment"/> when a <see cref="RebuildVerificationState.Pending"/> or
/// <see cref="RebuildVerificationState.Failed"/> evaluation has no state transition to report.
/// </summary>
public interface IRebuildVerificationOrchestrator
{
    /// <summary>
    /// Evaluates or initiates isolated reverse-dependency verification for a high-impact pull request.
    /// </summary>
    Task<RebuildVerificationResult> RequestVerificationAsync(
        RebuildVerificationRequest request,
        CancellationToken cancellationToken = default);
}
