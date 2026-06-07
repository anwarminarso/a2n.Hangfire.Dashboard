using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="HealthReportCache"/> — proving the single-flight / TTL behavior that the
/// whole cache exists for, plus the force-refresh bypass used by the manual UI refresh button.
/// </summary>
public class HealthReportCacheTests
{
    // Counts how many times the storage probe (GetStatistics) actually runs.
    private int _statisticsCalls;

    private ServiceProvider BuildProvider(int probeDelayMs = 0)
    {
        var api = new Mock<IMonitoringApi>();
        api.Setup(m => m.GetStatistics()).Returns(() =>
        {
            Interlocked.Increment(ref _statisticsCalls);
            if (probeDelayMs > 0) Thread.Sleep(probeDelayMs);
            return new StatisticsDto();
        });
        // Liveness only touches GetStatistics; keep the rest minimal for Full mode if used.
        api.Setup(m => m.Servers()).Returns(new List<ServerDto> { new() { Name = "s", Heartbeat = DateTime.UtcNow } });
        api.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        api.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
        api.Setup(m => m.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long>());
        api.Setup(m => m.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long>());

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);

        var services = new ServiceCollection();
        services.AddSingleton(new DashboardUIOptions());
        services.AddSingleton(new HangfireMonitorService(storage.Object));
        services.AddScoped(sp => new HealthCheckService(
            sp.GetRequiredService<HangfireMonitorService>(),
            sp.GetRequiredService<DashboardUIOptions>(),
            null));
        services.AddSingleton<HealthReportCache>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SecondCallWithinTtl_ReusesCachedReport_NoSecondProbe()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<HealthReportCache>();

        var first = await cache.GetAsync(HealthReportCache.Mode.Live);
        var second = await cache.GetAsync(HealthReportCache.Mode.Live);

        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref _statisticsCalls));
    }

    [Fact]
    public async Task ConcurrentCallers_ShareSingleComputation()
    {
        // Slow probe so all callers pile up behind the gate at the same time.
        var sp = BuildProvider(probeDelayMs: 200);
        var cache = sp.GetRequiredService<HealthReportCache>();

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => cache.GetAsync(HealthReportCache.Mode.Live))
            .ToArray();
        var reports = await Task.WhenAll(tasks);

        // Exactly one probe ran; everyone got the same instance.
        Assert.Equal(1, Volatile.Read(ref _statisticsCalls));
        Assert.All(reports, r => Assert.Same(reports[0], r));
    }

    [Fact]
    public async Task ForceRefresh_BypassesCache_AndComputesAgain()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<HealthReportCache>();

        await cache.GetAsync(HealthReportCache.Mode.Live);                  // probe #1
        await cache.GetAsync(HealthReportCache.Mode.Live);                  // cached, no probe
        await cache.GetAsync(HealthReportCache.Mode.Live, forceRefresh: true); // probe #2

        Assert.Equal(2, Volatile.Read(ref _statisticsCalls));
    }

    [Fact]
    public async Task DifferentModes_AreCachedIndependently()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<HealthReportCache>();

        await cache.GetAsync(HealthReportCache.Mode.Live);  // probe (storage only)
        await cache.GetAsync(HealthReportCache.Mode.Full);  // probe (storage + others)

        // Live and Full each ran the storage probe once → 2 total; they don't share a slot.
        Assert.Equal(2, Volatile.Read(ref _statisticsCalls));
    }

    [Fact]
    public async Task ExpiredTtl_RecomputesReport()
    {
        var sp = BuildProvider();
        var cache = sp.GetRequiredService<HealthReportCache>();
        cache.Ttl = TimeSpan.FromMilliseconds(50);

        await cache.GetAsync(HealthReportCache.Mode.Live);
        await Task.Delay(120);
        await cache.GetAsync(HealthReportCache.Mode.Live);

        Assert.Equal(2, Volatile.Read(ref _statisticsCalls));
    }
}
