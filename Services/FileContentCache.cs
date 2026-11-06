using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using abaci_bot.Options;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Thread-safe in-memory cache for fetched Git file contents and blobs.
/// Deduplicates concurrent requests, avoids poisoning on cancellation or failure,
/// and enforces a bounded byte size limit with LRU eviction.
/// </summary>
public class FileContentCache : IFileContentCache
{
    private readonly ConcurrentDictionary<string, Task<string>> _cache = new(StringComparer.Ordinal);
    private readonly object _lruLock = new();
    private readonly LinkedList<string> _lruList = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, int Size)> _lruMap = new(StringComparer.Ordinal);
    private readonly long _maxSizeBytes;
    private long _currentBytes;

    public FileContentCache(IOptions<FileCacheOptions>? options = null)
    {
        _maxSizeBytes = options?.Value?.MaxSizeBytes ?? (50 * 1024 * 1024);
    }

    public FileContentCache(long maxSizeBytes)
    {
        _maxSizeBytes = maxSizeBytes;
    }

    /// <inheritdoc />
    public long CurrentBytes
    {
        get
        {
            lock (_lruLock)
            {
                return _currentBytes;
            }
        }
    }

    /// <inheritdoc />
    public int Count => _cache.Count;

    /// <inheritdoc />
    public async Task<string> GetOrCreateAsync(string key, Func<Task<string>> factory)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return await factory().ConfigureAwait(false);
        }

        while (true)
        {
            if (_cache.TryGetValue(key, out var existingTask))
            {
                if (existingTask.IsFaulted || existingTask.IsCanceled)
                {
                    _cache.TryRemove(new KeyValuePair<string, Task<string>>(key, existingTask));
                    continue;
                }

                try
                {
                    var result = await existingTask.ConfigureAwait(false);
                    TouchLru(key);
                    return result;
                }
                catch
                {
                    _cache.TryRemove(new KeyValuePair<string, Task<string>>(key, existingTask));
                    throw;
                }
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_cache.TryAdd(key, tcs.Task))
            {
                try
                {
                    var content = await factory().ConfigureAwait(false);
                    if (content == null)
                    {
                        _cache.TryRemove(key, out _);
                        tcs.TrySetResult(string.Empty);
                        return string.Empty;
                    }

                    RecordEntry(key, content);
                    tcs.TrySetResult(content);
                    return content;
                }
                catch (Exception ex)
                {
                    _cache.TryRemove(key, out _);
                    tcs.TrySetException(ex);
                    throw;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lruLock)
        {
            _cache.Clear();
            _lruList.Clear();
            _lruMap.Clear();
            _currentBytes = 0;
        }
    }

    private void TouchLru(string key)
    {
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(key, out var entry))
            {
                _lruList.Remove(entry.Node);
                _lruList.AddLast(entry.Node);
            }
        }
    }

    private void RecordEntry(string key, string content)
    {
        int size = (content.Length * sizeof(char)) + (key.Length * sizeof(char));
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(key, out var existing))
            {
                _lruList.Remove(existing.Node);
                _currentBytes -= existing.Size;
            }

            var node = new LinkedListNode<string>(key);
            _lruList.AddLast(node);
            _lruMap[key] = (node, size);
            _currentBytes += size;

            while (_currentBytes > _maxSizeBytes && _lruList.First != null)
            {
                var oldestNode = _lruList.First;
                var oldestKey = oldestNode.Value;
                _lruList.RemoveFirst();
                if (_lruMap.Remove(oldestKey, out var removed))
                {
                    _currentBytes -= removed.Size;
                }
                _cache.TryRemove(oldestKey, out _);
            }
        }
    }
}
