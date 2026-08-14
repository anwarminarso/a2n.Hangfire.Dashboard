using System;
using System.Collections.Generic;
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
/// Tests for <see cref="ThrottlingSnapshotCache"/> — the sharing that lets the Throttling page
/// auto-refresh without multiplying storage load by the number of open tabs, and the shape of the
/// snapshot the page renders from.
/// </summary>
public class ThrottlingSnapshotCacheTests
{
    // Counts how many times a snapshot read actually reaches storage.
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
        services.AddScoped<ThrottlingDataReader>(sp => sp.GetRequiredService<CountingReader>());
        services.AddSingleton<ThrottlingSnapshotCache>();
        return services.BuildServiceProvider();
    }

    private void Seed(Action<IWriteOnlyTransaction> write)
    {
        using var connection = _storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        write(transaction);
        transaction.Commit();
    }

    private void SeedSemaphore(string id, string max, params string[] holders)
        => Seed(tx =>
        {
            tx.AddToSet("sync:set:sm", id);
            tx.SetRangeInHash($"sync:sm:{id}", new Dictionary<string, string> { ["max"] = max, ["d"] = "" });
            foreach (var holder in holders) tx.AddToSet($"sync:j:sm:{id}", holder);
        });

    private void SeedMutex(string id, string holder)
        => Seed(tx =>
        {
            tx.AddToSet("sync:set:mx", $"{id}/{holder}");
            tx.AddToSet($"sync:mx:{id}", holder);
        });

    [Fact]
    public async Task SecondCallWithinTtl_ReusesSnapshot_NoSecondRead()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingSnapshotCache>();

        await cache.GetAsync();
        await cache.GetAsync();

        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task ConcurrentTabs_ShareASingleRead()
    {
        // This is the reason the cache exists: the page refreshes on a timer, so without sharing,
        // every open tab would issue its own full pass on every tick.
        var sp = BuildProvider(probeDelayMs: 120);
        var cache = sp.GetRequiredService<ThrottlingSnapshotCache>();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync()));

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task ExpiredTtl_ReadsAgain()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingSnapshotCache>();
        cache.Ttl = TimeSpan.FromMilliseconds(20);

        await cache.GetAsync();
        await Task.Delay(80);
        await cache.GetAsync();

        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task Invalidate_ShowsADetachImmediately()
    {
        // Detach invalidates so the operator sees the freed slot at once rather than up to a TTL
        // later — the case where a stale read would look like the detach silently failed.
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<ThrottlingSnapshotCache>();

        SeedSemaphore("email-dispatch", "10", "41201");
        Assert.Single(Assert.Single((await cache.GetAsync()).Semaphores).HolderJobIds);

        var ops = new ThrottlingOperationsService(_storage, new AuditLogService(_storage, new DashboardUIOptions(), null, null, null));
        Assert.True(ops.DetachFromSemaphore("email-dispatch", "41201"));
        cache.Invalidate();

        Assert.Empty(Assert.Single((await cache.GetAsync()).Semaphores).HolderJobIds);
    }

    [Fact]
    public async Task Snapshot_FlattensMutexHolders_WithResolvedState()
    {
        SeedMutex("resource_a", "41201");
        SeedMutex("resource_b", "41202");

        var snapshot = await BuildProvider().GetRequiredService<ThrottlingSnapshotCache>().GetAsync();

        Assert.Equal(2, snapshot.Mutexes.Count);
        Assert.All(snapshot.Mutexes, x => Assert.NotNull(x.Holder));

        // Neither job exists, so both resolve as orphans — proving holder state was resolved rather
        // than left for the page to fill in.
        Assert.All(snapshot.Mutexes, x => Assert.True(x.Holder.IsOrphaned));
    }

    [Fact]
    public async Task Snapshot_KeepsMutexWithNoHolder()
    {
        Seed(tx => tx.AddToSet("sync:set:mx", "bare_entry"));

        var snapshot = await BuildProvider().GetRequiredService<ThrottlingSnapshotCache>().GetAsync();

        var row = Assert.Single(snapshot.Mutexes);
        Assert.Equal("bare_entry", row.MutexId);
        Assert.Null(row.Holder);
    }

    [Fact]
    public async Task Snapshot_CarriesSemaphoresAndWindows()
    {
        SeedSemaphore("email-dispatch", "10", "41201");
        Seed(tx =>
        {
            tx.AddToSet("sync:set:fw", "partner-api-uploads");
            tx.SetRangeInHash("sync:fw:partner-api-uploads", new Dictionary<string, string>
            {
                ["obj"] = "{\"l\":10,\"i\":3600,\"c\":4}",
            });
        });

        var snapshot = await BuildProvider().GetRequiredService<ThrottlingSnapshotCache>().GetAsync();

        Assert.Equal(10, Assert.Single(snapshot.Semaphores).MaxCount);
        Assert.Equal(4, Assert.Single(snapshot.Windows).Counter);
    }

    /// <summary>
    /// A real reader over real in-memory storage, with a hook so the tests can count how many
    /// snapshot reads actually reach storage rather than being served from the cache.
    /// </summary>
    private sealed class CountingReader : ThrottlingDataReader
    {
        private readonly Action _onRead;

        public CountingReader(JobStorage storage, Action onRead) : base(storage) => _onRead = onRead;

        public override IReadOnlyList<Models.SemaphoreDto> GetSemaphores()
        {
            _onRead();
            return base.GetSemaphores();
        }
    }
}
