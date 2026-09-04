using abaci_bot.Services;
using Microsoft.Extensions.Configuration;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using OctokitPullRequest = Octokit.PullRequest;
using OctokitPullRequestFile = Octokit.PullRequestFile;
using WebhookIssue = Octokit.Webhooks.Models.Issue;
using WebhookIssueCommentCreatedEvent = Octokit.Webhooks.Events.IssueComment.IssueCommentCreatedEvent;
using WebhookIssuePullRequest = Octokit.Webhooks.Models.IssuePullRequest;
using WebhookLabel = Octokit.Webhooks.Models.Label;
using WebhookPullRequest = Octokit.Webhooks.Models.PullRequestEvent.PullRequest;
using WebhookPullRequestHead = Octokit.Webhooks.Models.PullRequestEvent.PullRequestHead;
using WebhookPullRequestReviewEvent = Octokit.Webhooks.Events.PullRequestReviewEvent;
using WebhookPullRequestReviewSubmittedEvent = Octokit.Webhooks.Events.PullRequestReview.PullRequestReviewSubmittedEvent;
using WebhookRepository = Octokit.Webhooks.Models.Repository;
using WebhookSimplePullRequest = Octokit.Webhooks.Models.SimplePullRequest;
using WebhookUser = Octokit.Webhooks.Models.User;
using Xunit;

namespace abaci_bot.Tests;

public class GitHubWebhookProcessorTests
{
    private const string AiCheckboxLineChecked = "- [x] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.";
    private const string AiCheckboxLineUnchecked = "- [ ] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.";

    #region Existing Blocked / Review Flow Tests

    [Theory]
    [InlineData(true, GitHubLabels.WorkflowInReview)]
    [InlineData(false, GitHubLabels.WorkflowInReview)]
    [InlineData(true, GitHubLabels.WorkflowReadyForReview)]
    [InlineData(false, GitHubLabels.WorkflowReadyForReview)]
    [InlineData(true, GitHubLabels.CommitsUpdated)]
    [InlineData(false, GitHubLabels.CommitsUpdated)]
    public async Task NonBlockedLabelChangesDoNotRunBlockedWorkflow(bool isLabeled, string labelName)
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        PullRequestEvent payload = isLabeled
            ? LabeledEvent(labelName)
            : UnlabeledEvent(labelName);
        var action = isLabeled ? PullRequestAction.Labeled : PullRequestAction.Unlabeled;

        await processor.ProcessPullRequestAsync(payload, action);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task BlockedLabelAddedRemovesReviewWorkflowLabels()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessPullRequestAsync(LabeledEvent(GitHubLabels.WorkflowBlocked), PullRequestAction.Labeled);

        Assert.Equal(
            new[] { $"remove:{GitHubLabels.WorkflowInReview}", $"remove:{GitHubLabels.WorkflowReadyForReview}" },
            github.LabelOperations);
    }

    [Fact]
    public async Task BlockedLabelRemovedRestoresInReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessPullRequestAsync(UnlabeledEvent(GitHubLabels.WorkflowBlocked), PullRequestAction.Unlabeled);

        Assert.Equal(new[] { $"add:{GitHubLabels.WorkflowInReview}" }, github.LabelOperations);
    }

    [Fact]
    public async Task BlockedSynchronizeAddsCommitsUpdatedButDoesNotAddReadyForReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestSynchronizeEvent>(labels: new[] { GitHubLabels.WorkflowBlocked });

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Synchronize);

        Assert.Contains($"add:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
    }

    [Fact]
    public async Task AuthorCommentDoesNotRemoveCommitsUpdated()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessIssueCommentAsync(IssueCommentEvent(author: "contributor", sender: "contributor"), IssueCommentAction.Created);

        Assert.DoesNotContain($"remove:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
    }

    [Fact]
    public async Task CaptainCommentRemovesCommitsUpdated()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessIssueCommentAsync(IssueCommentEvent(author: "contributor", sender: "captain"), IssueCommentAction.Created);

        Assert.Contains($"remove:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
    }

    [Fact]
    public async Task AuthorCaptainCommentDoesNotRemoveCommitsUpdated()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessIssueCommentAsync(IssueCommentEvent(author: "captain", sender: "captain"), IssueCommentAction.Created);

        Assert.DoesNotContain($"remove:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
    }

    [Fact]
    public async Task AuthorReviewDoesNotRemoveCommitsUpdated()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessPullRequestReviewAsync(PullRequestReviewEvent(author: "contributor", sender: "contributor"), PullRequestReviewAction.Submitted);

        Assert.DoesNotContain($"remove:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
    }

    [Fact]
    public async Task CaptainReviewRemovesCommitsUpdated()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessPullRequestReviewAsync(PullRequestReviewEvent(author: "contributor", sender: "captain"), PullRequestReviewAction.Submitted);

        Assert.Contains($"remove:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
    }

    [Fact]
    public async Task AuthorCaptainReviewDoesNotRemoveCommitsUpdated()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);

        await processor.ProcessPullRequestReviewAsync(PullRequestReviewEvent(author: "captain", sender: "captain"), PullRequestReviewAction.Submitted);

        Assert.DoesNotContain($"remove:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
    }

    #endregion

    #region WorkflowStateModule: PR Lifecycle & State Transitions

    [Fact]
    public async Task OpenedNonDraftPrAddsReadyForReviewAndRemovesInDev()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(draft: false, title: "Feature: Add RISC-V support");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"add:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
    }

    [Fact]
    public async Task OpenedDraftPrAddsInDevAndRemovesReadyForReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(draft: true, title: "Feature: Under construction");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"add:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
    }

    [Fact]
    public async Task OpenedWipTitlePrAddsInDevAndRemovesReadyForReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(draft: false, title: "wip: not ready yet");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"add:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
    }

    [Fact]
    public async Task OpenedNonDraftPrWithExistingInReviewDoesNotAddReadyForReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(
            draft: false,
            title: "Normal PR",
            labels: new[] { GitHubLabels.WorkflowInReview });

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"remove:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
    }

    [Fact]
    public async Task ReopenedActionRunsWorkflowEvaluation()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(draft: false, title: "Ready PR");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Reopened);

        Assert.Contains($"add:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
    }

    [Fact]
    public async Task ConvertedToDraftActionRunsWorkflowEvaluation()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(draft: true, title: "Draft PR");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.ConvertedToDraft);

        Assert.Contains($"add:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
    }

    [Fact]
    public async Task ReadyForReviewActionRunsWorkflowEvaluation()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(draft: false, title: "Ready PR");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.ReadyForReview);

        Assert.Contains($"add:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
    }

    [Fact]
    public async Task OpenedBlockedPrReturnsEarlyWithoutWorkflowLabelChange()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(
            draft: false,
            title: "Blocked PR",
            labels: new[] { GitHubLabels.WorkflowBlocked });

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
    }

    [Fact]
    public async Task EditedPrOnlyAnalyzesDescription()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestEditedEvent>(body: AiCheckboxLineChecked);

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Edited);

        Assert.Equal(new[] { $"add:{GitHubLabels.AiAssistance}" }, github.LabelOperations);
    }

    [Fact]
    public async Task ClosedMergedPrAddsWorkflowCompleteAndRemovesActiveWorkflowLabels()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestClosedEvent>(merged: true);

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Closed);

        Assert.Contains($"add:{GitHubLabels.WorkflowComplete}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task ClosedUnmergedPrRemovesActiveWorkflowLabelsWithoutAddingComplete()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestClosedEvent>(merged: false);

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Closed);

        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowComplete}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowInDev}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
        Assert.Contains($"remove:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task UnhandledPrActionDoesNothing()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestAssignedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Assigned);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task MissingRepositoryContextReturnsEarly()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var pullRequest = PullRequest();

        // 1. Repository is null
        var payload1 = Create<PullRequestOpenedEvent>(
            (nameof(PullRequestEvent.Number), pullRequest.Number),
            (nameof(PullRequestEvent.PullRequest), pullRequest),
            (nameof(PullRequestEvent.Repository), null),
            (nameof(PullRequestEvent.Sender), User("contributor")));

        await processor.ProcessPullRequestAsync(payload1, PullRequestAction.Opened);

        // 2. Repository owner or name is empty
        var emptyRepo = Create<WebhookRepository>(
            (nameof(WebhookRepository.Name), ""),
            (nameof(WebhookRepository.Owner), User("")));
        var payload2 = Create<PullRequestOpenedEvent>(
            (nameof(PullRequestEvent.Number), pullRequest.Number),
            (nameof(PullRequestEvent.PullRequest), pullRequest),
            (nameof(PullRequestEvent.Repository), emptyRepo),
            (nameof(PullRequestEvent.Sender), User("contributor")));

        await processor.ProcessPullRequestAsync(payload2, PullRequestAction.Opened);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task AuthorEmailExceptionContinuesWithoutThrowing()
    {
        var github = new FakeGitHubService { ThrowOnGetAuthorEmail = true };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        var ex = await Record.ExceptionAsync(async () => await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened));

        Assert.Null(ex);
        Assert.Contains($"add:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
    }

    #endregion

    #region WorkflowStateModule: Reviews & Issue Comments

    [Fact]
    public async Task ReviewSubmittedByCaptainOnNonBlockedPrTransitionsToInReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = PullRequestReviewEvent(author: "contributor", sender: "captain");

        await processor.ProcessPullRequestReviewAsync(payload, PullRequestReviewAction.Submitted);

        Assert.Contains($"remove:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
        Assert.Contains($"add:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task ReviewSubmittedByCaptainOnBlockedPrDoesNotTransitionToInReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = PullRequestReviewEvent(
            author: "contributor",
            sender: "captain",
            labels: new[] { GitHubLabels.WorkflowBlocked });

        await processor.ProcessPullRequestReviewAsync(payload, PullRequestReviewAction.Submitted);

        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task ReviewSubmittedByNonCaptainDoesNotTransitionToInReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = PullRequestReviewEvent(author: "contributor", sender: "other_user");

        await processor.ProcessPullRequestReviewAsync(payload, PullRequestReviewAction.Submitted);

        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task ReviewNonSubmittedActionDoesNothing()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = PullRequestReviewEvent(author: "contributor", sender: "captain");

        await processor.ProcessPullRequestReviewAsync(payload, PullRequestReviewAction.Dismissed);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task ReviewMissingRepoContextOrSenderReturnsEarly()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var pullRequest = Create<WebhookSimplePullRequest>((nameof(WebhookSimplePullRequest.Number), 1L));

        var noRepoPayload = Create<WebhookPullRequestReviewSubmittedEvent>(
            (nameof(WebhookPullRequestReviewSubmittedEvent.PullRequest), pullRequest),
            (nameof(WebhookPullRequestReviewSubmittedEvent.Sender), User("captain")));

        await processor.ProcessPullRequestReviewAsync(noRepoPayload, PullRequestReviewAction.Submitted);

        var noSenderPayload = Create<WebhookPullRequestReviewSubmittedEvent>(
            (nameof(WebhookPullRequestReviewSubmittedEvent.PullRequest), pullRequest),
            (nameof(WebhookPullRequestReviewSubmittedEvent.Repository), Repository()));

        await processor.ProcessPullRequestReviewAsync(noSenderPayload, PullRequestReviewAction.Submitted);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentOnRegularIssueWithoutPullRequestDoesNothing()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var issue = Create<WebhookIssue>(
            (nameof(WebhookIssue.Number), 10L),
            (nameof(WebhookIssue.Title), "Just an issue"),
            (nameof(WebhookIssue.PullRequest), null));

        var payload = Create<WebhookIssueCommentCreatedEvent>(
            (nameof(WebhookIssueCommentCreatedEvent.Issue), issue),
            (nameof(WebhookIssueCommentCreatedEvent.Repository), Repository()),
            (nameof(WebhookIssueCommentCreatedEvent.Sender), User("captain")));

        await processor.ProcessIssueCommentAsync(payload, IssueCommentAction.Created);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentNonCreatedActionDoesNothing()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = IssueCommentEvent(author: "contributor", sender: "captain");

        await processor.ProcessIssueCommentAsync(payload, IssueCommentAction.Edited);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentMissingRepoOrSenderReturnsEarly()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var issue = Create<WebhookIssue>(
            (nameof(WebhookIssue.Number), 1L),
            (nameof(WebhookIssue.PullRequest), Create<WebhookIssuePullRequest>()));

        var noRepoPayload = Create<WebhookIssueCommentCreatedEvent>(
            (nameof(WebhookIssueCommentCreatedEvent.Issue), issue),
            (nameof(WebhookIssueCommentCreatedEvent.Sender), User("captain")));

        await processor.ProcessIssueCommentAsync(noRepoPayload, IssueCommentAction.Created);

        var noSenderPayload = Create<WebhookIssueCommentCreatedEvent>(
            (nameof(WebhookIssueCommentCreatedEvent.Issue), issue),
            (nameof(WebhookIssueCommentCreatedEvent.Repository), Repository()));

        await processor.ProcessIssueCommentAsync(noSenderPayload, IssueCommentAction.Created);

        Assert.Empty(github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentByCaptainOnPRTransitionsToInReviewWhenEligible()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = IssueCommentEvent(author: "contributor", sender: "captain", title: "Clean PR");

        await processor.ProcessIssueCommentAsync(payload, IssueCommentAction.Created);

        Assert.Contains($"remove:{GitHubLabels.WorkflowReadyForReview}", github.LabelOperations);
        Assert.Contains($"add:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentByCaptainOnBlockedPrDoesNotTransitionToInReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = IssueCommentEvent(author: "contributor", sender: "captain", labels: new[] { GitHubLabels.WorkflowBlocked });

        await processor.ProcessIssueCommentAsync(payload, IssueCommentAction.Created);

        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentByCaptainOnDraftPrDoesNotTransitionToInReview()
    {
        var draftPr = (OctokitPullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(OctokitPullRequest));
        typeof(OctokitPullRequest).GetProperty(nameof(OctokitPullRequest.Draft))?.SetValue(draftPr, true);

        var github = new FakeGitHubService
        {
            PullRequest = draftPr
        };
        var processor = CreateProcessor(github);
        var payload = IssueCommentEvent(author: "contributor", sender: "captain", draft: true);

        await processor.ProcessIssueCommentAsync(payload, IssueCommentAction.Created);

        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentByCaptainOnWipPrDoesNotTransitionToInReview()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = IssueCommentEvent(author: "contributor", sender: "captain", title: "WIP: Not done yet");

        await processor.ProcessIssueCommentAsync(payload, IssueCommentAction.Created);

        Assert.DoesNotContain($"add:{GitHubLabels.WorkflowInReview}", github.LabelOperations);
    }

    [Fact]
    public async Task IssueCommentAuthorFallbackFromPullRequestWhenIssueUserNull()
    {
        var github = new FakeGitHubService
        {
            PullRequest = CreateOctokitPullRequestWithUser("pr_author")
        };
        var processor = CreateProcessor(github);
        var payload = IssueCommentEvent(author: null, sender: "captain");

        await processor.ProcessIssueCommentAsync(payload, IssueCommentAction.Created);

        Assert.Contains($"remove:{GitHubLabels.CommitsUpdated}", github.LabelOperations);
    }

    #endregion

    #region UserContributionModule: Email Domain Recognition

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-email")]
    [InlineData("user@")]
    public async Task UserEmailNullOrMalformedDoesNotAddContributionLabel(string? email)
    {
        var github = new FakeGitHubService { AuthorEmail = email };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.DoesNotContain(github.LabelOperations, op => op.Contains("Community:"));
    }

    [Theory]
    [InlineData("staff@iscas.ac.cn")]
    [InlineData("Leader@ISCAS.AC.CN")]
    public async Task UserEmailIscasStaffIsExcludedFromCommunityLabels(string staffEmail)
    {
        var github = new FakeGitHubService { AuthorEmail = staffEmail };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.DoesNotContain(github.LabelOperations, op => op.Contains("Community:"));
    }

    [Theory]
    [InlineData("alice.oerv@isrc.iscas.ac.cn")]
    [InlineData("bob.or@isrc.iscas.ac.cn")]
    [InlineData("charlie.riscv@isrc.iscas.ac.cn")]
    [InlineData("DAVID.RISCV@ISRC.ISCAS.AC.CN")]
    public async Task UserEmailInternSubdomainsAddStudentContributionLabel(string internEmail)
    {
        var github = new FakeGitHubService { AuthorEmail = internEmail };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains("add:Community: Student contribution", github.LabelOperations);
    }

    [Theory]
    [InlineData("developer@gmail.com")]
    [InlineData("contributor@163.com")]
    [InlineData("student@pku.edu.cn")]
    public async Task UserEmailOtherDomainsAddCommunityContributionLabel(string communityEmail)
    {
        var github = new FakeGitHubService { AuthorEmail = communityEmail };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains("add:Community: Contribution", github.LabelOperations);
    }

    #endregion

    #region BuildSystemAnalysisModule: RPM Spec & File Analysis

    [Fact]
    public async Task FileAnalysisGithubDirectoryAddsCILabel()
    {
        var github = new FakeGitHubService
        {
            PullRequestFiles = new[] { CreateOctokitFile(".github/workflows/ci.yml") }
        };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains("add:CI", github.LabelOperations);
    }

    [Fact]
    public async Task FileAnalysisSpecsDirectoryAddsTargetRollingLabel()
    {
        var github = new FakeGitHubService
        {
            PullRequestFiles = new[] { CreateOctokitFile("SPECS/package.spec") }
        };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains("add:Target: Rolling", github.LabelOperations);
    }

    [Fact]
    public async Task FileAnalysisRemovedFileDoesNotFetchContent()
    {
        var github = new FakeGitHubService
        {
            PullRequestFiles = new[] { CreateOctokitFile("SPECS/deleted.spec", status: "removed") }
        };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        // Does not fetch content for removed file, but adds Target: Rolling because it starts with SPECS/
        Assert.Contains("add:Target: Rolling", github.LabelOperations);
        Assert.DoesNotContain(github.LabelOperations, op => op.Contains("BuildSystem:"));
    }

    [Fact]
    public async Task FileAnalysisNonSpecFileDoesNotCheckBuildSystem()
    {
        var github = new FakeGitHubService
        {
            PullRequestFiles = new[] { CreateOctokitFile("docs/README.md") }
        };
        github.FileContents["docs/README.md"] = "BuildSystem: autotools";
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.DoesNotContain(github.LabelOperations, op => op.Contains("BuildSystem:"));
    }

    [Theory]
    [InlineData("BuildSystem:    autotools\nName: test", "BuildSystem: autotools")]
    [InlineData("buildsystem:    cmake\nName: test", "BuildSystem: cmake")]
    [InlineData("BuildSystem:    golangmodule\nName: test", "BuildSystem: golangmodule")]
    [InlineData("BuildSystem:    golang\nName: test", "BuildSystem: golang")]
    [InlineData("BuildSystem:    rustcrate\nName: test", "BuildSystem: rustcrate")]
    [InlineData("BuildSystem:    rust\nName: test", "BuildSystem: rust")]
    [InlineData("BuildSystem:    meson\nName: test", "BuildSystem: meson")]
    [InlineData("BuildSystem:    pyproject\nName: test", "BuildSystem: pyproject")]
    [InlineData("Name: standard-package\nVersion: 1.0", "BuildSystem: misc")]
    public async Task FileAnalysisSpecBuildSystemsRecognized(string specContent, string expectedLabel)
    {
        var filePath = "mypkg.spec";
        var github = new FakeGitHubService
        {
            PullRequestFiles = new[] { CreateOctokitFile(filePath) }
        };
        github.FileContents[filePath] = specContent;
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"add:{expectedLabel}", github.LabelOperations);
    }

    [Fact]
    public async Task FileAnalysisNoMatchingLabelsDoesNotCallAddLabels()
    {
        var github = new FakeGitHubService
        {
            PullRequestFiles = new[] { CreateOctokitFile("src/main.c") }
        };
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>();

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.DoesNotContain(github.LabelOperations, op => op.Contains("CI") || op.Contains("Target: Rolling") || op.Contains("BuildSystem:"));
    }

    #endregion

    #region AiAssistanceModule: Checkbox Synchronization

    [Fact]
    public async Task AiAssistanceCheckedWithLowercaseXAddsLabel()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(body: $"Introduction\n{AiCheckboxLineChecked}\nDetails");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"add:{GitHubLabels.AiAssistance}", github.LabelOperations);
        Assert.DoesNotContain($"remove:{GitHubLabels.AiAssistance}", github.LabelOperations);
    }

    [Fact]
    public async Task AiAssistanceCheckedWithUppercaseXAddsLabel()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var uppercaseCheckbox = "- [X] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.";
        var payload = CreatePREvent<PullRequestOpenedEvent>(body: uppercaseCheckbox);

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"add:{GitHubLabels.AiAssistance}", github.LabelOperations);
    }

    [Fact]
    public async Task AiAssistanceUncheckedRemovesLabel()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(body: AiCheckboxLineUnchecked);

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"remove:{GitHubLabels.AiAssistance}", github.LabelOperations);
        Assert.DoesNotContain($"add:{GitHubLabels.AiAssistance}", github.LabelOperations);
    }

    [Fact]
    public async Task AiAssistanceMissingCheckboxRemovesLabel()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(body: "This is a regular description without checkboxes.");

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"remove:{GitHubLabels.AiAssistance}", github.LabelOperations);
    }

    [Fact]
    public async Task AiAssistanceNullBodyRemovesLabel()
    {
        var github = new FakeGitHubService();
        var processor = CreateProcessor(github);
        var payload = CreatePREvent<PullRequestOpenedEvent>(body: null);

        await processor.ProcessPullRequestAsync(payload, PullRequestAction.Opened);

        Assert.Contains($"remove:{GitHubLabels.AiAssistance}", github.LabelOperations);
    }

    #endregion

    #region Helper & Edge Case Tests

    [Fact]
    public void TestLabelNamesHelperHandlesNullAndWhitespace()
    {
        var labels = new[]
        {
            Label("valid"),
            Label(""),
            Label("   "),
            (WebhookLabel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookLabel))
        };

        var set = GitHubWebhookProcessor.GetLabelNames(labels);
        Assert.Single(set);
        Assert.Contains("valid", set);

        var nullSet = GitHubWebhookProcessor.GetLabelNames(null);
        Assert.Empty(nullSet);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("user@example.com", false)]
    [InlineData("student.oerv@isrc.iscas.ac.cn", true)]
    [InlineData("student.or@isrc.iscas.ac.cn", true)]
    [InlineData("student.riscv@isrc.iscas.ac.cn", true)]
    public void TestIsInternEmail(string? email, bool expected)
    {
        Assert.Equal(expected, GitHubWebhookProcessor.IsInternEmail(email));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("no-at-sign", null)]
    [InlineData("trailing-at@", null)]
    [InlineData("alice@example.com", "example.com")]
    [InlineData("bob@iscas.ac.cn", "iscas.ac.cn")]
    public void TestExtractEmailDomain(string? email, string? expected)
    {
        Assert.Equal(expected, GitHubWebhookProcessor.ExtractEmailDomain(email));
    }

    [Theory]
    [InlineData(null, "alice", false)]
    [InlineData("alice", null, false)]
    [InlineData("", "", false)]
    [InlineData("   ", "   ", false)]
    [InlineData("alice", "bob", false)]
    [InlineData("Alice", "alice", true)]
    public void TestIsSameUser(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, GitHubWebhookProcessor.IsSameUser(left, right));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Other: Label", false)]
    [InlineData("Workflow: Blocked", true)]
    [InlineData("workflow: blocked", true)]
    public void TestIsBlockedLabel(string? label, bool expected)
    {
        Assert.Equal(expected, GitHubWebhookProcessor.IsBlockedLabel(label));
    }

    [Fact]
    public void TestTryGetSender()
    {
        Assert.False(GitHubWebhookProcessor.TryGetSender(null, out var login1));
        Assert.Empty(login1);

        var emptyUser = (WebhookUser)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WebhookUser));
        Assert.False(GitHubWebhookProcessor.TryGetSender(emptyUser, out var login2));
        Assert.Empty(login2);

        var validUser = User("CaptainUser");
        Assert.True(GitHubWebhookProcessor.TryGetSender(validUser, out var login3));
        Assert.Equal("captainuser", login3);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Regular PR body", false)]
    [InlineData("- [ ] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.", false)]
    [InlineData("- [x] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.", true)]
    [InlineData("- [X] I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.", true)]
    public void TestIsAiAssistedPullRequest(string? body, bool expected)
    {
        Assert.Equal(expected, GitHubWebhookProcessor.IsAiAssistedPullRequest(body));
    }

    #endregion

    #region Test Factories & Helpers

    private static TestGitHubWebhookProcessor CreateProcessor(FakeGitHubService github)
    {
        var config = new ConfigurationManager
        {
            ["GitHubApp:TeamName"] = "captains"
        };

        return new TestGitHubWebhookProcessor(github, config);
    }

    private static PullRequestLabeledEvent LabeledEvent(string labelName)
    {
        var pullRequest = PullRequest();
        return Create<PullRequestLabeledEvent>(
            (nameof(PullRequestLabeledEvent.Number), pullRequest.Number),
            (nameof(PullRequestLabeledEvent.PullRequest), pullRequest),
            (nameof(PullRequestLabeledEvent.Repository), Repository()),
            (nameof(PullRequestLabeledEvent.Sender), User("maintainer")),
            (nameof(PullRequestLabeledEvent.Label), Label(labelName)));
    }

    private static PullRequestUnlabeledEvent UnlabeledEvent(string labelName)
    {
        var pullRequest = PullRequest();
        return Create<PullRequestUnlabeledEvent>(
            (nameof(PullRequestUnlabeledEvent.Number), pullRequest.Number),
            (nameof(PullRequestUnlabeledEvent.PullRequest), pullRequest),
            (nameof(PullRequestUnlabeledEvent.Repository), Repository()),
            (nameof(PullRequestUnlabeledEvent.Sender), User("maintainer")),
            (nameof(PullRequestUnlabeledEvent.Label), Label(labelName)));
    }

    private static T CreatePREvent<T>(
        string title = "Ready PR",
        string? body = "",
        bool draft = false,
        bool? merged = null,
        IReadOnlyList<string>? labels = null,
        WebhookRepository? repository = null)
        where T : PullRequestEvent
    {
        var repo = repository ?? Repository();
        var pullRequest = PullRequest(labels: labels, title: title, body: body, draft: draft, merged: merged);
        return Create<T>(
            (nameof(PullRequestEvent.Number), pullRequest.Number),
            (nameof(PullRequestEvent.PullRequest), pullRequest),
            (nameof(PullRequestEvent.Repository), repo),
            (nameof(PullRequestEvent.Sender), User("contributor")));
    }

    private static WebhookIssueCommentCreatedEvent IssueCommentEvent(
        string? author,
        string sender,
        string title = "Ready PR",
        bool draft = false,
        IReadOnlyList<string>? labels = null)
    {
        var user = author != null ? User(author) : null;
        var issue = Create<WebhookIssue>(
            (nameof(WebhookIssue.Number), 1L),
            (nameof(WebhookIssue.Title), title),
            (nameof(WebhookIssue.User), user),
            (nameof(WebhookIssue.PullRequest), Create<WebhookIssuePullRequest>()),
            (nameof(WebhookIssue.Labels), Labels(labels)));

        return Create<WebhookIssueCommentCreatedEvent>(
            (nameof(WebhookIssueCommentCreatedEvent.Issue), issue),
            (nameof(WebhookIssueCommentCreatedEvent.Repository), Repository()),
            (nameof(WebhookIssueCommentCreatedEvent.Sender), User(sender)));
    }

    private static WebhookPullRequestReviewEvent PullRequestReviewEvent(string author, string sender, IReadOnlyList<string>? labels = null)
    {
        var pullRequest = Create<WebhookSimplePullRequest>(
            (nameof(WebhookSimplePullRequest.Number), 1L),
            (nameof(WebhookSimplePullRequest.Title), "Ready PR"),
            (nameof(WebhookSimplePullRequest.User), User(author)),
            (nameof(WebhookSimplePullRequest.Labels), Labels(labels)));

        return Create<WebhookPullRequestReviewSubmittedEvent>(
            (nameof(WebhookPullRequestReviewSubmittedEvent.PullRequest), pullRequest),
            (nameof(WebhookPullRequestReviewSubmittedEvent.Repository), Repository()),
            (nameof(WebhookPullRequestReviewSubmittedEvent.Sender), User(sender)));
    }

    private static WebhookPullRequest PullRequest(
        IReadOnlyList<string>? labels = null,
        string title = "Ready PR",
        string? body = "",
        bool draft = false,
        bool? merged = null)
    {
        var head = Create<WebhookPullRequestHead>(
            (nameof(WebhookPullRequestHead.Sha), "head-sha"));

        return Create<WebhookPullRequest>(
            (nameof(WebhookPullRequest.Number), 1L),
            (nameof(WebhookPullRequest.Title), title),
            (nameof(WebhookPullRequest.Body), body),
            (nameof(WebhookPullRequest.Draft), draft),
            (nameof(WebhookPullRequest.Merged), merged),
            (nameof(WebhookPullRequest.Head), head),
            (nameof(WebhookPullRequest.User), User("contributor")),
            (nameof(WebhookPullRequest.Labels), Labels(labels)));
    }

    private static WebhookRepository Repository()
    {
        return Create<WebhookRepository>(
            (nameof(WebhookRepository.Name), "repo"),
            (nameof(WebhookRepository.Owner), User("owner")));
    }

    private static WebhookUser User(string login)
    {
        return Create<WebhookUser>(
            (nameof(WebhookUser.Login), login));
    }

    private static IReadOnlyList<WebhookLabel> Labels(IReadOnlyList<string>? names)
    {
        return names?.Select(Label).ToArray() ?? Array.Empty<WebhookLabel>();
    }

    private static WebhookLabel Label(string name)
    {
        return Create<WebhookLabel>(
            (nameof(WebhookLabel.Name), name));
    }

    private static OctokitPullRequestFile CreateOctokitFile(string filename, string status = "modified")
    {
        var file = (OctokitPullRequestFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(OctokitPullRequestFile));
        typeof(OctokitPullRequestFile).GetProperty(nameof(OctokitPullRequestFile.FileName))?.SetValue(file, filename);
        typeof(OctokitPullRequestFile).GetProperty(nameof(OctokitPullRequestFile.Status))?.SetValue(file, status);
        return file;
    }

    private static OctokitPullRequest CreateOctokitPullRequestWithUser(string login)
    {
        var user = (Octokit.User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Octokit.User));
        typeof(Octokit.User).GetProperty(nameof(Octokit.User.Login))?.SetValue(user, login);

        var pr = (OctokitPullRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(OctokitPullRequest));
        typeof(OctokitPullRequest).GetProperty(nameof(OctokitPullRequest.User))?.SetValue(pr, user);
        return pr;
    }

    private static T Create<T>(params (string PropertyName, object? Value)[] properties)
        where T : class
    {
        var instance = (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

        foreach (var (propertyName, value) in properties)
        {
            typeof(T).GetProperty(propertyName)!.SetValue(instance, value);
        }

        return instance;
    }

    private sealed class TestGitHubWebhookProcessor : GitHubWebhookProcessor
    {
        public TestGitHubWebhookProcessor(IGitHubService github, IConfiguration config)
            : base(github, config)
        {
        }

        public ValueTask ProcessPullRequestAsync(PullRequestEvent payload, PullRequestAction action)
        {
            return ProcessPullRequestWebhookAsync(null!, payload, action);
        }

        public ValueTask ProcessIssueCommentAsync(IssueCommentEvent payload, IssueCommentAction action)
        {
            return ProcessIssueCommentWebhookAsync(null!, payload, action);
        }

        public ValueTask ProcessPullRequestReviewAsync(WebhookPullRequestReviewEvent payload, PullRequestReviewAction action)
        {
            return ProcessPullRequestReviewWebhookAsync(null!, payload, action);
        }
    }

    private sealed class FakeGitHubService : IGitHubService
    {
        public List<string> LabelOperations { get; } = new();

        public OctokitPullRequest PullRequest { get; set; } = new();

        public HashSet<string> TeamMembers { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            "captain"
        };

        public IReadOnlyList<OctokitPullRequestFile> PullRequestFiles { get; set; } = Array.Empty<OctokitPullRequestFile>();

        public string? AuthorEmail { get; set; }

        public bool ThrowOnGetAuthorEmail { get; set; }

        public Dictionary<string, string> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<OctokitPullRequestFile>> GetPullRequestFilesAsync(
            string owner, string repo, int prNumber)
        {
            return Task.FromResult(PullRequestFiles);
        }

        public Task<string?> GetPullRequestAuthorEmailAsync(
            string owner, string repo, int prNumber)
        {
            if (ThrowOnGetAuthorEmail)
                throw new InvalidOperationException("Simulated GitHub API error");
            return Task.FromResult(AuthorEmail);
        }

        public Task<string> GetFileContentAsync(
            string owner, string repo, string path, string sha)
        {
            if (FileContents.TryGetValue(path, out var content))
                return Task.FromResult(content);
            return Task.FromResult("");
        }

        public Task AddLabelsAsync(
            string owner, string repo, int issueNumber, params string[] labels)
        {
            LabelOperations.AddRange(labels.Select(label => $"add:{label}"));
            return Task.CompletedTask;
        }

        public Task RemoveLabelAsync(
            string owner, string repo, int issueNumber, string label)
        {
            LabelOperations.Add($"remove:{label}");
            return Task.CompletedTask;
        }

        public Task<HashSet<string>> GetTeamMembersAsync(
            string owner, string teamName)
        {
            return Task.FromResult(TeamMembers);
        }

        public Task<OctokitPullRequest> GetPullRequestAsync(
            string owner, string repo, int prNumber)
        {
            return Task.FromResult(PullRequest);
        }
    }

    #endregion
}
