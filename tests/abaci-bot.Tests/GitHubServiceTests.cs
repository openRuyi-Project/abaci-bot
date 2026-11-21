using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Octokit;
using Xunit;

namespace abaci_bot.Tests;

public class GitHubServiceTests
{
    private static (string privateKeyPem, RSA rsa) GenerateRsaKeyPair()
    {
        var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa);
    }

    private static (GitHubService service, IGitHubClient client) CreateTestService(
        string privateKeyPem,
        int appId = 123456,
        long installationId = 789012,
        ILogger<GitHubService>? logger = null)
    {
        var client = Substitute.For<IGitHubClient>();
        var connection = Substitute.For<IConnection>();
        client.Connection.Returns(connection);

        var appsClient = Substitute.For<IGitHubAppsClient>();
        client.GitHubApps.Returns(appsClient);

        var tokenResponse = CreateInstallationToken("test-installation-token", DateTimeOffset.UtcNow.AddHours(1));
        appsClient.CreateInstallationToken(Arg.Any<long>()).Returns(Task.FromResult(tokenResponse));

        var service = new GitHubService(appId, privateKeyPem, installationId, client, logger);
        return (service, client);
    }

    private static ApiInfo CreateMockApiInfo(int remaining)
    {
        var rateLimit = (RateLimit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RateLimit));
        typeof(RateLimit).GetProperty(nameof(RateLimit.Remaining))?.SetValue(rateLimit, remaining);

        var apiInfo = (ApiInfo)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ApiInfo));
        typeof(ApiInfo).GetProperty(nameof(ApiInfo.RateLimit))?.SetValue(apiInfo, rateLimit);
        return apiInfo;
    }

    private static AccessToken CreateInstallationToken(string token, DateTimeOffset expiresAt)
    {
        // Octokit.AccessToken has internal or private setters, create via uninitialized object or reflection
        var inst = (AccessToken)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AccessToken));
        typeof(AccessToken).GetProperty(nameof(AccessToken.Token))?.SetValue(inst, token);
        typeof(AccessToken).GetProperty(nameof(AccessToken.ExpiresAt))?.SetValue(inst, expiresAt);
        return inst;
    }

    [Fact]
    public void PublicConstructorInstantiatesSuccessfully()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var service = new GitHubService(12345, pem, 67890);
        Assert.NotNull(service);
    }

    [Fact]
    public async Task EnsureAuthenticatedAsyncGeneratesValidJwtAndFetchesInstallationToken()
    {
        var (pem, rsa) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem, appId: 9999, installationId: 5555);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        prClient.Files("owner", "repo", 1).Returns(Task.FromResult<IReadOnlyList<PullRequestFile>>(Array.Empty<PullRequestFile>()));

        await service.GetPullRequestFilesAsync("owner", "repo", 1);

        await client.GitHubApps.Received(1).CreateInstallationToken(5555);
        Assert.NotNull(client.Connection.Credentials);
        Assert.Equal("test-installation-token", client.Connection.Credentials.Password);

        // Calling a second time within 5 minutes should NOT generate JWT or request token again
        await service.GetPullRequestFilesAsync("owner", "repo", 1);
        await client.GitHubApps.Received(1).CreateInstallationToken(5555);
    }

    [Fact]
    public async Task GitHubService_UsesConfiguredInstallationId_AndReusesValidToken()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem, appId: 9999, installationId: 1000);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        prClient.Files("owner", "repo", 1).Returns(Task.FromResult<IReadOnlyList<PullRequestFile>>(Array.Empty<PullRequestFile>()));

        // First call with installation 1000
        await service.GetPullRequestFilesAsync("owner", "repo", 1);
        await client.GitHubApps.Received(1).CreateInstallationToken(1000);

        // Subsequent call reuses the cached token
        await service.GetPullRequestFilesAsync("owner", "repo", 1);
        await client.GitHubApps.Received(1).CreateInstallationToken(1000);
    }

    [Fact]
    public async Task EnsureAuthenticatedAsync_ConcurrentCallers_UsesDoubleCheckedLockingToPreventDuplicateTokenCreation()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem, appId: 9999, installationId: 1000);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        prClient.Files("owner", "repo", 1).Returns(Task.FromResult<IReadOnlyList<PullRequestFile>>(Array.Empty<PullRequestFile>()));

        // Run 10 concurrent requests simultaneously when cache is cold
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => service.GetPullRequestFilesAsync("owner", "repo", 1))
            .ToArray();

        await Task.WhenAll(tasks);

        // Exactly 1 installation token should be generated thanks to double-checked lock
        await client.GitHubApps.Received(1).CreateInstallationToken(1000);
    }

    [Fact]
    public async Task GetPullRequestFilesAsyncReturnsFiles()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        var expectedFiles = new List<PullRequestFile>();
        prClient.Files("owner", "repo", 42).Returns(Task.FromResult<IReadOnlyList<PullRequestFile>>(expectedFiles));

        var result = await service.GetPullRequestFilesAsync("owner", "repo", 42);

        Assert.Same(expectedFiles, result);
        await prClient.Received(1).Files("owner", "repo", 42);
    }

    [Fact]
    public async Task GetPullRequestAuthorEmailAsyncWithCommitsReturnsLatestAuthorEmail()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);

        var commit1 = CreatePullRequestCommit("old@example.com");
        var commit2 = CreatePullRequestCommit("latest@example.com");
        IReadOnlyList<PullRequestCommit> commits = new[] { commit1, commit2 };

        prClient.Commits("owner", "repo", 10).Returns(Task.FromResult(commits));

        var email = await service.GetPullRequestAuthorEmailAsync("owner", "repo", 10);

        Assert.Equal("latest@example.com", email);
    }

    [Fact]
    public async Task GetPullRequestAuthorEmailAsyncWithEmptyCommitsReturnsNull()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);

        IReadOnlyList<PullRequestCommit> commits = Array.Empty<PullRequestCommit>();
        prClient.Commits("owner", "repo", 10).Returns(Task.FromResult(commits));

        var email = await service.GetPullRequestAuthorEmailAsync("owner", "repo", 10);

        Assert.Null(email);
    }

    [Fact]
    public async Task GetFileContentAsyncReturnsDecodedUtf8String()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var repoClient = Substitute.For<IRepositoriesClient>();
        var contentClient = Substitute.For<IRepositoryContentsClient>();
        client.Repository.Returns(repoClient);
        repoClient.Content.Returns(contentClient);

        var bytes = Encoding.UTF8.GetBytes("BuildSystem: autotools\nName: test");
        contentClient.GetRawContentByRef("owner", "repo", "SPECS/test.spec", "sha-123")
            .Returns(Task.FromResult(bytes));

        var content = await service.GetFileContentAsync("owner", "repo", "SPECS/test.spec", "sha-123");

        Assert.Equal("BuildSystem: autotools\nName: test", content);
    }

    [Fact]
    public async Task AddLabelsAsyncCallsIssuesLabelsAddToIssue()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var issueLabelsClient = Substitute.For<IIssuesLabelsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Labels.Returns(issueLabelsClient);

        await service.AddLabelsAsync("owner", "repo", 100, new[] { "label1", "label2" });

        await issueLabelsClient.Received(1).AddToIssue("owner", "repo", 100, Arg.Is<string[]>(l => l.SequenceEqual(new[] { "label1", "label2" })));
    }

    [Fact]
    public async Task RemoveLabelAsyncCallsRemoveFromIssue()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var issueLabelsClient = Substitute.For<IIssuesLabelsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Labels.Returns(issueLabelsClient);

        await service.RemoveLabelAsync("owner", "repo", 100, "label1");

        await issueLabelsClient.Received(1).RemoveFromIssue("owner", "repo", 100, "label1");
    }

    [Fact]
    public async Task RemoveLabelAsyncSwallowsNotFoundException()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var issueLabelsClient = Substitute.For<IIssuesLabelsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Labels.Returns(issueLabelsClient);

        issueLabelsClient.RemoveFromIssue("owner", "repo", 100, "label1")
            .Returns(Task.FromException<IReadOnlyList<Label>>(new NotFoundException("Label not found", System.Net.HttpStatusCode.NotFound)));

        var exception = await Record.ExceptionAsync(() =>
            service.RemoveLabelAsync("owner", "repo", 100, "label1"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetTeamMembersAsync_PreservesOriginalCasing_WithCaseInsensitiveComparer()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var orgClient = Substitute.For<IOrganizationsClient>();
        var teamsClient = Substitute.For<ITeamsClient>();
        client.Organization.Returns(orgClient);
        orgClient.Team.Returns(teamsClient);

        var team = CreateTeam(999, "captains");
        teamsClient.GetByName("owner", "captains").Returns(Task.FromResult(team));

        var user1 = CreateUser("CaptainAlice");
        var user2 = CreateUser("CAPTAINBOB");
        IReadOnlyList<User> members = new[] { user1, user2 };
        teamsClient.GetAllMembers(999).Returns(Task.FromResult(members));

        var result = await service.GetTeamMembersAsync("owner", "Captains");

        // Verifies case-insensitive lookup
        Assert.Contains("CaptainAlice", result);
        Assert.Contains("captainalice", result);
        Assert.Contains("captainbob", result);
        Assert.Contains("CAPTAINBOB", result);
        Assert.Equal(2, result.Count);

        // Verify original casing preservation
        Assert.Contains(result, m => m == "CaptainAlice");
        Assert.Contains(result, m => m == "CAPTAINBOB");

        // Verify that raw elements preserved their original casing
        var rawList = result.ToList();
        Assert.Contains("CaptainAlice", rawList);
        Assert.Contains("CAPTAINBOB", rawList);
    }

    [Fact]
    public async Task SearchStaleCandidatesAsync_QueriesIssuesWithPagination()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var issue1 = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue1, 101);

        var searchResult = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(searchResult, new List<Issue> { issue1 });
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(searchResult, 1);

        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>()).Returns(Task.FromResult(searchResult));

        var candidates = await service.SearchStaleCandidatesAsync("owner", "repo", DateTimeOffset.UtcNow, isPr: false, "-label:\"Status: Stale\"");

        Assert.Single(candidates);
        Assert.Equal(101, candidates[0].Number);
    }

    [Fact]
    public async Task CloseIssueAsync_UpdatesStateToClosed()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        client.Issue.Returns(issueClient);

        await service.CloseIssueAsync("owner", "repo", 202);

        await issueClient.Received(1).Update("owner", "repo", 202, Arg.Is<IssueUpdate>(u => u.State == ItemState.Closed));
    }

    [Fact]
    public async Task CreateCommentAsync_PostsComment()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var commentsClient = Substitute.For<IIssueCommentsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Comment.Returns(commentsClient);

        await service.CreateCommentAsync("owner", "repo", 303, "Test Comment");

        await commentsClient.Received(1).Create("owner", "repo", 303, "Test Comment");
    }

    [Fact]
    public async Task GetRateLimitSnapshotAsync_ReturnsCoreAndSearchQuotasAndResets()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var rateLimitClient = Substitute.For<IRateLimitClient>();
        client.RateLimit.Returns(rateLimitClient);

        var coreResetTime = DateTimeOffset.UtcNow.AddMinutes(50);
        var searchResetTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var coreRateLimit = new RateLimit(5000, 4800, coreResetTime.ToUnixTimeSeconds());
        var searchRateLimit = new RateLimit(30, 25, searchResetTime.ToUnixTimeSeconds());

        var resources = (ResourceRateLimit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ResourceRateLimit));
        typeof(ResourceRateLimit).GetProperty(nameof(ResourceRateLimit.Core))?.SetValue(resources, coreRateLimit);
        typeof(ResourceRateLimit).GetProperty(nameof(ResourceRateLimit.Search))?.SetValue(resources, searchRateLimit);

        var miscRateLimit = (MiscellaneousRateLimit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MiscellaneousRateLimit));
        typeof(MiscellaneousRateLimit).GetProperty(nameof(MiscellaneousRateLimit.Rate))?.SetValue(miscRateLimit, coreRateLimit);
        typeof(MiscellaneousRateLimit).GetProperty(nameof(MiscellaneousRateLimit.Resources))?.SetValue(miscRateLimit, resources);

        rateLimitClient.GetRateLimits().Returns(Task.FromResult(miscRateLimit));

        var snapshot = await service.GetRateLimitSnapshotAsync();

        Assert.Equal(4800, snapshot.CoreRemaining);
        Assert.Equal(coreResetTime.ToUnixTimeSeconds(), snapshot.CoreReset?.ToUnixTimeSeconds());
        Assert.Equal(25, snapshot.SearchRemaining);
        Assert.Equal(searchResetTime.ToUnixTimeSeconds(), snapshot.SearchReset?.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task GetRateLimitSnapshotAsync_WhenApiThrows_FallsBackToLastApiInfo()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var rateLimitClient = Substitute.For<IRateLimitClient>();
        client.RateLimit.Returns(rateLimitClient);
        rateLimitClient.GetRateLimits().Returns(Task.FromException<MiscellaneousRateLimit>(new Exception("Network error")));

        var lastApiInfo = (ApiInfo)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ApiInfo));
        typeof(ApiInfo).GetProperty(nameof(ApiInfo.RateLimit))?.SetValue(lastApiInfo, new RateLimit(5000, 4200, 123456789));
        client.GetLastApiInfo().Returns(lastApiInfo);

        var snapshot = await service.GetRateLimitSnapshotAsync();

        Assert.Equal(4200, snapshot.CoreRemaining);
        Assert.Equal(123456789, snapshot.CoreReset?.ToUnixTimeSeconds());
        Assert.Null(snapshot.SearchRemaining);
        Assert.Null(snapshot.SearchReset);
    }

    [Fact]
    public async Task GetPullRequestAsync_WhenNotFound_ReturnsNull()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);

        var notFoundEx = (NotFoundException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NotFoundException));
        prClient.Get("owner", "repo", 999).Returns(Task.FromException<PullRequest>(notFoundEx));

        var result = await service.GetPullRequestAsync("owner", "repo", 999);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureAuthenticatedAsync_WhenCalledConcurrently_SerializesTokenCreation()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        // Run 5 concurrent API calls that require authentication
        var tasks = Enumerable.Range(1, 5)
            .Select(i => service.CloseIssueAsync("owner", "repo", i))
            .ToList();

        await Task.WhenAll(tasks);

        // Even with 5 concurrent requests, installation token should only be created once
        await client.GitHubApps.Received(1).CreateInstallationToken(Arg.Any<long>());
    }

    [Fact]
    public async Task SearchStaleCandidatesAsync_WhenMultiplePages_FetchesUntilTotalReachedOrPerPage()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var page1Items = Enumerable.Range(1, 100).Select(i =>
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, i);
            return issue;
        }).ToList();

        var page2Items = Enumerable.Range(101, 50).Select(i =>
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, i);
            return issue;
        }).ToList();

        var res1 = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(res1, page1Items);
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(res1, 150);

        var res2 = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(res2, page2Items);
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(res2, 150);

        searchClient.SearchIssues(Arg.Is<SearchIssuesRequest>(r => r.Page == 1)).Returns(Task.FromResult(res1));
        searchClient.SearchIssues(Arg.Is<SearchIssuesRequest>(r => r.Page == 2)).Returns(Task.FromResult(res2));

        var candidates = await service.SearchStaleCandidatesAsync("owner", "repo", DateTimeOffset.UtcNow, isPr: false);

        Assert.Equal(150, candidates.Count);
    }

    [Fact]
    public async Task GetRateLimitSnapshotAsync_WhenApiAndLastApiInfoThrow_ReturnsNullCoreRemaining()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var rateLimitClient = Substitute.For<IRateLimitClient>();
        client.RateLimit.Returns(rateLimitClient);
        rateLimitClient.GetRateLimits().Returns(Task.FromException<MiscellaneousRateLimit>(new Exception("Auth failed 401")));
        client.GetLastApiInfo().Returns((ApiInfo?)null);

        var snapshot = await service.GetRateLimitSnapshotAsync();

        Assert.Null(snapshot.CoreRemaining);
        Assert.Null(snapshot.SearchRemaining);
    }

    [Fact]
    public async Task GetRateLimitSnapshotAsync_WhenAuthorizationException_ReturnsNullSnapshot()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var rateLimitClient = Substitute.For<IRateLimitClient>();
        client.RateLimit.Returns(rateLimitClient);
        rateLimitClient.GetRateLimits().Returns(Task.FromException<MiscellaneousRateLimit>(new AuthorizationException()));

        var snapshot = await service.GetRateLimitSnapshotAsync();

        Assert.Null(snapshot.CoreRemaining);
        Assert.Null(snapshot.SearchRemaining);
    }

    [Fact]
    public async Task GetRateLimitSnapshotAsync_WhenApiThrowsNonAuthAndLastApiInfoHasValue_ReturnsLastRemaining()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var rateLimitClient = Substitute.For<IRateLimitClient>();
        client.RateLimit.Returns(rateLimitClient);
        rateLimitClient.GetRateLimits().Returns(Task.FromException<MiscellaneousRateLimit>(new WebException("Temporary network glitch")));
        client.GetLastApiInfo().Returns(CreateMockApiInfo(42));

        var snapshot = await service.GetRateLimitSnapshotAsync();

        Assert.Equal(42, snapshot.CoreRemaining);
        Assert.Null(snapshot.SearchRemaining);
    }

    [Fact]
    public async Task GetRateLimitSnapshotAsync_WhenOperationCanceled_ThrowsOperationCanceledException()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var rateLimitClient = Substitute.For<IRateLimitClient>();
        client.RateLimit.Returns(rateLimitClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        rateLimitClient.GetRateLimits().Returns(Task.FromException<MiscellaneousRateLimit>(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRateLimitSnapshotAsync(cts.Token));
    }

    [Fact]
    public async Task SearchStaleCandidatesAsync_WhenRateLimitExceededDuringPagination_LogsDebugAndRethrows()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var logger = Substitute.For<ILogger<GitHubService>>();
        var (service, client) = CreateTestService(pem, logger: logger);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var page1Items = Enumerable.Range(1, 100).Select(i =>
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, i);
            return issue;
        }).ToList();

        var res1 = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(res1, page1Items);
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(res1, 200);

        searchClient.SearchIssues(Arg.Is<SearchIssuesRequest>(r => r.Page == 1)).Returns(Task.FromResult(res1));

        var rateLimitEx = (RateLimitExceededException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RateLimitExceededException));
        searchClient.SearchIssues(Arg.Is<SearchIssuesRequest>(r => r.Page == 2)).Returns(Task.FromException<SearchIssuesResult>(rateLimitEx));

        var ex = await Assert.ThrowsAsync<RateLimitExceededException>(() =>
            service.SearchStaleCandidatesAsync("owner", "repo", DateTimeOffset.UtcNow, isPr: false));

        Assert.Same(rateLimitEx, ex);

        // Verify diagnostic log includes page number and count of discarded items
        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("page 2") && o.ToString()!.Contains("100")),
            rateLimitEx,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task SearchStaleCandidatesAsync_WhenResultsExceedMaxPages_LogsWarningAndTruncates()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var logger = Substitute.For<ILogger<GitHubService>>();
        var (service, client) = CreateTestService(pem, logger: logger);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>()).Returns(callInfo =>
        {
            var req = callInfo.Arg<SearchIssuesRequest>();
            var pageItems = Enumerable.Range(req.Page * 100, 100).Select(i =>
            {
                var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
                typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, i);
                return issue;
            }).ToList();

            var res = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
            typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(res, pageItems);
            typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(res, 600);
            return Task.FromResult(res);
        });

        var candidates = await service.SearchStaleCandidatesAsync("owner", "repo", DateTimeOffset.UtcNow, isPr: false);

        Assert.Equal(500, candidates.Count);
        logger.ReceivedWithAnyArgs().Log(LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task GetPullRequestAsyncReturnsPullRequest()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);

        var expectedPr = new PullRequest();
        prClient.Get("owner", "repo", 77).Returns(Task.FromResult(expectedPr));

        var result = await service.GetPullRequestAsync("owner", "repo", 77);

        Assert.Same(expectedPr, result);
        await prClient.Received(1).Get("owner", "repo", 77);
    }

    [Fact]
    public async Task SearchStaleCandidatesAsync_WhenItemsEmpty_BreaksImmediately()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var emptyRes = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(emptyRes, new List<Issue>());
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(emptyRes, 0);

        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>()).Returns(Task.FromResult(emptyRes));

        var items = await service.SearchStaleCandidatesAsync("owner", "repo", DateTimeOffset.UtcNow, isPr: false);

        Assert.Empty(items);
        await searchClient.Received(1).SearchIssues(Arg.Any<SearchIssuesRequest>());
    }

    [Fact]
    public async Task SearchStaleCandidatesAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchStaleCandidatesAsync("owner", "repo", DateTimeOffset.UtcNow, isPr: false, ct: cts.Token));
    }

    private static PullRequestCommit CreatePullRequestCommit(string authorEmail)
    {
        var commit = (Commit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Commit));
        var committer = (Committer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Committer));
        typeof(Committer).GetProperty(nameof(Committer.Email))?.SetValue(committer, authorEmail);
        typeof(Commit).GetProperty(nameof(Commit.Author))?.SetValue(commit, committer);

        var prCommit = (PullRequestCommit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequestCommit));
        typeof(PullRequestCommit).GetProperty(nameof(PullRequestCommit.Commit))?.SetValue(prCommit, commit);

        return prCommit;
    }

    private static Team CreateTeam(int id, string name)
    {
        var team = (Team)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Team));
        typeof(Team).GetProperty(nameof(Team.Id))?.SetValue(team, id);
        typeof(Team).GetProperty(nameof(Team.Name))?.SetValue(team, name);
        return team;
    }

    private static User CreateUser(string login)
    {
        var user = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(user, login);
        return user;
    }

    private static NotFoundException CreateNotFoundException()
    {
        return (NotFoundException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NotFoundException));
    }

    [Fact]
    public async Task GetPullRequestAsync_EnforcesTransportLayerCancellation()
    {
        var (pem, _) = GenerateRsaKeyPair();
        using var cts = new CancellationTokenSource();
        CancellationToken receivedTokenInTransport = default;

        var handler = new ControllableHttpMessageHandler(async (req, ct) =>
        {
            // Handle installation token request first
            if (req.RequestUri?.AbsolutePath.Contains("access_tokens") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_test", expires_at = DateTimeOffset.UtcNow.AddHours(1) }),
                        Encoding.UTF8,
                        "application/json")
                };
            }

            // For the API request, record token and trigger cancellation at transport layer
            receivedTokenInTransport = ct;
            cts.Cancel();
            await Task.Delay(10000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var service = new GitHubService(12345, pem, 67890, handler);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetPullRequestAsync("owner", "repo", 1, cts.Token));
        stopwatch.Stop();

        // Must abort promptly at transport layer
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(receivedTokenInTransport.CanBeCanceled);
        Assert.True(receivedTokenInTransport.IsCancellationRequested);
    }

    [Fact]
    public async Task AddLabelsAsync_EnforcesTransportLayerCancellation()
    {
        var (pem, _) = GenerateRsaKeyPair();
        using var cts = new CancellationTokenSource();
        CancellationToken receivedTokenInTransport = default;

        var handler = new ControllableHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri?.AbsolutePath.Contains("access_tokens") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { token = "ghs_test", expires_at = DateTimeOffset.UtcNow.AddHours(1) }),
                        Encoding.UTF8,
                        "application/json")
                };
            }

            receivedTokenInTransport = ct;
            cts.Cancel();
            await Task.Delay(10000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var service = new GitHubService(12345, pem, 67890, handler);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AddLabelsAsync("owner", "repo", 1, new[] { "label-1" }, cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(receivedTokenInTransport.CanBeCanceled);
        Assert.True(receivedTokenInTransport.IsCancellationRequested);
    }

    [Fact]
    public async Task EnsureAuthenticatedAsync_AbortsAtTransportLayer_WhenTokenCancelled()
    {
        var (pem, _) = GenerateRsaKeyPair();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        var handler = new ControllableHttpMessageHandler((req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var service = new GitHubService(12345, pem, 67890, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetPullRequestAsync("owner", "repo", 1, cts.Token));
    }

    [Fact]
    public async Task GetMilestonesAsync_CallsMilestonesGetAllForRepository()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var milestonesClient = Substitute.For<IMilestonesClient>();
        client.Issue.Returns(issueClient);
        issueClient.Milestone.Returns(milestonesClient);

        milestonesClient.GetAllForRepository("owner", "repo", Arg.Any<MilestoneRequest>())
            .Returns(Task.FromResult<IReadOnlyList<Milestone>>(Array.Empty<Milestone>()));

        var result = await service.GetMilestonesAsync("owner", "repo", ItemStateFilter.All);

        Assert.NotNull(result);
        await milestonesClient.Received(1).GetAllForRepository("owner", "repo", Arg.Is<MilestoneRequest>(r => r.State == ItemStateFilter.All));
    }

    [Fact]
    public async Task CreateMilestoneAsync_CalibratesDueOnDateWhenMismatched()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var milestonesClient = Substitute.For<IMilestonesClient>();
        client.Issue.Returns(issueClient);
        issueClient.Milestone.Returns(milestonesClient);

        var targetDue = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        var createdMismatched = (Milestone)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Milestone));
        typeof(Milestone).GetProperty(nameof(Milestone.Number))?.SetValue(createdMismatched, 10);
        typeof(Milestone).GetProperty(nameof(Milestone.DueOn))?.SetValue(createdMismatched, (DateTimeOffset?)new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero));

        milestonesClient.Create("owner", "repo", Arg.Any<NewMilestone>())
            .Returns(Task.FromResult(createdMismatched));
        milestonesClient.Update("owner", "repo", 10, Arg.Any<MilestoneUpdate>())
            .Returns(Task.FromResult(createdMismatched));

        var result = await service.CreateMilestoneAsync("owner", "repo", "2026.11", targetDue, "desc");

        Assert.NotNull(result);
        await milestonesClient.Received(1).Update("owner", "repo", 10, Arg.Is<MilestoneUpdate>(u => u.DueOn == targetDue));
    }

    [Fact]
    public async Task UpdateMilestoneAsync_CallsMilestonesUpdate()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var milestonesClient = Substitute.For<IMilestonesClient>();
        client.Issue.Returns(issueClient);
        issueClient.Milestone.Returns(milestonesClient);

        var ms = (Milestone)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Milestone));
        milestonesClient.Update("owner", "repo", 5, Arg.Any<MilestoneUpdate>()).Returns(Task.FromResult(ms));

        var result = await service.UpdateMilestoneAsync("owner", "repo", 5, "new-title");
        Assert.NotNull(result);
        await milestonesClient.Received(1).Update("owner", "repo", 5, Arg.Is<MilestoneUpdate>(u => u.Title == "new-title"));
    }

    [Fact]
    public async Task SetIssueMilestoneAsync_UpdatesIssueWithMilestoneNumber()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        client.Issue.Returns(issueClient);

        var issueObj = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        issueClient.Update("owner", "repo", 42, Arg.Any<IssueUpdate>())
            .Returns(Task.FromResult(issueObj));

        await service.SetIssueMilestoneAsync("owner", "repo", 42, 5);

        await issueClient.Received(1).Update("owner", "repo", 42, Arg.Is<IssueUpdate>(u => u.Milestone == 5));
    }

    [Fact]
    public async Task LockIssueAsync_CallsLockUnlockClient()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var lockUnlock = Substitute.For<ILockUnlockClient>();
        client.Issue.Returns(issueClient);
        issueClient.LockUnlock.Returns(lockUnlock);

        await service.LockIssueAsync("owner", "repo", 42, LockReason.TooHeated);

        await lockUnlock.Received(1).Lock("owner", "repo", 42L, LockReason.TooHeated);
    }

    [Fact]
    public async Task UnlockIssueAsync_CallsLockUnlockClient()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var lockUnlock = Substitute.For<ILockUnlockClient>();
        client.Issue.Returns(issueClient);
        issueClient.LockUnlock.Returns(lockUnlock);

        await service.UnlockIssueAsync("owner", "repo", 42);

        await lockUnlock.Received(1).Unlock("owner", "repo", 42L);
    }

    [Fact]
    public async Task IsIssueLockedAsync_ReturnsLockedState()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        client.Issue.Returns(issueClient);

        var issueObj = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Locked))?.SetValue(issueObj, true);

        issueClient.Get("owner", "repo", 42).Returns(Task.FromResult(issueObj));

        var locked = await service.IsIssueLockedAsync("owner", "repo", 42);
        Assert.True(locked);
    }

    [Fact]
    public async Task MinimizeCommentAsync_ExecutesGraphQLPost()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        await service.MinimizeCommentAsync("owner", "repo", 100, "node_100", "SPAM");

        await client.Connection.Received(1).Post<object>(
            new Uri("graphql", UriKind.Relative),
            Arg.Any<object>(),
            "application/json",
            "application/json",
            (IDictionary<string, string>?)null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MinimizeCommentAsync_WhenNodeIdMissing_FetchesAndReturnsWhenUnavailable()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var issueClient = Substitute.For<IIssuesClient>();
        var commentsClient = Substitute.For<IIssueCommentsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Comment.Returns(commentsClient);
        commentsClient.GetAllForIssue("owner", "repo", Arg.Any<long>())
            .Returns(Task.FromResult<IReadOnlyList<IssueComment>>(Array.Empty<IssueComment>()));

        var result = await service.MinimizeCommentAsync("owner", "repo", 100, null, "SPAM");

        Assert.False(result);
        await client.Connection.DidNotReceiveWithAnyArgs().Post<object>(default!, default!, default!, default!, (IDictionary<string, string>?)null, default);
    }

    [Fact]
    public async Task MinimizeCommentAsync_WhenNodeIdMissingAndCommentIdExceedsInt32_FetchesUsingInt64AndMinimizes()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        const long largeCommentId = 7_059_426_126L;
        var issueComment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Id))?.SetValue(issueComment, largeCommentId);
        typeof(IssueComment).GetProperty(nameof(IssueComment.NodeId))?.SetValue(issueComment, "IC_kwDO_large");

        var issueClient = Substitute.For<IIssuesClient>();
        var commentsClient = Substitute.For<IIssueCommentsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Comment.Returns(commentsClient);
        commentsClient.Get("owner", "repo", largeCommentId)
            .Returns(Task.FromResult(issueComment));

        var apiResponse = Substitute.For<IApiResponse<object>>();
        apiResponse.Body.Returns(new Dictionary<string, object>());
        client.Connection.Post<object>(
                new Uri("graphql", UriKind.Relative),
                Arg.Any<object>(),
                "application/json",
                "application/json",
                (IDictionary<string, string>?)null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(apiResponse));

        var result = await service.MinimizeCommentAsync("owner", "repo", largeCommentId, null, "OUTDATED");

        Assert.True(result);
        await commentsClient.Received(1).Get("owner", "repo", largeCommentId);
        await client.Connection.Received(1).Post<object>(
            new Uri("graphql", UriKind.Relative),
            Arg.Is<object>(p => new Octokit.Internal.SimpleJsonSerializer().Serialize(p).Contains("IC_kwDO_large", StringComparison.Ordinal)),
            "application/json",
            "application/json",
            (IDictionary<string, string>?)null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MinimizeCommentAsync_WhenGraphQlReturnsErrors_ReturnsFalse()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var apiResponse = Substitute.For<IApiResponse<object>>();
        var errorDict = new Dictionary<string, object> { ["errors"] = new[] { "Something went wrong" } };
        apiResponse.Body.Returns(errorDict);

        client.Connection.Post<object>(
                new Uri("graphql", UriKind.Relative),
                Arg.Any<object>(),
                "application/json",
                "application/json",
                (IDictionary<string, string>?)null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(apiResponse));

        var result = await service.MinimizeCommentAsync("owner", "repo", 100, "node_100", "SPAM");
        Assert.False(result);
    }

    [Fact]
    public async Task ArchivePullRequestAsync_ExecutesGraphQLPost()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        await service.ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO123");

        await client.Connection.Received(1).Post<object>(
            new Uri("graphql", UriKind.Relative),
            Arg.Any<object>(),
            "application/json",
            "application/json",
            (IDictionary<string, string>?)null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArchivePullRequestAsync_WhenNodeIdMissing_ThrowsInvalidOperationException()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        prClient.Get("owner", "repo", 42).Returns(Task.FromResult<PullRequest?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ArchivePullRequestAsync("owner", "repo", 42, null));

        await client.Connection.DidNotReceiveWithAnyArgs().Post<object>(default!, default!, default!, default!, (IDictionary<string, string>?)null, default);
    }

    [Fact]
    public async Task ArchivePullRequestAsync_WhenGraphQlReturnsErrors_ThrowsInvalidOperationException()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var apiResponse = Substitute.For<IApiResponse<object>>();
        var errorJson = JsonDocument.Parse("{\"errors\":[{\"message\":\"Forbidden\"}]}").RootElement;
        apiResponse.Body.Returns(errorJson);

        client.Connection.Post<object>(
                new Uri("graphql", UriKind.Relative),
                Arg.Any<object>(),
                "application/json",
                "application/json",
                (IDictionary<string, string>?)null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(apiResponse));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO123"));
    }

    [Fact]
    public async Task UnarchivePullRequestAsync_ExecutesGraphQLPost()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        await service.UnarchivePullRequestAsync("owner", "repo", 42, "PR_kwDO123");

        await client.Connection.Received(1).Post<object>(
            new Uri("graphql", UriKind.Relative),
            Arg.Any<object>(),
            "application/json",
            "application/json",
            (IDictionary<string, string>?)null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnarchivePullRequestAsync_WhenNodeIdMissing_ThrowsInvalidOperationException()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        prClient.Get("owner", "repo", 42).Returns(Task.FromResult<PullRequest?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UnarchivePullRequestAsync("owner", "repo", 42, null));

        await client.Connection.DidNotReceiveWithAnyArgs().Post<object>(default!, default!, default!, default!, (IDictionary<string, string>?)null, default);
    }

    [Fact]
    public async Task SearchClosedPullRequestsAsync_ExecutesSearchWithDateThreshold()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var searchResult = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(searchResult, Array.Empty<Issue>());

        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>())
            .Returns(Task.FromResult(searchResult));

        var result = await service.SearchClosedPullRequestsAsync("owner", "repo", DateTimeOffset.UtcNow);
        Assert.NotNull(result);
        await searchClient.Received(1).SearchIssues(Arg.Any<SearchIssuesRequest>());
    }

    [Fact]
    public async Task SearchMergedPullRequestsWithoutMilestoneAsync_ExecutesSearchWithNoMilestone()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var searchResult = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(searchResult, Array.Empty<Issue>());

        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>())
            .Returns(Task.FromResult(searchResult));

        var result = await service.SearchMergedPullRequestsWithoutMilestoneAsync("owner", "repo", DateTimeOffset.UtcNow);
        Assert.NotNull(result);
        await searchClient.Received(1).SearchIssues(Arg.Any<SearchIssuesRequest>());
    }

    [Fact]
    public async Task SearchMergedPullRequestsWithoutMilestoneAsync_PaginatesAcrossMultiplePages()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var page1Items = Enumerable.Range(1, 100).Select(i =>
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, i);
            return issue;
        }).ToList();

        var page2Items = Enumerable.Range(101, 20).Select(i =>
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, i);
            return issue;
        }).ToList();

        var res1 = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(res1, page1Items);
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(res1, 120);

        var res2 = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(res2, page2Items);
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(res2, 120);

        searchClient.SearchIssues(Arg.Is<SearchIssuesRequest>(r => r.Page == 1)).Returns(Task.FromResult(res1));
        searchClient.SearchIssues(Arg.Is<SearchIssuesRequest>(r => r.Page == 2)).Returns(Task.FromResult(res2));

        var result = await service.SearchMergedPullRequestsWithoutMilestoneAsync("owner", "repo", DateTimeOffset.UtcNow);

        Assert.Equal(120, result.Items.Count);
        Assert.Equal(120, result.TotalCount);
        Assert.False(result.IsTruncated);
        await searchClient.Received(2).SearchIssues(Arg.Any<SearchIssuesRequest>());
    }

    [Fact]
    public async Task SearchClosedPullRequestsAsync_WhenExceedingMaxPages_MarksResultAsTruncated()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);

        var fullPage = Enumerable.Range(1, 100).Select(i =>
        {
            var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
            typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, i);
            return issue;
        }).ToList();

        var pageResult = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(pageResult, fullPage);
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.TotalCount))?.SetValue(pageResult, 650);

        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>()).Returns(Task.FromResult(pageResult));

        var result = await service.SearchClosedPullRequestsAsync("owner", "repo", DateTimeOffset.UtcNow);

        Assert.Equal(500, result.Items.Count);
        Assert.Equal(650, result.TotalCount);
        Assert.True(result.IsTruncated);
    }

    [Theory]
    [InlineData("admin", null, RepositoryAccessLevel.Admin)]
    [InlineData("maintain", "write", RepositoryAccessLevel.Maintain)]
    [InlineData(null, "write", RepositoryAccessLevel.Write)]
    [InlineData("triage", "read", RepositoryAccessLevel.Triage)]
    [InlineData(null, "read", RepositoryAccessLevel.Read)]
    [InlineData("none", "none", RepositoryAccessLevel.None)]
    [InlineData("custom", null, RepositoryAccessLevel.None)]
    public async Task GetUserPermissionAsync_MapsRoleAndLegacyPermissionLevels(
        string? roleName, string? permission, RepositoryAccessLevel expected)
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var reposClient = Substitute.For<IRepositoriesClient>();
        var collabClient = Substitute.For<IRepoCollaboratorsClient>();
        client.Repository.Returns(reposClient);
        reposClient.Collaborator.Returns(collabClient);

        var resp = (CollaboratorPermissionResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CollaboratorPermissionResponse));
        typeof(CollaboratorPermissionResponse).GetProperty(nameof(CollaboratorPermissionResponse.RoleName))?.SetValue(resp, roleName);
        typeof(CollaboratorPermissionResponse).GetProperty(nameof(CollaboratorPermissionResponse.Permission))?.SetValue(resp, permission);

        collabClient.ReviewPermission("owner", "repo", "alice").Returns(Task.FromResult(resp));

        var actual = await service.GetUserPermissionAsync("owner", "repo", "alice");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task GetUserPermissionAsync_NotFoundOrEmptyUsername_ReturnsNone_AndForbiddenPropagates()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        Assert.Equal(RepositoryAccessLevel.None, await service.GetUserPermissionAsync("owner", "repo", "  "));

        var reposClient = Substitute.For<IRepositoriesClient>();
        var collabClient = Substitute.For<IRepoCollaboratorsClient>();
        client.Repository.Returns(reposClient);
        reposClient.Collaborator.Returns(collabClient);

        var notFoundResp = Substitute.For<IResponse>();
        notFoundResp.StatusCode.Returns(HttpStatusCode.NotFound);
        var notFoundEx = new NotFoundException(notFoundResp);
        collabClient.ReviewPermission("owner", "repo", "ghost")
            .Returns(Task.FromException<CollaboratorPermissionResponse>(notFoundEx));

        Assert.Equal(RepositoryAccessLevel.None, await service.GetUserPermissionAsync("owner", "repo", "ghost"));

        var forbiddenResp = Substitute.For<IResponse>();
        forbiddenResp.StatusCode.Returns(HttpStatusCode.Forbidden);
        var forbiddenEx = new ForbiddenException(forbiddenResp);
        collabClient.ReviewPermission("owner", "repo", "blocked")
            .Returns(Task.FromException<CollaboratorPermissionResponse>(forbiddenEx));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.GetUserPermissionAsync("owner", "repo", "blocked"));
    }

    [Fact]
    public async Task VerifyCollaboratorPermissionProbeAsync_CallsCollaboratorGetAll()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var reposClient = Substitute.For<IRepositoriesClient>();
        var collabClient = Substitute.For<IRepoCollaboratorsClient>();
        client.Repository.Returns(reposClient);
        reposClient.Collaborator.Returns(collabClient);

        collabClient.GetAll("owner", "repo", Arg.Any<ApiOptions>())
            .Returns(Task.FromResult<IReadOnlyList<Collaborator>>(Array.Empty<Collaborator>()));

        await service.VerifyCollaboratorPermissionProbeAsync("owner", "repo");

        await collabClient.Received(1).GetAll("owner", "repo", Arg.Is<ApiOptions>(o => o.PageSize == 1 && o.PageCount == 1));
    }

    [Fact]
    public async Task MinimizeCommentAsync_WhenRawHttpResponseContainsGraphQlErrorsOrInvalidJson_HandlesCorrectly()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var httpRespErr = Substitute.For<IResponse>();
        httpRespErr.Body.Returns("{\"errors\":[{\"message\":\"bad\"}]}");
        var apiRespErr = Substitute.For<IApiResponse<object>>();
        apiRespErr.Body.Returns((object?)null);
        apiRespErr.HttpResponse.Returns(httpRespErr);

        client.Connection.Post<object>(
                new Uri("graphql", UriKind.Relative),
                Arg.Any<object>(),
                "application/json",
                "application/json",
                (IDictionary<string, string>?)null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(apiRespErr));

        Assert.False(await service.MinimizeCommentAsync("owner", "repo", 10, "node_10", "SPAM"));

        var httpRespMalformed = Substitute.For<IResponse>();
        httpRespMalformed.Body.Returns("not-json \"errors\"");
        var apiRespMalformed = Substitute.For<IApiResponse<object>>();
        apiRespMalformed.HttpResponse.Returns(httpRespMalformed);

        client.Connection.Post<object>(
                new Uri("graphql", UriKind.Relative),
                Arg.Any<object>(),
                "application/json",
                "application/json",
                (IDictionary<string, string>?)null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(apiRespMalformed));

        Assert.True(await service.MinimizeCommentAsync("owner", "repo", 10, "node_10", "SPAM"));
    }

    [Fact]
    public async Task GitAndWorkflowHelperMethods_CoverAllBranches()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        // 1. Refs: GetRefShaAsync, CreateOrUpdateRefAsync, DeleteRefAsync
        var gitClient = Substitute.For<IGitDatabaseClient>();
        var refClient = Substitute.For<IReferencesClient>();
        client.Git.Returns(gitClient);
        gitClient.Reference.Returns(refClient);

        var refObj = (Reference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Reference));
        var tagObj = (TagObject)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TagObject));
        typeof(TagObject).GetProperty(nameof(TagObject.Sha))?.SetValue(tagObj, "sha123");
        typeof(Reference).GetProperty(nameof(Reference.Object))?.SetValue(refObj, tagObj);

        refClient.Get("owner", "repo", "heads/feat").Returns(Task.FromResult(refObj));
        Assert.Equal("sha123", await service.GetRefShaAsync("owner", "repo", "feat"));
        Assert.Equal("sha123", await service.GetRefShaAsync("owner", "repo", "heads/feat"));

        var notFoundResp = Substitute.For<IResponse>();
        notFoundResp.StatusCode.Returns(HttpStatusCode.NotFound);
        var notFoundEx = new NotFoundException(notFoundResp);
        refClient.Get("owner", "repo", "heads/missing")
            .Returns(Task.FromException<Reference>(notFoundEx));
        Assert.Null(await service.GetRefShaAsync("owner", "repo", "missing"));

        await service.CreateOrUpdateRefAsync("owner", "repo", "refs/heads/feat", "sha456");
        await refClient.Received(1).Update("owner", "repo", "heads/feat", Arg.Any<ReferenceUpdate>());

        await service.CreateOrUpdateRefAsync("owner", "repo", "heads/missing", "sha456");
        await refClient.Received(1).Create("owner", "repo", Arg.Is<NewReference>(r => r.Ref == "refs/heads/missing"));

        await service.DeleteRefAsync("owner", "repo", "refs/heads/feat");
        await refClient.Received(1).Delete("owner", "repo", "heads/feat");
        refClient.Delete("owner", "repo", "heads/missing")
            .Returns(Task.FromException(notFoundEx));
        await service.DeleteRefAsync("owner", "repo", "heads/missing");

        // 2. Content: UpdateFileContentAsync (update existing + create new on 404)
        var reposClient = Substitute.For<IRepositoriesClient>();
        var contentClient = Substitute.For<IRepositoryContentsClient>();
        client.Repository.Returns(reposClient);
        reposClient.Content.Returns(contentClient);

        var existingContent = (RepositoryContent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RepositoryContent));
        typeof(RepositoryContent).GetProperty(nameof(RepositoryContent.Sha))?.SetValue(existingContent, "blobsha");
        contentClient.GetAllContentsByRef("owner", "repo", "a.spec", "feat")
            .Returns(Task.FromResult<IReadOnlyList<RepositoryContent>>(new[] { existingContent }));

        var updatedChangeSet = (RepositoryContentChangeSet)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RepositoryContentChangeSet));
        var commit1 = (Octokit.Commit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Commit));
        typeof(Octokit.Commit).GetProperty(nameof(Octokit.Commit.Sha))?.SetValue(commit1, "commit_upd");
        typeof(RepositoryContentChangeSet).GetProperty(nameof(RepositoryContentChangeSet.Commit))?.SetValue(updatedChangeSet, commit1);
        contentClient.UpdateFile("owner", "repo", "a.spec", Arg.Any<UpdateFileRequest>()).Returns(Task.FromResult(updatedChangeSet));
        Assert.Equal("commit_upd", await service.UpdateFileContentAsync("owner", "repo", "a.spec", "msg", "body", "feat"));

        contentClient.GetAllContentsByRef("owner", "repo", "new.spec", "feat")
            .Returns(Task.FromException<IReadOnlyList<RepositoryContent>>(notFoundEx));
        var createdChangeSet = (RepositoryContentChangeSet)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RepositoryContentChangeSet));
        var commit2 = (Octokit.Commit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Commit));
        typeof(Octokit.Commit).GetProperty(nameof(Octokit.Commit.Sha))?.SetValue(commit2, "commit_new");
        typeof(RepositoryContentChangeSet).GetProperty(nameof(RepositoryContentChangeSet.Commit))?.SetValue(createdChangeSet, commit2);
        contentClient.CreateFile("owner", "repo", "new.spec", Arg.Any<CreateFileRequest>()).Returns(Task.FromResult(createdChangeSet));
        Assert.Equal("commit_new", await service.UpdateFileContentAsync("owner", "repo", "new.spec", "msg", "body", "feat"));

        // 3. Branch latest commit
        var branchesClient = Substitute.For<IRepositoryBranchesClient>();
        var commitsClient = Substitute.For<IRepositoryCommitsClient>();
        reposClient.Branch.Returns(branchesClient);
        reposClient.Commit.Returns(commitsClient);

        var branchModel = (Branch)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Branch));
        var branchCommit = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(branchCommit, "headsha");
        typeof(Branch).GetProperty(nameof(Branch.Commit))?.SetValue(branchModel, branchCommit);
        branchesClient.Get("owner", "repo", "feat").Returns(Task.FromResult(branchModel));

        var ghCommit = (GitHubCommit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitHubCommit));
        typeof(GitHubCommit).GetProperty(nameof(GitHubCommit.Sha))?.SetValue(ghCommit, "headsha");
        var gitCommitObj = (Octokit.Commit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Commit));
        typeof(Octokit.Commit).GetProperty(nameof(Octokit.Commit.Message))?.SetValue(gitCommitObj, "bump");
        var committerIdentity = (Committer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Committer));
        typeof(Committer).GetProperty(nameof(Committer.Email))?.SetValue(committerIdentity, "bot@openruyi.org");
        typeof(Octokit.Commit).GetProperty(nameof(Octokit.Commit.Committer))?.SetValue(gitCommitObj, committerIdentity);
        typeof(GitHubCommit).GetProperty(nameof(GitHubCommit.Commit))?.SetValue(ghCommit, gitCommitObj);
        var ghUser = (Author)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Author));
        typeof(Author).GetProperty(nameof(Author.Login))?.SetValue(ghUser, "aqua-rium");
        typeof(GitHubCommit).GetProperty(nameof(GitHubCommit.Committer))?.SetValue(ghCommit, ghUser);
        commitsClient.Get("owner", "repo", "headsha").Returns(Task.FromResult(ghCommit));

        var latest = await service.GetBranchLatestCommitAsync("owner", "repo", "feat");
        Assert.NotNull(latest);
        Assert.Equal("headsha", latest.Value.Sha);
        Assert.Equal("aqua-rium", latest.Value.CommitterLogin);

        branchesClient.Get("owner", "repo", "gone")
            .Returns(Task.FromException<Branch>(notFoundEx));
        Assert.Null(await service.GetBranchLatestCommitAsync("owner", "repo", "gone"));

        // 4. FindOpenPullRequestByHeadAsync
        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        var prItem = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(prItem, 77);
        prClient.GetAllForRepository("owner", "repo", Arg.Any<PullRequestRequest>())
            .Returns(Task.FromResult<IReadOnlyList<PullRequest>>(new[] { prItem }));
        Assert.Equal(77, (await service.FindOpenPullRequestByHeadAsync("owner", "repo", "refs/heads/feat"))?.Number);

        // 5. Actions workflows / artifacts
        var actionsClient = Substitute.For<IActionsClient>();
        var workflowsClient = Substitute.For<IActionsWorkflowsClient>();
        var runsClient = Substitute.For<IActionsWorkflowRunsClient>();
        var artifactsClient = Substitute.For<IActionsArtifactsClient>();
        client.Actions.Returns(actionsClient);
        actionsClient.Workflows.Returns(workflowsClient);
        workflowsClient.Runs.Returns(runsClient);
        actionsClient.Artifacts.Returns(artifactsClient);

        var runsResp = (WorkflowRunsResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowRunsResponse));
        typeof(WorkflowRunsResponse).GetProperty(nameof(WorkflowRunsResponse.WorkflowRuns))?.SetValue(runsResp, Array.Empty<WorkflowRun>());
        runsClient.List("owner", "repo", Arg.Any<WorkflowRunsRequest>()).Returns(Task.FromResult(runsResp));
        Assert.Empty(await service.ListWorkflowRunsAsync("owner", "repo", "feat"));

        var artifactsResp = (ListArtifactsResponse)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ListArtifactsResponse));
        var artifact = (Artifact)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Artifact));
        typeof(Artifact).GetProperty(nameof(Artifact.Id))?.SetValue(artifact, 9L);
        typeof(Artifact).GetProperty(nameof(Artifact.Name))?.SetValue(artifact, "rpms");
        typeof(Artifact).GetProperty(nameof(Artifact.SizeInBytes))?.SetValue(artifact, 1024);
        typeof(ListArtifactsResponse).GetProperty(nameof(ListArtifactsResponse.Artifacts))?.SetValue(artifactsResp, new List<Artifact> { artifact });
        artifactsClient.ListWorkflowArtifacts("owner", "repo", 50L).Returns(Task.FromResult(artifactsResp));

        Assert.Single(await service.ListWorkflowRunArtifactsAsync("owner", "repo", 50L));
        Assert.Single(await service.ListWorkflowArtifactsAsync("owner", "repo", 50L));

        // 6. Labels & Issue creation + SearchMergedPullRequestsAsync
        var issueClient = Substitute.For<IIssuesClient>();
        var labelsClient = Substitute.For<IIssuesLabelsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Labels.Returns(labelsClient);
        var existingLabel = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(existingLabel, "Existing");
        labelsClient.GetAllForRepository("owner", "repo").Returns(Task.FromResult<IReadOnlyList<Label>>(new[] { existingLabel }));

        await service.EnsureLabelsExistAsync("owner", "repo", new[]
        {
            ("Existing", "ffffff", "already exists"),
            ("NewLabel", "#d73a4a", "created")
        });
        await labelsClient.Received(1).Create("owner", "repo", Arg.Is<NewLabel>(l => l.Name == "NewLabel"));

        var createdIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(createdIssue, 33);
        issueClient.Create("owner", "repo", Arg.Any<NewIssue>()).Returns(Task.FromResult(createdIssue));
        var issueRes = await service.CreateIssueAsync("owner", "repo", "t", "b", new[] { "L1" });
        Assert.Equal(33, issueRes.Number);

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);
        var mergedIssue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(mergedIssue, 77);
        var mergedSearchRes = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(mergedSearchRes, new List<Issue> { mergedIssue });
        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>()).Returns(Task.FromResult(mergedSearchRes));
        prClient.Get("owner", "repo", 77).Returns(Task.FromResult(prItem));
        var mergedPrs = await service.SearchMergedPullRequestsAsync("owner", "repo", "aqua-rium", DateTimeOffset.UtcNow);
        Assert.Single(mergedPrs);
    }

    [Fact]
    public async Task GitAndWorkflowErrorFallbackPaths_AreHandledCleanly()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem);

        var notFoundResp = Substitute.For<IResponse>();
        notFoundResp.StatusCode.Returns(HttpStatusCode.NotFound);
        var notFoundEx = new NotFoundException(notFoundResp);

        // Rate limit catch paths: AuthorizationException + fallback to GetLastApiInfo
        var rateClient = Substitute.For<IRateLimitClient>();
        client.RateLimit.Returns(rateClient);
        rateClient.GetRateLimits().Returns(Task.FromException<MiscellaneousRateLimit>(new AuthorizationException()));
        var authFail = await service.GetRateLimitSnapshotAsync();
        Assert.Null(authFail.CoreRemaining);

        rateClient.GetRateLimits().Returns(Task.FromException<MiscellaneousRateLimit>(new InvalidOperationException("network error")));
        client.GetLastApiInfo().Returns(CreateMockApiInfo(1234));
        var fallback = await service.GetRateLimitSnapshotAsync();
        Assert.Equal(1234, fallback.CoreRemaining);

        // Actions artifacts download & error catches
        var actionsClient = Substitute.For<IActionsClient>();
        var artifactsClient = Substitute.For<IActionsArtifactsClient>();
        client.Actions.Returns(actionsClient);
        actionsClient.Artifacts.Returns(artifactsClient);

        artifactsClient.ListWorkflowArtifacts("owner", "repo", 1L)
            .Returns(Task.FromException<ListArtifactsResponse>(notFoundEx));
        Assert.Empty(await service.ListWorkflowRunArtifactsAsync("owner", "repo", 1L));

        artifactsClient.ListWorkflowArtifacts("owner", "repo", 2L)
            .Returns(Task.FromException<ListArtifactsResponse>(new InvalidOperationException("boom")));
        Assert.Empty(await service.ListWorkflowRunArtifactsAsync("owner", "repo", 2L));
        Assert.Empty(await service.ListWorkflowArtifactsAsync("owner", "repo", 2L));

        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        artifactsClient.DownloadArtifact("owner", "repo", 10L, "zip").Returns(Task.FromResult<Stream>(stream));
        using (var downloaded = await service.DownloadArtifactAsync("owner", "repo", 10L))
        {
            Assert.Equal(3, downloaded.Length);
        }

        artifactsClient.DownloadArtifact("owner", "repo", 11L, "zip")
            .Returns(Task.FromException<Stream>(new InvalidOperationException("dl err")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadArtifactZipStreamAsync("owner", "repo", 11L));

        // FindOpenPullRequestByHeadAsync empty + "heads/" prefix
        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        prClient.GetAllForRepository("owner", "repo", Arg.Any<PullRequestRequest>())
            .Returns(Task.FromResult<IReadOnlyList<PullRequest>>(Array.Empty<PullRequest>()));
        Assert.Null(await service.FindOpenPullRequestByHeadAsync("owner", "repo", "heads/feat"));
        Assert.Null(await service.FindOpenPullRequestByHeadAsync("owner", "repo", "feat"));

        // EnsureLabelsExistAsync failure paths
        var issueClient = Substitute.For<IIssuesClient>();
        var labelsClient = Substitute.For<IIssuesLabelsClient>();
        client.Issue.Returns(issueClient);
        issueClient.Labels.Returns(labelsClient);
        labelsClient.GetAllForRepository("owner", "repo")
            .Returns(Task.FromException<IReadOnlyList<Label>>(new InvalidOperationException("list err")));
        await service.EnsureLabelsExistAsync("owner", "repo", new[] { ("L1", "ffffff", "d") });

        labelsClient.GetAllForRepository("owner", "repo")
            .Returns(Task.FromResult<IReadOnlyList<Label>>(Array.Empty<Label>()));
        labelsClient.Create("owner", "repo", Arg.Any<NewLabel>())
            .Returns(Task.FromException<Label>(new InvalidOperationException("create err")));
        await service.EnsureLabelsExistAsync("owner", "repo", new[] { ("L1", "ffffff", "d") });

        // GetBranchLatestCommitAsync: empty branchModel.Commit, author fallback when Committer is null
        var reposClient = Substitute.For<IRepositoriesClient>();
        var branchesClient = Substitute.For<IRepositoryBranchesClient>();
        var commitsClient = Substitute.For<IRepositoryCommitsClient>();
        client.Repository.Returns(reposClient);
        reposClient.Branch.Returns(branchesClient);
        reposClient.Commit.Returns(commitsClient);

        var emptyBranch = (Branch)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Branch));
        branchesClient.Get("owner", "repo", "empty").Returns(Task.FromResult(emptyBranch));
        Assert.Null(await service.GetBranchLatestCommitAsync("owner", "repo", "empty"));

        var validBranch = (Branch)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Branch));
        var bCommit = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(bCommit, "abc");
        typeof(Branch).GetProperty(nameof(Branch.Commit))?.SetValue(validBranch, bCommit);
        branchesClient.Get("owner", "repo", "author-only").Returns(Task.FromResult(validBranch));

        var authorCommit = (GitHubCommit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitHubCommit));
        typeof(GitHubCommit).GetProperty(nameof(GitHubCommit.Sha))?.SetValue(authorCommit, "abc");
        var innerCommit = (Octokit.Commit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Commit));
        var innerAuthor = (Committer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Committer));
        typeof(Committer).GetProperty(nameof(Committer.Email))?.SetValue(innerAuthor, "author@openruyi.org");
        typeof(Octokit.Commit).GetProperty(nameof(Octokit.Commit.Author))?.SetValue(innerCommit, innerAuthor);
        typeof(GitHubCommit).GetProperty(nameof(GitHubCommit.Commit))?.SetValue(authorCommit, innerCommit);
        var ghAuthor = (Author)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Author));
        typeof(Author).GetProperty(nameof(Author.Login))?.SetValue(ghAuthor, "alice");
        typeof(GitHubCommit).GetProperty(nameof(GitHubCommit.Author))?.SetValue(authorCommit, ghAuthor);
        commitsClient.Get("owner", "repo", "abc").Returns(Task.FromResult(authorCommit));

        var authorMeta = await service.GetBranchLatestCommitAsync("owner", "repo", "author-only");
        Assert.NotNull(authorMeta);
        Assert.Equal("alice", authorMeta.Value.CommitterLogin);
        Assert.Equal("author@openruyi.org", authorMeta.Value.CommitterEmail);

        // Additional simple branches: null ReviewPermission, RateLimitExceeded during paginated search, milestone matching date
        var collabClient = Substitute.For<IRepoCollaboratorsClient>();
        reposClient.Collaborator.Returns(collabClient);
        collabClient.ReviewPermission("owner", "repo", "nullresp").Returns(Task.FromResult<CollaboratorPermissionResponse>(null!));
        Assert.Equal(RepositoryAccessLevel.None, await service.GetUserPermissionAsync("owner", "repo", "nullresp"));

        var searchClient = Substitute.For<ISearchClient>();
        client.Search.Returns(searchClient);
        var rateEx = (RateLimitExceededException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RateLimitExceededException));
        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>()).Returns(Task.FromException<SearchIssuesResult>(rateEx));
        await Assert.ThrowsAsync<RateLimitExceededException>(() => service.SearchClosedPullRequestsAsync("owner", "repo", DateTimeOffset.UtcNow));

        var milestonesClient = Substitute.For<IMilestonesClient>();
        issueClient.Milestone.Returns(milestonesClient);
        var exactDue = new DateTimeOffset(2026, 12, 25, 23, 59, 59, TimeSpan.Zero);
        var createdExact = (Milestone)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Milestone));
        typeof(Milestone).GetProperty(nameof(Milestone.Number))?.SetValue(createdExact, 99);
        typeof(Milestone).GetProperty(nameof(Milestone.DueOn))?.SetValue(createdExact, (DateTimeOffset?)exactDue);
        milestonesClient.Create("owner", "repo", Arg.Any<NewMilestone>()).Returns(Task.FromResult(createdExact));
        await service.CreateMilestoneAsync("owner", "repo", "2026.12", exactDue, null);
        await milestonesClient.DidNotReceive().Update("owner", "repo", 99, Arg.Any<MilestoneUpdate>());

        // Empty search page break & Unarchive GraphQL error & CreateFile when existing content list is empty
        var emptySearchRes = (SearchIssuesResult)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SearchIssuesResult));
        typeof(SearchIssuesResult).GetProperty(nameof(SearchIssuesResult.Items))?.SetValue(emptySearchRes, Array.Empty<Issue>());
        searchClient.SearchIssues(Arg.Any<SearchIssuesRequest>()).Returns(Task.FromResult(emptySearchRes));
        var emptyClosed = await service.SearchClosedPullRequestsAsync("owner", "repo", DateTimeOffset.UtcNow);
        Assert.Empty(emptyClosed.Items);

        var apiRespErr = Substitute.For<IApiResponse<object>>();
        apiRespErr.Body.Returns(new Dictionary<string, object> { ["errors"] = new[] { "failed" } });
        client.Connection.Post<object>(
                new Uri("graphql", UriKind.Relative),
                Arg.Any<object>(),
                "application/json",
                "application/json",
                (IDictionary<string, string>?)null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(apiRespErr));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnarchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42"));

        var contentClient = Substitute.For<IRepositoryContentsClient>();
        reposClient.Content.Returns(contentClient);
        contentClient.GetAllContentsByRef("owner", "repo", "empty.spec", "main")
            .Returns(Task.FromResult<IReadOnlyList<RepositoryContent>>(Array.Empty<RepositoryContent>()));
        var createdSet = (RepositoryContentChangeSet)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RepositoryContentChangeSet));
        var createdCommit = (Octokit.Commit)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Commit));
        typeof(Octokit.Commit).GetProperty(nameof(Octokit.Commit.Sha))?.SetValue(createdCommit, "created_sha");
        typeof(RepositoryContentChangeSet).GetProperty(nameof(RepositoryContentChangeSet.Commit))?.SetValue(createdSet, createdCommit);
        contentClient.CreateFile("owner", "repo", "empty.spec", Arg.Any<CreateFileRequest>()).Returns(Task.FromResult(createdSet));
        Assert.Equal("created_sha", await service.UpdateFileContentAsync("owner", "repo", "empty.spec", "msg", "body", "main"));
    }

    [Fact]
    public void GitHubService_Dispose_CanBeCalledSafely()
    {
        var (pem, rsa) = GenerateRsaKeyPair();
        using (rsa)
        {
            var (service, _) = CreateTestService(pem);
            service.Dispose();
        }
    }

    [Fact]
    public async Task GraphQlMutations_PreserveCamelCaseVariableNamesUnderOctokitSerializer()
    {
        var (pem, rsa) = GenerateRsaKeyPair();
        using (rsa)
        {
            var (service, client) = CreateTestService(pem);
            var capturedPayloads = new List<object>();
            var okResponse = Substitute.For<IApiResponse<object>>();

            client.Connection.Post<object>(
                    new Uri("graphql", UriKind.Relative),
                    Arg.Do<object>(p => capturedPayloads.Add(p)),
                    "application/json",
                    "application/json",
                    (IDictionary<string, string>?)null,
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(okResponse));

            await service.MinimizeCommentAsync("owner", "repo", 10, "IC_kwDO10", "SPAM");
            await service.ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42");
            await service.UnarchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42");

            Assert.Equal(3, capturedPayloads.Count);
            var serializer = new Octokit.Internal.SimpleJsonSerializer();

            var minimizeJson = serializer.Serialize(capturedPayloads[0]);
            Assert.Contains("\"subjectId\":\"IC_kwDO10\"", minimizeJson);
            Assert.Contains("\"classifier\":\"SPAM\"", minimizeJson);
            Assert.DoesNotContain("subject_id", minimizeJson);

            var archiveJson = serializer.Serialize(capturedPayloads[1]);
            Assert.Contains("\"pullRequestId\":\"PR_kwDO42\"", archiveJson);
            Assert.DoesNotContain("pull_request_id", archiveJson);

            var unarchiveJson = serializer.Serialize(capturedPayloads[2]);
            Assert.Contains("\"pullRequestId\":\"PR_kwDO42\"", unarchiveJson);
            Assert.DoesNotContain("pull_request_id", unarchiveJson);
        }
    }

    [Fact]
    public async Task VerifyArchivePermissionProbeAsync_ValidatesAppAdministrationWriteAndBotPatTriage()
    {
        var (pem, rsa) = GenerateRsaKeyPair();
        using (rsa)
        {
            // 1. GitHub App mode: Administration == Write succeeds
            var (appService, appClient) = CreateTestService(pem);
            var appsClient = Substitute.For<IGitHubAppsClient>();
            appClient.GitHubApps.Returns(appsClient);

            var writePerms = (InstallationPermissions)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InstallationPermissions));
            typeof(InstallationPermissions).GetProperty(nameof(InstallationPermissions.Administration))?.SetValue(
                writePerms, (StringEnum<InstallationReadWritePermissionLevel>?)new StringEnum<InstallationReadWritePermissionLevel>(InstallationReadWritePermissionLevel.Write));
            var writeInst = (Installation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Installation));
            typeof(Installation).GetProperty(nameof(Installation.Permissions))?.SetValue(writeInst, writePerms);
            appsClient.GetInstallationForCurrent(789012).Returns(Task.FromResult(writeInst));

            await appService.VerifyArchivePermissionProbeAsync("owner", "repo");

            // 2. GitHub App mode: Administration == Read throws InvalidOperationException
            var readPerms = (InstallationPermissions)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InstallationPermissions));
            typeof(InstallationPermissions).GetProperty(nameof(InstallationPermissions.Administration))?.SetValue(
                readPerms, (StringEnum<InstallationReadWritePermissionLevel>?)new StringEnum<InstallationReadWritePermissionLevel>(InstallationReadWritePermissionLevel.Read));
            var readInst = (Installation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Installation));
            typeof(Installation).GetProperty(nameof(Installation.Permissions))?.SetValue(readInst, readPerms);
            appsClient.GetInstallationForCurrent(789012).Returns(Task.FromResult(readInst));

            var exApp = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                appService.VerifyArchivePermissionProbeAsync("owner", "repo"));
            Assert.Contains("Administration: Read & write", exApp.Message);

            // 3. BotPat mode: Triage+ succeeds; Read-only fails
            var patClient = Substitute.For<IGitHubClient>();
            patClient.Connection.Returns(Substitute.For<IConnection>());
            var reposClient = Substitute.For<IRepositoriesClient>();
            patClient.Repository.Returns(reposClient);
            var patService = new GitHubService(123456, pem, 789012, "ghp_testpat", patClient);

            var triageRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
            typeof(Repository).GetProperty(nameof(Repository.Permissions))?.SetValue(
                triageRepo, new RepositoryPermissions(admin: false, maintain: false, push: false, triage: true, pull: true));
            reposClient.Get("owner", "repo").Returns(Task.FromResult(triageRepo));

            await patService.VerifyArchivePermissionProbeAsync("owner", "repo");

            var readOnlyRepo = (Repository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Repository));
            typeof(Repository).GetProperty(nameof(Repository.Permissions))?.SetValue(
                readOnlyRepo, new RepositoryPermissions(admin: false, maintain: false, push: false, triage: false, pull: true));
            reposClient.Get("owner", "repo").Returns(Task.FromResult(readOnlyRepo));

            var exPat = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                patService.VerifyArchivePermissionProbeAsync("owner", "repo"));
            Assert.Contains("BotPat lacks triage+ permission", exPat.Message);
        }
    }

    private sealed class ControllableHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public ControllableHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
