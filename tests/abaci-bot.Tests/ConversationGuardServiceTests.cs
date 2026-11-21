using abaci_bot.Contexts;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;

namespace abaci_bot.Tests;

public class ConversationGuardServiceTests
{
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IRepositoryPermissionService _permissionService = Substitute.For<IRepositoryPermissionService>();
    private readonly IBotIdentityProvider _botIdentity = Substitute.For<IBotIdentityProvider>();

    public ConversationGuardServiceTests()
    {
        _botIdentity.GetBotLoginAsync(Arg.Any<CancellationToken>()).Returns("abaci-bot");
    }

    private PullRequestContext CreateContext(
        string senderLogin,
        string authorLogin = "author-user",
        RepositoryAccessLevel senderPermission = RepositoryAccessLevel.None)
    {
        _permissionService.GetUserPermissionAsync("owner", "repo", senderLogin, Arg.Any<CancellationToken>())
            .Returns(senderPermission);

        var octokitPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Number))?.SetValue(octokitPr, 42);
        typeof(PullRequest).GetProperty(nameof(PullRequest.NodeId))?.SetValue(octokitPr, "PR_kwDO42");
        _gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>()).Returns(octokitPr);

        return new PullRequestContext(
            gitHubService: _gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.IssueComment,
            owner: "owner",
            repo: "repo",
            prNumber: 42,
            headSha: "head123",
            baseBranch: "main",
            isDraft: false,
            isMerged: false,
            title: "Test PR",
            body: "PR body",
            senderLogin: senderLogin,
            authorLogin: authorLogin,
            action: "created",
            existingLabels: Array.Empty<string>());
    }

    private ConversationGuardService CreateService(ConversationGuardOptions? opts = null, TimeProvider? timeProvider = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(opts ?? new ConversationGuardOptions { Enabled = true, DryRun = false });
        return new ConversationGuardService(
            _gitHub,
            _permissionService,
            options,
            NullLogger<ConversationGuardService>.Instance,
            timeProvider,
            botIdentity: _botIdentity);
    }

    [Fact]
    public async Task HandleCommandAsync_UnknownCommand_DoesNothing()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "/unknown", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().CreateCommentAsync(default!, default!, default, default!);
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_UnauthorizedSenderOrReadOnly_Rejects()
    {
        var service = CreateService();
        var context = CreateContext("random-user", senderPermission: RepositoryAccessLevel.Read);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "random-user", CancellationToken.None);

        await _gitHub.DidNotReceive().LockIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<LockReason?>(), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("Only the pull request author")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_PermissionLookupThrows_FailsClosedAndDeniesPrivilegedCommand()
    {
        var service = CreateService();
        var context = CreateContext("random-user");
        _permissionService.GetUserPermissionAsync("owner", "repo", "random-user", Arg.Any<CancellationToken>())
            .Returns<Task<RepositoryAccessLevel>>(_ => throw new InvalidOperationException("Forbidden"));

        await service.HandleCommandAsync(context, "@abaci-bot unlock", "random-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().UnlockIssueAsync(default!, default!, default, default);
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("Only repository maintainers")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_AuthorInitialRequest_PromptsForConfirmation()
    {
        var service = CreateService();
        var context = CreateContext("author-user", senderPermission: RepositoryAccessLevel.None);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "author-user", CancellationToken.None);

        await _gitHub.DidNotReceive().LockIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<LockReason?>(), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("@abaci-bot lock confirm")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_AuthorConfirm_LocksConversation()
    {
        var service = CreateService();
        var context = CreateContext("author-user", senderPermission: RepositoryAccessLevel.None);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "author-user", CancellationToken.None);
        await service.HandleCommandAsync(context, "@abaci-bot lock confirm", "author-user", CancellationToken.None);

        await _gitHub.Received(1).LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("has been locked")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_AuthorYes_LocksConversation()
    {
        var service = CreateService();
        var context = CreateContext("author-user", senderPermission: RepositoryAccessLevel.None);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "author-user", CancellationToken.None);
        await service.HandleCommandAsync(context, "@abaci-bot lock yes", "author-user", CancellationToken.None);

        await _gitHub.Received(1).LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("has been locked")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_AuthorConfirmExpiredOrMissing_PromptsInsteadOfLocking()
    {
        var fakeTime = new TestTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, AuthorLockConfirmTimeoutMinutes = 15 }, fakeTime);
        var context = CreateContext("author-user", senderPermission: RepositoryAccessLevel.None);

        await service.HandleCommandAsync(context, "@abaci-bot lock confirm", "author-user", CancellationToken.None);
        await _gitHub.DidNotReceive().LockIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<LockReason?>(), Arg.Any<CancellationToken>());

        await service.HandleCommandAsync(context, "@abaci-bot lock", "author-user", CancellationToken.None);
        fakeTime.Advance(TimeSpan.FromMinutes(16));
        await service.HandleCommandAsync(context, "@abaci-bot lock confirm", "author-user", CancellationToken.None);

        await _gitHub.DidNotReceive().LockIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<LockReason?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RepositoryAccessLevel.Triage)]
    [InlineData(RepositoryAccessLevel.Write)]
    [InlineData(RepositoryAccessLevel.Maintain)]
    [InlineData(RepositoryAccessLevel.Admin)]
    public async Task HandleCommandAsync_Lock_TriageOrHigher_LocksImmediately(RepositoryAccessLevel level)
    {
        var service = CreateService();
        var context = CreateContext("privileged-user", senderPermission: level);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "privileged-user", CancellationToken.None);

        await _gitHub.Received(1).LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_DryRun_DoesNotLock()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceive().LockIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<LockReason?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Unlock_NonMaintainer_Rejects()
    {
        var service = CreateService();
        var context = CreateContext("random-user", senderPermission: RepositoryAccessLevel.Read);

        await service.HandleCommandAsync(context, "@abaci-bot unlock", "random-user", CancellationToken.None);

        await _gitHub.DidNotReceive().UnlockIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("maintainers (triage+) may unlock")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Unlock_DryRun_DoesNotUnlock()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot unlock", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceive().UnlockIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Unlock_WhenUnarchiveThrows_StillUnlocks()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        _gitHub.IsIssueLockedAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(true);
        _gitHub.UnarchivePullRequestAsync("owner", "repo", 42, null, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("GraphQL error"));

        await service.HandleCommandAsync(context, "@abaci-bot unlock", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).UnlockIssueAsync("owner", "repo", 42, Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("unlocked")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_NonMaintainer_Rejects()
    {
        var service = CreateService();
        var context = CreateContext("random-user", senderPermission: RepositoryAccessLevel.None);

        await service.HandleCommandAsync(context, "@abaci-bot hide @spammer", "random-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().MinimizeCommentAsync(default!, default!, default, default, default!, default);
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("maintainers (triage+) may hide")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_InvalidSyntax_ShowsUsage()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot hide", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("Usage: `@abaci-bot hide @username`")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_TargetHasTriageOrHigher_ProtectsTarget()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);
        _permissionService.GetUserPermissionAsync("owner", "repo", "other-triager", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Triage);

        await service.HandleCommandAsync(context, "@abaci-bot hide @other-triager", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().MinimizeCommentAsync(default!, default!, default, default, default!, default);
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("cannot be hidden")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_Maintainer_MinimizesTargetUserComments()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        var spammerComment = CreateIssueComment(12345L, "node_12345", "spammer");
        var innocentComment = CreateIssueComment(12346L, "node_12346", "bystander");
        _gitHub.GetIssueCommentsAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(new List<IssueComment> { spammerComment, innocentComment });

        _gitHub.MinimizeCommentAsync("owner", "repo", 12345L, "node_12345", "SPAM", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await service.HandleCommandAsync(context, "@abaci-bot hide @spammer", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).MinimizeCommentAsync("owner", "repo", 12345L, "node_12345", "SPAM", Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().MinimizeCommentAsync("owner", "repo", 12346L, Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("1 comment(s)")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_AuthorWhoAlsoHasWritePermission_LocksImmediatelyWithoutConfirmation()
    {
        var service = CreateService();
        var context = CreateContext("author-user", authorLogin: "author-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "author-user", CancellationToken.None);

        await _gitHub.Received(1).LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_WhenMinimizeReturnsFalseOrThrows_ReportsAllFailedMessage()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        var c1 = CreateIssueComment(101L, "n1", "spammer");
        var c2 = CreateIssueComment(102L, "n2", "spammer");
        var cNullUser = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        _gitHub.GetIssueCommentsAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(new List<IssueComment> { c1, c2, cNullUser });

        _gitHub.MinimizeCommentAsync("owner", "repo", 101L, "n1", "SPAM", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        _gitHub.MinimizeCommentAsync("owner", "repo", 102L, "n2", "SPAM", Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("Failed minimize"));

        await service.HandleCommandAsync(context, "@abaci-bot hide @spammer", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(s => s.Contains("Found 2 comment(s) authored by @spammer, but failed to minimize all of them")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_WhenPartialFailure_ReportsSuccessAndFailedCounts()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        var c1 = CreateIssueComment(101L, "n1", "spammer");
        var c2 = CreateIssueComment(102L, "n2", "spammer");
        _gitHub.GetIssueCommentsAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(new List<IssueComment> { c1, c2 });

        _gitHub.MinimizeCommentAsync("owner", "repo", 101L, "n1", "SPAM", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _gitHub.MinimizeCommentAsync("owner", "repo", 102L, "n2", "SPAM", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        await service.HandleCommandAsync(context, "@abaci-bot hide @spammer", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(s => s.Contains("1 of 2 comment(s)") && s.Contains("(1 failed)")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_DryRun_DoesNotMinimize()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot hide @spammer", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().MinimizeCommentAsync(default!, default!, default, default, default!, default);
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_Unauthorized_Rejects()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, ArchiveEnabled = true });
        var context = CreateContext("random-user", senderPermission: RepositoryAccessLevel.Read);

        await service.HandleCommandAsync(context, "@abaci-bot archive", "random-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().ArchivePullRequestAsync(default!, default!, default, default, default);
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("maintainers (triage+) may archive")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_Disabled_Rejects()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, ArchiveEnabled = false });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot archive", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().ArchivePullRequestAsync(default!, default!, default, default, default);
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("currently disabled")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_DryRun_DoesNotArchive()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = true, ArchiveEnabled = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot archive", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().ArchivePullRequestAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_Maintainer_WhenEnabled_PostsExitCommentAndArchives()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, ArchiveEnabled = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot archive", "maintainer-user", CancellationToken.None);

        Received.InOrder(async () =>
        {
            await _gitHub.CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("has been archived")), Arg.Any<CancellationToken>());
            await _gitHub.ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_WhenGraphQlThrows_PostsCompensationComment()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, ArchiveEnabled = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        _gitHub.ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Archive mutation error"));

        await service.HandleCommandAsync(context, "@abaci-bot archive", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("Failed to archive")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_WhenBothGraphQlAndCompensationThrow403_DoesNotPropagate()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, ArchiveEnabled = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        _gitHub.CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("has been archived")), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _gitHub.ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Already archived"));
        _gitHub.CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("Failed to archive")), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("403 Pull request is archived"));

        var ex = await Record.ExceptionAsync(() =>
            service.HandleCommandAsync(context, "@abaci-bot archive", "maintainer-user", CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_AuthorRequiresTwoPhaseConfirmation()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, ArchiveEnabled = true });
        var context = CreateContext("author-user", senderPermission: RepositoryAccessLevel.None);

        await service.HandleCommandAsync(context, "@abaci-bot archive", "author-user", CancellationToken.None);
        await _gitHub.DidNotReceiveWithAnyArgs().ArchivePullRequestAsync(default!, default!, default, default, default);

        await service.HandleCommandAsync(context, "@abaci-bot archive confirm", "author-user", CancellationToken.None);
        await _gitHub.Received(1).ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LockInactivePullRequestsAsync_DryRun_DoesNotLock()
    {
        var service = CreateService();
        var unlockedIssue = CreateIssue(10, locked: false);

        _gitHub.SearchClosedPullRequestsAsync("owner", "repo", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new List<Issue> { unlockedIssue }, 1, false));

        await service.LockInactivePullRequestsAsync("owner", "repo", 60, dryRun: true, CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().LockIssueAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task LockInactivePullRequestsAsync_WhenLockThrows_ContinuesGracefully()
    {
        var service = CreateService();
        var unlockedIssue = CreateIssue(10, locked: false);

        _gitHub.SearchClosedPullRequestsAsync("owner", "repo", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new List<Issue> { unlockedIssue }, 1, false));

        _gitHub.LockIssueAsync("owner", "repo", 10, LockReason.TooHeated, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("API failure"));

        var ex = await Record.ExceptionAsync(() =>
            service.LockInactivePullRequestsAsync("owner", "repo", 60, dryRun: false, CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task LockInactivePullRequestsAsync_LocksUnlockedCandidatesAndHandlesTruncatedResults()
    {
        var service = CreateService();
        var unlockedIssue = CreateIssue(10, locked: false);
        var lockedIssue = CreateIssue(20, locked: true);

        _gitHub.SearchClosedPullRequestsAsync("owner", "repo", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubSearchPageResult<Issue>(new List<Issue> { unlockedIssue, lockedIssue }, 700, true));

        await service.LockInactivePullRequestsAsync("owner", "repo", 60, dryRun: false, CancellationToken.None);

        await _gitHub.Received(1).LockIssueAsync("owner", "repo", 10, LockReason.TooHeated, Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceive().LockIssueAsync("owner", "repo", 20, Arg.Any<LockReason?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_DryRun_SuppressesAllCommentsAcrossAllRejectionHintAndConfirmPaths()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = true, ArchiveEnabled = false });

        // Unauthorized /lock (read-only non-author)
        var nonAuthorCtx = CreateContext("random-user", authorLogin: "author-user", senderPermission: RepositoryAccessLevel.Read);
        await service.HandleCommandAsync(nonAuthorCtx, "@abaci-bot lock", "random-user", CancellationToken.None);

        // Author two-phase /lock initial request and missing/expired confirm
        var authorCtx = CreateContext("author-user", authorLogin: "author-user", senderPermission: RepositoryAccessLevel.None);
        await service.HandleCommandAsync(authorCtx, "@abaci-bot lock confirm", "author-user", CancellationToken.None);
        await service.HandleCommandAsync(authorCtx, "@abaci-bot lock", "author-user", CancellationToken.None);

        // Unauthorized /unlock
        await service.HandleCommandAsync(nonAuthorCtx, "@abaci-bot unlock", "random-user", CancellationToken.None);

        // /hide paths: unauthorized, empty syntax, target is maintainer
        await service.HandleCommandAsync(nonAuthorCtx, "@abaci-bot hide @spammer", "random-user", CancellationToken.None);
        var maintainerCtx = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Maintain);
        await service.HandleCommandAsync(maintainerCtx, "@abaci-bot hide", "maintainer-user", CancellationToken.None);
        _permissionService.GetUserPermissionAsync("owner", "repo", "other-maintainer", Arg.Any<CancellationToken>())
            .Returns(RepositoryAccessLevel.Triage);
        await service.HandleCommandAsync(maintainerCtx, "@abaci-bot hide @other-maintainer", "maintainer-user", CancellationToken.None);

        // /archive unauthorized and disabled paths
        await service.HandleCommandAsync(nonAuthorCtx, "@abaci-bot archive", "random-user", CancellationToken.None);
        await service.HandleCommandAsync(maintainerCtx, "@abaci-bot archive", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().CreateCommentAsync(default!, default!, default, default!);
        await _gitHub.DidNotReceiveWithAnyArgs().LockIssueAsync(default!, default!, default, default);
        await _gitHub.DidNotReceiveWithAnyArgs().UnlockIssueAsync(default!, default!, default);
        await _gitHub.DidNotReceiveWithAnyArgs().MinimizeCommentAsync(default!, default!, default, default, default!);
        await _gitHub.DidNotReceiveWithAnyArgs().ArchivePullRequestAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_PostsCommentBeforeLocking()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "maintainer-user", CancellationToken.None);

        Received.InOrder(() =>
        {
            _gitHub.CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("has been locked")), Arg.Any<CancellationToken>());
            _gitHub.LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_WhenAlreadyLocked_SkipsCommentAndLock()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);
        _gitHub.IsIssueLockedAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(true);

        await service.HandleCommandAsync(context, "@abaci-bot lock", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().CreateCommentAsync(default!, default!, default, default!);
        await _gitHub.DidNotReceiveWithAnyArgs().LockIssueAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_WhenConcurrentLockReturnsForbiddenOnComment_DoesNotThrowOrLock()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);
        var forbiddenEx = new ForbiddenException(Substitute.For<IResponse>());
        _gitHub.CreateCommentAsync("owner", "repo", 42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw forbiddenEx);

        var ex = await Record.ExceptionAsync(() =>
            service.HandleCommandAsync(context, "@abaci-bot lock", "maintainer-user", CancellationToken.None));

        Assert.Null(ex);
        await _gitHub.DidNotReceiveWithAnyArgs().LockIssueAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task HandleCommandAsync_Lock_WhenLockThrows_PostsCompensationCommentWithoutThrowing()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);
        _gitHub.LockIssueAsync("owner", "repo", 42, LockReason.TooHeated, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("API error"));

        var ex = await Record.ExceptionAsync(() =>
            service.HandleCommandAsync(context, "@abaci-bot lock", "maintainer-user", CancellationToken.None));

        Assert.Null(ex);
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("has been locked")), Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync("owner", "repo", 42, Arg.Is<string>(s => s.Contains("remains unlocked")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Unlock_WhenNotLocked_DoesNotCallUnlockOrPostUnlockedNotice()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);
        _gitHub.IsIssueLockedAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(false);

        await service.HandleCommandAsync(context, "@abaci-bot unlock", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().UnlockIssueAsync(default!, default!, default);
        await _gitHub.DidNotReceiveWithAnyArgs().UnarchivePullRequestAsync(default!, default!, default, default);
        await _gitHub.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(s => s.Contains("not currently locked")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_BotLoginWithBracketSuffix_MinimizesBotComments()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);
        var botComment = CreateIssueComment(555L, "node_bot", "abaci-bot-test[bot]");
        _gitHub.GetIssueCommentsAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(new List<IssueComment> { botComment });
        _gitHub.MinimizeCommentAsync("owner", "repo", 555L, "node_bot", "SPAM", Arg.Any<CancellationToken>())
            .Returns(true);

        await service.HandleCommandAsync(context, "@abaci-bot hide @abaci-bot-test[bot]", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).MinimizeCommentAsync("owner", "repo", 555L, "node_bot", "SPAM", Arg.Any<CancellationToken>());
        await _gitHub.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(s => s.Contains("1 comment(s) authored by @abaci-bot-test[bot]")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Hide_NoMatchingComments_PostsDistinctNotFoundMessage()
    {
        var service = CreateService();
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);
        _gitHub.GetIssueCommentsAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(new List<IssueComment> { CreateIssueComment(1L, "n1", "other-user") });

        await service.HandleCommandAsync(context, "@abaci-bot hide @nonexistent-user", "maintainer-user", CancellationToken.None);

        await _gitHub.DidNotReceiveWithAnyArgs().MinimizeCommentAsync(default!, default!, default, default, default!);
        await _gitHub.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(s => s.Contains("No comments authored by @nonexistent-user were found")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCommandAsync_Archive_WhenGraphQlReturnsForbidden_PostsActionablePermissionNotice()
    {
        var service = CreateService(new ConversationGuardOptions { Enabled = true, DryRun = false, ArchiveEnabled = true });
        var context = CreateContext("maintainer-user", senderPermission: RepositoryAccessLevel.Write);

        _gitHub.ArchivePullRequestAsync("owner", "repo", 42, "PR_kwDO42", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("GraphQL server error archiving PR #42: FORBIDDEN does not have permission"));

        await service.HandleCommandAsync(context, "@abaci-bot archive", "maintainer-user", CancellationToken.None);

        await _gitHub.Received(1).CreateCommentAsync(
            "owner",
            "repo",
            42,
            Arg.Is<string>(s => s.Contains("lacks GitHub archive permission") && s.Contains("Administration: Read & write")),
            Arg.Any<CancellationToken>());
    }

    private static Issue CreateIssue(int number, bool locked)
    {
        var issue = (Issue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Issue));
        typeof(Issue).GetProperty(nameof(Issue.Number))?.SetValue(issue, number);
        typeof(Issue).GetProperty(nameof(Issue.Locked))?.SetValue(issue, locked);
        return issue;
    }

    private static IssueComment CreateIssueComment(long id, string nodeId, string login)
    {
        var comment = (IssueComment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(IssueComment));
        typeof(IssueComment).GetProperty(nameof(IssueComment.Id))?.SetValue(comment, id);
        typeof(IssueComment).GetProperty(nameof(IssueComment.NodeId))?.SetValue(comment, nodeId);
        var user = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty(nameof(User.Login))?.SetValue(user, login);
        typeof(IssueComment).GetProperty(nameof(IssueComment.User))?.SetValue(comment, user);
        return comment;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset initialUtcNow) => _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
