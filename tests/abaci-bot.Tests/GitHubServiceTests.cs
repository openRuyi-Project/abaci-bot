using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using abaci_bot.Services;
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
        long installationId = 789012)
    {
        var client = Substitute.For<IGitHubClient>();
        var connection = Substitute.For<IConnection>();
        client.Connection.Returns(connection);

        var appsClient = Substitute.For<IGitHubAppsClient>();
        client.GitHubApps.Returns(appsClient);

        var tokenResponse = CreateInstallationToken("test-installation-token", DateTimeOffset.UtcNow.AddHours(1));
        appsClient.CreateInstallationToken(Arg.Any<long>()).Returns(Task.FromResult(tokenResponse));

        var service = new GitHubService(appId, privateKeyPem, installationId, client);
        return (service, client);
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
    public async Task SetInstallationIdUpdatesIdAndResetsTokenExpiry()
    {
        var (pem, _) = GenerateRsaKeyPair();
        var (service, client) = CreateTestService(pem, appId: 9999, installationId: 1000);

        var prClient = Substitute.For<IPullRequestsClient>();
        client.PullRequest.Returns(prClient);
        prClient.Files("owner", "repo", 1).Returns(Task.FromResult<IReadOnlyList<PullRequestFile>>(Array.Empty<PullRequestFile>()));

        // First call with installation 1000
        await service.GetPullRequestFilesAsync("owner", "repo", 1);
        await client.GitHubApps.Received(1).CreateInstallationToken(1000);

        // Setting the SAME installation ID should not invalidate cache
        service.SetInstallationId(1000);
        await service.GetPullRequestFilesAsync("owner", "repo", 1);
        await client.GitHubApps.Received(1).CreateInstallationToken(1000);

        // Changing installation ID should invalidate cache and fetch with new ID
        service.SetInstallationId(2000);
        await service.GetPullRequestFilesAsync("owner", "repo", 1);
        await client.GitHubApps.Received(1).CreateInstallationToken(2000);
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

        await service.AddLabelsAsync("owner", "repo", 100, "label1", "label2");

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

        var notFoundEx = CreateNotFoundException();
        issueLabelsClient.RemoveFromIssue("owner", "repo", 100, "label1").Throws(notFoundEx);

        var exception = await Record.ExceptionAsync(() => service.RemoveLabelAsync("owner", "repo", 100, "label1"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetTeamMembersAsyncReturnsLowercasedLogins()
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

        Assert.Contains("captainalice", result);
        Assert.Contains("captainbob", result);
        Assert.Equal(2, result.Count);
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
}
