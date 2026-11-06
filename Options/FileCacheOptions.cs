namespace abaci_bot.Options;

/// <summary>
/// Configuration options for the in-memory Git blob and file content cache.
/// </summary>
public class FileCacheOptions
{
    /// <summary>
    /// Maximum total bytes allowed in the in-memory file content cache before LRU eviction.
    /// Defaults to 50 MB (52,428,800 bytes).
    /// </summary>
    public long MaxSizeBytes { get; set; } = 50 * 1024 * 1024;
}

