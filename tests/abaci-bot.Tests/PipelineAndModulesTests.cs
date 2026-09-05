using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Octokit;
using WebhookIssue = Octokit.Webhooks.Models.Issue;
using WebhookIssueCommentCreatedEvent = Octokit.Webhooks.Events.IssueComment.IssueCommentCreatedEvent;
using WebhookIssueCommentEvent = Octokit.Webhooks.Events.IssueCommentEvent;
using WebhookIssueCommentAction = Octokit.Webhooks.Events.IssueComment.IssueCommentAction;
using WebhookIssuePullRequest = Octokit.Webhooks.Models.IssuePullRequest;
using WebhookPullRequest = Octokit.Webhooks.Models.PullRequestEvent.PullRequest;
using WebhookPullRequestAction = Octokit.Webhooks.Events.PullRequest.PullRequestAction;
using WebhookPullRequestAssignedEvent = Octokit.Webhooks.Events.PullRequest.PullRequestAssignedEvent;
using WebhookPullRequestEvent = Octokit.Webhooks.Events.PullRequestEvent;
using WebhookPullRequestReviewAction = Octokit.Webhooks.Events.PullRequestReview.PullRequestReviewAction;
using WebhookPullRequestReviewEvent = Octokit.Webhooks.Events.PullRequestReviewEvent;
using WebhookPullRequestReviewSubmittedEvent = Octokit.Webhooks.Events.PullRequestReview.PullRequestReviewSubmittedEvent;
using WebhookRepository = Octokit.Webhooks.Models.Repository;
using WebhookSimplePullRequest = Octokit.Webhooks.Models.SimplePullRequest;
using WebhookUser = Octokit.Webhooks.Models.User;
using Xunit;

namespace abaci_bot.Tests;

public class PipelineAndModulesTests
{
    #region TheoryData for StringEnums

    public static TheoryData<WebhookPullRequestReviewAction> NonSubmittedReviewActions => new()
    {
        WebhookPullRequestReviewAction.Dismissed,
        WebhookPullRequestReviewAction.Edited
    };

    public static TheoryData<WebhookIssueCommentAction> NonCreatedCommentActions => new()
    {
        WebhookIssueCommentAction.Edited,
        WebhookIssueCommentAction.Deleted
    };

    public static TheoryData<WebhookPullRequestAction> UnhandledPrActions => new()
    {
        WebhookPullRequestAction.Assigned,
        WebhookPullRequestAction.Unassigned,
        WebhookPullRequestAction.ReviewRequested
    };

    #endregion

    #region LabelMutexEngine Tests

    [Fact]
    public void LabelMutexEngine_EvictsOtherMembersInSameWorkflowGroup()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { GitHubLabels.WorkflowReadyForReview, "CI" };
        var toAdd = new[] { GitHubLabels.WorkflowInDev };
        var toRemove = new List<string>();

        var diff = engine.ComputeDiff(existing, toAdd, toRemove);

        Assert.Contains(GitHubLabels.WorkflowInDev, diff.LabelsToAdd);
        Assert.Contains(GitHubLabels.WorkflowReadyForReview, diff.LabelsToRemove);
        Assert.DoesNotContain("CI", diff.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_CommunityGroup_EvictsConflictingCommunityLabel()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { "Community: Contribution" };
        var toAdd = new[] { "Community: Student contribution" };
        var toRemove = new List<string>();

        var diff = engine.ComputeDiff(existing, toAdd, toRemove);

        Assert.Contains("Community: Student contribution", diff.LabelsToAdd);
        Assert.Contains("Community: Contribution", diff.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_ConflictResolution_AddTakesPrecedenceOverRemove()
    {
        var engine = new LabelMutexEngine();
        var existing = Array.Empty<string>();
        var toAdd = new[] { GitHubLabels.WorkflowReadyForReview };
        var toRemove = new[] { GitHubLabels.WorkflowReadyForReview };

        var diff = engine.ComputeDiff(existing, toAdd, toRemove);

        Assert.Contains(GitHubLabels.WorkflowReadyForReview, diff.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.WorkflowReadyForReview, diff.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_NegativePath_EmptyChangesProduceEmptyDiff()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { "Workflow: In Review", "CI" };

        var diff = engine.ComputeDiff(existing, Array.Empty<string>(), Array.Empty<string>());

        Assert.Empty(diff.LabelsToAdd);
        Assert.Empty(diff.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_NegativePath_NonMutexLabelsPreservedWithoutInterference()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { "Custom: One" };
        var toAdd = new[] { "Custom: Two" };
        var toRemove = new List<string>();

        var diff = engine.ComputeDiff(existing, toAdd, toRemove);

        Assert.Contains("Custom: Two", diff.LabelsToAdd);
        Assert.Empty(diff.LabelsToRemove);
    }

    #endregion

    #region PullRequestLockManager Tests

    [Fact]
    public async Task PullRequestLockManager_SerializesExecutionOnSamePr()
    {
        var manager = new PullRequestLockManager();
        var executionOrder = new List<int>();

        var task1 = Task.Run(async () =>
        {
            using (await manager.AcquireLockAsync("owner", "repo", 1))
            {
                executionOrder.Add(1);
                await Task.Delay(50);
                executionOrder.Add(2);
            }
        });

        // Ensure task1 starts first
        await Task.Delay(10);

        var task2 = Task.Run(async () =>
        {
            using (await manager.AcquireLockAsync("owner", "repo", 1))
            {
                executionOrder.Add(3);
            }
        });

        await Task.WhenAll(task1, task2);

        // task2 must execute only after task1 releases lock
        Assert.Equal(new[] { 1, 2, 3 }, executionOrder);
    }

    [Fact]
    public async Task PullRequestLockManager_AllowsConcurrentExecutionOnDifferentPrs()
    {
        var manager = new PullRequestLockManager();
        var started1 = new TaskCompletionSource();
        var canFinish1 = new TaskCompletionSource();
        var ran2 = false;

        var task1 = Task.Run(async () =>
        {
            using (await manager.AcquireLockAsync("owner", "repo", 1))
            {
                started1.SetResult();
                await canFinish1.Task;
            }
        });

        await started1.Task;

        var task2 = Task.Run(async () =>
        {
            using (await manager.AcquireLockAsync("owner", "repo", 2))
            {
                ran2 = true;
            }
        });

        await task2;
        Assert.True(ran2);

        canFinish1.SetResult();
        await task1;
    }

    #endregion

    #region PullRequestPipeline & Fail-Fast Tests

    [Fact]
    public async Task PullRequestPipeline_ExecutesModulesInPriorityOrder()
    {
        var executionList = new List<string>();

        var moduleA = Substitute.For<IPullRequestModule>();
        moduleA.ModuleName.Returns("ModuleA");
        moduleA.Priority.Returns(50);
        moduleA.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        moduleA.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => { executionList.Add("A"); return Task.CompletedTask; });

        var moduleB = Substitute.For<IPullRequestModule>();
        moduleB.ModuleName.Returns("ModuleB");
        moduleB.Priority.Returns(10);
        moduleB.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        moduleB.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => { executionList.Add("B"); return Task.CompletedTask; });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { moduleA, moduleB }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub);
        await pipeline.ExecuteAsync(context);

        Assert.Equal(new[] { "B", "A" }, executionList);
    }

    [Fact]
    public async Task PullRequestPipeline_FailFast_WhenModuleThrows_DoesNotFlushDirtyState()
    {
        var module1 = Substitute.For<IPullRequestModule>();
        module1.Priority.Returns(10);
        module1.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module1.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add("Dirty: Label");
                return Task.CompletedTask;
            });

        var module2 = Substitute.For<IPullRequestModule>();
        module2.Priority.Returns(20);
        module2.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module2.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulation of external failure"));

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module1, module2 }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context));

        // GitHub API must NEVER be called to write back dirty labels
        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!);
    }

    [Fact]
    public async Task PullRequestPipeline_NegativePath_NoChangesMakesZeroApiCalls()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub);
        await pipeline.ExecuteAsync(context);

        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!);
    }

    #endregion

    #region Context Caching Tests

    [Fact]
    public async Task PullRequestContext_LazyLoading_CallsApiOnlyOnce()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestFilesAsync("owner", "repo", 42)
            .Returns(new List<PullRequestFile>());

        var context = CreateMockContext(gitHub, prNumber: 42);

        // Access multiple times
        var files1 = await context.ChangedFiles.Value;
        var files2 = await context.ChangedFiles.Value;

        Assert.NotNull(files1);
        Assert.NotNull(files2);
        await gitHub.Received(1).GetPullRequestFilesAsync("owner", "repo", 42);
    }

    [Fact]
    public async Task PullRequestContext_GetFileContentAsync_CachesPerPathAndSha()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetFileContentAsync("owner", "repo", "test.spec", "sha123")
            .Returns("Content of spec");

        var context = CreateMockContext(gitHub, headSha: "sha123");

        var content1 = await context.GetFileContentAsync("test.spec");
        var content2 = await context.GetFileContentAsync("test.spec");

        Assert.Equal("Content of spec", content1);
        Assert.Equal("Content of spec", content2);
        await gitHub.Received(1).GetFileContentAsync("owner", "repo", "test.spec", "sha123");
    }

    #endregion

    #region Action Filtering & Negative Path Tests for Processor

    [Theory]
    [MemberData(nameof(NonSubmittedReviewActions))]
    public async Task Processor_NegativePath_ReviewActionNotSubmitted_IgnoredCompletely(WebhookPullRequestReviewAction action)
    {
        var gitHub = Substitute.For<IGitHubService>();
        var config = new ConfigurationManager { ["GitHubApp:TeamName"] = "captains" };
        var processor = new TestProcessor(gitHub, config);

        var reviewEvent = CreateReviewEvent(sender: "captain");
        await processor.ProcessReviewAsync(reviewEvent, action);

        // Must NOT call any GitHub API or mutate labels
        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!);
    }

    [Theory]
    [MemberData(nameof(NonCreatedCommentActions))]
    public async Task Processor_NegativePath_CommentActionNotCreated_IgnoredCompletely(WebhookIssueCommentAction action)
    {
        var gitHub = Substitute.For<IGitHubService>();
        var config = new ConfigurationManager { ["GitHubApp:TeamName"] = "captains" };
        var processor = new TestProcessor(gitHub, config);

        var commentEvent = CreateCommentEvent(sender: "captain", isPr: true);
        await processor.ProcessCommentAsync(commentEvent, action);

        // Must NOT call GetPullRequestAsync or mutate labels
        await gitHub.DidNotReceiveWithAnyArgs().GetPullRequestAsync(default!, default!, default);
        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!);
    }

    [Fact]
    public async Task Processor_NegativePath_CommentOnRegularIssue_IgnoredCompletely()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var config = new ConfigurationManager { ["GitHubApp:TeamName"] = "captains" };
        var processor = new TestProcessor(gitHub, config);

        // isPr: false -> issueCommentEvent.Issue.PullRequest is null
        var commentEvent = CreateCommentEvent(sender: "captain", isPr: false);
        await processor.ProcessCommentAsync(commentEvent, WebhookIssueCommentAction.Created);

        await gitHub.DidNotReceiveWithAnyArgs().GetPullRequestAsync(default!, default!, default);
        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!);
    }

    [Theory]
    [MemberData(nameof(UnhandledPrActions))]
    public async Task Processor_NegativePath_UnhandledPullRequestAction_IgnoredCompletely(WebhookPullRequestAction action)
    {
        var gitHub = Substitute.For<IGitHubService>();
        var config = new ConfigurationManager { ["GitHubApp:TeamName"] = "captains" };
        var processor = new TestProcessor(gitHub, config);

        var prEvent = CreatePullRequestEvent();
        await processor.ProcessPrAsync(prEvent, action);

        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!);
    }

    #endregion

    #region Isolated Module Negative Path Tests

    [Theory]
    [InlineData("staff@iscas.ac.cn")]
    [InlineData("ADMIN.ISCAS.AC.CN")]
    public async Task UserContributionModule_NegativePath_StaffEmailExcluded(string staffEmail)
    {
        var module = new UserContributionModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAuthorEmailAsync("owner", "repo", Arg.Any<int>())
            .Returns(staffEmail);

        var context = CreateMockContext(gitHub);
        await module.ProcessAsync(context);

        Assert.Empty(context.LabelsToAdd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-email-without-at")]
    public async Task UserContributionModule_NegativePath_InvalidEmailDoesNotAddLabels(string? invalidEmail)
    {
        var module = new UserContributionModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAuthorEmailAsync("owner", "repo", Arg.Any<int>())
            .Returns(invalidEmail);

        var context = CreateMockContext(gitHub);
        await module.ProcessAsync(context);

        Assert.Empty(context.LabelsToAdd);
    }

    [Fact]
    public async Task BuildSystemAnalysisModule_NegativePath_RemovedFileDoesNotFetchContent()
    {
        var module = new BuildSystemAnalysisModule();
        var gitHub = Substitute.For<IGitHubService>();

        var removedFile = (PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequestFile));
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.FileName))?.SetValue(removedFile, "pkg.spec");
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.Status))?.SetValue(removedFile, "removed");

        gitHub.GetPullRequestFilesAsync("owner", "repo", Arg.Any<int>())
            .Returns(new List<PullRequestFile> { removedFile });

        var context = CreateMockContext(gitHub);
        await module.ProcessAsync(context);

        await gitHub.DidNotReceiveWithAnyArgs().GetFileContentAsync(default!, default!, default!, default!);
        Assert.DoesNotContain(context.LabelsToAdd, l => l.StartsWith("BuildSystem:"));
    }

    [Fact]
    public async Task BuildSystemAnalysisModule_NegativePath_NonSpecFileDoesNotTagBuildSystem()
    {
        var module = new BuildSystemAnalysisModule();
        var gitHub = Substitute.For<IGitHubService>();

        var regularFile = (PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequestFile));
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.FileName))?.SetValue(regularFile, "src/main.c");
        typeof(PullRequestFile).GetProperty(nameof(PullRequestFile.Status))?.SetValue(regularFile, "modified");

        gitHub.GetPullRequestFilesAsync("owner", "repo", Arg.Any<int>())
            .Returns(new List<PullRequestFile> { regularFile });

        var context = CreateMockContext(gitHub);
        await module.ProcessAsync(context);

        Assert.DoesNotContain(context.LabelsToAdd, l => l.StartsWith("BuildSystem:"));
    }

    [Theory]
    [InlineData("- [ ] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.")]
    [InlineData("Just a normal description without checkbox")]
    [InlineData(null)]
    public async Task AiAssistanceModule_NegativePath_UncheckedOrMissing_RemovesLabel(string? body)
    {
        var module = new AiAssistanceModule();
        var gitHub = Substitute.For<IGitHubService>();
        var context = CreateMockContext(gitHub, body: body);

        await module.ProcessAsync(context);

        Assert.Contains(GitHubLabels.AiAssistance, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.AiAssistance, context.LabelsToAdd);
    }

    [Fact]
    public async Task WorkflowStateModule_NegativePath_NonCaptainReview_DoesNotTransition()
    {
        var module = new WorkflowStateModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetTeamMembersAsync("owner", "captains")
            .Returns(new HashSet<string> { "captain1" });

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.Review,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "PR",
            body: "",
            senderLogin: "random-user",
            authorLogin: "author",
            action: nameof(WebhookPullRequestReviewAction.Submitted),
            existingLabels: new[] { GitHubLabels.WorkflowReadyForReview },
            reviewAction: WebhookPullRequestReviewAction.Submitted);

        await module.ProcessAsync(context);

        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, context.LabelsToAdd);
    }

    [Fact]
    public async Task WorkflowStateModule_NegativePath_CaptainAuthorReview_DoesNotRemoveCommitsUpdated()
    {
        var module = new WorkflowStateModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetTeamMembersAsync("owner", "captains")
            .Returns(new HashSet<string> { "captain-author" });

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.Review,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "PR",
            body: "",
            senderLogin: "captain-author",
            authorLogin: "captain-author",
            action: nameof(WebhookPullRequestReviewAction.Submitted),
            existingLabels: new[] { GitHubLabels.CommitsUpdated },
            reviewAction: WebhookPullRequestReviewAction.Submitted);

        await module.ProcessAsync(context);

        // When captain is the author, their review does NOT acknowledge Commits: Updated
        Assert.DoesNotContain(GitHubLabels.CommitsUpdated, context.LabelsToRemove);
    }

    #endregion

    #region Helpers & Test Processor

    private sealed class TestProcessor : GitHubWebhookProcessor
    {
        public TestProcessor(IGitHubService github, IConfiguration config)
            : base(github, config)
        {
        }

        public ValueTask ProcessReviewAsync(WebhookPullRequestReviewEvent payload, WebhookPullRequestReviewAction action)
        {
            return ProcessPullRequestReviewWebhookAsync(null!, payload, action);
        }

        public ValueTask ProcessCommentAsync(WebhookIssueCommentEvent payload, WebhookIssueCommentAction action)
        {
            return ProcessIssueCommentWebhookAsync(null!, payload, action);
        }

        public ValueTask ProcessPrAsync(WebhookPullRequestEvent payload, WebhookPullRequestAction action)
        {
            return ProcessPullRequestWebhookAsync(null!, payload, action);
        }
    }

    private static PullRequestContext CreateMockContext(
        IGitHubService gitHub,
        int prNumber = 1,
        string headSha = "headsha",
        string? body = "")
    {
        return new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: prNumber,
            headSha: headSha,
            baseBranch: "main",
            isDraft: false,
            isMerged: false,
            title: "Test PR",
            body: body,
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: nameof(WebhookPullRequestAction.Opened),
            existingLabels: Array.Empty<string>(),
            prAction: WebhookPullRequestAction.Opened);
    }

    private static WebhookPullRequestReviewEvent CreateReviewEvent(string sender)
    {
        var repo = (WebhookRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookRepository));
        typeof(WebhookRepository).GetProperty(nameof(WebhookRepository.Name))?.SetValue(repo, "repo");
        var ownerUser = (WebhookUser)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookUser));
        typeof(WebhookUser).GetProperty(nameof(WebhookUser.Login))?.SetValue(ownerUser, "owner");
        typeof(WebhookRepository).GetProperty(nameof(WebhookRepository.Owner))?.SetValue(repo, ownerUser);

        var senderUser = (WebhookUser)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookUser));
        typeof(WebhookUser).GetProperty(nameof(WebhookUser.Login))?.SetValue(senderUser, sender);

        var pr = (WebhookSimplePullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookSimplePullRequest));
        typeof(WebhookSimplePullRequest).GetProperty(nameof(WebhookSimplePullRequest.Number))?.SetValue(pr, 100L);

        var ev = (WebhookPullRequestReviewEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequestReviewSubmittedEvent));
        typeof(WebhookPullRequestReviewEvent).GetProperty(nameof(WebhookPullRequestReviewEvent.Repository))?.SetValue(ev, repo);
        typeof(WebhookPullRequestReviewEvent).GetProperty(nameof(WebhookPullRequestReviewEvent.Sender))?.SetValue(ev, senderUser);
        typeof(WebhookPullRequestReviewEvent).GetProperty(nameof(WebhookPullRequestReviewEvent.PullRequest))?.SetValue(ev, pr);

        return ev;
    }

    private static WebhookIssueCommentEvent CreateCommentEvent(string sender, bool isPr)
    {
        var repo = (WebhookRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookRepository));
        typeof(WebhookRepository).GetProperty(nameof(WebhookRepository.Name))?.SetValue(repo, "repo");
        var ownerUser = (WebhookUser)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookUser));
        typeof(WebhookUser).GetProperty(nameof(WebhookUser.Login))?.SetValue(ownerUser, "owner");
        typeof(WebhookRepository).GetProperty(nameof(WebhookRepository.Owner))?.SetValue(repo, ownerUser);

        var senderUser = (WebhookUser)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookUser));
        typeof(WebhookUser).GetProperty(nameof(WebhookUser.Login))?.SetValue(senderUser, sender);

        var issue = (WebhookIssue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookIssue));
        typeof(WebhookIssue).GetProperty(nameof(WebhookIssue.Number))?.SetValue(issue, 100);

        if (isPr)
        {
            var issuePr = (WebhookIssuePullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookIssuePullRequest));
            typeof(WebhookIssue).GetProperty(nameof(WebhookIssue.PullRequest))?.SetValue(issue, issuePr);
        }

        var ev = (WebhookIssueCommentEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookIssueCommentCreatedEvent));
        typeof(WebhookIssueCommentEvent).GetProperty(nameof(WebhookIssueCommentEvent.Repository))?.SetValue(ev, repo);
        typeof(WebhookIssueCommentEvent).GetProperty(nameof(WebhookIssueCommentEvent.Sender))?.SetValue(ev, senderUser);
        typeof(WebhookIssueCommentEvent).GetProperty(nameof(WebhookIssueCommentEvent.Issue))?.SetValue(ev, issue);

        return ev;
    }

    private static WebhookPullRequestEvent CreatePullRequestEvent()
    {
        var repo = (WebhookRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookRepository));
        typeof(WebhookRepository).GetProperty(nameof(WebhookRepository.Name))?.SetValue(repo, "repo");
        var ownerUser = (WebhookUser)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookUser));
        typeof(WebhookUser).GetProperty(nameof(WebhookUser.Login))?.SetValue(ownerUser, "owner");
        typeof(WebhookRepository).GetProperty(nameof(WebhookRepository.Owner))?.SetValue(repo, ownerUser);

        var pr = (WebhookPullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequest));
        typeof(WebhookPullRequest).GetProperty(nameof(WebhookPullRequest.Number))?.SetValue(pr, 100);

        var ev = (WebhookPullRequestEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequestAssignedEvent));
        typeof(WebhookPullRequestEvent).GetProperty(nameof(WebhookPullRequestEvent.Repository))?.SetValue(ev, repo);
        typeof(WebhookPullRequestEvent).GetProperty(nameof(WebhookPullRequestEvent.PullRequest))?.SetValue(ev, pr);

        return ev;
    }

    #endregion
}
