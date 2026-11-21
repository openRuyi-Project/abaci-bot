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
    private readonly ISpecCatalog _specCatalog;
    private readonly IRemoteAssetBackfiller _remoteAssetBackfiller;
    private readonly IBotStateStore? _stateStore;
    private readonly PackageUpdateOptions _options;
    private readonly ILogger<PackageUpdateService>? _logger;

    // In-memory tracking of consecutive failures and cooldown timestamps
    private readonly ConcurrentDictionary<string, int> _failureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    // Unit-test fallback only when constructed with stateStore == null; production DI always persists via IBotStateStore.
    private readonly ConcurrentDictionary<int, bool> _recordedFailedPrs = new();
    private readonly ConcurrentDictionary<string, int> _trackedBlockedIssues = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex BlockedIssueTitleRegex = new(
        @"^\[Auto-Update:\s*Blocked\]\s*Repeated CI failures on package\s+(?<pkg>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PackageUpdateService(
        IGitHubService gitHubService,
        IEnumerable<IUpdateCandidateSource> candidateSources,
        IEnumerable<ISpecVersionRewriter> specRewriters,
        IPackageUpdatePublisher publisher,
        ObsPostMergeVerifier obsPostMergeVerifier,
        ISpecCatalog specCatalog,
        IOptions<PackageUpdateOptions> options,
        IRemoteAssetBackfiller remoteAssetBackfiller,
        ILogger<PackageUpdateService>? logger = null,
        IBotStateStore? stateStore = null)
    {
        _gitHubService = gitHubService;
        _candidateSources = candidateSources;
        _specRewriters = specRewriters;
        _publisher = publisher;
        _obsPostMergeVerifier = obsPostMergeVerifier;
        _specCatalog = specCatalog ?? throw new ArgumentNullException(nameof(specCatalog));
        _options = options.Value;
        _remoteAssetBackfiller = remoteAssetBackfiller ?? throw new ArgumentNullException(nameof(remoteAssetBackfiller));
        _logger = logger;
        _stateStore = stateStore;
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

        if (!_options.DryRun)
        {
            await EnsureRequiredLabelsAsync(owner, repo, targetRepo, ct);
        }

        // 1. Quota & Rate Limit Gate
        var rateLimit = await _gitHubService.GetRateLimitSnapshotAsync(ct);
        if (rateLimit.CoreRemaining == null || rateLimit.CoreRemaining < _options.MinRateLimitRemaining)
        {
            _logger?.LogWarning("Rate limit remaining ({Remaining}) is unknown or below threshold ({Min}); deferring sweep",
                rateLimit.CoreRemaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown", _options.MinRateLimitRemaining);
            return;
        }

        // 2. Deadlock Resolution: check open bot PRs for failed CI TTL, count active PRs, and load blocked gates
        var blockedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedLoaded = await LoadBlockedPackagesAsync(owner, repo, blockedPackages, ct);
        var activePrCount = await CleanUpStaleFailedPrsAsync(owner, repo, blockedPackages, ct);

        if (!blockedLoaded || activePrCount is null)
        {
            _logger?.LogWarning(
                "Failed to load Auto-Update: Blocked gates or active PR inventory for {TargetRepo}; deferring candidate collection and publishing until next sweep",
                targetRepo);
            await RunPostSweepTasksAsync(targetRepo, ct);
            return;
        }

        // 3. Collect candidates from all registered sources
        var candidates = await CollectCandidatesAsync(ct);
        if (candidates.Count == 0)
        {
            _logger?.LogWarning("PackageUpdate sweep discovered 0 candidates across all enabled update sources for {TargetRepo}", targetRepo);
        }

        // 4. Process candidates respecting gates and sweep limits
        await ProcessCandidatesAsync(owner, repo, targetRepo, activePrCount.Value, candidates, blockedPackages, ct);

        // 5. Post-sweep tasks (OBS post-merge check)
        await RunPostSweepTasksAsync(targetRepo, ct);
    }

    private async Task<bool> LoadBlockedPackagesAsync(
        string owner,
        string repo,
        HashSet<string> blockedPackages,
        CancellationToken ct)
    {
        try
        {
            var blockedIssues = await _gitHubService.SearchStaleCandidatesAsync(
                owner,
                repo,
                DateTimeOffset.UtcNow.AddDays(1),
                isPr: false,
                labelFilter: $"label:\"{GitHubLabels.AutoUpdateBlocked}\"",
                ct: ct);

            if (blockedIssues == null)
            {
                return true;
            }

            foreach (var issue in blockedIssues)
            {
                if (issue == null)
                {
                    continue;
                }

                var match = BlockedIssueTitleRegex.Match(issue.Title ?? string.Empty);
                if (match.Success)
                {
                    var pkg = match.Groups["pkg"].Value.Trim();
                    if (!string.IsNullOrEmpty(pkg))
                    {
                        blockedPackages.Add(pkg);
                        _trackedBlockedIssues.TryAdd(pkg, issue.Number);
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to load blocked package tracking issues for {Owner}/{Repo}", owner, repo);
            return false;
        }
    }

    private async Task EnsureRequiredLabelsAsync(string owner, string repo, string targetRepo, CancellationToken ct)
    {
        try
        {
            await _gitHubService.EnsureLabelsExistAsync(owner, repo, new[]
            {
                (GitHubLabels.AutoUpdate, "0366d6", "Pull requests automatically created for package updates"),
                (GitHubLabels.AutoUpdateRust, "dea584", "Pull requests automatically created for Rust crate updates"),
                (GitHubLabels.AutoUpdateBlocked, "d73a4a", "Package updates blocked due to repeated failures or missing dependencies"),
                (GitHubLabels.StatusStaleExempt, "ffffff", "Exempt from automatic stale closure")
            }, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to ensure labels in {TargetRepo}", targetRepo);
        }
    }

    private async Task<List<UpdateCandidate>> CollectCandidatesAsync(CancellationToken ct)
    {
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
        return allCandidates.OrderByDescending(c => c.IsSecurityAdvisory).ToList();
    }

    private async Task ProcessCandidatesAsync(
        string owner,
        string repo,
        string targetRepo,
        int activePrCount,
        IReadOnlyList<UpdateCandidate> prioritizedCandidates,
        IReadOnlySet<string> blockedPackages,
        CancellationToken ct)
    {
        const int SecurityAdvisoryConcurrencyAllowance = 5;
        int publishedThisSweep = 0;

        foreach (var candidate in prioritizedCandidates)
        {
            if (ct.IsCancellationRequested)
                break;

            if (publishedThisSweep >= _options.MaxCandidatesPerSweep)
            {
                _logger?.LogInformation("Reached MaxCandidatesPerSweep ({Max}) for this sweep", _options.MaxCandidatesPerSweep);
                break;
            }

            int maxAllowed = candidate.IsSecurityAdvisory
                ? _options.MaxConcurrentPullRequests + SecurityAdvisoryConcurrencyAllowance
                : _options.MaxConcurrentPullRequests;

            if (activePrCount + publishedThisSweep >= maxAllowed)
            {
                if (candidate.IsSecurityAdvisory)
                {
                    _logger?.LogInformation("Reached security advisory concurrency limit ({Max}); stopping sweep", maxAllowed);
                    break;
                }

                _logger?.LogDebug("Active PRs ({Active}) reached MaxConcurrentPullRequests ({Max}); skipping routine candidate {Package}",
                    activePrCount + publishedThisSweep, _options.MaxConcurrentPullRequests, candidate.PackageName);
                continue;
            }

            var published = await ProcessSingleCandidateAsync(owner, repo, targetRepo, candidate, blockedPackages, ct);
            if (published)
            {
                publishedThisSweep++;
                if (_options.RequestThrottleMilliseconds > 0)
                {
                    await Task.Delay(_options.RequestThrottleMilliseconds, ct);
                }
            }
        }
    }

    private async Task<bool> ProcessSingleCandidateAsync(
        string owner,
        string repo,
        string targetRepo,
        UpdateCandidate candidate,
        IReadOnlySet<string> blockedPackages,
        CancellationToken ct)
    {
        // 0. Whitelist and sanity check candidate package name and target version
        if (!UpdateAdmission.IsValidPackageName(candidate.PackageName) || !UpdateAdmission.IsValidTargetVersion(candidate.TargetVersion))
        {
            _logger?.LogWarning("Invalid candidate package name '{Package}' or target version '{Version}'; skipping",
                candidate.PackageName, candidate.TargetVersion);
            return false;
        }

        // A. Upstream version stabilization window (CooldownHours)
        if (_options.CooldownHours > 0 && candidate.PublishedAt.HasValue && !candidate.IsSecurityAdvisory)
        {
            var age = DateTimeOffset.UtcNow - candidate.PublishedAt.Value;
            if (age < TimeSpan.FromHours(_options.CooldownHours))
            {
                _logger?.LogDebug("Package {Package} version {Version} published {Age:F1}h ago, within {Cooldown}h stabilization window; skipping",
                    candidate.PackageName, candidate.TargetVersion, age.TotalHours, _options.CooldownHours);
                return false;
            }
        }

        // B. Read existing SPEC from base branch (main) using required ISpecCatalog
        SpecContext? specContext = null;
        try
        {
            specContext = await _specCatalog.GetSpecAsync(candidate.PackageName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Could not load SPEC for candidate {Package} via SpecCatalog", candidate.PackageName);
        }

        if (specContext == null || string.IsNullOrWhiteSpace(specContext.Content))
            return false;

        var specContent = specContext.Content;

        // Check if main branch was already manually updated to or past target version (closed-loop reset)
        if (UpdateAdmission.IsVersionAtOrPastTarget(specContent, candidate.TargetVersion))
        {
            _logger?.LogInformation("Package {Package} on main is already at or past target version {Version}; resetting cooldown",
                candidate.PackageName, candidate.TargetVersion);
            ResetCooldown(candidate.PackageName);
            return false;
        }

        // Pre-gate: skip packages blocked by an open Auto-Update: Blocked tracking issue or PR
        if (blockedPackages.Contains(candidate.PackageName))
        {
            _logger?.LogInformation("Skipping {Package} because an open issue or PR carries '{BlockedLabel}'",
                candidate.PackageName, GitHubLabels.AutoUpdateBlocked);
            return false;
        }

        // C. Check repeated CI failure cooldown and closed-loop issue reset
        if (IsInCooldown(candidate.PackageName))
        {
            bool issueWasClosed = false;
            if (_trackedBlockedIssues.TryGetValue(candidate.PackageName, out var issueNum))
            {
                try
                {
                    var issue = await _gitHubService.GetIssueAsync(owner, repo, issueNum, ct);
                    if (issue != null && issue.State.Value == Octokit.ItemState.Closed)
                    {
                        _logger?.LogInformation("Tracking issue #{IssueNum} for package {Package} was closed; resetting cooldown", issueNum, candidate.PackageName);
                        ResetCooldown(candidate.PackageName);
                        _trackedBlockedIssues.TryRemove(candidate.PackageName, out _);
                        issueWasClosed = true;
                    }
                    else
                    {
                        _logger?.LogDebug("Package {Package} remains blocked by open issue #{IssueNum}; skipping", candidate.PackageName, issueNum);
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Failed to query tracking issue #{IssueNum} for {Package}; maintaining cooldown", issueNum, candidate.PackageName);
                    return false;
                }
            }

            if (!issueWasClosed && IsInCooldown(candidate.PackageName))
            {
                _logger?.LogDebug("Package {Package} is in failure cooldown; skipping", candidate.PackageName);
                return false;
            }
        }

        // D. Check runtime @bot ignore and #!NoAutoUpdate directive
        var runtimeIgnore = _stateStore?.GetIgnoredPackage(owner, repo, candidate.PackageName);
        if (runtimeIgnore != null)
        {
            _logger?.LogInformation("Skipping {Package} because it is ignored at runtime by @{IgnoredBy}: {Reason}",
                candidate.PackageName, runtimeIgnore.IgnoredBy, runtimeIgnore.Reason ?? "no reason specified");
            return false;
        }

        if (NoAutoUpdateMarker.HasNoAutoUpdate(specContent, out var optOutReason))
        {
            _logger?.LogInformation("Skipping {Package} due to in-spec opt-out: {Reason}", candidate.PackageName, optOutReason);
            return false;
        }

        // Check SkipPackagesWithPatches option
        if (_options.SkipPackagesWithPatches && Regex.IsMatch(specContent, @"^Patch\d*:", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            _logger?.LogInformation("Skipping {Package} because it contains downstream Patch directives and SkipPackagesWithPatches=true", candidate.PackageName);
            return false;
        }

        // Check AllowCrossMajorVersion option
        if (!_options.AllowCrossMajorVersion && UpdateAdmission.IsCrossMajorVersion(candidate.CurrentVersion, candidate.TargetVersion))
        {
            _logger?.LogInformation("Skipping {Package} because {Current} -> {Target} crosses major version boundary and AllowCrossMajorVersion=false",
                candidate.PackageName, candidate.CurrentVersion, candidate.TargetVersion);
            return false;
        }

        // E. Select compatible rewriter
        var rewriter = _specRewriters.FirstOrDefault(r => r.CanHandle(specContext));
        if (rewriter == null)
        {
            _logger?.LogDebug("No compatible rewriter found for {Package}", candidate.PackageName);
            return false;
        }

        var rewriteResult = rewriter.Rewrite(specContext, candidate.TargetVersion);
        if (!rewriteResult.Success || !rewriteResult.HasChanged)
        {
            _logger?.LogDebug("Rewrite failed or produced no change for {Package}: {Error}", candidate.PackageName, rewriteResult.ErrorMessage);
            return false;
        }

        // F. Publish PR with authoritative spec path from SpecContext
        var publishResult = await _publisher.PublishUpdateAsync(candidate, rewriteResult, targetRepo, "main", specContext.RelativePath, ct);
        if (publishResult.Success && !publishResult.Skipped)
        {
            _logger?.LogInformation("Successfully published update for {Package} -> {Version} (PR #{PrNumber})",
                candidate.PackageName, candidate.TargetVersion, publishResult.PullRequestNumber);
            return true;
        }

        if (publishResult.Skipped)
        {
            _logger?.LogDebug("Publish skipped for {Package}: {Reason}", candidate.PackageName, publishResult.SkipReason);
        }

        return false;
    }

    private async Task<int?> CleanUpStaleFailedPrsAsync(
        string owner,
        string repo,
        HashSet<string> blockedPackages,
        CancellationToken ct)
    {
        // Counts active bot PRs and cleans up inactive failed PRs past TTL
        int activeCount = 0;
        try
        {
            // Search all open auto-update PRs by label instead of hardcoded author login to support GitHub App bots seamlessly
            var openPrs = await _gitHubService.SearchStaleCandidatesAsync(
                owner, repo, DateTimeOffset.UtcNow, isPr: true, labelFilter: $"label:\"{GitHubLabels.AutoUpdate}\" is:pr is:open", ct: ct);

            if (openPrs == null)
            {
                return 0;
            }

            foreach (var pr in openPrs)
            {
                if (ct.IsCancellationRequested)
                    break;

                // Check PR branch workflow runs
                var fullPr = await _gitHubService.GetPullRequestAsync(owner, repo, pr.Number, ct);
                var branchName = fullPr?.Head?.Ref;
                var headSha = fullPr?.Head?.Sha;
                var runs = !string.IsNullOrWhiteSpace(branchName)
                    ? await _gitHubService.ListWorkflowRunsAsync(owner, repo, branchName, ct)
                    : Array.Empty<Octokit.WorkflowRun>();

                // Precise gate matching: find remoteasset run for backfill
                var remoteAssetRun = WorkflowGateLocator.FindRemoteAssetRun(runs, headSha);
                bool isRemoteAssetFailed = remoteAssetRun != null && remoteAssetRun.Conclusion == Octokit.WorkflowRunConclusion.Failure;

                // Overall CI failure: check if any run on head commit failed
                var runsOnHead = runs.Where(r => headSha == null || string.Equals(r.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)).ToList();
                bool isFailed = runsOnHead.Any(r => r.Conclusion == Octokit.WorkflowRunConclusion.Failure);

                // Backfill updated #!RemoteAsset checksums when remoteasset workflow fails
                if (isRemoteAssetFailed && !string.IsNullOrWhiteSpace(branchName) && remoteAssetRun != null && !_options.DryRun)
                {
                    await _remoteAssetBackfiller.TryBackfillAsync(owner, repo, branchName, pr.Number, remoteAssetRun.Id, ct);
                }

                // Wire failure detection into cooldown tracking
                var branchPackageName = branchName != null && branchName.StartsWith(_options.BranchPrefix ?? "autoupdate/", StringComparison.OrdinalIgnoreCase)
                    ? branchName.Substring((_options.BranchPrefix ?? "autoupdate/").Length)
                    : null;

                var hasBlockedLabel = (pr.Labels != null && pr.Labels.Any(l => string.Equals(l.Name, GitHubLabels.AutoUpdateBlocked, StringComparison.OrdinalIgnoreCase))) ||
                                      (fullPr?.Labels != null && fullPr.Labels.Any(l => string.Equals(l.Name, GitHubLabels.AutoUpdateBlocked, StringComparison.OrdinalIgnoreCase)));
                if (hasBlockedLabel && !string.IsNullOrEmpty(branchPackageName))
                {
                    blockedPackages.Add(branchPackageName);
                }

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

                        // Delete remote branch if exists, belongs to target repository, and matches branch prefix
                        var headRepoFullName = fullPr?.Head?.Repository?.FullName;
                        var isSameRepo = headRepoFullName != null && string.Equals(headRepoFullName, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase);
                        var prefix = !string.IsNullOrEmpty(_options.BranchPrefix) ? _options.BranchPrefix : "autoupdate/";
                        var hasExpectedPrefix = !string.IsNullOrWhiteSpace(branchName) && branchName.StartsWith(prefix, StringComparison.Ordinal);

                        if (isSameRepo && hasExpectedPrefix && !string.IsNullOrWhiteSpace(branchName))
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
            return null;
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

    public bool IsInCooldown(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return false;

        if (_cooldowns.TryGetValue(packageName, out var cooldownUntil))
        {
            if (DateTimeOffset.UtcNow < cooldownUntil)
                return true;

            _cooldowns.TryRemove(packageName, out _);
        }

        var (owner, repo) = ParseTargetRepository();
        var persisted = owner != null && repo != null
            ? _stateStore?.GetPackageFailure(owner, repo, packageName)
            : null;
        if (persisted?.CooldownUntil != null)
        {
            if (DateTimeOffset.UtcNow < persisted.CooldownUntil.Value)
            {
                _cooldowns[packageName] = persisted.CooldownUntil.Value;
                return true;
            }

            _stateStore?.ClearPackageFailure(owner!, repo!, packageName);
        }

        return false;
    }

    public void ResetCooldown(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return;

        _cooldowns.TryRemove(packageName, out _);
        _failureCounts.TryRemove(packageName, out _);
        var (owner, repo) = ParseTargetRepository();
        if (owner != null && repo != null)
        {
            _stateStore?.ClearPackageFailure(owner, repo, packageName);
        }
        _logger?.LogInformation("Reset cooldown and failure count for package {Package}", packageName);
    }

    public bool RecordFailure(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) || IsInCooldown(packageName))
            return false;

        var (owner, repo) = ParseTargetRepository();
        var initialCount = owner != null && repo != null
            ? (_stateStore?.GetPackageFailure(owner, repo, packageName)?.ConsecutiveFailures ?? 0)
            : 0;
        var count = _failureCounts.AddOrUpdate(
            packageName,
            _ => initialCount + 1,
            (_, current) => Math.Max(current, initialCount) + 1);
        _logger?.LogWarning("Recorded failure for package {Package} (Consecutive failures: {Count})", packageName, count);

        if (count >= _options.MaxConsecutiveFailures)
        {
            var cooldownDays = _options.GiveUpIssueCooldownDays > 0 ? _options.GiveUpIssueCooldownDays : 30;
            var cooldownUntil = DateTimeOffset.UtcNow.AddHours(cooldownDays * 24);
            _cooldowns[packageName] = cooldownUntil;
            _failureCounts.TryRemove(packageName, out _);
            if (owner != null && repo != null)
            {
                _stateStore?.SetPackageFailure(owner, repo, packageName, count, cooldownUntil);
            }
            _logger?.LogWarning("Package {Package} reached {Max} consecutive CI failures; entering {Days}-day cooldown",
                packageName, _options.MaxConsecutiveFailures, cooldownDays);
            return true;
        }

        if (owner != null && repo != null)
        {
            _stateStore?.SetPackageFailure(owner, repo, packageName, count, null);
        }

        return false;
    }

    private (string? Owner, string? Repo) ParseTargetRepository()
    {
        if (string.IsNullOrWhiteSpace(_options.TargetRepository))
            return (null, null);

        var parts = _options.TargetRepository.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }

    private async Task CheckAndRecordFailureAsync(string owner, string repo, string packageName, int prNumber, CancellationToken ct)
    {
        // Primary path persists deduplication in bot-state.json across restarts; in-memory set is only for unit tests with stateStore == null.
        var isNewFailure = _stateStore != null
            ? _stateStore.TryMarkFailedPrRecorded(owner, repo, prNumber)
            : _recordedFailedPrs.TryAdd(prNumber, true);
        if (!isNewFailure)
            return;

        var enteredCooldown = RecordFailure(packageName);
        if (enteredCooldown && !_options.DryRun && _options.IssueOnGivingUp)
        {
            var cooldownDays = _options.GiveUpIssueCooldownDays > 0 ? _options.GiveUpIssueCooldownDays : 30;
            var issueTitle = $"[{GitHubLabels.AutoUpdateBlocked}] Repeated CI failures on package {packageName}";
            var issueBody = $"""
                ## Repeated CI Failures Alert
                Automated updates for package `{packageName}` have encountered repeated CI failures ({_options.MaxConsecutiveFailures} distinct failed PRs).

                - **Latest Failed PR**: #{prNumber}
                - **Action Taken**: Entering {cooldownDays}-day cooldown before further automated attempts.
                - **Guidance**: Please inspect dependencies or build requirements manually.
                """;

            try
            {
                var issueLabel = !string.IsNullOrWhiteSpace(_options.GiveUpIssueLabel)
                    ? _options.GiveUpIssueLabel
                    : GitHubLabels.StatusStaleExempt;

                var createdIssue = await _gitHubService.CreateIssueAsync(owner, repo, issueTitle, issueBody, new[] { GitHubLabels.AutoUpdateBlocked, issueLabel }, ct);
                if (createdIssue != null)
                {
                    _trackedBlockedIssues[packageName] = createdIssue.Number;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create blocked issue for {Package}", packageName);
            }
        }
    }
}

