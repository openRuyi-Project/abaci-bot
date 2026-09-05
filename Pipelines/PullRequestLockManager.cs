using System.Collections.Concurrent;

namespace abaci_bot.Pipelines;

/// <summary>
/// 单 PR 细粒度并发锁管理器。保证针对同一 PR 的 Webhook 事件串行化执行，防止乱序竞争。
/// </summary>
public class PullRequestLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireLockAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken = default)
    {
        var key = $"{owner}/{repo}#{prNumber}";
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

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
