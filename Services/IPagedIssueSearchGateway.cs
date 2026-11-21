using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Narrow gateway for paginated GitHub issue searches that preserve truncation metadata.
/// </summary>
public interface IPagedIssueSearchGateway
{
    /// <summary>
    /// Searches open issues matching <paramref name="label"/> in <paramref name="owner"/>/<paramref name="repo"/>,
    /// returning both the bounded page items and whether GitHub search results were truncated.
    /// </summary>
    Task<GitHubSearchPageResult<Issue>> SearchOpenIssuesByLabelAsync(
        string owner, string repo, string label, CancellationToken ct = default);
}
