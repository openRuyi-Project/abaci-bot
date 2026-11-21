using abaci_bot.Services;
using Xunit;

namespace abaci_bot.Tests;

public class PendingConfirmationStoreTests
{
    [Fact]
    public void Constructor_NonPositiveMaxEntries_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PendingConfirmationStore(maxEntries: 0));
    }

    [Fact]
    public void RegisterAndTryConsume_CaseInsensitiveAtomicConsumption_SucceedsOnce()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
        var store = new PendingConfirmationStore(time);

        var key = new ConfirmationKey("Owner", "Repo", 42, "lock", "Alice");
        store.Register(key, TimeSpan.FromMinutes(15));
        Assert.Equal(1, store.Count);

        // Consuming with different casing must succeed atomically once
        var matchingKey = new ConfirmationKey("owner", "repo", 42, "LOCK", "alice");
        Assert.True(store.TryConsume(matchingKey));
        Assert.Equal(0, store.Count);

        // Second consume (replay) fails
        Assert.False(store.TryConsume(matchingKey));
    }

    [Fact]
    public void RegisterAndTryConsume_IsolatesByCommandPrAndSender()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
        var store = new PendingConfirmationStore(time);

        store.Register(new ConfirmationKey("owner", "repo", 42, "lock", "alice"), TimeSpan.FromMinutes(15));

        Assert.False(store.TryConsume(new ConfirmationKey("owner", "repo", 42, "archive", "alice")));
        Assert.False(store.TryConsume(new ConfirmationKey("owner", "repo", 43, "lock", "alice")));
        Assert.False(store.TryConsume(new ConfirmationKey("owner", "repo", 42, "lock", "bob")));
        Assert.True(store.TryConsume(new ConfirmationKey("owner", "repo", 42, "lock", "alice")));
    }

    [Fact]
    public void TryConsume_ExpiredEntry_ReturnsFalseAndRemovesEntry()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
        var store = new PendingConfirmationStore(time);

        var key = new ConfirmationKey("owner", "repo", 42, "lock", "alice");
        store.Register(key, TimeSpan.FromMinutes(15));

        time.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));

        Assert.False(store.TryConsume(key));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Register_PrunesExpiredEntriesAndEnforcesMaxEntriesCap()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
        var store = new PendingConfirmationStore(time, maxEntries: 2);

        store.Register(new ConfirmationKey("o", "r", 1, "lock", "u1"), TimeSpan.FromMinutes(5));
        store.Register(new ConfirmationKey("o", "r", 2, "lock", "u2"), TimeSpan.FromMinutes(10));
        // Exceeding maxEntries (2) must evict earliest-expiring entry (#1)
        store.Register(new ConfirmationKey("o", "r", 3, "lock", "u3"), TimeSpan.FromMinutes(20));

        Assert.Equal(2, store.Count);
        Assert.False(store.TryConsume(new ConfirmationKey("o", "r", 1, "lock", "u1")));
        Assert.True(store.TryConsume(new ConfirmationKey("o", "r", 2, "lock", "u2")));
        Assert.True(store.TryConsume(new ConfirmationKey("o", "r", 3, "lock", "u3")));

        // Verify Count prunes stale entries lazily
        store.Register(new ConfirmationKey("o", "r", 4, "lock", "u4"), TimeSpan.FromMinutes(5));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(0, store.Count);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset initial) => _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
