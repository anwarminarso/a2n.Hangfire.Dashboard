using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Example-based unit tests for <see cref="HeatmapService"/>'s Projected-source orchestration
/// (task 9.4). These cover behavior that is not a universal property and so is not exercised by the
/// FsCheck pure-engine suites:
/// <list type="bullet">
/// <item>empty-state rendering when there are no recurring jobs or storage is unreadable (Req 1.5, 1.7);</item>
/// <item>the capacity source indicator — detected vs. manually overridden (Req 5.3);</item>
/// <item>cache hit serving without recompute (Req 13.2);</item>
/// <item>single-flight computing exactly once under concurrency (Req 13.4);</item>
/// <item>stale-while-revalidate serving a stale value immediately while refreshing in the
/// background (Req 13.8).</item>
/// </list>
///
/// The Projected source reads recurring jobs through the storage-agnostic
/// <c>connection.GetRecurringJobs()</c>. We mock <see cref="JobStorage"/> + <see cref="IStorageConnection"/>
/// so <c>GetAllItemsFromSet("recurring-jobs")</c> drives the recurring-job set and doubles as a
/// recompute counter (it is hit exactly once per aggregation computation). This lets us assert the
/// caching contracts deterministically without standing up real Hangfire storage.
/// </summary>
public class HeatmapServiceProjectedTests
{
    /// <summary>The well-known Hangfire set key holding recurring-job ids (read by GetRecurringJobs()).</summary>
    private const string RecurringJobsSetKey = "recurring-jobs";

    /// <summary>
    /// Builds a <see cref="HeatmapService"/> backed by a mocked storage/connection. The returned
    /// <paramref name="computeCount"/> reports how many times the aggregation was (re)computed — i.e.
    /// how many times the recurring-job set was read.
    /// </summary>
    /// <param name="recurringJobIds">Ids returned by the recurring-jobs set (empty by default → empty matrix).</param>
    /// <param name="cacheTtlSeconds">The aggregation cache freshness window (Req 13.5).</param>
    /// <param name="computeDelay">An optional per-compute delay used to make caching races observable.</param>
    /// <param name="servers">Optional server list backing capacity detection; <c>null</c> leaves the monitoring API unset.</param>
    /// <param name="throwOnConnection">When <c>true</c>, opening a connection throws (unreadable storage, Req 1.7).</param>
    private static (HeatmapService service, Func<int> computeCount) CreateProjectedService(
        IEnumerable<string> recurringJobIds = null,
        int cacheTtlSeconds = 60,
        TimeSpan? computeDelay = null,
        IList<ServerDto> servers = null,
        bool throwOnConnection = false)
    {
        var computeCounter = 0;
        var ids = new HashSet<string>(recurringJobIds ?? Enumerable.Empty<string>());

        var connection = new Mock<IStorageConnection>();
        connection.Setup(c => c.GetAllItemsFromSet(RecurringJobsSetKey))
            .Returns(() =>
            {
                Interlocked.Increment(ref computeCounter);
                if (computeDelay.HasValue)
                {
                    Thread.Sleep(computeDelay.Value);
                }
                return ids;
            });

        var storage = new Mock<JobStorage>();
        if (throwOnConnection)
        {
            storage.Setup(s => s.GetConnection()).Throws(new InvalidOperationException("storage unreadable"));
        }
        else
        {
            storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        }

        if (servers != null)
        {
            var monitoringApi = new Mock<IMonitoringApi>();
            monitoringApi.Setup(m => m.Servers()).Returns(servers);
            storage.Setup(s => s.GetMonitoringApi()).Returns(monitoringApi.Object);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton<JobStorage>(storage.Object);
        services.AddSingleton(new HangfireMonitorService(storage.Object));

        var options = new DashboardUIOptions();
        options.Heatmap.CacheTtlSeconds = cacheTtlSeconds;
        services.AddSingleton(options);

        var sp = services.BuildServiceProvider();
        return (new HeatmapService(sp), () => Volatile.Read(ref computeCounter));
    }

    private static HeatmapQuery ProjectedQuery(
        LoadMetric metric = LoadMetric.FireCount,
        ProjectionWindowKind window = ProjectionWindowKind.IdealizedWeek,
        string viewerTz = null) => new(
        Source: HeatmapSource.Projected,
        WindowKind: window,
        ViewerTimeZoneId: viewerTz,
        JobClass: "Cron",
        LoadMetric: metric,
        TopN: 10,
        HideSubHourly: false,
        LogScale: false,
        LookbackWeeks: 4,
        AggregationStatistic: "Average",
        ManualCapacity: null);

    // ─── Empty-state (Req 1.5, 1.7) ─────────────────────────────────────────────

    [Fact]
    public async Task ProjectedRequest_WithNoJobStorage_RendersEmptyStateWithoutError()
    {
        // No JobStorage registered at all → the service must render the empty state, not throw (Req 1.7).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton(new DashboardUIOptions());
        var service = new HeatmapService(services.BuildServiceProvider());

        var result = await service.GetMatrixAsync(ProjectedQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Matrix.Cells);
        Assert.Empty(result.Matrix.Queues);
        Assert.Null(result.HistoricalError);
        Assert.Empty(result.UnparseableJobIds);
        Assert.Empty(result.UnknownTimeZoneJobIds);
        Assert.Empty(result.LongPeriodJobIds);
    }

    [Fact]
    public async Task ProjectedRequest_WithNoRecurringJobs_RendersEmptyState()
    {
        // Storage is readable but the recurring-jobs set is empty (Req 1.5).
        var (service, computeCount) = CreateProjectedService(recurringJobIds: Array.Empty<string>());

        var result = await service.GetMatrixAsync(ProjectedQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Matrix.Cells);
        Assert.Equal(0d, result.Matrix.Min);
        Assert.Equal(0d, result.Matrix.Max);
        Assert.Null(result.HistoricalError);
        Assert.Equal(1, computeCount());
    }

    [Fact]
    public async Task ProjectedRequest_WhenStorageUnreadable_RendersEmptyStateWithoutError()
    {
        // Opening a connection throws — the service swallows it and renders the empty state (Req 1.7).
        var (service, _) = CreateProjectedService(throwOnConnection: true);

        var result = await service.GetMatrixAsync(ProjectedQuery(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Matrix.Cells);
        Assert.Null(result.HistoricalError);
    }

    // ─── Capacity source indicator (Req 5.3, with 5.1 / 5.4) ────────────────────

    [Fact]
    public void ResolveCapacity_WithServers_ReportsDetectedSumOfWorkerCounts()
    {
        var servers = new List<ServerDto>
        {
            new() { Name = "srv-1", WorkersCount = 3 },
            new() { Name = "srv-2", WorkersCount = 5 },
        };
        var (service, _) = CreateProjectedService(servers: servers);

        var capacity = service.ResolveCapacity(manualOverride: null);

        Assert.Equal(CapacitySource.Detected, capacity.Source); // "detected" indicator (Req 5.3)
        Assert.Equal(8, capacity.Capacity);                     // 3 + 5 (Req 5.1)
    }

    [Fact]
    public void ResolveCapacity_WithNoServers_ReportsDetectedCapacityOfOne()
    {
        var (service, _) = CreateProjectedService(servers: new List<ServerDto>());

        var capacity = service.ResolveCapacity(manualOverride: null);

        Assert.Equal(CapacitySource.Detected, capacity.Source);
        Assert.Equal(1, capacity.Capacity); // zero servers → capacity 1 (Req 5.4)
    }

    [Fact]
    public void ResolveCapacity_WithManualOverride_ReportsManuallyOverriddenSource()
    {
        // A manual override wins over detection and is reported as "manually overridden" (Req 5.3).
        var servers = new List<ServerDto>
        {
            new() { Name = "srv-1", WorkersCount = 4 },
        };
        var (service, _) = CreateProjectedService(servers: servers);

        var capacity = service.ResolveCapacity(manualOverride: 25);

        Assert.Equal(CapacitySource.ManualOverride, capacity.Source);
        Assert.Equal(25, capacity.Capacity);
    }

    // ─── Cache hit serves without recompute (Req 13.2) ──────────────────────────

    [Fact]
    public async Task ProjectedRequest_RepeatedSameKey_ServedFromCacheWithoutRecompute()
    {
        var (service, computeCount) = CreateProjectedService();

        var first = await service.GetMatrixAsync(ProjectedQuery(), CancellationToken.None);
        var second = await service.GetMatrixAsync(ProjectedQuery(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // The second request shares the cache key (source/window/tz/metric) and is served without a
        // recompute (Req 13.2): the recurring-job set was read exactly once.
        Assert.Equal(1, computeCount());
    }

    [Fact]
    public async Task ProjectedRequest_DifferentWindow_RecomputesUnderDistinctKey()
    {
        // A control change that alters the cache key (the projection window) does require a recompute,
        // confirming the cache is keyed (and not over-caching across distinct requests).
        var (service, computeCount) = CreateProjectedService();

        await service.GetMatrixAsync(ProjectedQuery(window: ProjectionWindowKind.IdealizedWeek), CancellationToken.None);
        await service.GetMatrixAsync(ProjectedQuery(window: ProjectionWindowKind.Next7Days), CancellationToken.None);

        Assert.Equal(2, computeCount());
    }

    // ─── Single-flight under concurrency (Req 13.4) ─────────────────────────────

    [Fact]
    public async Task ProjectedRequest_ConcurrentColdCalls_ComputeExactlyOnce()
    {
        // A slow compute makes the cold-key race observable: many concurrent callers for the same key
        // must serialize on the per-key gate so the aggregation is computed exactly once (Req 13.4).
        var (service, computeCount) = CreateProjectedService(computeDelay: TimeSpan.FromMilliseconds(250));

        const int concurrency = 10;
        var query = ProjectedQuery();
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => service.GetMatrixAsync(query, CancellationToken.None)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(1, computeCount()); // single-flight: computed once, shared by all callers
    }

    // ─── Stale-while-revalidate (Req 13.8) ──────────────────────────────────────

    [Fact]
    public async Task ProjectedRequest_AfterTtlExpiry_ServesStaleImmediatelyThenRefreshes()
    {
        // TTL of 1s (the minimum) plus a 1s compute so a stale hit that recomputed inline would be
        // obviously slow. A correct stale-while-revalidate serves the cached value immediately and
        // refreshes in the background (Req 13.8).
        var (service, computeCount) = CreateProjectedService(
            cacheTtlSeconds: 1,
            computeDelay: TimeSpan.FromSeconds(1));

        // Prime the cache (cold compute → count 1).
        await service.GetMatrixAsync(ProjectedQuery(), CancellationToken.None);
        Assert.Equal(1, computeCount());

        // Let the entry pass its freshness window so it is now stale (but still physically retained).
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        // The stale hit must return quickly — well under the 1s recompute cost — proving it served the
        // stale value rather than blocking on a recompute.
        var sw = Stopwatch.StartNew();
        var staleResult = await service.GetMatrixAsync(ProjectedQuery(), CancellationToken.None);
        sw.Stop();

        Assert.NotNull(staleResult);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Stale value should be served immediately, but the call took {sw.Elapsed}.");

        // The refresh runs in the background; eventually the recompute count reaches 2.
        var refreshed = await WaitForCountAsync(computeCount, expected: 2, timeout: TimeSpan.FromSeconds(5));
        Assert.True(refreshed, $"Background refresh should have recomputed; compute count = {computeCount()}.");
    }

    /// <summary>Polls <paramref name="counter"/> until it reaches <paramref name="expected"/> or the timeout elapses.</summary>
    private static async Task<bool> WaitForCountAsync(Func<int> counter, int expected, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (counter() >= expected)
            {
                return true;
            }
            await Task.Delay(50);
        }
        return counter() >= expected;
    }
}
