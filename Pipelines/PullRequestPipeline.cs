using abaci_bot.Contexts;
using abaci_bot.Services;
using Microsoft.Extensions.Logging;
using Octokit;

namespace abaci_bot.Pipelines;

/// <summary>
/// Orchestrates the execution of registered <see cref="IPullRequestModule"/> instances.
/// Orders modules by priority, applies a fail-fast error policy, and performs best-effort
/// write-back with automated partial-write recovery to reconcile state drift on GitHub.
/// </summary>
public class PullRequestPipeline
{
    private readonly IReadOnlyList<IPullRequestModule> _modules;
    private readonly LabelMutexEngine _mutexEngine;
    private readonly IGitHubService _gitHubService;
    private readonly ILogger<PullRequestPipeline>? _logger;
    private readonly TimeSpan _recoveryTimeout;

    /// <summary>
    /// Default timeout budget allocated for automated partial write recovery reconciliation.
    /// An independent cancellation source is used so that caller cancellation does not prevent
    /// restoring consistent label state on the remote repository.
    /// </summary>
    public static readonly TimeSpan DefaultRecoveryTimeout = TimeSpan.FromSeconds(15);

    public PullRequestPipeline(
        IEnumerable<IPullRequestModule> modules,
        LabelMutexEngine mutexEngine,
        IGitHubService gitHubService,
        ILogger<PullRequestPipeline>? logger = null,
        TimeSpan? recoveryTimeout = null)
    {
        _modules = modules.OrderBy(m => m.Priority).ToList();
        _mutexEngine = mutexEngine;
        _gitHubService = gitHubService;
        _logger = logger;
        _recoveryTimeout = recoveryTimeout ?? DefaultRecoveryTimeout;
    }

    /// <summary>
    /// Registered modules in priority-sorted execution order.
    /// </summary>
    public IReadOnlyList<IPullRequestModule> Modules => _modules;

    /// <summary>
    /// Evaluates whether at least one registered module can process the given context.
    /// </summary>
    public bool HasEligibleModule(PullRequestContext context) =>
        _modules.Any(m => m.ShouldProcess(context));

    /// <summary>
    /// Executes the pipeline sequentially across all active modules and synchronizes label changes.
    /// In case of partial write failures, attempts automated recovery by fetching fresh remote state and retrying.
    /// </summary>
    public async Task ExecuteAsync(PullRequestContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Refresh PR state and labels after lock acquisition to ensure workflow decisions
        // and diff calculations evaluate against fresh remote state rather than stale payloads.
        // Apply bounded retries; if authoritative remote state cannot be obtained, abort label
        // mutation to prevent stale payload states from writing inconsistent labels to GitHub.
        var refreshed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await context.RefreshAsync(cancellationToken))
            {
                refreshed = true;
                break;
            }

            var lastEx = context.LastRefreshException;
            if (lastEx != null && IsNonRetriable(lastEx))
            {
                _logger?.LogError(lastEx,
                    "Non-retriable error refreshing remote PR {Owner}/{Repo}#{PrNumber}; aborting refresh.",
                    context.Owner, context.Repo, context.PrNumber);
                break;
            }

            if (attempt < 2)
            {
                var delay = TimeSpan.FromMilliseconds(50 * (attempt + 1) + Random.Shared.Next(10, 30));
                _logger?.LogWarning(lastEx,
                    "Transient error refreshing remote PR {Owner}/{Repo}#{PrNumber} on attempt {Attempt}; retrying in {DelayMs}ms...",
                    context.Owner, context.Repo, context.PrNumber, attempt + 1, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                _logger?.LogError(lastEx,
                    "Failed to refresh remote PR {Owner}/{Repo}#{PrNumber} on final attempt {Attempt}.",
                    context.Owner, context.Repo, context.PrNumber, attempt + 1);
            }
        }

        if (!refreshed)
        {
            _logger?.LogError(
                "Post-lock refresh failed for {Owner}/{Repo}#{PrNumber} after retries; aborting label mutation to prevent stale state writes.",
                context.Owner, context.Repo, context.PrNumber);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Evaluate module eligibility against refreshed authoritative remote state
        if (!HasEligibleModule(context))
        {
            _logger?.LogDebug("No eligible governance modules to process {Owner}/{Repo}#{PrNumber} after post-lock refresh.",
                context.Owner, context.Repo, context.PrNumber);
            return;
        }

        foreach (var module in _modules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!module.ShouldProcess(context))
                continue;

            _logger?.LogDebug("Executing governance module {ModuleName} (Priority: {Priority}) on {Owner}/{Repo}#{PrNumber}",
                module.ModuleName, module.Priority, context.Owner, context.Repo, context.PrNumber);

            try
            {
                // Fail-fast policy: unhandled exceptions abort the pipeline immediately,
                // preventing partial or corrupted label state from being pushed to GitHub.
                await module.ProcessAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Governance module {ModuleName} threw an unhandled exception while processing {Owner}/{Repo}#{PrNumber}. Aborting pipeline fail-fast.",
                    module.ModuleName, context.Owner, context.Repo, context.PrNumber);
                throw;
            }
        }

        var diff = _mutexEngine.ComputeDiff(context.ExistingLabels, context.LabelsToAdd, context.LabelsToRemove);
        var tracker = new WriteTracker();

        try
        {
            await ApplyDiffAsync(context, diff, tracker, cancellationToken);
        }
        catch (Exception ex)
        {
            // If cancellation is requested before any write attempt was sent to GitHub,
            // fail-fast without attempting recovery because remote state has not been touched.
            if ((ex is OperationCanceledException || cancellationToken.IsCancellationRequested) && tracker.AttemptedWrites == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            _logger?.LogWarning(ex,
                "Partial label write failure or cancellation on pull request {Owner}/{Repo}#{PrNumber} after {Attempted} attempted ({Completed} completed) writes. Commencing automated recovery.",
                context.Owner, context.Repo, context.PrNumber, tracker.AttemptedWrites, tracker.CompletedWrites);

            // Recovery plan for partial writes or API failures:
            // Fetch fresh labels from GitHub, recalculate diff, and retry the remaining operations.
            // If cancellation arrived after a write succeeded, we use a bounded recovery token to reconcile
            // the partial update on GitHub so competing/mutex labels are not left in an inconsistent state.
            var recoverySucceeded = false;
            WriteTracker? recoveryTracker = null;
            Exception? recoveryFailure = null;

            try
            {
                using var recoveryCts = new CancellationTokenSource(_recoveryTimeout);
                var recoveryToken = recoveryCts.Token;

                var freshPr = await _gitHubService.GetPullRequestAsync(context.Owner, context.Repo, context.PrNumber, recoveryToken);
                if (freshPr?.Labels == null)
                {
                    throw new InvalidOperationException(
                        $"Automated recovery failed: unable to fetch authoritative remote pull request state or labels for {context.Owner}/{context.Repo}#{context.PrNumber}.");
                }

                var freshLabels = freshPr.Labels.Select(l => l.Name);
                var recoveryDiff = _mutexEngine.ComputeDiff(freshLabels, context.LabelsToAdd, context.LabelsToRemove);

                recoveryTracker = new WriteTracker();
                await ApplyDiffAsync(context, recoveryDiff, recoveryTracker, recoveryToken);
                recoverySucceeded = true;
            }
            catch (Exception recEx)
            {
                recoveryFailure = recEx;
            }

            if (recoverySucceeded)
            {
                if (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogWarning(
                        "Pipeline cancelled after {AttemptedWrites} attempted writes (recovery applied {RecoveryWrites} writes) for pull request {Owner}/{Repo}#{PrNumber}; automated recovery successfully reconciled state. Re-throwing cancellation.",
                        tracker.AttemptedWrites, recoveryTracker?.CompletedWrites ?? 0, context.Owner, context.Repo, context.PrNumber);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

                _logger?.LogInformation(
                    "Automated recovery succeeded for pull request {Owner}/{Repo}#{PrNumber} after initial write failure (initial writes: {InitialWrites}, recovery writes: {RecoveryWrites}).",
                    context.Owner, context.Repo, context.PrNumber, tracker.CompletedWrites, recoveryTracker?.CompletedWrites ?? 0);
            }
            else
            {
                _logger?.LogError(recoveryFailure,
                    "Failed to recover partial label updates for pull request {Owner}/{Repo}#{PrNumber} after initial failure.",
                    context.Owner, context.Repo, context.PrNumber);

                throw new AggregateException(
                    (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
                        ? $"Pipeline cancelled after {tracker.AttemptedWrites} attempted writes and recovery attempt failed."
                        : "Failed to synchronize pull request labels after recovery attempt.",
                    ex, recoveryFailure!);
            }
        }
    }

    private sealed class WriteTracker
    {
        public int AttemptedWrites;
        public int CompletedWrites;
    }

    private async Task ApplyDiffAsync(PullRequestContext context, LabelDiffResult diff, WriteTracker tracker, CancellationToken cancellationToken)
    {
        if (diff.LabelsToAdd.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.AttemptedWrites++;
            await _gitHubService.AddLabelsAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                diff.LabelsToAdd.ToArray(),
                cancellationToken);
            tracker.CompletedWrites++;
        }

        foreach (var label in diff.LabelsToRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.AttemptedWrites++;
            await _gitHubService.RemoveLabelAsync(
                context.Owner,
                context.Repo,
                context.PrNumber,
                label,
                cancellationToken);
            tracker.CompletedWrites++;
        }
    }

    private static bool IsNonRetriable(Exception ex)
    {
        // Rate limiting and abuse detection are transient: honour backoff/retry instead of failing fast.
        if (ex is RateLimitExceededException or AbuseException)
            return false;

        // Specific Octokit client error exception types are always non-retriable.
        if (ex is AuthorizationException or ForbiddenException or NotFoundException)
            return true;

        // Octokit throws the ApiException family for HTTP response failures.
        if (ex is ApiException api)
        {
            var code = (int)api.StatusCode;
            return code is >= 400 and < 500;
        }

        // Transport-level HttpRequestException with a specific status code
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            var code = (int)httpEx.StatusCode.Value;
            if (code is >= 400 and < 500 && code != 429)
                return true;
        }

        return false;
    }
}
