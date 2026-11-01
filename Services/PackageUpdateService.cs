using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Background orchestration worker for automated upstream package updates.
/// Dispatches candidate discovery across dual sources, controls rate limits and concurrency quotas,
/// executes remote SPEC updates, performs failure backoff with 30-day cooldowns, and post-merge OBS checks.
/// </summary>
public class PackageUpdateService : BackgroundService
{
    private readonly IGitHubService _gitHubService;
    private readonly IEnumerable<IUpdateCandidateSource> _candidateSources;
    private readonly IEnumerable<ISpecVersionRewriter> _specRewriters;
    private readonly IPackageUpdatePublisher _publisher;
    private readonly ObsPostMergeVerifier _obsPostMergeVerifier;
    private readonly PackageUpdateOptions _options;
    private readonly ILogger<PackageUpdateService>? _logger;

    // In-memory tracking of consecutive failures and cooldown timestamps
    private readonly ConcurrentDictionary<string, int> _failureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, bool> _recordedFailedPrs = new();

    public PackageUpdateService(
        IGitHubService gitHubService,
        IEnumerable<IUpdateCandidateSource> candidateSources,
        IEnumerable<ISpecVersionRewriter> specRewriters,
        IPackageUpdatePublisher publisher,
        ObsPostMergeVerifier obsPostMergeVerifier,
        IOptions<PackageUpdateOptions> options,
        ILogger<PackageUpdateService>? logger = null)
    {
        _gitHubService = gitHubService;
        _candidateSources = candidateSources;
        _specRewriters = specRewriters;
        _publisher = publisher;
        _obsPostMergeVerifier = obsPostMergeVerifier;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("PackageUpdateService background worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.TargetRepository))
                {
                    await RunSweepAsync(stoppingToken);
                }
                else
                {
                    _logger?.LogDebug("PackageUpdateService is disabled or TargetRepository is unconfigured; skipping sweep");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error occurred during PackageUpdateService sweep");
            }

            var interval = TimeSpan.FromHours(Math.Max(1, _options.CheckIntervalHours));
            await Task.Delay(interval, stoppingToken);
        }

        _logger?.LogInformation("PackageUpdateService background worker stopping");
    }

    public async Task RunSweepAsync(CancellationToken ct = default)
    {
        var targetRepo = _options.TargetRepository.Trim();
        var parts = targetRepo.Split('/');
        if (parts.Length != 2)
        {
            _logger?.LogError("Invalid TargetRepository format '{Repo}'; expected 'owner/repo'", targetRepo);
            return;
        }

        var owner = parts[0];
        var repo = parts[1];

        _logger?.LogInformation("Starting PackageUpdateService sweep for {TargetRepo} (DryRun={DryRun})", targetRepo, _options.DryRun);

        // P1-5 & P1-7: Ensure automation labels exist in target repository (Self-healing idempotency)
        try
        {
            await _gitHubService.EnsureLabelsExistAsync(owner, repo, new[]
            {
                ("Auto-Update", "0366d6", "Pull requests automatically created for package updates"),
                ("Auto-Update: Rust", "dea584", "Pull requests automatically created for Rust crate updates"),
                ("Auto-Update: Blocked", "d73a4a", "Package updates blocked due to repeated failures or missing dependencies"),
                (GitHubLabels.StatusPinned, "ffffff", "Exempt from automatic stale closure")
            }, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to ensure labels in {TargetRepo}", targetRepo);
        }

        // 1. Quota & Rate Limit Gate
        var rateLimit = await _gitHubService.GetRateLimitSnapshotAsync(ct);
        if (rateLimit.CoreRemaining == null || rateLimit.CoreRemaining < _options.MinRateLimitRemaining)
        {
            _logger?.LogWarning("Rate limit remaining ({Remaining}) is unknown or below threshold ({Min}); deferring sweep",
                rateLimit.CoreRemaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown", _options.MinRateLimitRemaining);
            return;
        }

        // 2. Deadlock Resolution: check open bot PRs for failed CI TTL and count active PRs
        var activePrCount = await CleanUpStaleFailedPrsAsync(owner, repo, ct);
        if (activePrCount >= _options.MaxConcurrentPullRequests)
        {
            _logger?.LogInformation("Active bot PR count ({Count}) reached MaxConcurrentPullRequests ({Max}); skipping candidate discovery",
                activePrCount, _options.MaxConcurrentPullRequests);
            await RunPostSweepTasksAsync(targetRepo, ct);
            return;
        }

        // 3. Collect candidates from all registered sources in parallel isolation
        var allCandidates = new List<UpdateCandidate>();
        foreach (var source in _candidateSources)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var candidates = await source.GetCandidatesAsync(ct);
                allCandidates.AddRange(candidates);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Failed to retrieve candidates from source {Source}", source.SourceName);
            }
        }

        _logger?.LogInformation("Discovered {Count} total candidates across all sources", allCandidates.Count);

        // 4. Process candidates respecting gates and sweep limits
        int publishedThisSweep = 0;
        foreach (var candidate in allCandidates)
        {
            if (ct.IsCancellationRequested)
                break;

            if (_options.MaxCandidatesPerSweep > 0 && publishedThisSweep >= _options.MaxCandidatesPerSweep)
            {
                _logger?.LogInformation("Reached MaxCandidatesPerSweep ({Max}) for this sweep", _options.MaxCandidatesPerSweep);
                break;
            }

            if (activePrCount + publishedThisSweep >= _options.MaxConcurrentPullRequests)
            {
                _logger?.LogInformation("Reached MaxConcurrentPullRequests limit; stopping sweep");
                break;
            }

            // A. Check cooldown
            if (IsInCooldown(candidate.PackageName))
            {
                _logger?.LogDebug("Package {Package} is in 30-day failure cooldown; skipping", candidate.PackageName);
                continue;
            }

            // B. Read existing SPEC from base branch (main)
            var specPath = $"SPECS/{candidate.PackageName}/{candidate.PackageName}.spec";
            string specContent;
            try
            {
                specContent = await _gitHubService.GetFileContentAsync(owner, repo, specPath, "main", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Could not load SPEC for candidate {Package} from main; skipping", candidate.PackageName);
                continue;
            }

            if (string.IsNullOrWhiteSpace(specContent))
                continue;

            // C. Check #!NoAutoUpdate directive
            if (NoAutoUpdateMarker.HasNoAutoUpdate(specContent, out var optOutReason))
            {
                _logger?.LogInformation("Skipping {Package} due to in-spec opt-out: {Reason}", candidate.PackageName, optOutReason);
                continue;
            }

            // Check SkipPackagesWithPatches option
            if (_options.SkipPackagesWithPatches && Regex.IsMatch(specContent, @"^Patch\d*:", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                _logger?.LogInformation("Skipping {Package} because it contains downstream Patch directives and SkipPackagesWithPatches=true", candidate.PackageName);
                continue;
            }

            // D. Select compatible rewriter
            var specContext = new SpecContext(candidate.PackageName, candidate.PackageName, specContent);
            var rewriter = _specRewriters.FirstOrDefault(r => r.CanHandle(specContext));
            if (rewriter == null)
            {
                _logger?.LogDebug("No compatible rewriter found for {Package}", candidate.PackageName);
                continue;
            }

            var rewriteResult = rewriter.Rewrite(specContext, candidate.TargetVersion);
            if (!rewriteResult.Success || !rewriteResult.HasChanged)
            {
                _logger?.LogDebug("Rewrite failed or produced no change for {Package}: {Error}", candidate.PackageName, rewriteResult.ErrorMessage);
                continue;
            }

            // E. Publish PR
            var publishResult = await _publisher.PublishUpdateAsync(candidate, rewriteResult, targetRepo, "main", ct);
            if (publishResult.Success && !publishResult.Skipped)
            {
                publishedThisSweep++;
                _logger?.LogInformation("Successfully published update for {Package} -> {Version} (PR #{PrNumber})",
                    candidate.PackageName, candidate.TargetVersion, publishResult.PullRequestNumber);
            }
            else if (publishResult.Skipped)
            {
                _logger?.LogDebug("Publish skipped for {Package}: {Reason}", candidate.PackageName, publishResult.SkipReason);
            }
        }

        // 5. Post-sweep tasks (OBS post-merge check)
        await RunPostSweepTasksAsync(targetRepo, ct);
    }

    private async Task<int> CleanUpStaleFailedPrsAsync(string owner, string repo, CancellationToken ct)
    {
        // Counts active bot PRs and cleans up inactive failed PRs past TTL
        int activeCount = 0;
        try
        {
            // Search all open auto-update PRs by label instead of hardcoded author login to support GitHub App bots seamlessly
            var openPrs = await _gitHubService.SearchStaleCandidatesAsync(
                owner, repo, DateTimeOffset.UtcNow, isPr: true, labelFilter: "label:\"Auto-Update\" is:pr is:open", ct: ct);

            foreach (var pr in openPrs)
            {
                if (ct.IsCancellationRequested)
                    break;

                // P1-1: Check if this is an auto-update PR (has Auto-Update or Auto-Update: Rust label)
                var isAutoUpdatePr = pr.Labels.Any(l => l.Name.StartsWith("Auto-Update", StringComparison.OrdinalIgnoreCase));
                if (!isAutoUpdatePr)
                {
                    activeCount++;
                    continue;
                }

                // Check PR branch workflow runs
                var fullPr = await _gitHubService.GetPullRequestAsync(owner, repo, pr.Number, ct);
                var branchName = fullPr?.Head?.Ref;
                var runs = !string.IsNullOrWhiteSpace(branchName)
                    ? await _gitHubService.ListWorkflowRunsAsync(owner, repo, branchName, ct)
                    : Array.Empty<Octokit.WorkflowRun>();

                var latestRun = runs.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
                bool isFailed = latestRun != null && latestRun.Conclusion == Octokit.WorkflowRunConclusion.Failure;

                // P0-3: sha256 RemoteAsset backfill
                // If the run failed, inspect if there is a remoteassets diff artifact to commit back
                if (isFailed && !string.IsNullOrWhiteSpace(branchName) && latestRun != null && !_options.DryRun)
                {
                    await TryBackfillRemoteAssetDiffAsync(owner, repo, branchName, pr.Number, latestRun.Id, ct);
                }

                // P1-2: Wire failure detection into RecordFailure & 30-day cooldown
                var branchPackageName = branchName != null && branchName.StartsWith(_options.BranchPrefix ?? "autoupdate/", StringComparison.OrdinalIgnoreCase)
                    ? branchName.Substring((_options.BranchPrefix ?? "autoupdate/").Length)
                    : null;

                if (isFailed && !string.IsNullOrEmpty(branchPackageName))
                {
                    await CheckAndRecordFailureAsync(owner, repo, branchPackageName, pr.Number, ct);
                }

                var isInactiveFailed = isFailed && pr.UpdatedAt.HasValue &&
                                       pr.UpdatedAt.Value < DateTimeOffset.UtcNow.AddDays(-_options.FailedPrTtlDays);

                if (isInactiveFailed)
                {
                    _logger?.LogInformation("Closing inactive failed bot PR #{Number} (Branch: {Branch}) to free concurrency slot", pr.Number, branchName);
                    if (!_options.DryRun)
                    {
                        var closeMsg = $"Automated pull request has been failing CI and inactive for over {_options.FailedPrTtlDays} days. Closing to reclaim concurrency slot.";
                        await _gitHubService.CreateCommentAsync(owner, repo, pr.Number, closeMsg, ct);
                        await _gitHubService.CloseIssueAsync(owner, repo, pr.Number, ct);

                        // Delete remote branch if exists
                        if (!string.IsNullOrWhiteSpace(branchName))
                        {
                            try
                            {
                                await _gitHubService.DeleteRefAsync(owner, repo, branchName, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Failed to delete remote branch {Branch}", branchName);
                            }
                        }
                    }
                }
                else
                {
                    activeCount++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to enumerate active bot PRs for concurrency management");
        }

        return activeCount;
    }

    private async Task RunPostSweepTasksAsync(string targetRepo, CancellationToken ct)
    {
        try
        {
            await _obsPostMergeVerifier.VerifyMergedPullRequestsAsync(targetRepo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to run OBS post-merge verification");
        }
    }

    private bool IsInCooldown(string packageName)
    {
        if (_cooldowns.TryGetValue(packageName, out var cooldownUntil))
        {
            if (DateTimeOffset.UtcNow < cooldownUntil)
                return true;

            _cooldowns.TryRemove(packageName, out _);
        }

        return false;
    }

    private async Task TryBackfillRemoteAssetDiffAsync(
        string owner, string repo, string branchName, int prNumber, long runId, CancellationToken ct)
    {
        try
        {
            var artifacts = await _gitHubService.ListWorkflowArtifactsAsync(owner, repo, runId, ct);
            var diffArtifact = artifacts.FirstOrDefault(a => a.Name != null && a.Name.Contains("remoteasset", StringComparison.OrdinalIgnoreCase));
            if (diffArtifact == null)
                return;

            _logger?.LogInformation("Found remoteasset artifact '{Name}' (ID: {Id}) for PR #{PrNumber}; downloading diff to backfill",
                diffArtifact.Name, diffArtifact.Id, prNumber);

            await using var stream = await _gitHubService.DownloadArtifactAsync(owner, repo, diffArtifact.Id, ct);
            var diffContent = await ArtifactDiffExtractor.ExtractDiffContentAsync(stream, ct);
            if (string.IsNullOrWhiteSpace(diffContent))
                return;

            // Extract file path and modified remoteasset lines
            // Example diff header:
            // --- a/SPECS/foo/foo.spec
            // +++ b/SPECS/foo/foo.spec
            var fileMatch = Regex.Match(diffContent, @"\+\+\+\s+b/(?<path>SPECS/\S+)", RegexOptions.Multiline);
            if (!fileMatch.Success)
                return;

            var specPath = fileMatch.Groups["path"].Value.Trim();
            var currentSpec = await _gitHubService.GetFileContentAsync(owner, repo, specPath, branchName, ct);

            // Extract added RemoteAsset line (+#!RemoteAsset:...)
            var addedLines = Regex.Matches(diffContent, @"^\+(?<line>#!RemoteAsset:.*)$", RegexOptions.Multiline);
            var removedLines = Regex.Matches(diffContent, @"^-(?<line>#!RemoteAsset:.*)$", RegexOptions.Multiline);

            if (addedLines.Count > 0 && removedLines.Count > 0 && addedLines.Count == removedLines.Count)
            {
                var updatedSpec = currentSpec;
                for (int i = 0; i < addedLines.Count; i++)
                {
                    var oldLine = removedLines[i].Groups["line"].Value.Trim();
                    var newLine = addedLines[i].Groups["line"].Value.Trim();
                    updatedSpec = updatedSpec.Replace(oldLine, newLine);
                }

                if (!string.Equals(updatedSpec, currentSpec, StringComparison.Ordinal))
                {
                    _logger?.LogInformation("Backfilling updated #!RemoteAsset checksums to branch {Branch} for PR #{PrNumber}", branchName, prNumber);
                    await _gitHubService.UpdateFileContentAsync(
                        owner, repo, specPath, "chore: update #!RemoteAsset sha256 checksums", updatedSpec, branchName, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to backfill remoteasset diff artifact for PR #{PrNumber}", prNumber);
        }
    }

    private async Task CheckAndRecordFailureAsync(string owner, string repo, string packageName, int prNumber, CancellationToken ct)
    {
        // P1-2: De-duplicate failure events by PR Number: only count once per distinct failed PR
        if (!_recordedFailedPrs.TryAdd(prNumber, true))
            return;

        if (IsInCooldown(packageName))
            return;

        var count = _failureCounts.AddOrUpdate(packageName, 1, (_, current) => current + 1);
        _logger?.LogWarning("Detected CI failure on distinct PR #{Number} for package {Package} (Consecutive failures: {Count})", prNumber, packageName, count);

        if (count >= _options.MaxConsecutiveFailures)
        {
            _cooldowns[packageName] = DateTimeOffset.UtcNow.AddDays(_options.GiveUpIssueCooldownDays);
            _failureCounts.TryRemove(packageName, out _);
            _logger?.LogWarning("Package {Package} reached {Max} consecutive CI failures; entering {Days}-day cooldown",
                packageName, _options.MaxConsecutiveFailures, _options.GiveUpIssueCooldownDays);

            if (!_options.DryRun && _options.IssueOnGivingUp)
            {
                var issueTitle = $"[Auto-Update: Blocked] Repeated CI failures on package {packageName}";
                var issueBody = $"""
                    ## Repeated CI Failures Alert
                    Automated updates for package `{packageName}` have encountered repeated CI failures ({_options.MaxConsecutiveFailures} distinct failed PRs).

                    - **Latest Failed PR**: #{prNumber}
                    - **Action Taken**: Entering {_options.GiveUpIssueCooldownDays}-day cooldown before further automated attempts.
                    - **Guidance**: Please inspect dependencies or build requirements manually.
                    """;

                try
                {
                    var issueLabel = !string.IsNullOrWhiteSpace(_options.GiveUpIssueLabel)
                        ? _options.GiveUpIssueLabel
                        : GitHubLabels.StatusPinned;

                    await _gitHubService.CreateIssueAsync(owner, repo, issueTitle, issueBody, new[] { "Auto-Update: Blocked", issueLabel }, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to create blocked issue for {Package}", packageName);
                }
            }
        }
    }

    public void RecordFailure(string packageName)
    {
        var count = _failureCounts.AddOrUpdate(packageName, 1, (_, current) => current + 1);
        if (count >= _options.MaxConsecutiveFailures)
        {
            var cooldownHours = _options.CooldownHours > 0 ? _options.CooldownHours : _options.GiveUpIssueCooldownDays * 24;
            _cooldowns[packageName] = DateTimeOffset.UtcNow.AddHours(cooldownHours);
            _failureCounts.TryRemove(packageName, out _);
            _logger?.LogWarning("Package {Package} reached consecutive failure limit; entering {Hours}-hour cooldown",
                packageName, cooldownHours);
        }
    }
}

