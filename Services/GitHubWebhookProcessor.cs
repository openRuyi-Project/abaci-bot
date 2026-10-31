using abaci_bot.Contexts;
using abaci_bot.Modules;
using abaci_bot.Options;
using abaci_bot.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.PullRequestReviewComment;
using Octokit.Webhooks.Events.WorkflowRun;

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
    private readonly LabelMutexEngine _mutexEngine;
    private readonly ILogger<GitHubWebhookProcessor>? _logger;
    private readonly BotDetectionOptions _botOptions;
    private readonly IArtifactService? _artifactService;
    private readonly IAbiDiffService? _abiDiffService;

    public GitHubWebhookProcessor(
        IGitHubService github,
        IConfiguration config,
        PullRequestPipeline pipeline,
        PullRequestLockManager lockManager,
        LabelMutexEngine? mutexEngine = null,
        ILogger<GitHubWebhookProcessor>? logger = null,
        IOptions<BotDetectionOptions>? botOptions = null,
        IArtifactService? artifactService = null,
        IAbiDiffService? abiDiffService = null)
    {
        _github = github;
        _config = config;
        _pipeline = pipeline;
        _lockManager = lockManager;
        _mutexEngine = mutexEngine ?? new LabelMutexEngine();
        _logger = logger;
        _botOptions = botOptions?.Value
            ?? config.GetSection("BotDetection").Get<BotDetectionOptions>()
            ?? new BotDetectionOptions();
        _artifactService = artifactService;
        _abiDiffService = abiDiffService;
    }

    /// <summary>
    /// Convenience constructor for standalone testing and default pipeline assembly.
    /// </summary>
    public GitHubWebhookProcessor(IGitHubService github, IConfiguration config)
        : this(github, config, CreateDefaultPipeline(github), new PullRequestLockManager(), new LabelMutexEngine())
    {
    }

    /// <summary>
    /// Instantiates the default governance pipeline with built-in modules for unit tests.
    /// </summary>
    internal static PullRequestPipeline CreateDefaultPipeline(IGitHubService github)
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

        if (!HasSender(pullRequestReviewEvent.Sender))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromReviewEvent(_github, teamName, headers, pullRequestReviewEvent, action, cancellationToken);

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    protected override async ValueTask ProcessPullRequestReviewCommentWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewCommentEvent pullRequestReviewCommentEvent,
        PullRequestReviewCommentAction action,
        CancellationToken cancellationToken = default)
    {
        // Only react to newly created inline review comments; ignore edits and deletions
        if (action != PullRequestReviewCommentAction.Created)
            return;

        if (!TryGetRepositoryContext(pullRequestReviewCommentEvent.Repository, out var owner, out var repo))
            return;

        if (!HasSender(pullRequestReviewCommentEvent.Sender))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromReviewCommentEvent(_github, teamName, headers, pullRequestReviewCommentEvent, action, cancellationToken);

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

        if (!HasSender(issueCommentEvent.Sender))
            return;

        // Handle regular issue comments: instant un-stale for non-PR issues
        if (issueCommentEvent.Issue.PullRequest == null)
        {
            if (!TryGetRepositoryContext(issueCommentEvent.Repository, out var issueOwner, out var issueRepo))
                return;

            var existingLabels = GetLabelNames(issueCommentEvent.Issue.Labels);
            if (existingLabels.Contains(GitHubLabels.StatusStale))
            {
                var senderLogin = issueCommentEvent.Sender!.Login ?? string.Empty;
                var senderType = issueCommentEvent.Sender.Type?.ToString();

                if (!BotDetectionHelper.IsBot(senderLogin, senderType, _botOptions.Whitelist))
                {
                    using (await _lockManager.AcquireLockAsync(issueOwner, issueRepo, (int)issueCommentEvent.Issue.Number, cancellationToken))
                    {
                        try
                        {
                            await _github.RemoveLabelAsync(issueOwner, issueRepo, (int)issueCommentEvent.Issue.Number, GitHubLabels.StatusStale, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to remove label '{Label}' from issue {Owner}/{Repo}#{Number}",
                                GitHubLabels.StatusStale, issueOwner, issueRepo, issueCommentEvent.Issue.Number);
                        }
                    }
                }
            }
            return;
        }

        if (!TryGetRepositoryContext(issueCommentEvent.Repository, out var owner, out var repo))
            return;

        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;
        var context = PullRequestContext.FromIssueCommentEvent(_github, teamName, headers, issueCommentEvent, action, cancellationToken);

        using (await _lockManager.AcquireLockAsync(owner, repo, context.PrNumber, cancellationToken))
        {
            await _pipeline.ExecuteAsync(context, cancellationToken);
        }
    }

    protected override async ValueTask ProcessWorkflowRunWebhookAsync(
        WebhookHeaders headers,
        WorkflowRunEvent workflowRunEvent,
        WorkflowRunAction action,
        CancellationToken cancellationToken = default)
    {
        if (action != WorkflowRunAction.Completed)
            return;

        if (!TryGetRepositoryContext(workflowRunEvent.Repository, out var owner, out var repo))
            return;

        var pullRequests = workflowRunEvent.WorkflowRun?.PullRequests;
        if (pullRequests == null || pullRequests.Count == 0)
            return;

        var runId = workflowRunEvent.WorkflowRun?.Id ?? 0;
        var teamName = _config["GitHubApp:TeamName"] ?? string.Empty;

        // Perform ABI diff analysis if artifact & abiDiff services are available and run succeeded
        AbiDiffResult? abiResult = null;
        if (_artifactService != null && _abiDiffService != null && runId > 0 &&
            string.Equals(workflowRunEvent.WorkflowRun?.Conclusion?.StringValue, "success", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var tempExtractionDir = _config.GetSection("AbiCompliance").GetValue<string>("TempExtractionDirectory");
                var baseTemp = !string.IsNullOrWhiteSpace(tempExtractionDir)
                    ? tempExtractionDir
                    : Path.GetTempPath();
                var tempDir = Path.Combine(baseTemp, "abaci_abi_" + Guid.NewGuid().ToString("N"));
                try
                {
                    var prRpms = await _artifactService.DownloadRunRpmArtifactsAsync(owner, repo, runId, tempDir, cancellationToken);
                    foreach (var prRpm in prRpms)
                    {
                        var fileName = Path.GetFileName(prRpm);
                        var pkgName = ArtifactService.ExtractPackageName(fileName);
                        var baselineRpm = await _artifactService.DownloadBaselineRpmAsync(pkgName, tempDir, cancellationToken);
                        if (!string.IsNullOrEmpty(baselineRpm) && File.Exists(baselineRpm))
                        {
                            var diff = await _abiDiffService.CompareRpmsAsync(baselineRpm, prRpm, cancellationToken);
                            abiResult = diff;
                            if (diff.Succeeded && (diff.HasAbiChange || diff.IsBreakingChange))
                            {
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore cleanup error */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to perform ABI diff analysis for run {RunId} in {Owner}/{Repo}", runId, owner, repo);
            }
        }

        foreach (var pr in pullRequests)
        {
            var prNumber = (int)pr.Number;
            using (await _lockManager.AcquireLockAsync(owner, repo, prNumber, cancellationToken))
            {
                try
                {
                    var fullPr = await _github.GetPullRequestAsync(owner, repo, prNumber, cancellationToken);
                    if (fullPr == null)
                    {
                        _logger?.LogWarning("PR #{PrNumber} not found in {Owner}/{Repo} during workflow_run processing; skipping", prNumber, owner, repo);
                        continue;
                    }

                    var context = PullRequestContext.FromWorkflowRun(_github, teamName, owner, repo, fullPr, abiResult, headers, cancellationToken);
                    await _pipeline.ExecuteAsync(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to process workflow run for PR #{PrNumber} in {Owner}/{Repo}",
                        prNumber, owner, repo);
                }
            }
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
    /// Checks whether the webhook event sender is present and non-empty.
    /// </summary>
    public static bool HasSender(Octokit.Webhooks.Models.User? sender) =>
        !string.IsNullOrWhiteSpace(sender?.Login);

    #endregion
}
