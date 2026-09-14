using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Pipelines;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;

namespace abaci_bot.Services;

/// <summary>
/// Webhook event ingestion and dispatch coordinator.
/// Acts as a thin dispatcher that validates incoming payload context, builds the domain
/// <see cref="PullRequestContext"/>, acquires fine-grained per-PR locks, and delegates governance
/// evaluation to the <see cref="PullRequestPipeline"/>.
/// </summary>
public class GitHubWebhookProcessor : WebhookEventProcessor
{
    private readonly IGitHubService _github;
    private readonly IConfiguration _config;
    private readonly PullRequestPipeline _pipeline;
    private readonly PullRequestLockManager _lockManager;

    public GitHubWebhookProcessor(
        IGitHubService github,
        IConfiguration config,
        PullRequestPipeline pipeline,
        PullRequestLockManager lockManager)
    {
        _github = github;
        _config = config;
        _pipeline = pipeline;
        _lockManager = lockManager;
    }

    /// <summary>
    /// Convenience constructor for standalone testing and default pipeline assembly.
    /// </summary>
    public GitHubWebhookProcessor(IGitHubService github, IConfiguration config)
        : this(github, config, CreateDefaultPipeline(github), new PullRequestLockManager())
    {
    }

    /// <summary>
    /// Instantiates the standard default governance pipeline with built-in modules.
    /// </summary>
    public static PullRequestPipeline CreateDefaultPipeline(IGitHubService github)
    {
        var mutexEngine = new LabelMutexEngine();
        var modules = new IPullRequestModule[]
        {
            new WorkflowStateModule(),
            new UserContributionModule(),
            new BuildSystemAnalysisModule(),
            new AiAssistanceModule()
        };
        return new PullRequestPipeline(modules, mutexEngine, github);
    }

    protected override async ValueTask ProcessPullRequestWebhookAsync(
        WebhookHeaders headers,
        PullRequestEvent pullRequestEvent,
        PullRequestAction action,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetRepositoryContext(pullRequestEvent.Repository, out var owner, out var repo))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromPullRequestEvent(_github, teamName, headers, pullRequestEvent, action, cancellationToken);

        // Pre-flight check: skip lock acquisition and execution if no module processes this action
        if (!_pipeline.HasEligibleModule(context))
            return;

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    protected override async ValueTask ProcessPullRequestReviewWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewEvent pullRequestReviewEvent,
        PullRequestReviewAction action,
        CancellationToken cancellationToken = default)
    {
        // Ignore non-Submitted review actions (e.g. Dismissed, Edited) to prevent unintended state transitions
        if (action != PullRequestReviewAction.Submitted)
            return;

        if (!TryGetRepositoryContext(pullRequestReviewEvent.Repository, out var owner, out var repo))
            return;

        if (!TryGetSender(pullRequestReviewEvent.Sender, out _))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromReviewEvent(_github, teamName, headers, pullRequestReviewEvent, action, cancellationToken);

        if (!_pipeline.HasEligibleModule(context))
            return;

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    protected override async ValueTask ProcessIssueCommentWebhookAsync(
        WebhookHeaders headers,
        IssueCommentEvent issueCommentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken = default)
    {
        // Only react to newly created comments; ignore edits and deletions
        if (action != IssueCommentAction.Created)
            return;

        // Skip regular issue comments; only pull request comments are governed here
        if (issueCommentEvent.Issue.PullRequest == null)
            return;

        if (!TryGetRepositoryContext(issueCommentEvent.Repository, out var owner, out var repo))
            return;

        if (!TryGetSender(issueCommentEvent.Sender, out _))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromIssueCommentEvent(_github, teamName, headers, issueCommentEvent, action, cancellationToken);

        if (!_pipeline.HasEligibleModule(context))
            return;

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    #region Domain Parsing & Helper Methods

    /// <summary>
    /// Extracts a case-insensitive set of non-empty label names from a webhook label collection.
    /// </summary>
    public static HashSet<string> GetLabelNames(IEnumerable<Octokit.Webhooks.Models.Label>? labels)
    {
        return labels?
            .Select(label => label.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates and extracts repository owner and name from the webhook repository payload.
    /// </summary>
    public static bool TryGetRepositoryContext(
        Octokit.Webhooks.Models.Repository? repository,
        out string owner,
        out string repo)
    {
        owner = repository?.Owner?.Login ?? string.Empty;
        repo = repository?.Name ?? string.Empty;

        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    /// <summary>
    /// Validates and normalizes the webhook sender's login username.
    /// </summary>
    public static bool TryGetSender(Octokit.Webhooks.Models.User? sender, out string login)
    {
        login = sender?.Login?.ToLowerInvariant() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(login);
    }

    /// <summary>
    /// Checks whether the label name corresponds to the "Workflow: Blocked" label.
    /// </summary>
    public static bool IsBlockedLabel(string? labelName)
    {
        return string.Equals(labelName, GitHubLabels.WorkflowBlocked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitively determines whether two GitHub usernames refer to the same user.
    /// </summary>
    public static bool IsSameUser(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the domain portion of an email address. Returns null if invalid or missing.
    /// </summary>
    public static string? ExtractEmailDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return null;

        return email[(atIndex + 1)..].Trim();
    }

    /// <summary>
    /// Determines whether the email address belongs to recognized student intern subdomains.
    /// </summary>
    public static bool IsInternEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalizedEmail = email.Trim();

        return normalizedEmail.EndsWith(".oerv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".or@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase) ||
               normalizedEmail.EndsWith(".riscv@isrc.iscas.ac.cn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the pull request body to check if the AI-assisted contribution checkbox is checked.
    /// </summary>
    public static bool IsAiAssistedPullRequest(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        const string checkboxText =
            "I have read the [AI-Assisted Contribution Policy], and this Pull Request includes non-trivial AI-assisted content.";

        var lines = body.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (!line.Contains(checkboxText, StringComparison.Ordinal))
                continue;

            return line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    #endregion
}
