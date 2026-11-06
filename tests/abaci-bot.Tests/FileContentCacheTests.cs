using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using abaci_bot.Options;
using abaci_bot.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace abaci_bot.Tests;

public class FileContentCacheTests
{
    [Fact]
    public async Task GetOrCreateAsync_DeduplicatesConcurrentRequests_InvokesFactoryOnce()
    {
        var cache = new FileContentCache();
        var callCount = 0;
        var tcs = new TaskCompletionSource<string>();

        async Task<string> SlowFactory()
        {
            Interlocked.Increment(ref callCount);
            return await tcs.Task;
        }

        var task1 = cache.GetOrCreateAsync("key1", SlowFactory);
        var task2 = cache.GetOrCreateAsync("key1", SlowFactory);
        var task3 = cache.GetOrCreateAsync("key1", SlowFactory);

        tcs.SetResult("content1");

        var results = await Task.WhenAll(task1, task2, task3);

        Assert.Equal("content1", results[0]);
        Assert.Equal("content1", results[1]);
        Assert.Equal("content1", results[2]);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenFactoryThrows_DoesNotPoisonCache_SubsequentCallRetries()
    {
        var cache = new FileContentCache();
        var callCount = 0;

        Task<string> FailingFactory()
        {
            Interlocked.Increment(ref callCount);
            throw new InvalidOperationException("Simulated transient failure");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync("failing_key", FailingFactory));
        Assert.Equal(1, callCount);

        // Next call should cleanly retry rather than serving the faulted task
        var result = await cache.GetOrCreateAsync("failing_key", () =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult("recovered_content");
        });

        Assert.Equal("recovered_content", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenFactoryCancelled_DoesNotPoisonCache()
    {
        var cache = new FileContentCache();
        var callCount = 0;

        async Task<string> CancelledFactory()
        {
            Interlocked.Increment(ref callCount);
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Task.Delay(10, cts.Token);
            return "never_reached";
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetOrCreateAsync("cancelled_key", CancelledFactory));
        Assert.Equal(1, callCount);

        var result = await cache.GetOrCreateAsync("cancelled_key", () =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult("valid_content");
        });

        Assert.Equal("valid_content", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_BoundedByteLimit_EvictsOldestEntriesInLruOrder()
    {
        // 1 char = 2 bytes in memory.
        // Key length + content length:
        // "k1" (2 chars = 4 bytes) + "abc" (3 chars = 6 bytes) = 10 bytes.
        // With max size = 25 bytes, inserting 3 entries of 10 bytes will evict the first entry.
        var cache = new FileContentCache(maxSizeBytes: 25);

        await cache.GetOrCreateAsync("k1", () => Task.FromResult("abc"));
        await cache.GetOrCreateAsync("k2", () => Task.FromResult("abc"));
        Assert.True(cache.CurrentBytes <= 25);
        Assert.Equal(2, cache.Count);

        // Access k1 to make k2 the least recently used
        var readK1 = await cache.GetOrCreateAsync("k1", () => Task.FromResult("abc"));
        Assert.Equal("abc", readK1);

        // Insert k3 -> should evict k2 because k1 was touched
        var k2CallCount = 0;
        await cache.GetOrCreateAsync("k3", () => Task.FromResult("abc"));

        // Now if we read k2, factory should be invoked again because it was evicted
        var readK2 = await cache.GetOrCreateAsync("k2", () =>
        {
            Interlocked.Increment(ref k2CallCount);
            return Task.FromResult("abc_reloaded");
        });

        Assert.Equal("abc_reloaded", readK2);
        Assert.Equal(1, k2CallCount);
    }

    [Fact]
    public async Task Clear_ResetsCountAndCurrentBytes()
    {
        var cache = new FileContentCache();
        await cache.GetOrCreateAsync("item1", () => Task.FromResult("hello world"));
        Assert.True(cache.CurrentBytes > 0);
        Assert.Equal(1, cache.Count);

        cache.Clear();
        Assert.Equal(0, cache.CurrentBytes);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithOptionsConstructor_UsesConfiguredMaxSizeBytes()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FileCacheOptions
        {
            MaxSizeBytes = 100
        });
        var cache = new FileContentCache(options);

        await cache.GetOrCreateAsync("key1", () => Task.FromResult("content"));
        Assert.True(cache.CurrentBytes > 0 && cache.CurrentBytes <= 100);
    }
}

