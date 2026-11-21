namespace abaci_bot.Services;

/// <summary>
/// Strongly typed composite key identifying an author's pending two-phase confirmation request.
/// </summary>
public readonly record struct ConfirmationKey(
    string Owner,
    string Repo,
    int PrNumber,
    string Command,
    string SenderLogin);

/// <summary>
/// Deep module managing bounded, TTL-expiring, atomic in-memory author confirmation requests.
/// Pending entries are cleared automatically upon expiration or consumption and do not persist across process restarts.
/// </summary>
public sealed class PendingConfirmationStore
{
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly object _gate = new();
    private readonly Dictionary<ConfirmationKey, DateTimeOffset> _entries = new(CaseInsensitiveKeyComparer.Instance);

    public PendingConfirmationStore(TimeProvider? timeProvider = null, int maxEntries = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Gets the number of non-expired confirmation requests currently held in memory.
    /// </summary>
    public int Count
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            lock (_gate)
            {
                PruneExpiredLocked(now);
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Registers or refreshes a pending confirmation request with the specified <paramref name="ttl"/>.
    /// Prunes expired entries and evicts the earliest-expiring entry if capacity is exceeded.
    /// </summary>
    public void Register(ConfirmationKey key, TimeSpan ttl)
    {
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(ttl);

        lock (_gate)
        {
            PruneExpiredLocked(now);
            _entries[key] = expiresAt;

            if (_entries.Count > _maxEntries)
            {
                ConfirmationKey oldestKey = default;
                var earliestExpiry = DateTimeOffset.MaxValue;
                var found = false;

                foreach (var kvp in _entries)
                {
                    if (!CaseInsensitiveKeyComparer.Instance.Equals(kvp.Key, key) &&
                        (!found || kvp.Value < earliestExpiry))
                    {
                        oldestKey = kvp.Key;
                        earliestExpiry = kvp.Value;
                        found = true;
                    }
                }

                if (found)
                {
                    _entries.Remove(oldestKey);
                }
            }
        }
    }

    /// <summary>
    /// Atomically checks whether a valid, non-expired confirmation exists for <paramref name="key"/> and consumes it.
    /// Returns <c>false</c> if absent or expired.
    /// </summary>
    public bool TryConsume(ConfirmationKey key)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var expiresAt))
            {
                return false;
            }

            _entries.Remove(key);
            return now <= expiresAt;
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        List<ConfirmationKey>? expiredKeys = null;
        foreach (var kvp in _entries)
        {
            if (kvp.Value < now)
            {
                expiredKeys ??= new List<ConfirmationKey>();
                expiredKeys.Add(kvp.Key);
            }
        }

        if (expiredKeys != null)
        {
            foreach (var key in expiredKeys)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class CaseInsensitiveKeyComparer : IEqualityComparer<ConfirmationKey>
    {
        public static readonly CaseInsensitiveKeyComparer Instance = new();

        public bool Equals(ConfirmationKey x, ConfirmationKey y) =>
            x.PrNumber == y.PrNumber &&
            string.Equals(x.Owner, y.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Repo, y.Repo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Command, y.Command, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.SenderLogin, y.SenderLogin, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ConfirmationKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Owner),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Repo),
                obj.PrNumber,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Command),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SenderLogin));
    }
}
