namespace abaci_bot.Services;

public sealed record IgnoredPackageEntry(
    string PackageName,
    string? Reason,
    string IgnoredBy,
    DateTimeOffset IgnoredAt);

public sealed record PackageFailureEntry(
    int ConsecutiveFailures,
    DateTimeOffset? CooldownUntil);

public sealed record PendingEphemeralComment(
    string Owner,
    string Repo,
    int PrNumber,
    long CommentId,
    DateTimeOffset DeleteAt,
    int RetryCount = 0,
    string? CommentNodeId = null);

/// <summary>
/// Unified persistent state store for runtime package controls, failure cooldowns,
/// and user language preferences.
/// </summary>
public interface IBotStateStore
{
    string StateDirectory { get; }

    string? GetUserLocale(string userLogin);
    void SetUserLocale(string userLogin, string locale);
    bool ClearUserLocale(string userLogin);
    int PruneExpiredUserLocales(TimeSpan maxAge);

    IgnoredPackageEntry? GetIgnoredPackage(string owner, string repo, string packageName);
    IReadOnlyList<IgnoredPackageEntry> GetIgnoredPackages(string owner, string repo);
    void SetIgnoredPackage(string owner, string repo, string packageName, string? reason, string ignoredBy);
    bool RemoveIgnoredPackage(string owner, string repo, string packageName);

    PackageFailureEntry? GetPackageFailure(string owner, string repo, string packageName);
    void SetPackageFailure(string owner, string repo, string packageName, int consecutiveFailures, DateTimeOffset? cooldownUntil);
    bool ClearPackageFailure(string owner, string repo, string packageName);
    bool TryMarkFailedPrRecorded(string owner, string repo, int prNumber);

    void AddPendingEphemeralComment(PendingEphemeralComment entry);
    IReadOnlyList<PendingEphemeralComment> GetDueEphemeralComments(DateTimeOffset now);
    void RemovePendingEphemeralComment(string owner, string repo, long commentId);
    bool RecordEphemeralRetry(string owner, string repo, long commentId, DateTimeOffset nextAttemptAt, int maxRetries = 5);
}
