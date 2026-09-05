using System.Collections.Concurrent;
using abaci_bot.Services;
using Octokit;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using WebhookPullRequestReviewEvent = Octokit.Webhooks.Events.PullRequestReviewEvent;

namespace abaci_bot.Contexts;

public enum PullRequestEventType
{
    PullRequest,
    Review,
    IssueComment
}

public class PullRequestContext
{
    private readonly IGitHubService _gitHubService;
    private readonly ConcurrentDictionary<string, Task<string>> _fileContentCache = new(StringComparer.OrdinalIgnoreCase);

    public WebhookHeaders? Headers { get; }
    public PullRequestEventType EventType { get; }
    public string Owner { get; }
    public string Repo { get; }
    public int PrNumber { get; }
    public string HeadSha { get; }
    public string? BaseBranch { get; }
    public bool IsDraft { get; }
    public bool? IsMerged { get; }
    public string Title { get; }
    public string? Body { get; }
    public string? SenderLogin { get; }
    public string? AuthorLogin { get; }
    public string Action { get; }

    // 强类型 Webhook 动作
    public PullRequestAction? PullRequestAction { get; }
    public PullRequestReviewAction? PullRequestReviewAction { get; }
    public IssueCommentAction? IssueCommentAction { get; }

    // 原生事件对象弱引用/备查
    public PullRequestEvent? PullRequestEvent { get; }
    public WebhookPullRequestReviewEvent? PullRequestReviewEvent { get; }
    public IssueCommentEvent? IssueCommentEvent { get; }

    // 懒加载只读资源（单请求内仅调用一次 GitHub API）
    public Lazy<Task<IReadOnlyList<PullRequestFile>>> ChangedFiles { get; }
    public Lazy<Task<string?>> AuthorEmail { get; }
    public Lazy<Task<HashSet<string>>> TeamCaptains { get; }
    public Lazy<Task<PullRequest>> PullRequest { get; }

    // 标签与状态集合
    public HashSet<string> ExistingLabels { get; }
    public bool IsBlocked => ExistingLabels.Contains(GitHubLabels.WorkflowBlocked);

    // 意图收集容器（模块只写意图，末端统一执行原子互斥与批量写回）
    public HashSet<string> LabelsToAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> LabelsToRemove { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PendingComments { get; } = new();

    // 跨模块上下文共享字典
    public ConcurrentDictionary<string, object?> Items { get; } = new();

    public PullRequestContext(
        IGitHubService gitHubService,
        string teamName,
        WebhookHeaders? headers,
        PullRequestEventType eventType,
        string owner,
        string repo,
        int prNumber,
        string headSha,
        string? baseBranch,
        bool isDraft,
        bool? isMerged,
        string title,
        string? body,
        string? senderLogin,
        string? authorLogin,
        string action,
        IEnumerable<string> existingLabels,
        PullRequestAction? prAction = null,
        PullRequestReviewAction? reviewAction = null,
        IssueCommentAction? commentAction = null,
        PullRequestEvent? prEvent = null,
        WebhookPullRequestReviewEvent? reviewEvent = null,
        IssueCommentEvent? commentEvent = null)
    {
        _gitHubService = gitHubService;
        Headers = headers;
        EventType = eventType;
        Owner = owner;
        Repo = repo;
        PrNumber = prNumber;
        HeadSha = headSha;
        BaseBranch = baseBranch;
        IsDraft = isDraft;
        IsMerged = isMerged;
        Title = title;
        Body = body;
        SenderLogin = senderLogin;
        AuthorLogin = authorLogin;
        Action = action;
        PullRequestAction = prAction;
        PullRequestReviewAction = reviewAction;
        IssueCommentAction = commentAction;
        PullRequestEvent = prEvent;
        PullRequestReviewEvent = reviewEvent;
        IssueCommentEvent = commentEvent;

        ExistingLabels = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);

        ChangedFiles = new Lazy<Task<IReadOnlyList<PullRequestFile>>>(() =>
            _gitHubService.GetPullRequestFilesAsync(Owner, Repo, PrNumber));

        AuthorEmail = new Lazy<Task<string?>>(async () =>
        {
            try
            {
                return await _gitHubService.GetPullRequestAuthorEmailAsync(Owner, Repo, PrNumber);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to get commit author email for PR #{PrNumber}: {ex.Message}");
                return null;
            }
        });

        TeamCaptains = new Lazy<Task<HashSet<string>>>(() =>
            _gitHubService.GetTeamMembersAsync(Owner, teamName));

        PullRequest = new Lazy<Task<PullRequest>>(() =>
            _gitHubService.GetPullRequestAsync(Owner, Repo, PrNumber));
    }

    public static PullRequestContext FromPullRequestEvent(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        PullRequestEvent prEvent,
        PullRequestAction action)
    {
        var owner = prEvent.Repository?.Owner?.Login ?? string.Empty;
        var repo = prEvent.Repository?.Name ?? string.Empty;
        var prNumber = (int)prEvent.PullRequest.Number;
        var labels = GitHubWebhookProcessor.GetLabelNames(prEvent.PullRequest.Labels);

        return new PullRequestContext(
            gitHubService: gitHub,
            teamName: teamName,
            headers: headers,
            eventType: PullRequestEventType.PullRequest,
            owner: owner,
            repo: repo,
            prNumber: prNumber,
            headSha: prEvent.PullRequest.Head?.Sha ?? string.Empty,
            baseBranch: prEvent.PullRequest.Base?.Ref,
            isDraft: prEvent.PullRequest.Draft,
            isMerged: prEvent.PullRequest.Merged,
            title: prEvent.PullRequest.Title ?? string.Empty,
            body: prEvent.PullRequest.Body,
            senderLogin: prEvent.Sender?.Login,
            authorLogin: prEvent.PullRequest.User?.Login,
            action: action.ToString(),
            existingLabels: labels,
            prAction: action,
            prEvent: prEvent);
    }

    public static PullRequestContext FromReviewEvent(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        WebhookPullRequestReviewEvent reviewEvent,
        PullRequestReviewAction action)
    {
        var owner = reviewEvent.Repository?.Owner?.Login ?? string.Empty;
        var repo = reviewEvent.Repository?.Name ?? string.Empty;
        var prNumber = (int)reviewEvent.PullRequest.Number;
        var labels = GitHubWebhookProcessor.GetLabelNames(reviewEvent.PullRequest.Labels);

        return new PullRequestContext(
            gitHubService: gitHub,
            teamName: teamName,
            headers: headers,
            eventType: PullRequestEventType.Review,
            owner: owner,
            repo: repo,
            prNumber: prNumber,
            headSha: reviewEvent.PullRequest.Head?.Sha ?? string.Empty,
            baseBranch: reviewEvent.PullRequest.Base?.Ref,
            isDraft: reviewEvent.PullRequest.Draft,
            isMerged: null,
            title: reviewEvent.PullRequest.Title ?? string.Empty,
            body: reviewEvent.PullRequest.Body,
            senderLogin: reviewEvent.Sender?.Login,
            authorLogin: reviewEvent.PullRequest.User?.Login,
            action: action.ToString(),
            existingLabels: labels,
            reviewAction: action,
            reviewEvent: reviewEvent);
    }

    public static PullRequestContext FromIssueCommentEvent(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        IssueCommentEvent commentEvent,
        IssueCommentAction action)
    {
        var owner = commentEvent.Repository?.Owner?.Login ?? string.Empty;
        var repo = commentEvent.Repository?.Name ?? string.Empty;
        var prNumber = (int)commentEvent.Issue.Number;
        var labels = GitHubWebhookProcessor.GetLabelNames(commentEvent.Issue.Labels);
        var author = commentEvent.Issue.User?.Login;

        return new PullRequestContext(
            gitHubService: gitHub,
            teamName: teamName,
            headers: headers,
            eventType: PullRequestEventType.IssueComment,
            owner: owner,
            repo: repo,
            prNumber: prNumber,
            headSha: string.Empty,
            baseBranch: null,
            isDraft: false,
            isMerged: null,
            title: commentEvent.Issue.Title ?? string.Empty,
            body: commentEvent.Comment?.Body,
            senderLogin: commentEvent.Sender?.Login,
            authorLogin: author,
            action: action.ToString(),
            existingLabels: labels,
            commentAction: action,
            commentEvent: commentEvent);
    }

    public static Task<PullRequestContext> FromIssueCommentEventAsync(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        IssueCommentEvent commentEvent,
        IssueCommentAction action)
    {
        return Task.FromResult(FromIssueCommentEvent(gitHub, teamName, headers, commentEvent, action));
    }

    public Task<string> GetFileContentAsync(string path, string? sha = null)
    {
        var targetSha = sha ?? HeadSha;
        var key = $"{path}@{targetSha}";
        return _fileContentCache.GetOrAdd(key, _ => _gitHubService.GetFileContentAsync(Owner, Repo, path, targetSha));
    }
}
