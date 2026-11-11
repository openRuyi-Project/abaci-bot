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
    private readonly AbiComplianceOptions _abiOptions;
    private readonly IArtifactService? _artifactService;
    private readonly IAbiDiffService? _abiDiffService;
    private readonly IFileContentCache? _fileCache;

    public GitHubWebhookProcessor(
        IGitHubService github,
        IConfiguration config,
        PullRequestPipeline pipeline,
        PullRequestLockManager lockManager,
        LabelMutexEngine? mutexEngine = null,
        ILogger<GitHubWebhookProcessor>? logger = null,
        IOptions<BotDetectionOptions>? botOptions = null,
        IArtifactService? artifactService = null,
        IAbiDiffService? abiDiffService = null,
        IFileContentCache? fileCache = null,
        IOptions<AbiComplianceOptions>? abiOptions = null)
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
        _abiOptions = abiOptions?.Value
            ?? config.GetSection("AbiCompliance").Get<AbiComplianceOptions>()
            ?? new AbiComplianceOptions();
        _artifactService = artifactService;
        _abiDiffService = abiDiffService;
        _fileCache = fileCache;
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
        var context = PullRequestContext.FromPullRequestEvent(_github, teamName, headers, pullRequestEvent, action, _fileCache, cancellationToken);

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
        var context = PullRequestContext.FromReviewEvent(_github, teamName, headers, pullRequestReviewEvent, action, _fileCache, cancellationToken);

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
        var context = PullRequestContext.FromReviewCommentEvent(_github, teamName, headers, pullRequestReviewCommentEvent, action, _fileCache, cancellationToken);

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
        var context = PullRequestContext.FromIssueCommentEvent(_github, teamName, headers, issueCommentEvent, action, _fileCache, cancellationToken);

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
        var runHeadSha = workflowRunEvent.WorkflowRun?.HeadSha;

        // Pre-fetch PRs and guard against outdated runs before performing heavy artifact downloads
        var validPrs = new List<Octokit.PullRequest>();
        foreach (var pr in pullRequests)
        {
            var prNumber = (int)pr.Number;
            var fullPr = await _github.GetPullRequestAsync(owner, repo, prNumber, cancellationToken);
            if (fullPr == null)
            {
                _logger?.LogWarning("PR #{PrNumber} not found in {Owner}/{Repo} during workflow_run processing; skipping", prNumber, owner, repo);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(runHeadSha) &&
                !string.IsNullOrWhiteSpace(fullPr.Head?.Sha) &&
                !string.Equals(runHeadSha, fullPr.Head.Sha, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation(
                    "Workflow run {RunId} head SHA '{RunHeadSha}' does not match PR #{PrNumber} current HEAD '{PrHeadSha}'; skipping outdated run to prevent stale overwrite",
                    runId, runHeadSha, prNumber, fullPr.Head.Sha);
                continue;
            }

            validPrs.Add(fullPr);
        }

        if (validPrs.Count == 0)
            return;

        // Determine if this workflow is authorized for ABI compliance checks
        var workflowName = workflowRunEvent.WorkflowRun?.Name;
        var workflowPath = workflowRunEvent.WorkflowRun?.Path;
        var workflowNames = _abiOptions.WorkflowNames != null && _abiOptions.WorkflowNames.Count > 0
            ? _abiOptions.WorkflowNames
            : new List<string> { "build", "package", "rpm" };

        var isAbiWorkflow = IsAuthorizedWorkflow(workflowName, workflowPath, workflowNames);

        if (!isAbiWorkflow)
        {
            _logger?.LogInformation(
                "Workflow '{WorkflowName}' (path: '{WorkflowPath}') is not in authorized AbiCompliance:WorkflowNames [{ConfiguredNames}]; skipping ABI diff analysis",
                workflowName, workflowPath, string.Join(", ", workflowNames));
        }

        // Perform ABI diff analysis if artifact & abiDiff services are available, run succeeded, and workflow matches
        AbiDiffResult? abiResult = null;
        var conclusion = workflowRunEvent.WorkflowRun?.Conclusion?.StringValue;
        var isSuccess = string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase);

        if (isAbiWorkflow && _artifactService != null && _abiDiffService != null && runId > 0 && isSuccess)
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
                    IReadOnlyList<string> prRpms;
                    try
                    {
                        prRpms = await _artifactService.DownloadRunRpmArtifactsAsync(owner, repo, runId, tempDir, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to download or extract RPM artifacts for workflow run {RunId}", runId);
                        abiResult = AbiDiffResult.NotPerformed($"Failed to download or extract RPM artifacts: {ex.Message}");
                        prRpms = Array.Empty<string>();
                    }

                    if (abiResult == null)
                    {
                        var diffResults = new List<AbiDiffResult>();

                        foreach (var prRpm in prRpms)
                        {
                            var fileName = Path.GetFileName(prRpm);

                            // 1. Filter out source packages (.src.rpm) and debug packages (-debuginfo, -debugsource)
                            if (!ArtifactService.IsEligibleBinaryRpm(fileName))
                            {
                                _logger?.LogInformation("Skipping non-binary or auxiliary RPM package '{FileName}' from ABI comparison", fileName);
                                continue;
                            }

                            // 2. Pre-scan: if package contains no shared library binaries, skip ABI comparison.
                            // Fail-closed: unparseable/corrupted RPMs must not be silently skipped as "no libraries".
                            bool hasSharedLibs;
                            try
                            {
                                hasSharedLibs = await ManagedElfDiffRunner.ContainsSharedLibrariesAsync(prRpm, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed to inspect RPM package '{FileName}' for shared libraries; recording incomplete analysis", fileName);
                                diffResults.Add(AbiDiffResult.NotPerformed($"RPM package '{fileName}' could not be parsed: {ex.Message}"));
                                continue;
                            }

                            if (!hasSharedLibs)
                            {
                                _logger?.LogInformation("Skipping RPM package '{FileName}' from ABI comparison: contains no shared library binaries", fileName);
                                continue;
                            }

                            var pkgName = ArtifactService.ExtractPackageName(fileName);
                            var prDebuginfo = ArtifactService.FindMatchingDebuginfoRpm(prRpm, prRpms);
                            var baselineRpm = await _artifactService.DownloadBaselineRpmAsync(pkgName, tempDir, cancellationToken);
                            if (!string.IsNullOrEmpty(baselineRpm) && File.Exists(baselineRpm))
                            {
                                var debuginfoPkg = !string.IsNullOrWhiteSpace(prDebuginfo)
                                    ? ArtifactService.ExtractPackageName(Path.GetFileName(prDebuginfo))
                                    : pkgName;
                                var baselineDebuginfo = !string.IsNullOrWhiteSpace(prDebuginfo)
                                    ? await _artifactService.DownloadBaselineDebuginfoRpmAsync(debuginfoPkg, tempDir, cancellationToken)
                                    : null;
                                var diff = (!string.IsNullOrWhiteSpace(baselineDebuginfo) || !string.IsNullOrWhiteSpace(prDebuginfo))
                                    ? await _abiDiffService.CompareRpmsAsync(baselineRpm, prRpm, baselineDebuginfo, prDebuginfo, cancellationToken)
                                    : await _abiDiffService.CompareRpmsAsync(baselineRpm, prRpm, cancellationToken);
                                diffResults.Add(diff);
                            }
                            else
                            {
                                diffResults.Add(AbiDiffResult.NotPerformed($"Baseline RPM package not available for package '{pkgName}'"));
                            }
                        }

                        if (diffResults.Count > 0)
                        {
                            // Priority hierarchy: Breaking > Failure / Incomplete > AbiChange > Clean
                            var breaking = diffResults.FirstOrDefault(d => d.Succeeded && d.IsBreakingChange);
                            if (breaking != null)
                            {
                                abiResult = breaking;
                            }
                            else
                            {
                                var failure = diffResults.FirstOrDefault(d => !d.Succeeded);
                                if (failure != null)
                                {
                                    abiResult = failure;
                                }
                                else
                                {
                                    var abiChange = diffResults.FirstOrDefault(d => d.Succeeded && d.HasAbiChange);
                                    abiResult = abiChange ?? diffResults[0];
                                }
                            }
                        }
                        else if (prRpms.Count == 0)
                        {
                            abiResult = AbiDiffResult.NotPerformed("No RPM artifacts found for workflow run");
                        }
                        else
                        {
                            abiResult = new AbiDiffResult(
                                Succeeded: true,
                                HasAbiChange: false,
                                IsBreakingChange: false,
                                Details: "RPM packages contain no shared library binaries");
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

        foreach (var pr in validPrs)
        {
            var prNumber = pr.Number;
            using (await _lockManager.AcquireLockAsync(owner, repo, prNumber, cancellationToken))
            {
                try
                {
                    // Re-fetch current PR under the lock to verify the commit hasn't advanced during heavy artifact downloading / diffing
                    var currentPr = await _github.GetPullRequestAsync(owner, repo, prNumber, cancellationToken);
                    if (currentPr == null)
                    {
                        _logger?.LogWarning("PR #{PrNumber} not found under lock in {Owner}/{Repo}; skipping", prNumber, owner, repo);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(runHeadSha) &&
                        !string.IsNullOrWhiteSpace(currentPr.Head?.Sha) &&
                        !string.Equals(runHeadSha, currentPr.Head.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInformation(
                            "PR #{PrNumber} head advanced from '{RunHeadSha}' to '{CurrentHeadSha}' during workflow processing; discarding outdated run result",
                            prNumber, runHeadSha, currentPr.Head.Sha);
                        continue;
                    }

                    var context = PullRequestContext.FromWorkflowRun(_github, teamName, owner, repo, currentPr, abiResult, headers, _fileCache, cancellationToken);
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

    /// <summary>
    /// Evaluates whether a workflow name or workflow file path matches any of the configured patterns.
    /// Supports substring matching (as documented in README and configuration), filename matching, and glob wildcards (e.g. "*", "build*").
    /// Rejects unverified workflow identities (both name and path missing) unless wildcard "*" is configured.
    /// </summary>
    internal static bool IsAuthorizedWorkflow(string? workflowName, string? workflowPath, IReadOnlyList<string> configuredNames)
    {
        if (configuredNames == null || configuredNames.Count == 0)
            return false;

        if (configuredNames.Contains("*"))
            return true;

        if (string.IsNullOrWhiteSpace(workflowName) && string.IsNullOrWhiteSpace(workflowPath))
            return false; // Reject unverified workflow identities

        var pathFileName = !string.IsNullOrEmpty(workflowPath) ? Path.GetFileName(workflowPath) : null;
        var pathFileNameWithoutExt = !string.IsNullOrEmpty(workflowPath) ? Path.GetFileNameWithoutExtension(workflowPath) : null;

        foreach (var pattern in configuredNames)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            var trimmedPattern = pattern.Trim();

            // Glob pattern with wildcards, e.g. "build*", "*rpm*"
            if (trimmedPattern.Contains('*'))
            {
                var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(trimmedPattern).Replace("\\*", ".*") + "$";
                var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!string.IsNullOrEmpty(workflowName) && regex.IsMatch(workflowName))
                    return true;
                if (!string.IsNullOrEmpty(pathFileName) && regex.IsMatch(pathFileName))
                    return true;
                if (!string.IsNullOrEmpty(pathFileNameWithoutExt) && regex.IsMatch(pathFileNameWithoutExt))
                    return true;
            }
            else
            {
                // Substring match (case-insensitive) against workflow name, filename, or filename without extension
                if (!string.IsNullOrEmpty(workflowName) && workflowName.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(pathFileName) && pathFileName.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(pathFileNameWithoutExt) && pathFileNameWithoutExt.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    #endregion
}
