namespace abaci_bot.Services;

/// <summary>
/// Result of an automated package update publish attempt.
/// </summary>
public record PublishResult(
    bool Success,
    bool Skipped,
    string? SkipReason = null,
    int? PullRequestNumber = null,
    string? PullRequestUrl = null);

/// <summary>
/// Deep module interface for branch management, commit generation, and pull request upsert.
/// </summary>
public interface IPackageUpdatePublisher
{
    Task<PublishResult> PublishUpdateAsync(
        UpdateCandidate candidate,
        SpecRewriteResult rewriteResult,
        string targetRepo,
        string baseBranch = "main",
        CancellationToken ct = default);
}

