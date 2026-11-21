namespace abaci_bot.Services;

/// <summary>
/// Represents a bounded paginated GitHub search result, exposing truncation state and total match count.
/// </summary>
public sealed record GitHubSearchPageResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    bool IsTruncated);
