using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Abstraction layer over GitHub API interactions for pull requests, issues, teams, and rate limits.
/// All methods ensure the underlying GitHub App installation is authenticated prior to executing API calls.
/// </summary>
public interface IGitHubService
{
    /// <summary>
    /// Retrieves the list of files modified or added in the specified pull request.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="prNumber">Pull request number.</param>
    /// <returns>A read-only list of changed files with patch statistics.</returns>
    Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the commit author's email address from the latest commit of the pull request.
    /// Uses the base repository commits endpoint, avoiding cross-fork permissions.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="prNumber">Pull request number.</param>
    /// <returns>The author email address, or <c>null</c> if the pull request has no commits or email is missing.</returns>
    Task<string?> GetPullRequestAuthorEmailAsync(
        string owner, string repo, int prNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the raw text content of a file at a specific git reference or commit SHA.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="path">Relative file path within the repository.</param>
    /// <param name="sha">Git commit SHA, branch, or tag.</param>
    /// <returns>The file contents decoded as a UTF-8 string.</returns>
    Task<string> GetFileContentAsync(
        string owner, string repo, string path, string sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds one or more labels to an issue or pull request.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="issueNumber">Issue or pull request number.</param>
    /// <param name="labels">Array of label names to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddLabelsAsync(
        string owner, string repo, int issueNumber, string[] labels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a label from an issue or pull request.
    /// This operation is idempotent: if the label is already absent, the resulting <see cref="NotFoundException"/>
    /// is silently caught and swallowed.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="issueNumber">Issue or pull request number.</param>
    /// <param name="label">Label name to remove.</param>
    Task RemoveLabelAsync(
        string owner, string repo, int issueNumber, string label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the member logins of an organization team.
    /// </summary>
    /// <param name="owner">Organization name.</param>
    /// <param name="teamName">Team slug or name.</param>
    /// <returns>A hash set of GitHub login usernames configured with <see cref="StringComparer.OrdinalIgnoreCase"/>.</returns>
    Task<HashSet<string>> GetTeamMembersAsync(
        string owner, string teamName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the full metadata of a pull request.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="prNumber">Pull request number.</param>
    /// <returns>The <see cref="PullRequest"/> model from the GitHub API, or <c>null</c> if not found.</returns>
    Task<PullRequest?> GetPullRequestAsync(
        string owner, string repo, int prNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches open issues or pull requests that have not been updated since <paramref name="updatedBefore"/>.
    /// Enforces a defensive pagination safety cap of at most 5 pages (up to 500 items) to prevent quota exhaustion.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="updatedBefore">Cut-off timestamp. Only items with <c>updated:&lt;date</c> are returned.</param>
    /// <param name="isPr">When <c>true</c>, filters for pull requests (<c>is:pr</c>); otherwise filters for issues (<c>is:issue</c>).</param>
    /// <param name="labelFilter">Optional raw GitHub search qualifier string (e.g. <c>label:"Status: Stale"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of matching <see cref="Issue"/> models.</returns>
    Task<IReadOnlyList<Issue>> SearchStaleCandidatesAsync(
        string owner, string repo, DateTimeOffset updatedBefore, bool isPr, string? labelFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Closes an open issue or pull request.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Issue or pull request number.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CloseIssueAsync(
        string owner, string repo, int number, CancellationToken ct = default);

    /// <summary>
    /// Posts a comment on an issue or pull request.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Issue or pull request number.</param>
    /// <param name="comment">Markdown comment content.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreateCommentAsync(
        string owner, string repo, int number, string comment, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the authoritative Issue model for an issue or pull request.
    /// Returns null if the item is not found.
    /// </summary>
    /// <param name="owner">Repository owner or organization name.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Issue or pull request number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="Issue"/> model, or <c>null</c> if not found.</returns>
    Task<Issue?> GetIssueAsync(
        string owner, string repo, int number, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a point-in-time snapshot of both Core and Search API rate limit quotas and reset timestamps.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="GitHubRateLimitSnapshot"/> containing Core and Search limits and reset timestamps.</returns>
    Task<GitHubRateLimitSnapshot> GetRateLimitSnapshotAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Lists all workflow run artifacts associated with a GitHub Actions workflow run.
    /// </summary>
    Task<IReadOnlyList<(long Id, string Name, long SizeInBytes)>> ListWorkflowRunArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default);

    /// <summary>
    /// Downloads a workflow run artifact as a ZIP stream using authenticated GitHub App credentials.
    /// </summary>
    Task<Stream> DownloadArtifactZipStreamAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default);
}
