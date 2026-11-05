using Microsoft.Extensions.Logging;
using Octokit;
using Octokit.Internal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace abaci_bot.Services;

public class GitHubService : IGitHubService, IDisposable
{
    private static readonly AsyncLocal<CancellationToken> _ambientCancellationToken = new();

    private readonly int _appId;
    private readonly string _privateKey;
    private readonly long _installationId;
    private readonly string? _botPat;
    private IGitHubClient _client;
    private DateTimeOffset _tokenExpiry;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly ILogger<GitHubService>? _logger;

    public GitHubService(int appId, string privateKey, long installationId, ILogger<GitHubService>? logger = null)
        : this(appId, privateKey, installationId, (string?)null, CreateDefaultClient(), logger)
    {
    }

    public GitHubService(int appId, string privateKey, long installationId, string? botPat, ILogger<GitHubService>? logger = null)
        : this(appId, privateKey, installationId, botPat, CreateDefaultClient(), logger)
    {
    }

    internal GitHubService(int appId, string privateKey, long installationId, HttpMessageHandler httpMessageHandler, ILogger<GitHubService>? logger = null)
        : this(appId, privateKey, installationId, null, CreateClientWithTransport(new HttpClientAdapter(() => httpMessageHandler)), logger)
    {
    }

    internal GitHubService(int appId, string privateKey, long installationId, IGitHubClient client, ILogger<GitHubService>? logger = null)
        : this(appId, privateKey, installationId, null, client, logger)
    {
    }

    internal GitHubService(int appId, string privateKey, long installationId, string? botPat, IGitHubClient client, ILogger<GitHubService>? logger = null)
    {
        _appId = appId;
        _privateKey = privateKey;
        _installationId = installationId;
        _botPat = botPat;
        _client = client;
        _logger = logger;
    }

    private static IGitHubClient CreateDefaultClient()
    {
        var adapter = new HttpClientAdapter(() => new HttpClientHandler());
        return CreateClientWithTransport(adapter);
    }

    private static IGitHubClient CreateClientWithTransport(Octokit.Internal.IHttpClient httpClient)
    {
        var productHeader = new ProductHeaderValue("abaci-bot");
        var cancellable = new CancellableHttpClient(httpClient, () => _ambientCancellationToken.Value);
        var connection = new Connection(productHeader, cancellable);
        return new GitHubClient(connection);
    }

    private static IDisposable SetAmbientToken(CancellationToken cancellationToken)
    {
        var prior = _ambientCancellationToken.Value;
        _ambientCancellationToken.Value = cancellationToken;
        return new TokenScope(prior);
    }

    private sealed class TokenScope : IDisposable
    {
        private readonly CancellationToken _prior;
        public TokenScope(CancellationToken prior) => _prior = prior;
        public void Dispose() => _ambientCancellationToken.Value = _prior;
    }

    // Ensures the GitHub client has a valid installation access token before API calls.
    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);

        // If a dedicated Bot PAT is configured, prioritize it
        if (!string.IsNullOrWhiteSpace(_botPat))
        {
            _client.Connection.Credentials = new Credentials(_botPat);
            return;
        }

        if (_tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            return;

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (_tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
                return;

            var jwt = GenerateJwt();

            _client.Connection.Credentials = new Credentials(jwt, AuthenticationType.Bearer);
            var token = await _client.GitHubApps.CreateInstallationToken(_installationId);

            _client.Connection.Credentials = new Credentials(token.Token);
            _tokenExpiry = token.ExpiresAt;
        }
        finally
        {
            _authLock.Release();
        }
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

    public async Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await _client.PullRequest.Files(owner, repo, prNumber);
    }

    public async Task<string?> GetPullRequestAuthorEmailAsync(
        string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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
        string owner, string repo, string path, string sha, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var contents = await _client.Repository.Content.GetRawContentByRef(owner, repo, path, sha);
        return Encoding.UTF8.GetString(contents);
    }

    public async Task AddLabelsAsync(
        string owner, string repo, int issueNumber, string[] labels, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await _client.Issue.Labels.AddToIssue(owner, repo, issueNumber, labels);
    }

    public async Task RemoveLabelAsync(
        string owner, string repo, int issueNumber, string label, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _client.Issue.Labels.RemoveFromIssue(owner, repo, issueNumber, label);
        }
        catch (NotFoundException) { }
    }

    public async Task<HashSet<string>> GetTeamMembersAsync(
        string owner, string teamName, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var teamSlug = await _client.Organization.Team.GetByName(owner, teamName.ToLowerInvariant());
        var members = await _client.Organization.Team.GetAllMembers(teamSlug.Id);
        return members.Select(m => m.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<PullRequest?> GetPullRequestAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        using var _ = SetAmbientToken(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _client.PullRequest.Get(owner, repo, prNumber);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Issue>> SearchStaleCandidatesAsync(
        string owner, string repo, DateTimeOffset updatedBefore, bool isPr, string? labelFilter = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();
        var dateStr = updatedBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var baseQuery = isPr
            ? $"repo:{owner}/{repo} is:open is:pr -is:draft updated:<{dateStr}"
            : $"repo:{owner}/{repo} is:open is:issue updated:<{dateStr}";
        var query = !string.IsNullOrWhiteSpace(labelFilter) ? $"{baseQuery} {labelFilter}" : baseQuery;

        var allItems = new List<Issue>();
        var page = 1;
        const int perPage = 100;
        const int maxPages = 5; // Safety bound to prevent runaway loops

        while (page <= maxPages)
        {
            ct.ThrowIfCancellationRequested();

            var searchRequest = new SearchIssuesRequest(query)
            {
                Type = isPr ? IssueTypeQualifier.PullRequest : IssueTypeQualifier.Issue,
                Page = page,
                PerPage = perPage
            };

            SearchIssuesResult searchResult;
            try
            {
                searchResult = await _client.Search.SearchIssues(searchRequest);
            }
            catch (RateLimitExceededException ex)
            {
                _logger?.LogDebug(ex, "Search API rate limit exceeded during pagination (page {Page}) for {Owner}/{Repo}. Discarding {Count} partially retrieved candidates.",
                    page, owner, repo, allItems.Count);
                throw;
            }
            if (searchResult.Items.Count == 0)
                break;

            allItems.AddRange(searchResult.Items);
            if (searchResult.Items.Count < perPage || allItems.Count >= searchResult.TotalCount)
                break;

            if (page == maxPages && allItems.Count < searchResult.TotalCount)
            {
                _logger?.LogWarning("SearchStaleCandidatesAsync truncated for {Owner}/{Repo}: retrieved {Retrieved} of {Total} items (max {MaxPages} pages).",
                    owner, repo, allItems.Count, searchResult.TotalCount, maxPages);
            }

            page++;
            if (page <= maxPages)
            {
                // Throttle between pagination requests to prevent search API bursts
                await Task.Delay(500, ct);
            }
        }

        return allItems;
    }

    public async Task<Issue?> GetIssueAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();
        try
        {
            return await _client.Issue.Get(owner, repo, number);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task CloseIssueAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        var update = new IssueUpdate { State = ItemState.Closed };
        await _client.Issue.Update(owner, repo, number, update);
    }

    public async Task<Issue> CreateIssueAsync(
        string owner, string repo, string title, string body, string[]? labels = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var newIssue = new NewIssue(title)
        {
            Body = body
        };

        if (labels != null && labels.Length > 0)
        {
            foreach (var label in labels)
            {
                newIssue.Labels.Add(label);
            }
        }

        return await _client.Issue.Create(owner, repo, newIssue);
    }

    public async Task<Issue> UpdateIssueAsync(
        string owner, string repo, int number, string title, string body, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var update = new IssueUpdate
        {
            Title = title,
            Body = body
        };

        return await _client.Issue.Update(owner, repo, number, update);
    }

    public async Task CreateCommentAsync(string owner, string repo, int number, string comment, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        await _client.Issue.Comment.Create(owner, repo, number, comment);
    }

    public async Task UpdateCommentAsync(string owner, string repo, long commentId, string comment, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        await _client.Issue.Comment.Update(owner, repo, commentId, comment);
    }

    public async Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        await EnsureAuthenticatedAsync(ct);
        return await _client.Issue.Comment.GetAllForIssue(owner, repo, number);
    }

    public async Task<GitHubRateLimitSnapshot> GetRateLimitSnapshotAsync(CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        try
        {
            await EnsureAuthenticatedAsync(ct);
            var rateLimits = await _client.RateLimit.GetRateLimits();
            return new GitHubRateLimitSnapshot(
                CoreRemaining: rateLimits?.Rate?.Remaining,
                CoreReset: rateLimits?.Rate?.Reset,
                SearchRemaining: rateLimits?.Resources?.Search?.Remaining,
                SearchReset: rateLimits?.Resources?.Search?.Reset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthorizationException authEx)
        {
            _logger?.LogError(authEx, "Authentication failure while requesting GitHub rate limit quota.");
            return new GitHubRateLimitSnapshot(null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query GitHub rate limits due to network or API error.");
            var lastInfo = _client.GetLastApiInfo()?.RateLimit;
            return new GitHubRateLimitSnapshot(
                CoreRemaining: lastInfo?.Remaining,
                CoreReset: lastInfo?.Reset,
                SearchRemaining: null,
                SearchReset: null);
        }
    }

    public async Task<IReadOnlyList<(long Id, string Name, long SizeInBytes)>> ListWorkflowRunArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var response = await _client.Actions.Artifacts.ListWorkflowArtifacts(owner, repo, runId);
            if (response?.Artifacts == null)
                return Array.Empty<(long Id, string Name, long SizeInBytes)>();

            return response.Artifacts
                .Select(a => (a.Id, a.Name, (long)a.SizeInBytes))
                .ToList();
        }
        catch (NotFoundException)
        {
            _logger?.LogDebug("Workflow run {RunId} not found or has no artifacts for {Owner}/{Repo}", runId, owner, repo);
            return Array.Empty<(long Id, string Name, long SizeInBytes)>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to list artifacts for workflow run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            return Array.Empty<(long Id, string Name, long SizeInBytes)>();
        }
    }

    public async Task<Stream> DownloadArtifactZipStreamAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await _client.Actions.Artifacts.DownloadArtifact(owner, repo, artifactId, "zip");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to download artifact {ArtifactId} for {Owner}/{Repo}", artifactId, owner, repo);
            throw;
        }
    }

    public async Task<string?> GetRefShaAsync(
        string owner, string repo, string branchName, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var refPath = branchName.StartsWith("heads/", StringComparison.OrdinalIgnoreCase)
            ? branchName
            : $"heads/{branchName.TrimStart('/')}";

        try
        {
            var reference = await _client.Git.Reference.Get(owner, repo, refPath);
            return reference?.Object?.Sha;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task CreateOrUpdateRefAsync(
        string owner, string repo, string branchName, string commitSha, bool force = true, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var cleanBranch = branchName.TrimStart('/');
        if (cleanBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("refs/heads/".Length);
        else if (cleanBranch.StartsWith("heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("heads/".Length);

        var fullRefName = $"refs/heads/{cleanBranch}";
        var subRef = $"heads/{cleanBranch}";

        try
        {
            var existing = await _client.Git.Reference.Get(owner, repo, subRef);
            if (existing != null)
            {
                await _client.Git.Reference.Update(owner, repo, subRef, new ReferenceUpdate(commitSha, force));
                return;
            }
        }
        catch (NotFoundException)
        {
            // Fall through to create
        }

        await _client.Git.Reference.Create(owner, repo, new NewReference(fullRefName, commitSha));
    }

    public async Task DeleteRefAsync(
        string owner, string repo, string branchName, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var cleanBranch = branchName.TrimStart('/');
        if (cleanBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("refs/heads/".Length);
        else if (cleanBranch.StartsWith("heads/", StringComparison.OrdinalIgnoreCase))
            cleanBranch = cleanBranch.Substring("heads/".Length);

        try
        {
            await _client.Git.Reference.Delete(owner, repo, $"heads/{cleanBranch}");
        }
        catch (NotFoundException)
        {
            // Idempotent delete
        }
    }

    public async Task<string> UpdateFileContentAsync(
        string owner, string repo, string path, string message, string content, string branch, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var existing = await _client.Repository.Content.GetAllContentsByRef(owner, repo, path, branch);
            var existingSha = existing != null && existing.Count > 0 ? existing[0].Sha : null;

            if (!string.IsNullOrEmpty(existingSha))
            {
                var updateResult = await _client.Repository.Content.UpdateFile(
                    owner, repo, path, new UpdateFileRequest(message, content, existingSha, branch));
                return updateResult.Commit.Sha;
            }
        }
        catch (NotFoundException)
        {
            // File does not exist yet on branch, create it
        }

        var createResult = await _client.Repository.Content.CreateFile(
            owner, repo, path, new CreateFileRequest(message, content, branch));
        return createResult.Commit.Sha;
    }

    public async Task<PullRequest?> FindOpenPullRequestByHeadAsync(
        string owner, string repo, string headBranch, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var cleanHead = headBranch.TrimStart('/');
        if (cleanHead.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            cleanHead = cleanHead.Substring("refs/heads/".Length);
        else if (cleanHead.StartsWith("heads/", StringComparison.OrdinalIgnoreCase))
            cleanHead = cleanHead.Substring("heads/".Length);

        var request = new PullRequestRequest
        {
            State = ItemStateFilter.Open,
            Head = $"{owner}:{cleanHead}"
        };

        var prs = await _client.PullRequest.GetAllForRepository(owner, repo, request);
        return prs != null && prs.Count > 0 ? prs[0] : null;
    }

    public async Task<PullRequest> CreatePullRequestAsync(
        string owner, string repo, string title, string head, string baseBranch, string body, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var newPr = new NewPullRequest(title, head, baseBranch)
        {
            Body = body
        };

        return await _client.PullRequest.Create(owner, repo, newPr);
    }

    public async Task<PullRequest> UpdatePullRequestAsync(
        string owner, string repo, int prNumber, string title, string body, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var update = new PullRequestUpdate
        {
            Title = title,
            Body = body
        };

        return await _client.PullRequest.Update(owner, repo, prNumber, update);
    }

    public async Task<Stream> DownloadArtifactAsync(
        string owner, string repo, long artifactId, CancellationToken ct = default)
    {
        return await DownloadArtifactZipStreamAsync(owner, repo, artifactId, ct);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(
        string owner, string repo, string? branch = null, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var request = new WorkflowRunsRequest();
        if (!string.IsNullOrWhiteSpace(branch))
        {
            request.Branch = branch;
        }

        var response = await _client.Actions.Workflows.Runs.List(owner, repo, request);
        return response?.WorkflowRuns ?? (IReadOnlyList<WorkflowRun>)Array.Empty<WorkflowRun>();
    }

    public async Task<IReadOnlyList<PullRequest>> SearchMergedPullRequestsAsync(
        string owner, string repo, string author, DateTimeOffset updatedAfter, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var dateStr = updatedAfter.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        // Search merged auto-update PRs by label and optionally by author to support both GitHub App and PAT
        var authorFilter = !string.IsNullOrWhiteSpace(author) ? $"author:{author} " : "";
        var query = $"repo:{owner}/{repo} is:pr is:merged label:\"Auto-Update\" {authorFilter}updated:>{dateStr}";

        var searchResult = await _client.Search.SearchIssues(new SearchIssuesRequest(query)
        {
            PerPage = 30
        });

        if (searchResult?.Items == null || searchResult.Items.Count == 0)
            return Array.Empty<PullRequest>();

        var prList = new List<PullRequest>();
        foreach (var issue in searchResult.Items)
        {
            if (ct.IsCancellationRequested)
                break;

            var pr = await GetPullRequestAsync(owner, repo, issue.Number, ct);
            if (pr != null)
            {
                prList.Add(pr);
            }
        }

        return prList;
    }

    public async Task<(string Sha, string Message, string CommitterLogin, string CommitterEmail)?> GetBranchLatestCommitAsync(
        string owner, string repo, string branch, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var branchModel = await _client.Repository.Branch.Get(owner, repo, branch);
            if (branchModel?.Commit?.Sha == null)
                return null;

            var commit = await _client.Repository.Commit.Get(owner, repo, branchModel.Commit.Sha);
            if (commit == null)
                return null;

            var sha = commit.Sha;
            var msg = commit.Commit?.Message ?? string.Empty;
            var login = commit.Committer?.Login ?? commit.Author?.Login ?? string.Empty;
            var email = commit.Commit?.Committer?.Email ?? commit.Commit?.Author?.Email ?? string.Empty;

            return (sha, msg, login, email);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<TreeResponse?> GetTreeRecursiveAsync(
        string owner, string repo, string branchOrSha, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await _client.Git.Tree.GetRecursive(owner, repo, branchOrSha);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Artifact>> ListWorkflowArtifactsAsync(
        string owner, string repo, long runId, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var response = await _client.Actions.Artifacts.ListWorkflowArtifacts(owner, repo, runId);
            return response?.Artifacts ?? (IReadOnlyList<Artifact>)Array.Empty<Artifact>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list artifacts for workflow run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            return Array.Empty<Artifact>();
        }
    }

    public async Task EnsureLabelsExistAsync(
        string owner, string repo, IEnumerable<(string Name, string Color, string Description)> labels, CancellationToken ct = default)
    {
        using var _ = SetAmbientToken(ct);
        ct.ThrowIfCancellationRequested();
        await EnsureAuthenticatedAsync(ct);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<Label> existingLabels;
        try
        {
            existingLabels = await _client.Issue.Labels.GetAllForRepository(owner, repo);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query existing labels for {Owner}/{Repo}", owner, repo);
            return;
        }

        var existingSet = new HashSet<string>(existingLabels.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var (name, color, description) in labels)
        {
            if (existingSet.Contains(name))
                continue;

            try
            {
                var newLabel = new NewLabel(name, color.TrimStart('#'))
                {
                    Description = description
                };
                await _client.Issue.Labels.Create(owner, repo, newLabel);
                _logger?.LogInformation("Successfully created missing label '{Label}' in {Owner}/{Repo}", name, owner, repo);
                existingSet.Add(name);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create label '{Label}' in {Owner}/{Repo}", name, owner, repo);
            }
        }
    }

    public void Dispose()
    {
        _authLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
