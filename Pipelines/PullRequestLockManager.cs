using System.Collections.Concurrent;

namespace abaci_bot.Pipelines;

/// <summary>
/// Fine-grained, in-memory concurrency lock manager for pull requests.
/// Serializes webhook deliveries targeting the exact same pull request to prevent race conditions
/// and out-of-order label overwrites, while permitting distinct pull requests to execute concurrently.
/// </summary>
public class PullRequestLockManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Asynchronously acquires an exclusive lock for the specified pull request.
    /// The returned <see cref="IDisposable"/> must be disposed to release the lock.
    /// </summary>
    public async Task<IDisposable> AcquireLockAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = $"{owner}/{repo}#{prNumber}";
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    /// <summary>
    /// Disposes all allocated semaphores and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var semaphore in _locks.Values)
        {
            try
            {
                semaphore.Dispose();
            }
            catch
            {
                // Ignore disposal errors on shutdown
            }
        }

        _locks.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Thread-safe lock releaser ensuring the underlying semaphore is released exactly once.
    /// </summary>
    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
        }
    }
}
