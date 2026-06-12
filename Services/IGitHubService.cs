using Octokit;

namespace abaci_bot.Services;

public interface IGitHubService
{
    Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber);

    Task<string?> GetPullRequestAuthorEmailAsync(
        string owner, string repo, int prNumber);

    Task<string> GetFileContentAsync(
        string owner, string repo, string path, string sha);

    Task AddLabelsAsync(
        string owner, string repo, int issueNumber, params string[] labels);

    Task RemoveLabelAsync(
        string owner, string repo, int issueNumber, string label);

    Task<HashSet<string>> GetTeamMembersAsync(
        string owner, string teamName);

    Task<PullRequest> GetPullRequestAsync(
        string owner, string repo, int prNumber);
}
