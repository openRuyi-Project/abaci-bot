namespace abaci_bot.Pipelines;

/// <summary>
/// Fine-grained, in-memory concurrency lock manager for pull requests and issues.
/// Serializes webhook deliveries targeting the exact same pull request/issue to prevent race conditions
/// and out-of-order label overwrites, while permitting distinct items to execute concurrently.
/// Automatically evicts semaphores when no operations are waiting or holding the lock, preventing memory leaks.
/// </summary>
public class PullRequestLockManager : IDisposable
{
    private sealed class RefCountedLock : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount = 1;

        public void Dispose()
        {
            Semaphore.Dispose();
        }
    }

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, RefCountedLock> _locks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Returns the current number of tracked active locks (internal for diagnostics and testing).
    /// </summary>
    internal int ActiveLockCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _locks.Count;
            }
        }
    }

    /// <summary>
    /// Asynchronously acquires an exclusive lock for the specified pull request or issue.
    /// The returned <see cref="IDisposable"/> must be disposed to release the lock.
    /// </summary>
    public async Task<IDisposable> AcquireLockAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = $"{owner}/{repo}#{prNumber}";
        RefCountedLock lockEntry;

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_locks.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                lockEntry = existing;
            }
            else
            {
                lockEntry = new RefCountedLock();
                _locks[key] = lockEntry;
            }
        }

        try
        {
            await lockEntry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            // If waiting was cancelled, decrement ref count and clean up if needed
            ReleaseReference(key, lockEntry, acquired: false);
            throw;
        }

        return new Releaser(this, key, lockEntry);
    }

    private void ReleaseReference(string key, RefCountedLock lockEntry, bool acquired)
    {
        if (acquired)
        {
            try
            {
                lockEntry.Semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if semaphore was disposed during shutdown
            }
        }

        lock (_syncRoot)
        {
            lockEntry.RefCount--;
            if (lockEntry.RefCount <= 0)
            {
                _locks.Remove(key);
                try
                {
                    lockEntry.Semaphore.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
        }
    }

    /// <summary>
    /// Disposes all allocated semaphores and releases resources.
    /// </summary>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var entry in _locks.Values)
            {
                try
                {
                    entry.Semaphore.Dispose();
                }
                catch
                {
                    // Ignore disposal errors on shutdown
                }
            }

            _locks.Clear();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Thread-safe lock releaser ensuring the underlying semaphore is released and cleaned up exactly once.
    /// </summary>
    private sealed class Releaser : IDisposable
    {
        private readonly PullRequestLockManager _manager;
        private readonly string _key;
        private readonly RefCountedLock _lockEntry;
        private int _disposed;

        public Releaser(PullRequestLockManager manager, string key, RefCountedLock lockEntry)
        {
            _manager = manager;
            _key = key;
            _lockEntry = lockEntry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _manager.ReleaseReference(_key, _lockEntry, acquired: true);
            }
        }
    }
}
