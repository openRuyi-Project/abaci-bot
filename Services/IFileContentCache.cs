namespace abaci_bot.Services;

/// <summary>
/// Thread-safe in-memory cache for fetched Git file contents and blobs.
/// Deduplicates concurrent requests, avoids poisoning on cancellation or failure,
/// and enforces a bounded byte size limit with LRU eviction.
/// </summary>
public interface IFileContentCache
{
    /// <summary>
    /// Gets the cached content for the specified key, or invokes the factory to fetch and store it.
    /// Deduplicates concurrent requests for the same key. If the factory throws or is cancelled,
    /// the failed task is evicted immediately so subsequent callers can retry cleanly.
    /// </summary>
    Task<string> GetOrCreateAsync(string key, Func<Task<string>> factory);

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    void Clear();

    /// <summary>
    /// Gets the estimated current memory size of cached contents in bytes.
    /// </summary>
    long CurrentBytes { get; }

    /// <summary>
    /// Gets the current number of cached entries.
    /// </summary>
    int Count { get; }
}

