namespace abaci_bot.Services;

/// <summary>
/// Result of Libabigail ABI diff comparison.
/// </summary>
public record AbiDiffResult(
    bool Succeeded,
    bool HasAbiChange,
    bool IsBreakingChange,
    string Details,
    string? ErrorMessage = null,
    bool ToolMissing = false)
{
    public static AbiDiffResult NotPerformed(string reason) =>
        new(Succeeded: false, HasAbiChange: false, IsBreakingChange: false, Details: reason, ErrorMessage: reason);
}

/// <summary>
/// Pluggable runner contract for executing the external abidiff tool or simulation.
/// </summary>
public interface IAbiDiffRunner
{
    Task<AbiDiffResult> RunDiffAsync(
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath,
        CancellationToken ct = default);

    Task<AbiDiffResult> RunDiffAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default)
        => RunDiffAsync(oldRpmPath, newRpmPath, null, null, ct);
}

/// <summary>
/// High-level service for coordinating ABI compliance analysis between packages.
/// </summary>
public interface IAbiDiffService
{
    Task<AbiDiffResult> CompareRpmsAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default);

    Task<AbiDiffResult> CompareRpmsAsync(
        string oldRpmPath,
        string newRpmPath,
        string? oldDebuginfoPath,
        string? newDebuginfoPath,
        CancellationToken ct = default);
}
