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
    bool ToolMissing = false);

/// <summary>
/// Pluggable runner contract for executing the external abidiff tool or simulation.
/// </summary>
public interface IAbiDiffRunner
{
    Task<AbiDiffResult> RunDiffAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default);
}

/// <summary>
/// High-level service for coordinating ABI compliance analysis between packages.
/// </summary>
public interface IAbiDiffService
{
    Task<AbiDiffResult> CompareRpmsAsync(string oldRpmPath, string newRpmPath, CancellationToken ct = default);
}
