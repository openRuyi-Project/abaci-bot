using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;

namespace abaci_bot.Tests;

public class MilestoneResolverTests
{
    private static Milestone CreateMilestone(int number, string title, DateTimeOffset? dueOn = null)
    {
        var ms = (Milestone)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Milestone));
        typeof(Milestone).GetProperty(nameof(Milestone.Number))?.SetValue(ms, number);
        typeof(Milestone).GetProperty(nameof(Milestone.Title))?.SetValue(ms, title);
        typeof(Milestone).GetProperty(nameof(Milestone.DueOn))?.SetValue(ms, dueOn);
        return ms;
    }

    [Fact]
    public async Task ResolveMilestoneAsync_ExistingMilestone_ReturnsNumberFromCache()
    {
        var fakeGithub = new FakeGitHubService();
        fakeGithub.Milestones.Add(CreateMilestone(42, "2026.11"));
        fakeGithub.Milestones.Add(CreateMilestone(99, "Backlog"));

        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);
        var targetDate = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        var result = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate);
        Assert.Equal(42, result);

        // Call again to verify cache hit
        fakeGithub.Milestones.Clear();
        var cached = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate);
        Assert.Equal(42, cached);
    }

    [Fact]
    public async Task ResolveMilestoneAsync_MissingMilestone_CreationDisabled_ReturnsNull()
    {
        var fakeGithub = new FakeGitHubService();
        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);
        var targetDate = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        var result = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: false);
        Assert.Null(result);
        Assert.Empty(fakeGithub.Milestones);
    }

    [Fact]
    public async Task ResolveMilestoneAsync_MissingMilestone_CreationEnabled_CreatesAndReturnsNumber()
    {
        var fakeGithub = new FakeGitHubService();
        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);
        var targetDate = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        var result = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: true, dueDayOfMonth: 25);
        Assert.NotNull(result);
        Assert.Single(fakeGithub.Milestones);
        Assert.Equal("2026.11", fakeGithub.Milestones[0].Title);
        Assert.Equal(25, fakeGithub.Milestones[0].DueOn?.Day);
    }

    [Fact]
    public async Task ResolveMilestoneAsync_Creation422Conflict_RefreshesCacheAndSelfHeals()
    {
        var fakeGithub = new FakeGitHubService();
        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);

        // Simulate external creation after cache was filled
        fakeGithub.ConflictMilestone = CreateMilestone(88, "2026.11");
        fakeGithub.Throw422OnCreate = true;

        var targetDate = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var result = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: true, dueDayOfMonth: 25);

        Assert.Equal(88, result);
    }

    [Fact]
    public async Task ResolveMilestoneAsync_WithNullAndWhitespaceTitles_FiltersThemOut()
    {
        var fakeGithub = new FakeGitHubService();
        fakeGithub.Milestones.Add(CreateMilestone(1, null!));
        fakeGithub.Milestones.Add(CreateMilestone(2, "   "));
        fakeGithub.Milestones.Add(CreateMilestone(3, "v2.0"));
        fakeGithub.Milestones.Add(CreateMilestone(4, "2026.02"));

        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);
        var targetDate = new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero);

        var result = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate);
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task ResolveMilestoneAsync_DueDayExceedsMonthDays_ClampsToLastDayOfMonth()
    {
        var fakeGithub = new FakeGitHubService();
        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);
        var targetDate = new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero);

        var result = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: true, dueDayOfMonth: 31);
        Assert.NotNull(result);
        Assert.Single(fakeGithub.Milestones);
        Assert.Equal(28, fakeGithub.Milestones[0].DueOn?.Day);
    }

    [Fact]
    public async Task ResolveMilestoneAsync_AppliesConfiguredIanaTimeZoneWithDst()
    {
        var fakeGithub = new FakeGitHubService();
        fakeGithub.Milestones.Add(CreateMilestone(6, "2026.06"));
        fakeGithub.Milestones.Add(CreateMilestone(7, "2026.07"));
        fakeGithub.Milestones.Add(CreateMilestone(10, "2026.10"));
        fakeGithub.Milestones.Add(CreateMilestone(11, "2026.11"));
        fakeGithub.Milestones.Add(CreateMilestone(12, "2026.12"));

        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);

        // 2026-10-31 18:00 UTC is 2026-11-01 02:00 Asia/Shanghai (UTC+8)
        var boundaryUtc = new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(11, await resolver.ResolveMilestoneAsync("owner", "repo", boundaryUtc, timeZoneId: "Asia/Shanghai"));
        Assert.Equal(10, await resolver.ResolveMilestoneAsync("owner", "repo", boundaryUtc, timeZoneId: "UTC"));

        // America/New_York during summer DST (EDT = UTC-4): 2026-07-01 04:30 UTC -> 2026-07-01 00:30 EDT (2026.07)
        var nySummerUtc = new DateTimeOffset(2026, 7, 1, 4, 30, 0, TimeSpan.Zero);
        Assert.Equal(7, await resolver.ResolveMilestoneAsync("owner", "repo", nySummerUtc, timeZoneId: "America/New_York"));
        // 2026-07-01 03:30 UTC -> 2026-06-30 23:30 EDT (2026.06)
        Assert.Equal(6, await resolver.ResolveMilestoneAsync("owner", "repo", new DateTimeOffset(2026, 7, 1, 3, 30, 0, TimeSpan.Zero), timeZoneId: "America/New_York"));

        // America/New_York during winter standard time (EST = UTC-5): 2026-12-01 04:30 UTC -> 2026-11-30 23:30 EST (2026.11)
        var nyWinterUtc = new DateTimeOffset(2026, 12, 1, 4, 30, 0, TimeSpan.Zero);
        Assert.Equal(11, await resolver.ResolveMilestoneAsync("owner", "repo", nyWinterUtc, timeZoneId: "America/New_York"));
        // 2026-12-01 05:30 UTC -> 2026-12-01 00:30 EST (2026.12)
        Assert.Equal(12, await resolver.ResolveMilestoneAsync("owner", "repo", new DateTimeOffset(2026, 12, 1, 5, 30, 0, TimeSpan.Zero), timeZoneId: "America/New_York"));
    }

    [Fact]
    public async Task ResolveMilestoneAsync_BlankOrUnknownTimeZoneId_ThrowsFailFast()
    {
        var fakeGithub = new FakeGitHubService();
        var resolver = new MilestoneResolver(fakeGithub, NullLogger<MilestoneResolver>.Instance);
        var targetDate = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveMilestoneAsync("owner", "repo", targetDate, timeZoneId: "   "));

        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() =>
            resolver.ResolveMilestoneAsync("owner", "repo", targetDate, timeZoneId: "Invalid/Timezone_DoesNotExist"));
    }

    [Fact]
    public async Task ResolveMilestoneAsync_Creation422WithoutRefreshedMatch_RethrowsAndWorksWithoutLogger()
    {
        var fakeGithub = new FakeGitHubService
        {
            Throw422OnCreate = true,
            ConflictMilestone = null
        };
        var resolver = new MilestoneResolver(fakeGithub, logger: null, cacheTtl: TimeSpan.Zero);
        var targetDate = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        // Missing & creation disabled without logger
        Assert.Null(await resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: false));

        // Missing & creation enabled -> 422 without matching conflict milestone -> rethrows
        await Assert.ThrowsAsync<ApiValidationException>(() =>
            resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: true));
    }

    [Fact]
    public async Task ResolveMilestoneAsync_WithoutLogger_CreatesAndUsesCustomCacheTtl()
    {
        var fakeGithub = new FakeGitHubService();
        var resolver = new MilestoneResolver(fakeGithub, logger: null, cacheTtl: TimeSpan.FromMinutes(10));
        var targetDate = new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero);

        var createdNumber = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: true);
        Assert.NotNull(createdNumber);

        fakeGithub.Milestones.Clear();
        var cachedNumber = await resolver.ResolveMilestoneAsync("owner", "repo", targetDate, creationEnabled: false);
        Assert.Equal(createdNumber, cachedNumber);
    }

    private sealed class FakeGitHubService : IGitHubService
    {
        public List<Milestone> Milestones { get; } = new();
        public bool Throw422OnCreate { get; set; }
        public Milestone? ConflictMilestone { get; set; }

        public Task<IReadOnlyList<Milestone>> GetMilestonesAsync(string owner, string repo, ItemStateFilter state = ItemStateFilter.Open, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Milestone>>(Milestones.ToList());

        public Task<Milestone> CreateMilestoneAsync(string owner, string repo, string title, DateTimeOffset? dueOn = null, string? description = null, CancellationToken ct = default)
        {
            if (Throw422OnCreate)
            {
                if (ConflictMilestone != null)
                {
                    Milestones.Add(ConflictMilestone);
                }

                var ex = (ApiValidationException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ApiValidationException));
                throw ex;
            }

            var ms = CreateMilestone(Milestones.Count + 1, title, dueOn);
            Milestones.Add(ms);
            return Task.FromResult(ms);
        }

        public Task<Milestone> UpdateMilestoneAsync(string owner, string repo, int milestoneNumber, string title, DateTimeOffset? dueOn = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SetIssueMilestoneAsync(string owner, string repo, int issueNumber, int? milestoneNumber, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<GitHubSearchPageResult<Issue>> SearchMergedPullRequestsWithoutMilestoneAsync(string owner, string repo, DateTimeOffset mergedAfter, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task LockIssueAsync(string owner, string repo, int issueNumber, LockReason? lockReason = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UnlockIssueAsync(string owner, string repo, int issueNumber, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> IsIssueLockedAsync(string owner, string repo, int issueNumber, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> MinimizeCommentAsync(string owner, string repo, long commentId, string? commentNodeId = null, string classifier = "SPAM", CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task ArchivePullRequestAsync(string owner, string repo, int prNumber, string? pullRequestNodeId = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UnarchivePullRequestAsync(string owner, string repo, int prNumber, string? pullRequestNodeId = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<GitHubSearchPageResult<Issue>> SearchClosedPullRequestsAsync(string owner, string repo, DateTimeOffset closedBefore, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task AddLabelsAsync(string owner, string repo, int issueNumber, string[] labels, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RemoveLabelAsync(string owner, string repo, int issueNumber, string label, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(string owner, string repo, int prNumber, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<string?> GetPullRequestAuthorEmailAsync(string owner, string repo, int prNumber, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<string> GetFileContentAsync(string owner, string repo, string path, string sha, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<HashSet<string>> GetTeamMembersAsync(string owner, string teamName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PullRequest?> GetPullRequestAsync(string owner, string repo, int prNumber, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Issue?> GetIssueAsync(string owner, string repo, int number, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Issue>> SearchStaleCandidatesAsync(string owner, string repo, DateTimeOffset updatedBefore, bool isPr, string? labelFilter = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CloseIssueAsync(string owner, string repo, int number, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Issue> CreateIssueAsync(string owner, string repo, string title, string body, string[]? labels = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Issue> UpdateIssueAsync(string owner, string repo, int number, string title, string body, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CreateCommentAsync(string owner, string repo, int number, string comment, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateCommentAsync(string owner, string repo, long commentId, string comment, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<GitHubRateLimitSnapshot> GetRateLimitSnapshotAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<(long Id, string Name, long SizeInBytes)>> ListWorkflowRunArtifactsAsync(string owner, string repo, long runId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Stream> DownloadArtifactZipStreamAsync(string owner, string repo, long artifactId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<string?> GetRefShaAsync(string owner, string repo, string branchName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CreateOrUpdateRefAsync(string owner, string repo, string branchName, string commitSha, bool force = true, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DeleteRefAsync(string owner, string repo, string branchName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<string> UpdateFileContentAsync(string owner, string repo, string path, string message, string content, string branch, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PullRequest?> FindOpenPullRequestByHeadAsync(string owner, string repo, string headBranch, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PullRequest> CreatePullRequestAsync(string owner, string repo, string title, string head, string baseBranch, string body, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PullRequest> UpdatePullRequestAsync(string owner, string repo, int prNumber, string title, string body, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Stream> DownloadArtifactAsync(string owner, string repo, long artifactId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(string owner, string repo, string? branch = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PullRequest>> SearchMergedPullRequestsAsync(string owner, string repo, string author, DateTimeOffset updatedAfter, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(string Sha, string Message, string CommitterLogin, string CommitterEmail)?> GetBranchLatestCommitAsync(string owner, string repo, string branch, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(string owner, string repo, int number, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<TreeResponse?> GetTreeRecursiveAsync(string owner, string repo, string branchOrSha, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Artifact>> ListWorkflowArtifactsAsync(string owner, string repo, long runId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task EnsureLabelsExistAsync(string owner, string repo, IEnumerable<(string Name, string Color, string Description)> labels, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
