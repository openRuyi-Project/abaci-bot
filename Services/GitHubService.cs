using Octokit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace abaci_bot.Services;

public class GitHubService : IGitHubService
{
    private readonly int _appId;
    private readonly string _privateKey;
    private long _installationId;
    private GitHubClient _client;
    private DateTimeOffset _tokenExpiry;

    public GitHubService(int appId, string privateKey, long installationId)
    {
        _appId = appId;
        _privateKey = privateKey;
        _installationId = installationId;
        _client = new GitHubClient(new ProductHeaderValue("abaci-bot"));
    }

    public void SetInstallationId(long installationId)
    {
        if (_installationId != installationId)
        {
            _installationId = installationId;
            _tokenExpiry = DateTimeOffset.MinValue; // 强制刷新 token
        }
    }

    // Ensures the GitHub client has a valid installation access token before API calls.
    private async Task EnsureAuthenticatedAsync()
    {
        if (_tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            return;

        var jwt = GenerateJwt();

        _client.Credentials = new Credentials(jwt, AuthenticationType.Bearer);
        var token = await _client.GitHubApps.CreateInstallationToken(_installationId);

        _client.Credentials = new Credentials(token.Token);
        _tokenExpiry = token.ExpiresAt;
    }

    // Generates a GitHub App JWT
    // 1) Build the header.
    // 2) Build the payload.
    // 3) Sign `header.payload` with the RSA private key using SHA256.
    // 4) Return `header.payload.signature`.
    private string GenerateJwt()
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = _appId
        }));

        var dataToSign = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKey);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(dataToSign),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{dataToSign}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(string input) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(input));

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // API Call

    public async Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber)
    {
        await EnsureAuthenticatedAsync();
        return await _client.PullRequest.Files(owner, repo, prNumber);
    }

    public async Task<string?> GetPullRequestAuthorEmailAsync(
        string owner, string repo, int prNumber)
    {
        await EnsureAuthenticatedAsync();
        // Get commits from the PR endpoint (works on the base repo, no need to access fork)
        var commits = await _client.PullRequest.Commits(owner, repo, prNumber);
        if (commits.Count > 0)
        {
            // Return the author email from the latest commit
            return commits[^1].Commit.Author.Email;
        }
        return null;
    }

    public async Task<string> GetFileContentAsync(
        string owner, string repo, string path, string sha)
    {
        await EnsureAuthenticatedAsync();
        var contents = await _client.Repository.Content.GetRawContentByRef(owner, repo, path, sha);
        return Encoding.UTF8.GetString(contents);
    }

    public async Task AddLabelsAsync(
        string owner, string repo, int issueNumber, params string[] labels)
    {
        await EnsureAuthenticatedAsync();
        await _client.Issue.Labels.AddToIssue(owner, repo, issueNumber, labels);
    }

    public async Task RemoveLabelAsync(
        string owner, string repo, int issueNumber, string label)
    {
        await EnsureAuthenticatedAsync();
        try
        {
            await _client.Issue.Labels.RemoveFromIssue(owner, repo, issueNumber, label);
        }
        catch (NotFoundException) { }
    }

    public async Task<HashSet<string>> GetTeamMembersAsync(
        string owner, string teamName)
    {
        await EnsureAuthenticatedAsync();
        var teamSlug = await _client.Organization.Team.GetByName(owner, teamName.ToLowerInvariant());
        var members = await _client.Organization.Team.GetAllMembers(teamSlug.Id);
        return members.Select(m => m.Login.ToLowerInvariant()).ToHashSet();
    }

    // Add more GitHub API methods as needed.
    public async Task<PullRequest> GetPullRequestAsync(string owner, string repo, int prNumber)
    {
        await EnsureAuthenticatedAsync();
        return await _client.PullRequest.Get(owner, repo, prNumber);
    }
}
