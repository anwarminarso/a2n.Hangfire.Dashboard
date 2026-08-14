using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="ThrottlingDetectionCache"/> — the single-flight / TTL behavior the cache
/// exists for, and the invalidation path that lets a newly registered primitive show up without
/// waiting out the TTL.
/// </summary>
public class ThrottlingDetectionCacheTests
{
    // Counts how many times the detection actually reaches storage.
    private int _reads;

    private readonly JobStorage _storage = new InMemoryStorage();

    private ServiceProvider BuildProvider(int probeDelayMs = 0)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DashboardUIOptions());
        services.AddScoped(sp => new CountingReader(_storage, () =>
        {
            Interlocked.Increment(ref _reads);
            if (probeDelayMs > 0) Thread.Sleep(probeDelayMs);
        }));
        // The cache resolves ThrottlingDataReader from the scope, so the counting subclass has to
        // be what that resolves to.
        services.AddScoped<ThrottlingDataReader>(sp => sp.GetRequiredService<CountingReader>());
        services.AddSingleton<ThrottlingDetectionCache>();
        return services.BuildServiceProvider();
    }

    private void SeedSemaphore()
    {
        using var connection = _storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.AddToSet("sync:set:sm", "email-dispatch");
        transaction.Commit();
    }

    [Fact]
    public async Task SecondCallWithinTtl_ReusesResult_NoSecondRead()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingDetectionCache>();

        Assert.False(await cache.GetAsync());
        Assert.False(await cache.GetAsync());

        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task NegativeResultIsCached_NotJustPositiveOne()
    {
        // The expensive case is the installation without Hangfire.Throttling: it answers "no" only
        // after counting every registry set, and has nothing to latch onto, so it is precisely the
        // case the cache has to cover.
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingDetectionCache>();

        for (var i = 0; i < 10; i++)
        {
            Assert.False(await cache.GetAsync());
        }

        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task ExpiredTtl_ReadsAgain()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingDetectionCache>();
        cache.Ttl = TimeSpan.FromMilliseconds(20);

        Assert.False(await cache.GetAsync());
        await Task.Delay(80);
        Assert.False(await cache.GetAsync());

        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareASingleRead()
    {
        var sp = BuildProvider(probeDelayMs: 120);
        var cache = sp.GetRequiredService<ThrottlingDetectionCache>();

        // Many tabs opening at once is the situation that motivates sharing: without the gate each
        // circuit would issue its own set counts against the same storage.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync()));

        Assert.All(results, Assert.False);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task Invalidate_MakesNewlyRegisteredPrimitiveVisibleImmediately()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingDetectionCache>();

        Assert.False(await cache.GetAsync());

        SeedSemaphore();
        cache.Invalidate();

        Assert.True(await cache.GetAsync());
        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task ForceRefresh_BypassesCache()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingDetectionCache>();

        Assert.False(await cache.GetAsync());
        SeedSemaphore();

        Assert.True(await cache.GetAsync(forceRefresh: true));
        Assert.Equal(2, _reads);
    }

    /// <summary>
    /// A real reader over real in-memory storage, with a hook so the tests can count how many
    /// detections actually reach storage rather than being served from the cache.
    /// </summary>
    private sealed class CountingReader : ThrottlingDataReader
    {
        private readonly Action _onRead;

        public CountingReader(JobStorage storage, Action onRead) : base(storage) => _onRead = onRead;

        public override bool HasThrottlingData()
        {
            _onRead();
            return base.HasThrottlingData();
        }
    }
}
