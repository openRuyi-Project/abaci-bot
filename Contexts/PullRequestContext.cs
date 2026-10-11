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

/// <summary>
/// Identifies which GitHub webhook event kind originated this context.
/// </summary>
public enum PullRequestEventType
{
    PullRequest,
    Review,
    IssueComment
}

/// <summary>
/// Shared domain context for a single pull request webhook execution.
/// Holds event metadata, provides cached lazy-loaders to avoid duplicate GitHub API calls,
/// and collects label/comment modification intents for batch execution at the end of the pipeline.
/// </summary>
public class PullRequestContext
{
    private readonly IGitHubService _gitHubService;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _fileContentCache = new(StringComparer.Ordinal);
    private bool _isRefreshed;

    /// <summary>
    /// Raw webhook headers, containing delivery IDs and signature details.
    /// </summary>
    public WebhookHeaders? Headers { get; }

    /// <summary>
    /// Cancellation token associated with the current pipeline execution.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Type of the incoming webhook event (PullRequest, Review, or IssueComment).
    /// </summary>
    public PullRequestEventType EventType { get; }

    /// <summary>
    /// Repository owner username or organization name.
    /// </summary>
    public string Owner { get; }

    /// <summary>
    /// Repository name.
    /// </summary>
    public string Repo { get; }

    /// <summary>
    /// Pull request number.
    /// </summary>
    public int PrNumber { get; }

    /// <summary>
    /// Git commit SHA of the pull request HEAD.
    /// </summary>
    public string HeadSha { get; private set; }

    /// <summary>
    /// Target (base) branch of the pull request, if available.
    /// </summary>
    public string? BaseBranch { get; private set; }

    /// <summary>
    /// Indicates whether the pull request is currently marked as draft.
    /// </summary>
    public bool IsDraft { get; private set; }

    /// <summary>
    /// Indicates whether the pull request has been merged. Null if not closed or not applicable.
    /// </summary>
    public bool? IsMerged { get; private set; }

    /// <summary>
    /// The remote pull request state string (e.g. "open", "closed") from the authoritative PR entity.
    /// </summary>
    public string? RemoteState { get; private set; }

    /// <summary>
    /// Title of the pull request or associated issue.
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Description body of the pull request or comment content.
    /// </summary>
    public string? Body { get; private set; }

    /// <summary>
    /// Username of the sender who triggered this webhook action.
    /// </summary>
    public string? SenderLogin { get; }

    /// <summary>
    /// Username of the author who originally opened the pull request.
    /// </summary>
    public string? AuthorLogin { get; }

    /// <summary>
    /// Action string as reported by GitHub (e.g. "opened", "submitted", "created").
    /// </summary>
    public string Action { get; }

    /// <summary>
    /// Strongly-typed pull request action when EventType is PullRequest.
    /// </summary>
    public PullRequestAction? PullRequestAction { get; }

    /// <summary>
    /// Strongly-typed review action when EventType is Review.
    /// </summary>
    public PullRequestReviewAction? PullRequestReviewAction { get; }

    /// <summary>
    /// Strongly-typed issue comment action when EventType is IssueComment.
    /// </summary>
    public IssueCommentAction? IssueCommentAction { get; }

    /// <summary>
    /// Original pull request webhook event payload, if applicable.
    /// </summary>
    public PullRequestEvent? PullRequestEvent { get; }

    /// <summary>
    /// Original review webhook event payload, if applicable.
    /// </summary>
    public WebhookPullRequestReviewEvent? PullRequestReviewEvent { get; }

    /// <summary>
    /// Original comment webhook event payload, if applicable.
    /// </summary>
    public IssueCommentEvent? IssueCommentEvent { get; }

    /// <summary>
    /// Lazily fetches the list of files modified by this pull request.
    /// Evaluated at most once per webhook delivery and shared across all modules.
    /// </summary>
    public Lazy<Task<IReadOnlyList<PullRequestFile>>> ChangedFiles { get; }

    /// <summary>
    /// Lazily fetches the commit author's email via the base repository API.
    /// Catches transient API errors and returns null rather than crashing the pipeline.
    /// </summary>
    public Lazy<Task<string?>> AuthorEmail { get; }

    /// <summary>
    /// Indicates whether fetching the commit author's email failed due to a transient API or network exception.
    /// Used by identity governance modules to avoid clearing valid contributor labels on uncertainty.
    /// </summary>
    public bool AuthorEmailLookupFailed { get; private set; }

    /// <summary>
    /// Holds the exception encountered during the most recent call to <see cref="RefreshAsync"/>, if any.
    /// Used by the pipeline orchestrator to classify non-retriable errors.
    /// </summary>
    public Exception? LastRefreshException { get; private set; }

    /// <summary>
    /// Lazily fetches the captain team member logins for authorization checks.
    /// </summary>
    public Lazy<Task<HashSet<string>>> TeamCaptains { get; }

    /// <summary>
    /// Lazily fetches the full pull request entity when needed (e.g. from comment webhooks).
    /// </summary>
    public Lazy<Task<PullRequest>> PullRequest { get; private set; }

    /// <summary>
    /// Labels present on the pull request at the moment this webhook event occurred.
    /// </summary>
    public HashSet<string> ExistingLabels { get; }

    /// <summary>
    /// Indicates whether the pull request currently carries the "Workflow: Blocked" label.
    /// </summary>
    public bool IsBlocked => ExistingLabels.Contains(GitHubLabels.WorkflowBlocked);

    /// <summary>
    /// Indicates whether the pull request is closed or merged.
    /// When refreshed post-lock, the refreshed remote entity state is authoritative.
    /// </summary>
    public bool IsClosed => _isRefreshed
        ? (IsMerged == true || string.Equals(RemoteState, "closed", StringComparison.OrdinalIgnoreCase))
        : (IsMerged == true ||
           PullRequestAction == Octokit.Webhooks.Events.PullRequest.PullRequestAction.Closed ||
           string.Equals(RemoteState, "closed", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(PullRequestEvent?.PullRequest?.State?.StringValue, "closed", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(PullRequestReviewEvent?.PullRequest?.State?.StringValue, "closed", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Indicates whether the pull request has reached a terminal lifecycle state (closed, merged, or tagged as Complete).
    /// </summary>
    public bool IsTerminal => IsClosed || ExistingLabels.Contains(GitHubLabels.WorkflowComplete);

    /// <summary>
    /// Labels that governance modules intend to add to the pull request.
    /// The pipeline applies mutual-exclusion rules and syncs these in a batch at the end.
    /// Preserves insertion order to support deterministic conflict resolution.
    /// </summary>
    public ISet<string> LabelsToAdd { get; } = new OrderedSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Labels that governance modules intend to remove from the pull request.
    /// Preserves insertion order to support deterministic conflict resolution.
    /// </summary>
    public ISet<string> LabelsToRemove { get; } = new OrderedSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pending comments to be posted to the pull request.
    /// </summary>
    public List<string> PendingComments { get; } = new();

    /// <summary>
    /// Cross-module item dictionary for passing lightweight intermediate state between modules.
    /// </summary>
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
        IssueCommentEvent? commentEvent = null,
        CancellationToken cancellationToken = default)
    {
        _gitHubService = gitHubService;
        CancellationToken = cancellationToken;
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
        RemoteState = prEvent?.PullRequest?.State?.StringValue ?? reviewEvent?.PullRequest?.State?.StringValue;

        ExistingLabels = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);

        ChangedFiles = new Lazy<Task<IReadOnlyList<PullRequestFile>>>(() =>
            _gitHubService.GetPullRequestFilesAsync(Owner, Repo, PrNumber, CancellationToken));

        AuthorEmail = new Lazy<Task<string?>>(async () =>
        {
            try
            {
                var email = await _gitHubService.GetPullRequestAuthorEmailAsync(Owner, Repo, PrNumber, CancellationToken);
                AuthorEmailLookupFailed = false;
                return email;
            }
            catch (Exception ex)
            {
                AuthorEmailLookupFailed = true;
                // Define error out of existence: an email lookup failure should not block governance.
                Console.WriteLine($"Warning: Failed to get commit author email for PR #{PrNumber}: {ex.Message}");
                return null;
            }
        });

        TeamCaptains = new Lazy<Task<HashSet<string>>>(async () =>
        {
            var members = await _gitHubService.GetTeamMembersAsync(Owner, teamName, CancellationToken);
            return new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
        });

        PullRequest = new Lazy<Task<PullRequest>>(() =>
            _gitHubService.GetPullRequestAsync(Owner, Repo, PrNumber, CancellationToken));
    }

    /// <summary>
    /// Creates a PullRequestContext from a native GitHub pull request event.
    /// </summary>
    public static PullRequestContext FromPullRequestEvent(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        PullRequestEvent prEvent,
        PullRequestAction action,
        CancellationToken cancellationToken = default)
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
            prEvent: prEvent,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates a PullRequestContext from a native GitHub pull request review event.
    /// </summary>
    public static PullRequestContext FromReviewEvent(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        WebhookPullRequestReviewEvent reviewEvent,
        PullRequestReviewAction action,
        CancellationToken cancellationToken = default)
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
            reviewEvent: reviewEvent,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates a PullRequestContext from an issue comment event on a pull request.
    /// Avoids upfront remote PR API calls; PR details are loaded on-demand via the Lazy PullRequest property.
    /// </summary>
    public static PullRequestContext FromIssueCommentEvent(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        IssueCommentEvent commentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken = default)
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
            commentEvent: commentEvent,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Asynchronous overload for backward compatibility with existing callers.
    /// </summary>
    public static Task<PullRequestContext> FromIssueCommentEventAsync(
        IGitHubService gitHub,
        string teamName,
        WebhookHeaders? headers,
        IssueCommentEvent commentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(FromIssueCommentEvent(gitHub, teamName, headers, commentEvent, action, cancellationToken));
    }

    /// <summary>
    /// Fetches file contents with thread-safe path-and-SHA caching.
    /// Subsequent calls for the same path in the current context return the cached content without network I/O.
    /// </summary>
    public Task<string> GetFileContentAsync(string path, string? sha = null, CancellationToken cancellationToken = default)
    {
        var targetSha = sha ?? HeadSha;
        var key = $"{path}@{targetSha}";
        var token = cancellationToken != default ? cancellationToken : CancellationToken;
        return _fileContentCache.GetOrAdd(
            key,
            _ => new Lazy<Task<string>>(
                () => _gitHubService.GetFileContentAsync(Owner, Repo, path, targetSha, token),
                LazyThreadSafetyMode.ExecutionAndPublication)
        ).Value;
    }

    /// <summary>
    /// Refreshes the pull request state and existing labels from GitHub after acquiring the concurrency lock.
    /// Ensures that modules and mutex calculation evaluate against current remote state rather than stale webhook payloads.
    /// Returns true if remote state was refreshed successfully; false if a transient failure occurred and payload state was retained.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var token = cancellationToken != default ? cancellationToken : CancellationToken;
        PullRequest? freshPr;
        try
        {
            freshPr = await _gitHubService.GetPullRequestAsync(Owner, Repo, PrNumber, token);
            LastRefreshException = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // If remote refresh fails due to transient error, record exception and retain webhook payload state
            LastRefreshException = ex;
            return false;
        }

        if (freshPr == null)
            return false;

        if (freshPr.Labels != null)
        {
            ExistingLabels.Clear();
            foreach (var label in freshPr.Labels)
            {
                if (!string.IsNullOrWhiteSpace(label.Name))
                {
                    ExistingLabels.Add(label.Name);
                }
            }
        }

        // The post-lock remote state is authoritative for draft, merge, and lifecycle state.
        // Webhook event actions determine whether governance executes, but fresh entity determines actual state.
        IsDraft = freshPr.Draft;
        IsMerged = freshPr.Merged;
        RemoteState = freshPr.State.StringValue;
        _isRefreshed = true;
        if (!string.IsNullOrEmpty(freshPr.Title))
            Title = freshPr.Title;
        if (EventType != PullRequestEventType.IssueComment && freshPr.Body != null)
            Body = freshPr.Body;
        if (!string.IsNullOrEmpty(freshPr.Head?.Sha))
            HeadSha = freshPr.Head.Sha;
        if (!string.IsNullOrEmpty(freshPr.Base?.Ref))
            BaseBranch = freshPr.Base.Ref;

        PullRequest = new Lazy<Task<PullRequest>>(() => Task.FromResult(freshPr));
        return true;
    }

    /// <summary>
    /// Declaratively reconciles a set of module-managed labels against a predicate.
    /// Labels satisfying the predicate are staged in <see cref="LabelsToAdd"/>;
    /// pre-existing labels that do not satisfy the predicate are staged in <see cref="LabelsToRemove"/>.
    /// </summary>
    public void ReconcileLabels(IReadOnlyCollection<string> managedLabels, Func<string, bool> isDesired)
    {
        foreach (var label in managedLabels)
        {
            if (isDesired(label))
            {
                LabelsToAdd.Add(label);
            }
            else if (ExistingLabels.Contains(label))
            {
                LabelsToRemove.Add(label);
            }
        }
    }

    /// <summary>
    /// Declaratively reconciles a set of module-managed labels against an exact desired label.
    /// If <paramref name="desiredLabel"/> matches a managed label, it is staged for addition;
    /// all other pre-existing managed labels are staged for removal.
    /// If <paramref name="desiredLabel"/> is null, all pre-existing managed labels are staged for removal.
    /// </summary>
    public void ReconcileLabels(IReadOnlyCollection<string> managedLabels, string? desiredLabel)
    {
        ReconcileLabels(managedLabels, label => string.Equals(label, desiredLabel, StringComparison.OrdinalIgnoreCase));
    }
}
