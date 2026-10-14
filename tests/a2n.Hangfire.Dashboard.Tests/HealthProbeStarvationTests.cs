using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// The health checks on a stalled storage (issue #64). The storage probe used to block a thread on
/// <c>Task.Wait</c> and abandon the read, adding one blocked thread per probe, and then ran five
/// more synchronous checks with no timeout. Now it goes through the shared metrics feed, so a
/// stalled read is never doubled, and the other checks are skipped when it fails.
/// </summary>
public sealed class HealthProbeStarvationTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly ManualResetEventSlim _releaseStatistics = new(initialState: true);
    private readonly ManualResetEventSlim _releaseQueues = new(initialState: true);
    private readonly Mock<IMonitoringApi> _api = new();
    private readonly Mock<JobStorage> _storage = new();
    private readonly DashboardUIOptions _options = new();
    private readonly ServiceProvider _services;
    private int _statisticsCalls;

    public HealthProbeStarvationTests()
    {
        _api.Setup(m => m.GetStatistics()).Returns(() =>
        {
            Interlocked.Increment(ref _statisticsCalls);
            _releaseStatistics.Wait(Wait);
            return new StatisticsDto();
        });
        _api.Setup(m => m.Servers()).Returns(new List<ServerDto> { new() { Name = "s", Heartbeat = DateTime.UtcNow } });
        _api.Setup(m => m.Queues()).Returns(() =>
        {
            _releaseQueues.Wait(Wait);
            return new List<QueueWithTopEnqueuedJobsDto>();
        });
        _api.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
        _api.Setup(m => m.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long>());
        _api.Setup(m => m.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long>());
        _storage.Setup(s => s.GetMonitoringApi()).Returns(_api.Object);

        _options.HealthCheckThresholds.StorageProbeTimeoutMs = 200;

        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddSingleton(new HangfireMonitorService(_storage.Object));
        services.AddSingleton<DashboardSubscriptionTracker>();
        services.AddSingleton<DashboardMetricsFeed>();
        services.AddSingleton<DashboardStorageGate>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _releaseStatistics.Set();
        _releaseQueues.Set();
        _services.Dispose();
    }

    private DashboardMetricsFeed Feed => _services.GetRequiredService<DashboardMetricsFeed>();

    private HealthCheckService CreateService() => new(
        _services.GetRequiredService<HangfireMonitorService>(), _options, logger: null,
        Feed, _services.GetRequiredService<DashboardStorageGate>());

    [Fact]
    public async Task AStalledStorage_CostsOneRead_HoweverOftenTheProbeRuns()
    {
        _releaseStatistics.Reset();
        var service = CreateService();

        for (var i = 0; i < 5; i++)
        {
            var report = await service.CheckLivenessAsync().WaitAsync(Wait);
            Assert.Equal(HealthStatus.Unhealthy, report.Status);
            Assert.True((bool)report.Checks["storage"].Data["timedOut"]);
        }

        // Five probes, each giving up after 200 ms: one read in storage, not five.
        Assert.Equal(1, Volatile.Read(ref _statisticsCalls));

        _releaseStatistics.Set();
        await WaitUntil(() => Feed.Latest is not null);
        // The read that finished is reused. It took about a second, so it may report Degraded.
        var recovered = await service.CheckLivenessAsync().WaitAsync(Wait);
        Assert.NotEqual(HealthStatus.Unhealthy, recovered.Status);
        Assert.False(recovered.Checks["storage"].Data.ContainsKey("timedOut"));
        Assert.Equal(1, Volatile.Read(ref _statisticsCalls));
    }

    [Fact]
    public async Task WhenTheProbeTimesOut_TheOtherChecksAreSkipped_WithoutReadingStorage()
    {
        _releaseStatistics.Reset();

        var report = await CreateService().CheckFullAsync().WaitAsync(Wait);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        foreach (var name in new[] { "servers", "queue_depth", "stuck_processing", "failure_rate", "recurring_jobs" })
            Assert.True((bool)report.Checks[name].Data["skipped"], name);
        _api.Verify(m => m.Queues(), Times.Never);
        _api.Verify(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _api.Verify(m => m.HourlySucceededJobs(), Times.Never);
        _storage.Verify(s => s.GetConnection(), Times.Never);
    }

    [Fact]
    public async Task AFreshMetricsSnapshot_IsReused_AndReportsHowLongItsReadTook()
    {
        Feed.PublishMetrics(new DashboardMetricsSnapshot
        {
            Statistics = new StatisticsDto(),
            Servers = [new ServerDto { Name = "s", Heartbeat = DateTime.UtcNow }],
            CapturedAtUtc = DateTime.UtcNow,
            ReadDuration = TimeSpan.FromSeconds(2),
        });

        var report = await CreateService().CheckReadinessAsync().WaitAsync(Wait);

        Assert.Equal(0, Volatile.Read(ref _statisticsCalls));
        _api.Verify(m => m.Servers(), Times.Never);
        // 2 s is above the 1 s warn threshold.
        Assert.Equal(HealthStatus.Degraded, report.Checks["storage"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Checks["servers"].Status);
    }

    [Fact]
    public async Task AForcedProbe_ReadsStorageAgain()
    {
        Feed.PublishMetrics(new DashboardMetricsSnapshot { CapturedAtUtc = DateTime.UtcNow });

        await CreateService().CheckLivenessAsync(forceProbe: true).WaitAsync(Wait);

        Assert.Equal(1, Volatile.Read(ref _statisticsCalls));
    }

    [Fact]
    public async Task ACheckThatHangs_TimesOut_AndTheOthersStillRun()
    {
        _releaseQueues.Reset();

        var report = await CreateService().CheckFullAsync().WaitAsync(Wait);

        Assert.Equal(HealthStatus.Healthy, report.Checks["storage"].Status);
        Assert.Equal(HealthStatus.Degraded, report.Checks["queue_depth"].Status);
        Assert.True((bool)report.Checks["queue_depth"].Data["timedOut"]);
        Assert.False(report.Checks["failure_rate"].Data.ContainsKey("timedOut"));
        _api.Verify(m => m.HourlySucceededJobs(), Times.Once);
    }

    [Fact]
    public void TheSynchronousApi_StillReturnsTheFullReport()
    {
        var report = CreateService().CheckFull();

        Assert.Equal(6, report.Checks.Count);
        Assert.Equal(HealthStatus.Healthy, report.Checks["storage"].Status);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }
}
