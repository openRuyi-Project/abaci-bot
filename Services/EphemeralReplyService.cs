using abaci_bot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module owning the full lifecycle of ephemeral bot replies:
/// posting the comment, capturing its authoritative GitHub comment ID,
/// persisting its scheduled deletion time, and deleting it with retries on expiry.
/// </summary>
public interface IEphemeralReplyService
{
    Task PostEphemeralReplyAsync(
        string owner,
        string repo,
        int prNumber,
        string body,
        TimeSpan? ttl = null,
        CancellationToken ct = default);
}

public sealed class EphemeralReplyService : BackgroundService, IEphemeralReplyService
{
    private readonly IGitHubService _gitHub;
    private readonly IEphemeralCommentGateway? _commentGateway;
    private readonly IBotStateStore _stateStore;
    private readonly BotStateOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EphemeralReplyService>? _logger;

    public EphemeralReplyService(
        IGitHubService gitHub,
        IBotStateStore stateStore,
        IOptions<BotStateOptions> options,
        IEphemeralCommentGateway? commentGateway = null,
        ILogger<EphemeralReplyService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _gitHub = gitHub;
        _commentGateway = commentGateway ?? gitHub as IEphemeralCommentGateway;
        _stateStore = stateStore;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task PostEphemeralReplyAsync(
        string owner,
        string repo,
        int prNumber,
        string body,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        long commentId = 0;
        string? commentNodeId = null;
        if (_commentGateway != null)
        {
            var created = await _commentGateway.CreateCommentWithMetadataAsync(owner, repo, prNumber, body, ct);
            commentId = created.Id;
            commentNodeId = created.NodeId;
        }
        else
        {
            await _gitHub.CreateCommentAsync(owner, repo, prNumber, body, ct);
        }

        var effectiveTtl = ttl ?? TimeSpan.FromMinutes(_options.EphemeralReplyDeleteMinutes);
        if (commentId > 0 && effectiveTtl > TimeSpan.Zero)
        {
            var deleteAt = _timeProvider.GetUtcNow().Add(effectiveTtl);
            _stateStore.AddPendingEphemeralComment(
                new PendingEphemeralComment(owner, repo, prNumber, commentId, deleteAt, RetryCount: 0, CommentNodeId: commentNodeId));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessDueRepliesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Ephemeral reply cleanup sweep encountered an error");
            }
        }
    }

    public async Task<int> ProcessDueRepliesAsync(CancellationToken cancellationToken = default)
    {
        var isMinimize = _options.EphemeralReplyAction == EphemeralReplyAction.Minimize;
        if (!isMinimize && _commentGateway == null)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var dueEntries = _stateStore.GetDueEphemeralComments(now);
        if (dueEntries.Count == 0)
        {
            return 0;
        }

        var processedCount = 0;
        foreach (var entry in dueEntries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (isMinimize)
                {
                    var minimized = _commentGateway != null
                        ? await _commentGateway.MinimizeCommentAsync(
                            entry.Owner, entry.Repo, entry.CommentId, entry.CommentNodeId, "OUTDATED", cancellationToken)
                        : await _gitHub.MinimizeCommentAsync(
                            entry.Owner, entry.Repo, entry.CommentId, entry.CommentNodeId, "OUTDATED", cancellationToken);

                    if (!minimized)
                    {
                        var nextAttemptAt = now.AddMinutes(1);
                        var willRetry = _stateStore.RecordEphemeralRetry(entry.Owner, entry.Repo, entry.CommentId, nextAttemptAt);
                        if (willRetry)
                        {
                            _logger?.LogDebug("Minimize returned false for ephemeral comment {CommentId} on {Owner}/{Repo}#{PrNumber}; will retry",
                                entry.CommentId, entry.Owner, entry.Repo, entry.PrNumber);
                        }
                        else
                        {
                            _logger?.LogWarning("Exhausted retries minimizing ephemeral comment {CommentId} on {Owner}/{Repo}#{PrNumber}; dropping entry",
                                entry.CommentId, entry.Owner, entry.Repo, entry.PrNumber);
                        }

                        continue;
                    }
                }
                else
                {
                    await _commentGateway!.DeleteCommentAsync(entry.Owner, entry.Repo, entry.CommentId, cancellationToken);
                }

                _stateStore.RemovePendingEphemeralComment(entry.Owner, entry.Repo, entry.CommentId);
                processedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var nextAttemptAt = now.AddMinutes(1);
                var willRetry = _stateStore.RecordEphemeralRetry(entry.Owner, entry.Repo, entry.CommentId, nextAttemptAt);
                if (willRetry)
                {
                    _logger?.LogDebug(ex, "Transient error cleaning up ephemeral comment {CommentId} on {Owner}/{Repo}#{PrNumber}; will retry",
                        entry.CommentId, entry.Owner, entry.Repo, entry.PrNumber);
                }
                else
                {
                    _logger?.LogWarning(ex, "Exhausted retries cleaning up ephemeral comment {CommentId} on {Owner}/{Repo}#{PrNumber}; dropping entry",
                        entry.CommentId, entry.Owner, entry.Repo, entry.PrNumber);
                }
            }
        }

        return processedCount;
    }
}
