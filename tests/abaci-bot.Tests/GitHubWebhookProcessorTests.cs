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

namespace abaci_bot.Tests;

public class GitHubWebhookProcessorTests
{
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
        var payload = PullRequestEvent(PullRequestAction.Synchronize, labels: new[] { GitHubLabels.WorkflowBlocked });

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

    private static PullRequestSynchronizeEvent PullRequestEvent(
        PullRequestAction action,
        IReadOnlyList<string>? labels = null)
    {
        var pullRequest = PullRequest(labels: labels);
        return Create<PullRequestSynchronizeEvent>(
            (nameof(PullRequestSynchronizeEvent.Number), pullRequest.Number),
            (nameof(PullRequestSynchronizeEvent.PullRequest), pullRequest),
            (nameof(PullRequestSynchronizeEvent.Repository), Repository()),
            (nameof(PullRequestSynchronizeEvent.Sender), User("contributor")));
    }

    private static WebhookIssueCommentCreatedEvent IssueCommentEvent(string author, string sender, IReadOnlyList<string>? labels = null)
    {
        var issue = Create<WebhookIssue>(
            (nameof(WebhookIssue.Number), 1L),
            (nameof(WebhookIssue.Title), "Ready PR"),
            (nameof(WebhookIssue.User), User(author)),
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

    private static WebhookPullRequest PullRequest(IReadOnlyList<string>? labels = null)
    {
        var head = Create<WebhookPullRequestHead>(
            (nameof(WebhookPullRequestHead.Sha), "head-sha"));

        return Create<WebhookPullRequest>(
            (nameof(WebhookPullRequest.Number), 1L),
            (nameof(WebhookPullRequest.Title), "Ready PR"),
            (nameof(WebhookPullRequest.Body), ""),
            (nameof(WebhookPullRequest.Draft), false),
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

        public Task<IReadOnlyList<OctokitPullRequestFile>> GetPullRequestFilesAsync(
            string owner, string repo, int prNumber)
        {
            return Task.FromResult<IReadOnlyList<OctokitPullRequestFile>>(Array.Empty<OctokitPullRequestFile>());
        }

        public Task<string?> GetPullRequestAuthorEmailAsync(
            string owner, string repo, int prNumber)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string> GetFileContentAsync(
            string owner, string repo, string path, string sha)
        {
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
}
