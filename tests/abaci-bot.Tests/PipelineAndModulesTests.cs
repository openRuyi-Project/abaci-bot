using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Pipelines;
using abaci_bot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Octokit;
using WebhookIssue = Octokit.Webhooks.Models.Issue;
using WebhookIssueCommentCreatedEvent = Octokit.Webhooks.Events.IssueComment.IssueCommentCreatedEvent;
using WebhookIssueCommentEvent = Octokit.Webhooks.Events.IssueCommentEvent;
using WebhookIssueCommentAction = Octokit.Webhooks.Events.IssueComment.IssueCommentAction;
using WebhookIssuePullRequest = Octokit.Webhooks.Models.IssuePullRequest;
using WebhookLabel = Octokit.Webhooks.Models.Label;
using WebhookPullRequest = Octokit.Webhooks.Models.PullRequestEvent.PullRequest;
using WebhookPullRequestAction = Octokit.Webhooks.Events.PullRequest.PullRequestAction;
using WebhookPullRequestAssignedEvent = Octokit.Webhooks.Events.PullRequest.PullRequestAssignedEvent;
using WebhookPullRequestEvent = Octokit.Webhooks.Events.PullRequestEvent;
using WebhookPullRequestReviewAction = Octokit.Webhooks.Events.PullRequestReview.PullRequestReviewAction;
using WebhookPullRequestReviewEvent = Octokit.Webhooks.Events.PullRequestReviewEvent;
using WebhookPullRequestReviewSubmittedEvent = Octokit.Webhooks.Events.PullRequestReview.PullRequestReviewSubmittedEvent;
using WebhookPullRequestUnlabeledEvent = Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent;
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
    public void LabelMutexEngine_MultipleCandidatesInSameGroup_DeterministicWinnerWithoutAnnihilation()
    {
        var engine = new LabelMutexEngine();
        var existingEmpty = Array.Empty<string>();
        var toAdd = new[] { "Target: Rolling", "Target: LTS" }; // e.g. two modules declared targets
        var toRemove = new List<string>();

        var diff = engine.ComputeDiff(existingEmpty, toAdd, toRemove);

        // The last candidate ("Target: LTS") must win, not annihilate both
        Assert.Single(diff.LabelsToAdd);
        Assert.Contains("Target: LTS", diff.LabelsToAdd);
        Assert.Empty(diff.LabelsToRemove);

        // When existing has LTS and candidates declare [LTS, Rolling], Rolling wins and LTS is evicted:
        var existingLts = new[] { "Target: LTS" };
        var diffReverse = engine.ComputeDiff(existingLts, new[] { "Target: LTS", "Target: Rolling" }, toRemove);
        Assert.Single(diffReverse.LabelsToAdd);
        Assert.Contains("Target: Rolling", diffReverse.LabelsToAdd);
        Assert.Contains("Target: LTS", diffReverse.LabelsToRemove);
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

    [Fact]
    public void LabelMutexEngine_CompetingLabelsInSameGroup_DeterministicLastWins()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { GitHubLabels.WorkflowBlocked };
        var toAddForward = new[] { GitHubLabels.WorkflowInDev, GitHubLabels.WorkflowInReview, GitHubLabels.WorkflowReadyForReview };
        var diffForward = engine.ComputeDiff(existing, toAddForward, Array.Empty<string>());

        Assert.Equal(new[] { GitHubLabels.WorkflowReadyForReview }, diffForward.LabelsToAdd);
        Assert.Contains(GitHubLabels.WorkflowBlocked, diffForward.LabelsToRemove);

        // Reversed order should pick the reversed winner (last scheduled addition)
        var toAddReverse = new[] { GitHubLabels.WorkflowReadyForReview, GitHubLabels.WorkflowInDev };
        var diffReverse = engine.ComputeDiff(existing, toAddReverse, Array.Empty<string>());

        Assert.Equal(new[] { GitHubLabels.WorkflowInDev }, diffReverse.LabelsToAdd);
        Assert.Contains(GitHubLabels.WorkflowBlocked, diffReverse.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_RedundantAdditions_FilteredOut()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { GitHubLabels.WorkflowInReview, "CI" };
        var toAdd = new[] { "CI" };
        var toRemove = new List<string>();

        var diff = engine.ComputeDiff(existing, toAdd, toRemove);

        Assert.Empty(diff.LabelsToAdd);
        Assert.Empty(diff.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_RemovalsForMissingLabels_FilteredOut()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { GitHubLabels.WorkflowInReview };
        var toAdd = Array.Empty<string>();
        var toRemove = new[] { GitHubLabels.WorkflowInDev, GitHubLabels.WorkflowReadyForReview };

        var diff = engine.ComputeDiff(existing, toAdd, toRemove);

        // Neither InDev nor ReadyForReview exist on the PR, so redundant removals are filtered out
        Assert.Empty(diff.LabelsToRemove);
        Assert.Empty(diff.LabelsToAdd);
    }

    [Fact]
    public void LabelMutexEngine_MixedCaseCandidatesAndExisting_DeterministicResolutionAndFiltering()
    {
        var engine = new LabelMutexEngine();
        // Existing has mixed-case label from group 1 ("workflow: in dev")
        var existing = new[] { "workflow: in dev", "ci" };
        // Competing additions with various casings: lowercase then uppercase
        var toAdd = new[] { "workflow: ready for review", "WORKFLOW: IN REVIEW" };
        // Redundant removal with mixed case for a label that does NOT exist
        var toRemove = new[] { "WORKFLOW: BLOCKED" };

        var diff = engine.ComputeDiff(existing, toAdd, toRemove);

        // Deterministic last-wins: "WORKFLOW: IN REVIEW" wins over "workflow: ready for review"
        Assert.Single(diff.LabelsToAdd);
        Assert.Contains("WORKFLOW: IN REVIEW", diff.LabelsToAdd);

        // Existing conflicting member "workflow: in dev" must be evicted
        Assert.Single(diff.LabelsToRemove);
        Assert.Contains(GitHubLabels.WorkflowInDev, diff.LabelsToRemove);

        // Redundant removal "WORKFLOW: BLOCKED" was filtered out because it did not exist
        Assert.DoesNotContain("WORKFLOW: BLOCKED", diff.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_RedundantAdditionWithDifferentCasing_FilteredOut()
    {
        var engine = new LabelMutexEngine();
        var existing = new[] { "Workflow: In Review" };
        var toAdd = new[] { "workflow: in review" };

        var diff = engine.ComputeDiff(existing, toAdd, Array.Empty<string>());

        Assert.Empty(diff.LabelsToAdd);
        Assert.Empty(diff.LabelsToRemove);
    }

    [Fact]
    public void LabelMutexEngine_SecondRunAfterApplyingDiff_ProducesEmptyDiff()
    {
        var engine = new LabelMutexEngine();
        var initialExisting = new[] { GitHubLabels.WorkflowReadyForReview };
        var desiredAdd = new[] { GitHubLabels.WorkflowInReview };
        var desiredRemove = new[] { GitHubLabels.WorkflowReadyForReview };

        // First run: schedules addition of InReview and removal of ReadyForReview
        var diff1 = engine.ComputeDiff(initialExisting, desiredAdd, desiredRemove);
        Assert.Equal(new[] { GitHubLabels.WorkflowInReview }, diff1.LabelsToAdd);
        Assert.Equal(new[] { GitHubLabels.WorkflowReadyForReview }, diff1.LabelsToRemove);

        // Apply diff to simulate remote state
        var updatedExisting = initialExisting
            .Except(diff1.LabelsToRemove, StringComparer.OrdinalIgnoreCase)
            .Concat(diff1.LabelsToAdd)
            .ToList();

        // Second run with the same module desires: must produce empty diff
        var diff2 = engine.ComputeDiff(updatedExisting, desiredAdd, desiredRemove);

        Assert.Empty(diff2.LabelsToAdd);
        Assert.Empty(diff2.LabelsToRemove);
    }
    #endregion

    #region PullRequestLockManager Tests

    [Fact]
    public async Task PullRequestLockManager_SerializesExecutionOnSamePr()
    {
        var manager = new PullRequestLockManager();
        var executionOrder = new List<int>();
        var task1Acquired = new TaskCompletionSource();

        var task1 = Task.Run(async () =>
        {
            using (await manager.AcquireLockAsync("owner", "repo", 1))
            {
                task1Acquired.SetResult();
                executionOrder.Add(1);
                await Task.Delay(50);
                executionOrder.Add(2);
            }
        });

        // Ensure task1 has acquired the lock before launching task2
        await task1Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

    [Fact]
    public async Task PullRequestLockManager_EvictsSemaphoreWhenUnused_PreventingMemoryLeak()
    {
        var manager = new PullRequestLockManager();
        Assert.Equal(0, manager.ActiveLockCount);

        // Acquire and release
        using (await manager.AcquireLockAsync("owner", "repo", 100))
        {
            Assert.Equal(1, manager.ActiveLockCount);
        }

        // Must be evicted immediately upon release
        Assert.Equal(0, manager.ActiveLockCount);
    }

    [Fact]
    public async Task PullRequestLockManager_WhenAcquisitionCancelled_CleansUpReferenceCount()
    {
        var manager = new PullRequestLockManager();

        // Thread 1 acquires lock
        var lock1 = await manager.AcquireLockAsync("owner", "repo", 100);
        Assert.Equal(1, manager.ActiveLockCount);

        // Thread 2 attempts to acquire with cancelled token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await manager.AcquireLockAsync("owner", "repo", 100, cts.Token);
        });

        // Refcount decremented on cancellation; 1 active lock remains (held by thread 1)
        Assert.Equal(1, manager.ActiveLockCount);

        // Disposing thread 1's lock brings count to 0
        lock1.Dispose();
        Assert.Equal(0, manager.ActiveLockCount);
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

        var context = CreateMockContext(gitHub, mockRefresh: true);
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

        var context = CreateMockContext(gitHub, mockRefresh: true);

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

    [Fact]
    public async Task PullRequestPipeline_PartialWriteFailure_RecoversAndRetries()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        // AddLabelsAsync succeeds
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // First call to RemoveLabelAsync fails with simulated network error
        var removeCallCount = 0;
        gitHub.RemoveLabelAsync("owner", "repo", 100, GitHubLabels.WorkflowReadyForReview, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                removeCallCount++;
                if (removeCallCount == 1)
                {
                    throw new InvalidOperationException("Simulated transient socket reset");
                }
                return Task.CompletedTask;
            });

        // Fresh PR loaded during recovery reflects the newly added label and old label
        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var labelInReview = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelInReview, GitHubLabels.WorkflowInReview);
        var labelReady = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelReady, GitHubLabels.WorkflowReadyForReview);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, new[] { labelReady });
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, new[] { labelInReview, labelReady });

        var prCallCount = 0;
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++prCallCount == 1 ? initialPr : freshPr));

        // Execute pipeline: should catch the partial failure, query GetPullRequestAsync, recompute diff, and retry
        await pipeline.ExecuteAsync(context);

        // Verify recovery fetched fresh PR state and retried removal
        await gitHub.Received(2).GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>());
        Assert.Equal(2, removeCallCount);

        // Verify final state: addition occurred once, removal retried to completion
        await gitHub.Received(1).AddLabelsAsync("owner", "repo", 100, Arg.Is<string[]>(a => a.Contains(GitHubLabels.WorkflowInReview)), Arg.Any<CancellationToken>());
        await gitHub.Received(2).RemoveLabelAsync("owner", "repo", 100, GitHubLabels.WorkflowReadyForReview, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PullRequestPipeline_RecoveryFailure_ThrowsAggregateException()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        // Initial write fails
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Initial write failed")));

        var prCallCount = 0;
        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, new[] { (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label)) });

        // Call 1 is post-lock refresh; call 2 is automated recovery
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(_ => ++prCallCount == 1
                ? Task.FromResult(initialPr)
                : Task.FromException<PullRequest>(new HttpRequestException("Remote network unreachable")));

        var aggEx = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(context));
        Assert.Equal(2, aggEx.InnerExceptions.Count);
        Assert.IsType<InvalidOperationException>(aggEx.InnerExceptions[0]);
        Assert.IsType<HttpRequestException>(aggEx.InnerExceptions[1]);
    }

    [Fact]
    public async Task PullRequestPipeline_RecoveryNullPrState_ThrowsAggregateException()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                ctx.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        // Initial write fails
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Initial write failed")));

        var prCallCount = 0;
        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, Array.Empty<Label>());

        // Call 1 is post-lock refresh; call 2 is automated recovery
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(_ => ++prCallCount == 1 ? Task.FromResult<PullRequest>(initialPr) : Task.FromResult<PullRequest>(null!));

        var aggEx = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(context));
        Assert.Equal(2, aggEx.InnerExceptions.Count);
        Assert.IsType<InvalidOperationException>(aggEx.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(aggEx.InnerExceptions[1]);
        Assert.Contains("unable to fetch authoritative remote pull request state or labels", aggEx.InnerExceptions[1].Message);
    }

    [Fact]
    public async Task PullRequestPipeline_RecoveryNullLabels_ThrowsAggregateException()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                ctx.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        // Initial write fails
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Initial write failed")));

        var prCallCount = 0;
        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, Array.Empty<Label>());

        // Recovery query returns PR with null Labels collection
        var freshPrWithoutLabels = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPrWithoutLabels, null);

        // Call 1 is post-lock refresh; call 2 is automated recovery
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(_ => ++prCallCount == 1 ? Task.FromResult(initialPr) : Task.FromResult(freshPrWithoutLabels));

        var aggEx = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(context));
        Assert.Equal(2, aggEx.InnerExceptions.Count);
        Assert.IsType<InvalidOperationException>(aggEx.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(aggEx.InnerExceptions[1]);
        Assert.Contains("unable to fetch authoritative remote pull request state or labels", aggEx.InnerExceptions[1].Message);
    }

    [Fact]
    public async Task PullRequestPipeline_CustomRecoveryTimeout_Respected()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var customTimeout = TimeSpan.FromMilliseconds(50);
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, recoveryTimeout: customTimeout);
        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        // Initial write fails
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Initial write failed")));

        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var labelReady = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelReady, GitHubLabels.WorkflowReadyForReview);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, new[] { labelReady });

        var recoveryCallCount = 0;
        // Initial refresh returns immediately; recovery call hangs until recoveryToken is cancelled by custom timeout
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                recoveryCallCount++;
                if (recoveryCallCount == 1)
                    return freshPr;

                var ct = ci.Arg<CancellationToken>();
                await Task.Delay(10000, ct);
                return freshPr;
            });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var aggEx = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(context));
        stopwatch.Stop();

        // Must abort promptly via custom timeout (around 50ms) rather than waiting 15s default
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(aggEx.InnerExceptions, ex => ex is OperationCanceledException or TaskCanceledException);
    }

    [Fact]
    public async Task PullRequestPipeline_ExecutesModulesWithLiveCancellationToken()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        CancellationToken receivedToken = default;
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                receivedToken = ci.Arg<CancellationToken>();
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub, prNumber: 100, mockRefresh: true);

        using var cts = new CancellationTokenSource();
        await pipeline.ExecuteAsync(context, cts.Token);

        Assert.True(receivedToken.CanBeCanceled);
        Assert.Equal(cts.Token, receivedToken);
    }

    [Fact]
    public async Task PullRequestContext_FactoryMethods_PropagateLiveCancellationToken()
    {
        var gitHub = Substitute.For<IGitHubService>();
        using var cts = new CancellationTokenSource();

        var prEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestOpenedEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestOpenedEvent));
        var pr = (Octokit.Webhooks.Models.PullRequestEvent.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.PullRequestEvent.PullRequest));
        typeof(Octokit.Webhooks.Events.PullRequestEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequestEvent.PullRequest))?.SetValue(prEvent, pr);

        var ctx = PullRequestContext.FromPullRequestEvent(gitHub, "captains", null, prEvent, Octokit.Webhooks.Events.PullRequest.PullRequestAction.Opened, cts.Token);
        Assert.True(ctx.CancellationToken.CanBeCanceled);
        Assert.Equal(cts.Token, ctx.CancellationToken);

        // Verify lazy I/O passes the live token to IGitHubService
        gitHub.GetPullRequestFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Octokit.PullRequestFile>>(Array.Empty<Octokit.PullRequestFile>()));
        await ctx.ChangedFiles.Value;
        await gitHub.Received(1).GetPullRequestFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), cts.Token);
    }

    [Fact]
    public void PullRequestPipeline_HasEligibleModule_ReturnsCorrectStatus()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub, prNumber: 100);

        Assert.True(pipeline.HasEligibleModule(context));
    }

    [Fact]
    public async Task PullRequestPipeline_CancellationDuringRecovery_ThrowsOperationCanceledExceptionDirectly()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        using var cts = new CancellationTokenSource();

        // Initial write fails and cancels token during execution
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                cts.Cancel();
                throw new InvalidOperationException("Initial write failed");
            });

        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, Array.Empty<Label>());

        var labelInReview = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelInReview, GitHubLabels.WorkflowInReview);
        var recoveryPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(recoveryPr, new[] { labelInReview });

        var prCalls = 0;
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++prCalls == 1 ? initialPr : recoveryPr));

        // Pipeline should throw OperationCanceledException directly without wrapping in AggregateException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(context, cts.Token));
    }

    [Fact]
    public async Task PullRequestPipeline_CancellationAfterPartialWrite_ExecutesRecoveryAndRethrowsCancellation()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                ctx.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, logger);
        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        using var cts = new CancellationTokenSource();

        // Step 1: AddLabelsAsync succeeds, but cancels the token right after
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                cts.Cancel(); // Token is cancelled after the write succeeds
                return Task.CompletedTask;
            });

        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var labelReadyInitial = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelReadyInitial, GitHubLabels.WorkflowReadyForReview);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, new[] { labelReadyInitial });

        // Step 2: During recovery, fresh PR state is fetched
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var labelInReview = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelInReview, GitHubLabels.WorkflowInReview);
        var labelReady = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelReady, GitHubLabels.WorkflowReadyForReview);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, new[] { labelInReview, labelReady });

        var getPrCalls = 0;
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++getPrCalls == 1 ? initialPr : freshPr));

        // Step 3: Recovery retries RemoveLabelAsync and succeeds
        gitHub.RemoveLabelAsync("owner", "repo", 100, GitHubLabels.WorkflowReadyForReview, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Execute pipeline: must throw OperationCanceledException
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(context, cts.Token));

        // Verify recovery executed: GetPullRequestAsync was called for lock refresh and recovery
        await gitHub.Received(2).GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>());

        // Verify RemoveLabelAsync was called during recovery
        await gitHub.Received(1).RemoveLabelAsync("owner", "repo", 100, GitHubLabels.WorkflowReadyForReview, Arg.Any<CancellationToken>());

        // Verify structured warning was logged
        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task PullRequestPipeline_InFlightWriteTimeoutWithCallerTokenActive_ExecutesRecoveryAndReconcilesRemoteState()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                ctx.LabelsToRemove.Add(GitHubLabels.WorkflowReadyForReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, logger);
        var context = CreateMockContext(gitHub, prNumber: 100, existingLabels: new[] { GitHubLabels.WorkflowReadyForReview });

        using var callerCts = new CancellationTokenSource();
        var remoteLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GitHubLabels.WorkflowReadyForReview };

        // Initial write: AddLabelsAsync adds to fake remote state, but throws TaskCanceledException (simulating transport timeout)
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var labels = ci.Arg<string[]>();
                foreach (var l in labels)
                {
                    remoteLabels.Add(l);
                }
                throw new TaskCanceledException("HTTP request timed out at transport layer");
            });

        // Set up fresh PR returned during recovery reflecting remote state
        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var labelReady = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelReady, GitHubLabels.WorkflowReadyForReview);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, new[] { labelReady });

        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var labelInReview = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(labelInReview, GitHubLabels.WorkflowInReview);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, new[] { labelReady, labelInReview });

        var getPrCalls = 0;
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++getPrCalls == 1 ? initialPr : freshPr));

        // Recovery write: RemoveLabelAsync removes label from fake remote state
        gitHub.RemoveLabelAsync("owner", "repo", 100, GitHubLabels.WorkflowReadyForReview, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                remoteLabels.Remove(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        // Pipeline execution should throw TaskCanceledException after executing recovery
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => pipeline.ExecuteAsync(context, callerCts.Token));
        Assert.False(callerCts.IsCancellationRequested);

        // Assert that recovery was entered and executed:
        // 1. Initial lock refresh + recovery fetch = 2 calls to GetPullRequestAsync
        await gitHub.Received(2).GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>());

        // 2. Recovery performed removal of Workflow: Ready For Review
        await gitHub.Received(1).RemoveLabelAsync("owner", "repo", 100, GitHubLabels.WorkflowReadyForReview, Arg.Any<CancellationToken>());

        // 3. Fake remote state was reconciled: contains Workflow: In Review and NOT Workflow: Ready For Review
        Assert.Contains(GitHubLabels.WorkflowInReview, remoteLabels);
        Assert.DoesNotContain(GitHubLabels.WorkflowReadyForReview, remoteLabels);

        // 4. Warning was logged indicating recovery commenced
        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task PullRequestPipeline_CancellationRequested_AbortsWithoutWrites()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(context, cts.Token));

        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default(string[])!, default);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_PostLockRefreshFails_LogsWarningAndAbortsLabelMutation()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, logger);
        var context = CreateMockContext(gitHub, prNumber: 42, existingLabels: new[] { "Existing: Label" });

        gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns<PullRequest>(_ => throw new HttpRequestException("Simulated API failure"));

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Post-lock refresh failed")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default(string[])!, default);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_ModuleCancellation_RethrowsWithoutLoggingError()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException());

        var gitHub = Substitute.For<IGitHubService>();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, logger);
        var context = CreateMockContext(gitHub, prNumber: 42, mockRefresh: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(context, CancellationToken.None));

        logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task GitHubWebhookProcessor_Cancellation_ReleasesPullRequestLock()
    {
        var lockManager = new PullRequestLockManager();
        var gitHub = Substitute.For<IGitHubService>();

        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        using var cts = new CancellationTokenSource();

        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);
        var context = CreateMockContext(gitHub, prNumber: 999, mockRefresh: true);

        // Process under lock with cancellation token
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using (await lockManager.AcquireLockAsync("owner", "repo", 999, cts.Token))
            {
                await pipeline.ExecuteAsync(context, cts.Token);
            }
        });

        // Verify the lock was cleanly released and can be acquired immediately by a subsequent request
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var newLock = await lockManager.AcquireLockAsync("owner", "repo", 999, testCts.Token);
        Assert.NotNull(newLock);
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

    [Fact]
    public async Task PullRequestContext_GetFileContentAsync_IsCaseSensitiveAndThreadSafe()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetFileContentAsync("owner", "repo", "SPECS/Foo.spec", "sha123", Arg.Any<CancellationToken>())
            .Returns("Content of upper Foo");
        gitHub.GetFileContentAsync("owner", "repo", "SPECS/foo.spec", "sha123", Arg.Any<CancellationToken>())
            .Returns("Content of lower foo");

        // Concurrent uncached key: mock adds simulated delay to guarantee simultaneous contention
        gitHub.GetFileContentAsync("owner", "repo", "SPECS/Concurrent.spec", "sha123", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(20);
                return "Content of concurrent";
            });

        var context = CreateMockContext(gitHub, headSha: "sha123");

        // 1. Case-sensitivity test (case must not be folded on Linux/git paths)
        var contentUpper = await context.GetFileContentAsync("SPECS/Foo.spec");
        var contentLower = await context.GetFileContentAsync("SPECS/foo.spec");

        Assert.Equal("Content of upper Foo", contentUpper);
        Assert.Equal("Content of lower foo", contentLower);

        // 2. Concurrency test on an uncached key: Lazy execution guarantees underlying loader is invoked strictly once
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => context.GetFileContentAsync("SPECS/Concurrent.spec")))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("Content of concurrent", r));
        await gitHub.Received(1).GetFileContentAsync("owner", "repo", "SPECS/Concurrent.spec", "sha123", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_UpdatesAllPropertiesAndRefreshesExistingLabels()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        var label = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
        typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(label, "Workflow: Ready For Review");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, new[] { label });
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, false);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))?.SetValue(freshPr, "Updated Title");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Body))?.SetValue(freshPr, "Updated Body");

        var head = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Sha))?.SetValue(head, "new-sha");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))?.SetValue(freshPr, head);

        var baseRef = (GitReference)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitReference));
        typeof(GitReference).GetProperty(nameof(GitReference.Ref))?.SetValue(baseRef, "target-branch");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Base))?.SetValue(freshPr, baseRef);

        gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(freshPr));

        var context = CreateMockContext(gitHub, prNumber: 42, existingLabels: new[] { "Stale: Label" });
        var refreshed = await context.RefreshAsync();

        Assert.True(refreshed);
        Assert.Contains("Workflow: Ready For Review", context.ExistingLabels);
        Assert.DoesNotContain("Stale: Label", context.ExistingLabels);
        Assert.Equal("Updated Title", context.Title);
        Assert.Equal("Updated Body", context.Body);
        Assert.Equal("new-sha", context.HeadSha);
        Assert.Equal("target-branch", context.BaseBranch);

        var resolvedPr = await context.PullRequest.Value;
        Assert.Same(freshPr, resolvedPr);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_HandlesReadyForReviewAction()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, true);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, Array.Empty<Octokit.Label>());

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(freshPr));

        var prEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestReadyForReviewEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestReadyForReviewEvent));
        var pr = (Octokit.Webhooks.Models.PullRequestEvent.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.PullRequestEvent.PullRequest));
        typeof(Octokit.Webhooks.Events.PullRequestEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequestEvent.PullRequest))?.SetValue(prEvent, pr);

        var context = PullRequestContext.FromPullRequestEvent(gitHub, "captains", null, prEvent, Octokit.Webhooks.Events.PullRequest.PullRequestAction.ReadyForReview);
        var refreshed = await context.RefreshAsync();

        Assert.True(refreshed);
        Assert.True(context.IsDraft);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_SynchronizeAction_BiasesTowardsRemoteDraftStatus()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, true);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, Array.Empty<Octokit.Label>());

        gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(freshPr));

        var context = CreateMockContext(
            gitHub,
            prNumber: 42,
            isDraft: false,
            action: nameof(WebhookPullRequestAction.Synchronize),
            prAction: WebhookPullRequestAction.Synchronize);
        Assert.False(context.IsDraft);

        var refreshed = await context.RefreshAsync();
        Assert.True(refreshed);
        Assert.True(context.IsDraft);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_IssueCommentEvent_DoesNotOverwriteCommentBody()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Body))?.SetValue(freshPr, "PR description body");
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, Array.Empty<Octokit.Label>());

        gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(freshPr));

        var context = CreateMockContext(
            gitHub,
            prNumber: 42,
            body: "/lgtm comment command",
            eventType: PullRequestEventType.IssueComment,
            action: "created");
        Assert.Equal("/lgtm comment command", context.Body);

        var refreshed = await context.RefreshAsync();
        Assert.True(refreshed);
        Assert.Equal("/lgtm comment command", context.Body);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_HandlesNullPrResponse()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PullRequest>(null!));

        var context = CreateMockContext(gitHub, prNumber: 42, existingLabels: new[] { "Original: Label" });
        var refreshed = await context.RefreshAsync();

        Assert.False(refreshed);
        Assert.Contains("Original: Label", context.ExistingLabels);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_TransientFailure_PreservesWebhookPayload()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns<PullRequest>(_ => throw new InvalidOperationException("Simulated API glitch"));

        var context = CreateMockContext(gitHub, prNumber: 42, existingLabels: new[] { "Original: Label" });
        var refreshed = await context.RefreshAsync();

        Assert.False(refreshed);
        Assert.Contains("Original: Label", context.ExistingLabels);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_Cancellation_ThrowsOperationCanceledException()
    {
        var gitHub = Substitute.For<IGitHubService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        gitHub.GetPullRequestAsync("owner", "repo", 42, Arg.Any<CancellationToken>())
            .Returns<PullRequest>(_ => throw new OperationCanceledException(cts.Token));

        var context = CreateMockContext(gitHub, prNumber: 42);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.RefreshAsync(cts.Token));
    }

    [Fact]
    public async Task PullRequestContext_AuthorEmail_WhenApiThrows_ReturnsNullGracefully()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAuthorEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromException<string?>(new Exception("API network error")));

        var context = CreateMockContext(gitHub);

        var email = await context.AuthorEmail.Value;

        Assert.Null(email);
    }

    [Fact]
    public async Task PullRequestContext_AuthorEmail_WhenOperationCanceled_Throws()
    {
        var gitHub = Substitute.For<IGitHubService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        gitHub.GetPullRequestAuthorEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new OperationCanceledException(cts.Token)));

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: false,
            title: "PR",
            body: "",
            senderLogin: "user",
            authorLogin: "author",
            action: "opened",
            existingLabels: Array.Empty<string>(),
            cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.AuthorEmail.Value);
    }

    [Fact]
    public async Task PullRequestContext_IsClosedAndIsOpen_EvaluatesAllFiveSources()
    {
        var gitHub = Substitute.For<IGitHubService>();

        // 1. IsMerged == true
        var mergedCtx = new PullRequestContext(gitHub, "captains", null, PullRequestEventType.PullRequest,
            "o", "r", 1, "s", "main", false, isMerged: true, "t", null, "u", "a", "opened", Array.Empty<string>());
        Assert.True(mergedCtx.IsClosed);
        Assert.False(mergedCtx.IsOpen);

        // 2. PullRequestAction == Closed
        var actionClosedCtx = new PullRequestContext(gitHub, "captains", null, PullRequestEventType.PullRequest,
            "o", "r", 1, "s", "main", false, isMerged: null, "t", null, "u", "a", "closed", Array.Empty<string>(),
            prAction: WebhookPullRequestAction.Closed);
        Assert.True(actionClosedCtx.IsClosed);
        Assert.False(actionClosedCtx.IsOpen);

        // 3. RemoteState == "closed" (via RefreshAsync)
        var closedPr = (Octokit.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.PullRequest));
        typeof(Octokit.PullRequest).GetProperty(nameof(Octokit.PullRequest.State))?.SetValue(closedPr, new Octokit.StringEnum<Octokit.ItemState>(Octokit.ItemState.Closed));
        typeof(Octokit.PullRequest).GetProperty(nameof(Octokit.PullRequest.Labels))?.SetValue(closedPr, Array.Empty<Octokit.Label>());
        gitHub.GetPullRequestAsync("o", "r", 1).Returns(closedPr);
        var remoteClosedCtx = new PullRequestContext(gitHub, "captains", null, PullRequestEventType.PullRequest,
            "o", "r", 1, "s", "main", false, isMerged: null, "t", null, "u", "a", "opened", Array.Empty<string>());
        await remoteClosedCtx.RefreshAsync();
        Assert.True(remoteClosedCtx.IsClosed);
        Assert.False(remoteClosedCtx.IsOpen);

        // 4. PullRequestEvent.PullRequest.State == "closed"
        var prEvent = CreatePullRequestEvent();
        var prStateProp = typeof(WebhookPullRequest).GetProperty(nameof(WebhookPullRequest.State));
        if (prStateProp != null)
        {
            var prStateVal = Activator.CreateInstance(prStateProp.PropertyType, "closed");
            prStateProp.SetValue(prEvent.PullRequest, prStateVal);
        }
        var prEventClosedCtx = PullRequestContext.FromPullRequestEvent(gitHub, "captains", null, prEvent, WebhookPullRequestAction.Opened);
        Assert.True(prEventClosedCtx.IsClosed);
        Assert.False(prEventClosedCtx.IsOpen);

        // 5. PullRequestReviewEvent.PullRequest.State == "closed"
        var reviewEvent = CreateReviewEvent("reviewer");
        var simpleStateProp = typeof(WebhookSimplePullRequest).GetProperty(nameof(WebhookSimplePullRequest.State));
        if (simpleStateProp != null)
        {
            var simpleStateVal = Activator.CreateInstance(simpleStateProp.PropertyType, "closed");
            simpleStateProp.SetValue(reviewEvent.PullRequest, simpleStateVal);
        }
        var reviewEventClosedCtx = PullRequestContext.FromReviewEvent(gitHub, "captains", null, reviewEvent, WebhookPullRequestReviewAction.Submitted);
        Assert.True(reviewEventClosedCtx.IsClosed);
        Assert.False(reviewEventClosedCtx.IsOpen);
    }

    [Fact]
    public async Task PullRequestContext_TeamCaptains_LoadsCaseInsensitiveSet()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetTeamMembersAsync("owner", "captains")
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CaptainAlice" });

        var context = CreateMockContext(gitHub);

        var captains = await context.TeamCaptains.Value;

        Assert.Contains("captainalice", captains);
        Assert.Contains("CAPTAINALICE", captains);
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
    [InlineData("admin@ISCAS.AC.CN")]
    public async Task UserContributionModule_NegativePath_StaffEmailExcluded(string staffEmail)
    {
        var module = new UserContributionModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAuthorEmailAsync("owner", "repo", Arg.Any<int>())
            .Returns(staffEmail);

        var context = CreateMockContext(gitHub);
        await module.ProcessAsync(context, CancellationToken.None);

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
        await module.ProcessAsync(context, CancellationToken.None);

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
        await module.ProcessAsync(context, CancellationToken.None);

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
        await module.ProcessAsync(context, CancellationToken.None);

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

        await module.ProcessAsync(context, CancellationToken.None);

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

        await module.ProcessAsync(context, CancellationToken.None);

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

        await module.ProcessAsync(context, CancellationToken.None);

        // When captain is the author, their review does NOT acknowledge Commits: Updated
        Assert.DoesNotContain(GitHubLabels.CommitsUpdated, context.LabelsToRemove);
    }

    [Fact]
    public async Task WorkflowStateModule_MergedPr_DoesNotMoveBackToInReview_OnUnlabeledBlocked()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var module = new WorkflowStateModule();

        var prEvent = CreatePullRequestEvent();
        var unlabeledEvent = (WebhookPullRequestUnlabeledEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequestUnlabeledEvent));
        typeof(WebhookPullRequestEvent).GetProperty(nameof(WebhookPullRequestEvent.Repository))?.SetValue(unlabeledEvent, prEvent.Repository);
        typeof(WebhookPullRequestEvent).GetProperty(nameof(WebhookPullRequestEvent.PullRequest))?.SetValue(unlabeledEvent, prEvent.PullRequest);
        typeof(WebhookPullRequest).GetProperty(nameof(WebhookPullRequest.Merged))?.SetValue(prEvent.PullRequest, true);

        var blockedLabel = (WebhookLabel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookLabel));
        typeof(WebhookLabel).GetProperty(nameof(WebhookLabel.Name))?.SetValue(blockedLabel, GitHubLabels.WorkflowBlocked);
        typeof(WebhookPullRequestUnlabeledEvent).GetProperty(nameof(WebhookPullRequestUnlabeledEvent.Label))?.SetValue(unlabeledEvent, blockedLabel);

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 100,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: true,
            title: "Merged PR",
            body: null,
            senderLogin: "captain",
            authorLogin: "author",
            action: "unlabeled",
            existingLabels: new[] { GitHubLabels.WorkflowComplete },
            prAction: WebhookPullRequestAction.Unlabeled,
            prEvent: unlabeledEvent);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, context.LabelsToAdd);
    }

    [Fact]
    public async Task WorkflowStateModule_ClosedPr_DoesNotMoveToInReview_OnCaptainCommentOrReview()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetTeamMembersAsync("owner", "captains")
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CaptainAlice" });

        var closedPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(closedPr, new Octokit.StringEnum<ItemState>(ItemState.Closed));
        gitHub.GetPullRequestAsync("owner", "repo", 100).Returns(closedPr);

        var module = new WorkflowStateModule();

        // 1. Captain comment on closed PR
        var commentEvent = CreateCommentEvent(sender: "CaptainAlice", isPr: true);
        var commentContext = PullRequestContext.FromIssueCommentEvent(gitHub, "captains", null, commentEvent, WebhookIssueCommentAction.Created);

        await module.ProcessAsync(commentContext, CancellationToken.None);
        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, commentContext.LabelsToAdd);

        // 2. Captain review on closed PR (with Workflow: Complete label)
        var reviewEvent = CreateReviewEvent(sender: "CaptainAlice");
        var reviewContext = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.Review,
            owner: "owner",
            repo: "repo",
            prNumber: 100,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: true,
            title: "Closed PR",
            body: null,
            senderLogin: "CaptainAlice",
            authorLogin: "author",
            action: nameof(WebhookPullRequestReviewAction.Submitted),
            existingLabels: new[] { GitHubLabels.WorkflowComplete },
            reviewAction: WebhookPullRequestReviewAction.Submitted);

        await module.ProcessAsync(reviewContext, CancellationToken.None);
        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, reviewContext.LabelsToAdd);
    }

    [Fact]
    public async Task WorkflowStateModule_MergeEvent_FollowedByUnlabeledEvent_PreservesCompleteAndDoesNotAddInReview()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var module = new WorkflowStateModule();
        var mutexEngine = new LabelMutexEngine();

        // 1. Initial State: PR is open with Blocked and InReview
        var initialLabels = new[] { GitHubLabels.WorkflowBlocked, GitHubLabels.WorkflowInReview };

        // Step A: Process Merge Event (Closed with Merged = true)
        var mergePrEvent = CreatePullRequestEvent();
        typeof(WebhookPullRequest).GetProperty(nameof(WebhookPullRequest.Merged))?.SetValue(mergePrEvent.PullRequest, true);
        var prStateProp = typeof(WebhookPullRequest).GetProperty(nameof(WebhookPullRequest.State));
        if (prStateProp != null)
        {
            var prStateVal = Activator.CreateInstance(prStateProp.PropertyType, "closed");
            prStateProp.SetValue(mergePrEvent.PullRequest, prStateVal);
        }

        var mergeContext = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 100,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: true,
            title: "Merged PR",
            body: null,
            senderLogin: "captain",
            authorLogin: "author",
            action: "closed",
            existingLabels: initialLabels,
            prAction: WebhookPullRequestAction.Closed,
            prEvent: mergePrEvent);

        await module.ProcessAsync(mergeContext, CancellationToken.None);

        // Diff after merge event
        var mergeDiff = mutexEngine.ComputeDiff(initialLabels, mergeContext.LabelsToAdd, mergeContext.LabelsToRemove);
        Assert.Contains(GitHubLabels.WorkflowComplete, mergeDiff.LabelsToAdd);
        Assert.Contains(GitHubLabels.WorkflowInReview, mergeDiff.LabelsToRemove);

        // Remote state after applying merge diff: Blocked (still there) + Complete
        var postMergeLabels = initialLabels
            .Except(mergeDiff.LabelsToRemove, StringComparer.OrdinalIgnoreCase)
            .Concat(mergeDiff.LabelsToAdd)
            .ToList();
        Assert.Contains(GitHubLabels.WorkflowComplete, postMergeLabels);
        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, postMergeLabels);

        // Step B: Subsequent Unlabeled Event (unlabeling Blocked on the now-merged PR)
        var unlabeledEvent = (WebhookPullRequestUnlabeledEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequestUnlabeledEvent));
        typeof(WebhookPullRequestEvent).GetProperty(nameof(WebhookPullRequestEvent.Repository))?.SetValue(unlabeledEvent, mergePrEvent.Repository);
        typeof(WebhookPullRequestEvent).GetProperty(nameof(WebhookPullRequestEvent.PullRequest))?.SetValue(unlabeledEvent, mergePrEvent.PullRequest);

        var blockedLabel = (WebhookLabel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookLabel));
        typeof(WebhookLabel).GetProperty(nameof(WebhookLabel.Name))?.SetValue(blockedLabel, GitHubLabels.WorkflowBlocked);
        typeof(WebhookPullRequestUnlabeledEvent).GetProperty(nameof(WebhookPullRequestUnlabeledEvent.Label))?.SetValue(unlabeledEvent, blockedLabel);

        var unlabeledContext = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 100,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: true,
            title: "Merged PR",
            body: null,
            senderLogin: "captain",
            authorLogin: "author",
            action: "unlabeled",
            existingLabels: postMergeLabels,
            prAction: WebhookPullRequestAction.Unlabeled,
            prEvent: unlabeledEvent);

        await module.ProcessAsync(unlabeledContext, CancellationToken.None);

        // Must NOT attempt to add InReview, and must NOT remove Complete
        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, unlabeledContext.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.WorkflowComplete, unlabeledContext.LabelsToRemove);
    }

    [Fact]
    public async Task WorkflowStateModule_ClosedPrWithoutComplete_ReviewEvent_DoesNotAddInReview()
    {
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetTeamMembersAsync("owner", "captains", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CaptainAlice" });

        var module = new WorkflowStateModule();

        // Review on a closed PR (closed without merging, so NO Workflow: Complete label)
        var reviewEvent = CreateReviewEvent(sender: "CaptainAlice");
        var simpleStateProp = typeof(WebhookSimplePullRequest).GetProperty(nameof(WebhookSimplePullRequest.State));
        if (simpleStateProp != null)
        {
            var simpleStateVal = Activator.CreateInstance(simpleStateProp.PropertyType, "closed");
            simpleStateProp.SetValue(reviewEvent.PullRequest, simpleStateVal);
        }

        var reviewContext = PullRequestContext.FromReviewEvent(
            gitHub,
            "captains",
            null,
            reviewEvent,
            WebhookPullRequestReviewAction.Submitted);

        Assert.True(reviewContext.IsClosed);
        Assert.True(reviewContext.IsTerminal);

        await module.ProcessAsync(reviewContext, CancellationToken.None);

        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, reviewContext.LabelsToAdd);
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
        string? body = "",
        IEnumerable<string>? existingLabels = null,
        PullRequestEventType eventType = PullRequestEventType.PullRequest,
        bool isDraft = false,
        string action = nameof(WebhookPullRequestAction.Opened),
        WebhookPullRequestAction? prAction = null,
        bool mockRefresh = false)
    {
        var labelsList = (existingLabels ?? Array.Empty<string>()).ToList();

        if (mockRefresh)
        {
            var defaultPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(defaultPr, isDraft);
            typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(defaultPr, new StringEnum<ItemState>(ItemState.Open));
            var labelObjs = labelsList.Select(name =>
            {
                var l = (Octokit.Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Label));
                typeof(Octokit.Label).GetProperty(nameof(Octokit.Label.Name))?.SetValue(l, name);
                return l;
            }).ToArray();
            typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(defaultPr, labelObjs);

            gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), prNumber, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(defaultPr));
        }

        return new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: eventType,
            owner: "owner",
            repo: "repo",
            prNumber: prNumber,
            headSha: headSha,
            baseBranch: "main",
            isDraft: isDraft,
            isMerged: false,
            title: "Test PR",
            body: body,
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: action,
            existingLabels: labelsList,
            prAction: prAction ?? WebhookPullRequestAction.Opened);
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

    #region R12 Regression Tests

    [Fact]
    public async Task PullRequestPipeline_PostLockRefreshFails_StartsNewerRemoteWorkflowLabel_AbortsLabelMutationWithoutStaleWrites()
    {
        var workflowModule = new WorkflowStateModule();
        var gitHub = Substitute.For<IGitHubService>();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { workflowModule }, mutexEngine, gitHub, logger);

        // Stale converted_to_draft payload without workflow labels
        var prEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestConvertedToDraftEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestConvertedToDraftEvent));
        var pr = (Octokit.Webhooks.Models.PullRequestEvent.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.PullRequestEvent.PullRequest));
        typeof(Octokit.Webhooks.Events.PullRequestEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequestEvent.PullRequest))?.SetValue(prEvent, pr);

        var context = PullRequestContext.FromPullRequestEvent(
            gitHub,
            "captains",
            null,
            prEvent,
            Octokit.Webhooks.Events.PullRequest.PullRequestAction.ConvertedToDraft);

        // Remote GitHub already has Workflow: Ready For Review, but refresh fails transients
        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<PullRequest>(_ => throw new HttpRequestException("Simulated transient network failure"));

        await pipeline.ExecuteAsync(context);

        // Verify pipeline aborted and did not perform stale writes or removals
        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default(string[])!, default);
        await gitHub.DidNotReceiveWithAnyArgs().RemoveLabelAsync(default!, default!, default, default!, default);
        Assert.Empty(context.LabelsToAdd);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_DelayedConvertedToDraft_AuthoritativeRemoteNonDraftWins()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, false);
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, Array.Empty<Octokit.Label>());

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(freshPr));

        var prEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestConvertedToDraftEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestConvertedToDraftEvent));
        var pr = (Octokit.Webhooks.Models.PullRequestEvent.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.PullRequestEvent.PullRequest));
        typeof(Octokit.Webhooks.Events.PullRequestEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequestEvent.PullRequest))?.SetValue(prEvent, pr);

        var context = PullRequestContext.FromPullRequestEvent(
            gitHub,
            "captains",
            null,
            prEvent,
            Octokit.Webhooks.Events.PullRequest.PullRequestAction.ConvertedToDraft);

        var refreshed = await context.RefreshAsync();
        Assert.True(refreshed);
        Assert.False(context.IsDraft);
    }

    [Fact]
    public async Task PullRequestContext_RefreshAsync_DelayedClosedAction_AuthoritativeRemoteOpenWins()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, Array.Empty<Octokit.Label>());

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(freshPr));

        var prEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestClosedEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestClosedEvent));
        var pr = (Octokit.Webhooks.Models.PullRequestEvent.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.PullRequestEvent.PullRequest));
        typeof(Octokit.Webhooks.Events.PullRequestEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequestEvent.PullRequest))?.SetValue(prEvent, pr);

        var context = PullRequestContext.FromPullRequestEvent(
            gitHub,
            "captains",
            null,
            prEvent,
            Octokit.Webhooks.Events.PullRequest.PullRequestAction.Closed);

        var refreshed = await context.RefreshAsync();
        Assert.True(refreshed);
        Assert.False(context.IsClosed);
        Assert.False(context.IsTerminal);
    }

    [Fact]
    public async Task PullRequestPipeline_TaskCanceledExceptionInitialWrite_RecoveryTimeout_ThrowsAggregateExceptionAndLogsError()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);
        module.ProcessAsync(Arg.Any<PullRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<PullRequestContext>();
                ctx.LabelsToAdd.Add(GitHubLabels.WorkflowInReview);
                return Task.CompletedTask;
            });

        var gitHub = Substitute.For<IGitHubService>();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var mutexEngine = new LabelMutexEngine();
        // Short recovery timeout for fast test execution
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, logger, TimeSpan.FromMilliseconds(50));
        var context = CreateMockContext(gitHub, prNumber: 100);

        var initialPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(initialPr, Array.Empty<Label>());

        var prCallCount = 0;
        gitHub.GetPullRequestAsync("owner", "repo", 100, Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                if (++prCallCount == 1)
                {
                    return initialPr;
                }
                // Simulate recovery timeout by waiting on recoveryToken until canceled
                await Task.Delay(Timeout.Infinite, ct);
                return initialPr;
            });

        // Initial write throws TaskCanceledException
        gitHub.AddLabelsAsync("owner", "repo", 100, Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new TaskCanceledException("Initial write HTTP timeout")));

        var aggEx = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(context));
        Assert.Equal(2, aggEx.InnerExceptions.Count);
        Assert.IsType<TaskCanceledException>(aggEx.InnerExceptions[0]);
        Assert.IsAssignableFrom<OperationCanceledException>(aggEx.InnerExceptions[1]);

        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Failed to recover partial label updates")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task WorkflowStateModule_BlockedOnDraft_RemovesInDevAndReadyAndInReview()
    {
        var module = new WorkflowStateModule();
        var gitHub = Substitute.For<IGitHubService>();

        var labeledEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent));
        var blockedLabel = (Octokit.Webhooks.Models.Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.Label));
        typeof(Octokit.Webhooks.Models.Label).GetProperty(nameof(Octokit.Webhooks.Models.Label.Name))?.SetValue(blockedLabel, GitHubLabels.WorkflowBlocked);
        typeof(Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent.Label))?.SetValue(labeledEvent, blockedLabel);

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: true,
            isMerged: false,
            title: "Draft PR",
            body: null,
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "labeled",
            existingLabels: new[] { GitHubLabels.WorkflowInDev, GitHubLabels.WorkflowBlocked },
            prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Labeled,
            prEvent: labeledEvent);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.WorkflowInDev, context.LabelsToRemove);
        Assert.Contains(GitHubLabels.WorkflowInReview, context.LabelsToRemove);
        Assert.Contains(GitHubLabels.WorkflowReadyForReview, context.LabelsToRemove);
    }

    [Fact]
    public async Task WorkflowStateModule_UnlabeledBlocked_OnDraftPr_RestoresInDev()
    {
        var module = new WorkflowStateModule();
        var gitHub = Substitute.For<IGitHubService>();

        var unlabeledEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent));
        var blockedLabel = (Octokit.Webhooks.Models.Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.Label));
        typeof(Octokit.Webhooks.Models.Label).GetProperty(nameof(Octokit.Webhooks.Models.Label.Name))?.SetValue(blockedLabel, GitHubLabels.WorkflowBlocked);
        typeof(Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent.Label))?.SetValue(unlabeledEvent, blockedLabel);

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: true,
            isMerged: false,
            title: "Draft PR",
            body: null,
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "unlabeled",
            existingLabels: Array.Empty<string>(),
            prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Unlabeled,
            prEvent: unlabeledEvent);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.WorkflowInDev, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, context.LabelsToAdd);
    }

    [Fact]
    public async Task BuildSystemAnalysisModule_SpecBuildSystemChangedFromCmakeToMeson_RemovesStaleCmakeLabel()
    {
        var module = new BuildSystemAnalysisModule();
        var gitHub = Substitute.For<IGitHubService>();

        var file = (Octokit.PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.PullRequestFile));
        typeof(Octokit.PullRequestFile).GetProperty(nameof(Octokit.PullRequestFile.FileName))?.SetValue(file, "package.spec");
        typeof(Octokit.PullRequestFile).GetProperty(nameof(Octokit.PullRequestFile.Status))?.SetValue(file, "modified");

        gitHub.GetPullRequestFilesAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Returns(new[] { file });

        gitHub.GetFileContentAsync("owner", "repo", "package.spec", "sha", Arg.Any<CancellationToken>())
            .Returns("BuildSystem:    meson\nName: test");

        var context = CreateMockContext(
            gitHub,
            prNumber: 1,
            headSha: "sha",
            existingLabels: new[] { GitHubLabels.BuildSystemCmake },
            prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Synchronize);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.BuildSystemMeson, context.LabelsToAdd);
        Assert.Contains(GitHubLabels.BuildSystemCmake, context.LabelsToRemove);
    }

    [Fact]
    public async Task BuildSystemAnalysisModule_CiFileRemoved_RemovesCiLabel()
    {
        var module = new BuildSystemAnalysisModule();
        var gitHub = Substitute.For<IGitHubService>();

        gitHub.GetPullRequestFilesAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Octokit.PullRequestFile>());

        var context = CreateMockContext(
            gitHub,
            prNumber: 1,
            headSha: "sha",
            existingLabels: new[] { GitHubLabels.Ci, GitHubLabels.WorkflowReadyForReview },
            prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Synchronize);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Contains(GitHubLabels.Ci, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.WorkflowReadyForReview, context.LabelsToRemove);
    }

    [Fact]
    public async Task UserContributionModule_AuthorChangesToStaff_RemovesStaleCommunityLabels()
    {
        var module = new UserContributionModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAuthorEmailAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Returns("staff@iscas.ac.cn");

        var context = CreateMockContext(
            gitHub,
            prNumber: 1,
            existingLabels: new[] { GitHubLabels.CommunityContribution, GitHubLabels.CommunityStudent });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToAdd);
        Assert.Contains(GitHubLabels.CommunityContribution, context.LabelsToRemove);
        Assert.Contains(GitHubLabels.CommunityStudent, context.LabelsToRemove);
    }

    [Fact]
    public async Task UserContributionModule_TransientEmailFailure_PreservesExistingCommunityLabel()
    {
        var module = new UserContributionModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAuthorEmailAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Transient GitHub API gateway timeout"));

        var context = CreateMockContext(
            gitHub,
            prNumber: 1,
            existingLabels: new[] { GitHubLabels.CommunityStudent });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.True(context.AuthorEmailLookupFailed);
        Assert.Empty(context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.CommunityStudent, context.LabelsToRemove);
        Assert.DoesNotContain(GitHubLabels.CommunityContribution, context.LabelsToRemove);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-email-string")]
    public async Task UserContributionModule_MissingOrUnparseableEmail_ClearsCommunityLabels(string? email)
    {
        var module = new UserContributionModule();
        var gitHub = Substitute.For<IGitHubService>();
        gitHub.GetPullRequestAuthorEmailAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Returns(email);

        var context = CreateMockContext(
            gitHub,
            prNumber: 1,
            existingLabels: new[] { GitHubLabels.CommunityContribution });

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.Empty(context.LabelsToAdd);
        Assert.Contains(GitHubLabels.CommunityContribution, context.LabelsToRemove);
    }

    [Fact]
    public void BuildSystemAnalysisModule_ManagedLabels_ExcludesTargetRolling()
    {
        var module = new BuildSystemAnalysisModule();
        Assert.DoesNotContain(GitHubLabels.TargetRolling, BuildSystemAnalysisModule.ManagedLabels);
        Assert.Equal(BuildSystemAnalysisModule.ManagedLabels, ((IPullRequestModule)module).ManagedLabels);
    }

    [Fact]
    public async Task BuildSystemAnalysisModule_SpecsDirectoryRemoved_PreservesTargetRollingLabel()
    {
        var module = new BuildSystemAnalysisModule();
        var gitHub = Substitute.For<IGitHubService>();
        var file = (Octokit.PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.PullRequestFile));
        typeof(Octokit.PullRequestFile).GetProperty(nameof(Octokit.PullRequestFile.FileName))?.SetValue(file, "src/main.c");
        gitHub.GetPullRequestFilesAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Returns(new[] { file });

        var context = CreateMockContext(
            gitHub,
            prNumber: 1,
            existingLabels: new[] { GitHubLabels.TargetRolling },
            prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Synchronize);

        await module.ProcessAsync(context, CancellationToken.None);

        Assert.DoesNotContain(GitHubLabels.TargetRolling, context.LabelsToAdd);
        Assert.DoesNotContain(GitHubLabels.TargetRolling, context.LabelsToRemove);
    }

    [Fact]
    public void UserContributionModule_ExposesInterfaceManagedLabels()
    {
        var module = new UserContributionModule();
        Assert.Equal(UserContributionModule.ManagedLabels, ((IPullRequestModule)module).ManagedLabels);
    }

    [Fact]
    public async Task PullRequestLockManager_Dispose_CleansUpSemaphoresAndThrowsOnSubsequentCalls()
    {
        var manager = new PullRequestLockManager();
        using (await manager.AcquireLockAsync("owner", "repo", 42))
        {
            // Lock acquired and released
        }

        manager.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.AcquireLockAsync("owner", "repo", 42));
    }

    private static Octokit.IResponse CreateOctokitResponse(System.Net.HttpStatusCode statusCode)
    {
        var response = Substitute.For<Octokit.IResponse>();
        response.StatusCode.Returns(statusCode);
        return response;
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitNotFoundException_AbortsWithoutRetrying()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var notFoundEx = new Octokit.NotFoundException(CreateOctokitResponse(System.Net.HttpStatusCode.NotFound));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(notFoundEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Non-retriable 404 error should only be attempted once, not retried 3 times
        await gitHub.Received(1).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitApiException400_AbortsWithoutRetrying()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var badRequestEx = new Octokit.ApiException(CreateOctokitResponse(System.Net.HttpStatusCode.BadRequest));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(badRequestEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Non-retriable 400 error should only be attempted once
        await gitHub.Received(1).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitApiValidationException422_AbortsWithoutRetrying()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var validationEx = new Octokit.ApiValidationException(CreateOctokitResponse(System.Net.HttpStatusCode.UnprocessableEntity));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(validationEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Non-retriable 422 error should only be attempted once
        await gitHub.Received(1).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitRateLimitExceededException_RetriesThreeTimes()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var rateLimitEx = (Octokit.RateLimitExceededException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.RateLimitExceededException));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(rateLimitEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Rate limit 403 error is transient and should be retried across 3 attempts
        await gitHub.Received(3).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitAbuseException_RetriesThreeTimes()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var abuseEx = (Octokit.AbuseException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.AbuseException));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(abuseEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Abuse rate limit 403 error is transient and should be retried across 3 attempts
        await gitHub.Received(3).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitApiException500_RetriesThreeTimes()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var serverErrorEx = new Octokit.ApiException(CreateOctokitResponse(System.Net.HttpStatusCode.InternalServerError));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(serverErrorEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Server 500 error is transient and should be retried across 3 attempts
        await gitHub.Received(3).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_HttpRequestException400_AbortsWithoutRetrying()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var badRequestEx = new HttpRequestException("Bad request", null, System.Net.HttpStatusCode.BadRequest);
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(badRequestEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Non-retriable 400 error should only be attempted once
        await gitHub.Received(1).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitAuthorizationException_AbortsWithoutRetrying()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var authEx = (Octokit.AuthorizationException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.AuthorizationException));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(authEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Non-retriable 401 error should only be attempted once
        await gitHub.Received(1).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_OctokitForbiddenException_AbortsWithoutRetrying()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var forbiddenEx = (Octokit.ForbiddenException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.ForbiddenException));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(forbiddenEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Non-retriable 403 error should only be attempted once
        await gitHub.Received(1).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public async Task PullRequestPipeline_PostRefreshHasEligibleModuleFalse_ExitsEarly()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        // Initially returns true (if asked), but after refresh returns false
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(false);

        var gitHub = Substitute.For<IGitHubService>();
        var dummyPr = (Octokit.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.PullRequest));
        typeof(Octokit.PullRequest).GetProperty(nameof(Octokit.PullRequest.Labels))?.SetValue(dummyPr, Array.Empty<Octokit.Label>());
        typeof(Octokit.PullRequest).GetProperty(nameof(Octokit.PullRequest.State))?.SetValue(dummyPr, new Octokit.StringEnum<Octokit.ItemState>(Octokit.ItemState.Open));
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Returns(dummyPr);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Refresh succeeds, but HasEligibleModule returns false, so module.ProcessAsync is not called
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
        await gitHub.DidNotReceiveWithAnyArgs().AddLabelsAsync(default!, default!, default, default!);
    }

    [Fact]
    public async Task PullRequestPipeline_TransientNetworkErrorWithoutStatusCode_RetriesThreeTimes()
    {
        var module = Substitute.For<IPullRequestModule>();
        module.Priority.Returns(10);
        module.ShouldProcess(Arg.Any<PullRequestContext>()).Returns(true);

        var gitHub = Substitute.For<IGitHubService>();
        var networkEx = new HttpRequestException("Socket connection reset without status code");
        gitHub.GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>())
            .Throws(networkEx);

        var mutexEngine = new LabelMutexEngine();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub);

        var context = CreateMockContext(gitHub, prNumber: 1);
        await pipeline.ExecuteAsync(context);

        // Network error without 4xx status code should be retried across all 3 attempts
        await gitHub.Received(3).GetPullRequestAsync("owner", "repo", 1, Arg.Any<CancellationToken>());
        await module.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
    }

    [Fact]
    public void PullRequestContext_ReconcileLabels_WithPredicate_AddsAndRemovesCorrectly()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var context = CreateMockContext(gitHub, existingLabels: new[] { "label-a", "label-b", "unmanaged" });
        var managed = new[] { "label-a", "label-b", "label-c" };

        context.ReconcileLabels(managed, l => l == "label-c");

        Assert.Contains("label-c", context.LabelsToAdd);
        Assert.Contains("label-a", context.LabelsToRemove);
        Assert.Contains("label-b", context.LabelsToRemove);
        Assert.DoesNotContain("unmanaged", context.LabelsToRemove);
    }

    [Fact]
    public void PullRequestContext_ReconcileLabels_WithString_AddsAndRemovesCorrectly()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var context = CreateMockContext(gitHub, existingLabels: new[] { "label-a" });
        var managed = new[] { "label-a", "label-b" };

        context.ReconcileLabels(managed, "label-b");

        Assert.Contains("label-b", context.LabelsToAdd);
        Assert.Contains("label-a", context.LabelsToRemove);
    }

    #region R17 Regression Tests

    [Fact]
    public async Task PullRequestContext_RefreshAsync_NullLabels_FailsClosedAndRecordsException()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, null);

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(freshPr));

        var context = CreateMockContext(gitHub, prNumber: 1, existingLabels: new[] { "existing-label" });

        var success = await context.RefreshAsync();

        Assert.False(success);
        Assert.NotNull(context.LastRefreshException);
        Assert.IsType<InvalidOperationException>(context.LastRefreshException);
        // Preserves previous existing labels rather than corrupting state
        Assert.Contains("existing-label", context.ExistingLabels);
    }

    [Fact]
    public async Task PullRequestPipeline_DelayedClosedEvent_RemoteReopened_PreservesActiveWorkflowLabels()
    {
        // Scenario 1: Remote is open and has Workflow: In Review. A delayed closed event arrives.
        var remoteLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GitHubLabels.WorkflowInReview };
        var gitHub = Substitute.For<IGitHubService>();

        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, false);

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var labelList = remoteLabels.Select(name =>
                {
                    var lbl = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
                    typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(lbl, name);
                    return lbl;
                }).ToList();
                typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, labelList);
                return Task.FromResult(freshPr);
            });

        gitHub.AddLabelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                foreach (var l in ci.Arg<string[]>()) remoteLabels.Add(l);
                return Task.CompletedTask;
            });

        gitHub.RemoveLabelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                remoteLabels.Remove(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var workflowModule = new WorkflowStateModule();
        var mutexEngine = new LabelMutexEngine();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var pipeline = new PullRequestPipeline(new[] { workflowModule }, mutexEngine, gitHub, logger);

        // Stale closed webhook payload
        var closedEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestClosedEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestClosedEvent));
        var pr = (Octokit.Webhooks.Models.PullRequestEvent.PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.PullRequestEvent.PullRequest));
        typeof(Octokit.Webhooks.Events.PullRequestEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequestEvent.PullRequest))?.SetValue(closedEvent, pr);

        var context = PullRequestContext.FromPullRequestEvent(
            gitHub,
            "captains",
            null,
            closedEvent,
            Octokit.Webhooks.Events.PullRequest.PullRequestAction.Closed);

        await pipeline.ExecuteAsync(context);

        // Assert final remote set: In Review was preserved, not wiped!
        Assert.Contains(GitHubLabels.WorkflowInReview, remoteLabels);
        Assert.DoesNotContain(GitHubLabels.WorkflowComplete, remoteLabels);
    }

    [Fact]
    public async Task PullRequestPipeline_DelayedUnlabeledEvent_RemoteReblocked_PreservesWorkflowBlocked()
    {
        // Scenario 2: Remote is re-blocked (Workflow: Blocked). A delayed unlabeled event arrives.
        var remoteLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GitHubLabels.WorkflowBlocked };
        var gitHub = Substitute.For<IGitHubService>();

        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, false);

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var labelList = remoteLabels.Select(name =>
                {
                    var lbl = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
                    typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(lbl, name);
                    return lbl;
                }).ToList();
                typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, labelList);
                return Task.FromResult(freshPr);
            });

        gitHub.AddLabelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                foreach (var l in ci.Arg<string[]>()) remoteLabels.Add(l);
                return Task.CompletedTask;
            });

        gitHub.RemoveLabelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                remoteLabels.Remove(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var workflowModule = new WorkflowStateModule();
        var mutexEngine = new LabelMutexEngine();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var pipeline = new PullRequestPipeline(new[] { workflowModule }, mutexEngine, gitHub, logger);

        // Stale unlabeled webhook payload
        var unlabeledEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent));
        var blockedLabel = (Octokit.Webhooks.Models.Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.Label));
        typeof(Octokit.Webhooks.Models.Label).GetProperty(nameof(Octokit.Webhooks.Models.Label.Name))?.SetValue(blockedLabel, GitHubLabels.WorkflowBlocked);
        typeof(Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequest.PullRequestUnlabeledEvent.Label))?.SetValue(unlabeledEvent, blockedLabel);

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: false,
            title: "PR",
            body: null,
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "unlabeled",
            existingLabels: Array.Empty<string>(),
            prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Unlabeled,
            prEvent: unlabeledEvent);

        await pipeline.ExecuteAsync(context);

        // Assert final remote set: Blocked was preserved and In Review was not added!
        Assert.Contains(GitHubLabels.WorkflowBlocked, remoteLabels);
        Assert.DoesNotContain(GitHubLabels.WorkflowInReview, remoteLabels);
    }

    [Fact]
    public async Task PullRequestPipeline_DelayedLabeledEvent_RemoteUnblocked_PreservesActiveWorkflowLabels()
    {
        // Scenario 3: Remote was unblocked and is In Review. A delayed labeled(Blocked) event arrives.
        var remoteLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GitHubLabels.WorkflowInReview };
        var gitHub = Substitute.For<IGitHubService>();

        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, false);

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var labelList = remoteLabels.Select(name =>
                {
                    var lbl = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
                    typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(lbl, name);
                    return lbl;
                }).ToList();
                typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, labelList);
                return Task.FromResult(freshPr);
            });

        gitHub.AddLabelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                foreach (var l in ci.Arg<string[]>()) remoteLabels.Add(l);
                return Task.CompletedTask;
            });

        gitHub.RemoveLabelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                remoteLabels.Remove(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var workflowModule = new WorkflowStateModule();
        var mutexEngine = new LabelMutexEngine();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var pipeline = new PullRequestPipeline(new[] { workflowModule }, mutexEngine, gitHub, logger);

        // Stale labeled webhook payload
        var labeledEvent = (Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent));
        var blockedLabel = (Octokit.Webhooks.Models.Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.Webhooks.Models.Label));
        typeof(Octokit.Webhooks.Models.Label).GetProperty(nameof(Octokit.Webhooks.Models.Label.Name))?.SetValue(blockedLabel, GitHubLabels.WorkflowBlocked);
        typeof(Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent).GetProperty(nameof(Octokit.Webhooks.Events.PullRequest.PullRequestLabeledEvent.Label))?.SetValue(labeledEvent, blockedLabel);

        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: false,
            title: "PR",
            body: null,
            senderLogin: "contributor",
            authorLogin: "contributor",
            action: "labeled",
            existingLabels: new[] { GitHubLabels.WorkflowInReview },
            prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Labeled,
            prEvent: labeledEvent);

        await pipeline.ExecuteAsync(context);

        // Assert final remote set: In Review was preserved, not stripped!
        Assert.Contains(GitHubLabels.WorkflowInReview, remoteLabels);
        Assert.DoesNotContain(GitHubLabels.WorkflowBlocked, remoteLabels);
    }

    [Fact]
    public async Task PullRequestPipeline_MultiEvent_CiFileRemoved_ConvergesFinalRemoteLabels()
    {
        // Two consecutive pipeline executions:
        // Event 1: PR has .github/workflows/ci.yml -> adds CI.
        // Event 2: PR files changed, CI file removed -> removes CI.
        var remoteLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentFiles = new List<string> { ".github/workflows/ci.yml" };
        var gitHub = Substitute.For<IGitHubService>();

        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, false);

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var labelList = remoteLabels.Select(name =>
                {
                    var lbl = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
                    typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(lbl, name);
                    return lbl;
                }).ToList();
                typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, labelList);
                return Task.FromResult(freshPr);
            });

        gitHub.GetPullRequestFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                IReadOnlyList<Octokit.PullRequestFile> files = currentFiles.Select(f =>
                {
                    var prFile = (Octokit.PullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.PullRequestFile));
                    typeof(Octokit.PullRequestFile).GetProperty(nameof(Octokit.PullRequestFile.FileName))?.SetValue(prFile, f);
                    typeof(Octokit.PullRequestFile).GetProperty(nameof(Octokit.PullRequestFile.Status))?.SetValue(prFile, "modified");
                    return prFile;
                }).ToList();
                return Task.FromResult(files);
            });

        gitHub.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Name: test\nVersion: 1.0");

        gitHub.AddLabelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                foreach (var l in ci.Arg<string[]>()) remoteLabels.Add(l);
                return Task.CompletedTask;
            });

        gitHub.RemoveLabelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                remoteLabels.Remove(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var module = new BuildSystemAnalysisModule();
        var mutexEngine = new LabelMutexEngine();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, logger);

        // Run Event 1: Opened
        var ctx1 = CreateMockContext(gitHub, prNumber: 1, action: "opened", prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Opened);
        await pipeline.ExecuteAsync(ctx1);
        Assert.Contains(GitHubLabels.Ci, remoteLabels);

        // Run Event 2: Synchronize with CI file removed
        currentFiles.Clear();
        currentFiles.Add("src/main.c");
        var ctx2 = CreateMockContext(gitHub, prNumber: 1, action: "synchronize", prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Synchronize);
        await pipeline.ExecuteAsync(ctx2);

        // Final remote state: CI was cleanly removed
        Assert.DoesNotContain(GitHubLabels.Ci, remoteLabels);
    }

    [Fact]
    public async Task PullRequestPipeline_MultiEvent_CommunityEmailChangedToUnknown_ConvergesFinalRemoteLabels()
    {
        // Event 1: Author has community email -> adds Community: Contribution.
        // Event 2: Author email becomes unparseable/unknown -> removes Community: Contribution.
        var remoteLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? authorEmail = "alice@gmail.com";
        var gitHub = Substitute.For<IGitHubService>();

        var freshPr = (PullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PullRequest));
        typeof(PullRequest).GetProperty(nameof(PullRequest.State))?.SetValue(freshPr, new StringEnum<ItemState>(ItemState.Open));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Draft))?.SetValue(freshPr, false);

        gitHub.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var labelList = remoteLabels.Select(name =>
                {
                    var lbl = (Label)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Label));
                    typeof(Label).GetProperty(nameof(Label.Name))?.SetValue(lbl, name);
                    return lbl;
                }).ToList();
                typeof(PullRequest).GetProperty(nameof(PullRequest.Labels))?.SetValue(freshPr, labelList);
                return Task.FromResult(freshPr);
            });

        gitHub.GetPullRequestAuthorEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => authorEmail);

        gitHub.AddLabelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                foreach (var l in ci.Arg<string[]>()) remoteLabels.Add(l);
                return Task.CompletedTask;
            });

        gitHub.RemoveLabelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                remoteLabels.Remove(ci.ArgAt<string>(3));
                return Task.CompletedTask;
            });

        var module = new UserContributionModule();
        var mutexEngine = new LabelMutexEngine();
        var logger = Substitute.For<ILogger<PullRequestPipeline>>();
        var pipeline = new PullRequestPipeline(new[] { module }, mutexEngine, gitHub, logger);

        // Run Event 1: Opened with community email
        var ctx1 = CreateMockContext(gitHub, prNumber: 1, action: "opened", prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Opened);
        await pipeline.ExecuteAsync(ctx1);
        Assert.Contains(GitHubLabels.CommunityContribution, remoteLabels);

        // Run Event 2: Synchronize with unknown email
        authorEmail = "not-an-email";
        var ctx2 = CreateMockContext(gitHub, prNumber: 1, action: "synchronize", prAction: Octokit.Webhooks.Events.PullRequest.PullRequestAction.Synchronize);
        await pipeline.ExecuteAsync(ctx2);

        // Final remote state: Community: Contribution was cleanly removed
        Assert.DoesNotContain(GitHubLabels.CommunityContribution, remoteLabels);
    }

    #endregion

    #endregion

    #region Event and Context Tests
    [Fact]
    public void PullRequestContext_FromPullRequestEvent_InitializesProperties()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var ev = CreatePullRequestEvent();

        var context = PullRequestContext.FromPullRequestEvent(gitHub, "captains", null, ev, WebhookPullRequestAction.Opened);

        Assert.Equal("owner", context.Owner);
        Assert.Equal("repo", context.Repo);
        Assert.Equal(100, context.PrNumber);
        Assert.Equal(PullRequestEventType.PullRequest, context.EventType);
        Assert.Equal(WebhookPullRequestAction.Opened, context.PullRequestAction);
    }

    [Fact]
    public void PullRequestContext_FromReviewEvent_InitializesProperties()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var ev = CreateReviewEvent("reviewer");

        var context = PullRequestContext.FromReviewEvent(gitHub, "captains", null, ev, WebhookPullRequestReviewAction.Submitted);

        Assert.Equal("owner", context.Owner);
        Assert.Equal("repo", context.Repo);
        Assert.Equal(100, context.PrNumber);
        Assert.Equal(PullRequestEventType.Review, context.EventType);
        Assert.Equal(WebhookPullRequestReviewAction.Submitted, context.PullRequestReviewAction);
    }

    [Fact]
    public void PullRequestContext_FromIssueCommentEvent_InitializesProperties()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var ev = CreateCommentEvent("commenter", isPr: true);

        var context = PullRequestContext.FromIssueCommentEvent(gitHub, "captains", null, ev, WebhookIssueCommentAction.Created);

        Assert.Equal("owner", context.Owner);
        Assert.Equal("repo", context.Repo);
        Assert.Equal(100, context.PrNumber);
        Assert.Equal(PullRequestEventType.IssueComment, context.EventType);
        Assert.Equal(WebhookIssueCommentAction.Created, context.IssueCommentAction);
    }

    [Theory]
    [InlineData("author", "author", "User", true)]
    [InlineData("bot-user[bot]", "author", "Bot", true)]
    [InlineData("whitelisted-bot", "author", "User", true)]
    [InlineData("other-user", "author", "User", false)]
    [InlineData(null, "author", "User", false)]
    public void PullRequestContext_IsSenderAuthorOrBot_EvaluatesCorrectly(
        string? sender, string? author, string? senderType, bool expected)
    {
        var gitHub = Substitute.For<IGitHubService>();
        var context = new PullRequestContext(
            gitHubService: gitHub,
            teamName: "captains",
            headers: null,
            eventType: PullRequestEventType.PullRequest,
            owner: "owner",
            repo: "repo",
            prNumber: 1,
            headSha: "sha",
            baseBranch: "main",
            isDraft: false,
            isMerged: null,
            title: "PR",
            body: "",
            senderLogin: sender,
            authorLogin: author,
            action: string.Empty,
            existingLabels: Array.Empty<string>(),
            senderType: senderType);

        var isAuthorOrBot = context.IsSenderAuthorOrBot(new[] { "whitelisted-bot" });

        Assert.Equal(expected, isAuthorOrBot);
    }

    [Fact]
    public void BotDetectionHelper_WhenSenderLoginNullOrWhitespace_ReturnsFalse()
    {
        Assert.False(BotDetectionHelper.IsBot(null, "User"));
        Assert.False(BotDetectionHelper.IsBot("", "User"));
        Assert.False(BotDetectionHelper.IsBot("   ", "User"));
    }

    [Fact]
    public void PullRequestContext_FromPullRequestEvent_WithNullRepositoryAndHead_HandlesNullGracefully()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var ev = (WebhookPullRequestEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequestAssignedEvent));
        var pr = (WebhookPullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequest));
        typeof(WebhookPullRequestEvent).GetProperty(nameof(WebhookPullRequestEvent.PullRequest))?.SetValue(ev, pr);

        var context = PullRequestContext.FromPullRequestEvent(gitHub, "captains", null, ev, WebhookPullRequestAction.Opened);

        Assert.Equal(string.Empty, context.Owner);
        Assert.Equal(string.Empty, context.Repo);
        Assert.Equal(string.Empty, context.HeadSha);
    }

    [Fact]
    public void PullRequestContext_FromReviewEvent_WithNullRepositoryAndHead_HandlesNullGracefully()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var ev = (WebhookPullRequestReviewEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookPullRequestReviewSubmittedEvent));
        var pr = (WebhookSimplePullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookSimplePullRequest));
        typeof(WebhookPullRequestReviewEvent).GetProperty(nameof(WebhookPullRequestReviewEvent.PullRequest))?.SetValue(ev, pr);

        var context = PullRequestContext.FromReviewEvent(gitHub, "captains", null, ev, WebhookPullRequestReviewAction.Submitted);

        Assert.Equal(string.Empty, context.Owner);
        Assert.Equal(string.Empty, context.Repo);
        Assert.Equal(string.Empty, context.HeadSha);
    }

    [Fact]
    public void PullRequestContext_FromIssueCommentEvent_WithNullRepositoryAndUser_HandlesNullGracefully()
    {
        var gitHub = Substitute.For<IGitHubService>();
        var ev = (WebhookIssueCommentEvent)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookIssueCommentCreatedEvent));
        var issue = (WebhookIssue)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookIssue));
        typeof(WebhookIssueCommentEvent).GetProperty(nameof(WebhookIssueCommentEvent.Issue))?.SetValue(ev, issue);

        var context = PullRequestContext.FromIssueCommentEvent(gitHub, "captains", null, ev, WebhookIssueCommentAction.Created);

        Assert.Equal(string.Empty, context.Owner);
        Assert.Equal(string.Empty, context.Repo);
        Assert.Null(context.AuthorLogin);
    }

    #endregion

    #endregion
}
