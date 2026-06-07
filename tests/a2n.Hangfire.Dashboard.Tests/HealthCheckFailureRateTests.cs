using System;
using System.Collections.Generic;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for the failure-rate health check, focused on the false-positive guard:
/// a single failure at the start of a clock hour must not flip the check to Unhealthy
/// (which would return HTTP 503 and drop a healthy pod out of rotation).
/// </summary>
public class HealthCheckFailureRateTests
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _api = new();
    private readonly DashboardUIOptions _options = new();

    public HealthCheckFailureRateTests()
    {
        _storage.Setup(s => s.GetMonitoringApi()).Returns(_api.Object);

        // Defaults so unrelated checks stay Healthy.
        _api.Setup(m => m.GetStatistics()).Returns(new StatisticsDto());
        _api.Setup(m => m.Servers()).Returns(new List<ServerDto>
        {
            new() { Name = "srv-1", Heartbeat = DateTime.UtcNow }
        });
        _api.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        _api.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
        _api.Setup(m => m.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long>());
        _api.Setup(m => m.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long>());
    }

    private HealthCheckService CreateService()
        => new(new HangfireMonitorService(_storage.Object), _options, null);

    private void SetupHourly(Dictionary<DateTime, long> succeeded, Dictionary<DateTime, long> failed)
    {
        _api.Setup(m => m.HourlySucceededJobs()).Returns(succeeded);
        _api.Setup(m => m.HourlyFailedJobs()).Returns(failed);
    }

    [Fact]
    public void SingleFailure_BelowMinimumSample_StaysHealthy()
    {
        var hour = new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);
        // 1 failure, 0 success in the current bucket → 100% but below minimum sample (20).
        SetupHourly(
            succeeded: new Dictionary<DateTime, long> { [hour] = 0 },
            failed: new Dictionary<DateTime, long> { [hour] = 1 });

        var report = CreateService().CheckFull();

        Assert.Equal(HealthStatus.Healthy, report.Checks["failure_rate"].Status);
    }

    [Fact]
    public void HighFailureRate_AboveMinimumSample_IsUnhealthy()
    {
        var hour = new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);
        // 40 failed of 50 = 80% over the critical threshold (25%), well above minimum sample.
        SetupHourly(
            succeeded: new Dictionary<DateTime, long> { [hour] = 10 },
            failed: new Dictionary<DateTime, long> { [hour] = 40 });

        var report = CreateService().CheckFull();

        Assert.Equal(HealthStatus.Unhealthy, report.Checks["failure_rate"].Status);
    }

    [Fact]
    public void ModerateFailureRate_AboveWarnBelowCritical_IsDegraded()
    {
        var hour = new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);
        // 15 failed of 100 = 15% (warn 10%, critical 25%).
        SetupHourly(
            succeeded: new Dictionary<DateTime, long> { [hour] = 85 },
            failed: new Dictionary<DateTime, long> { [hour] = 15 });

        var report = CreateService().CheckFull();

        Assert.Equal(HealthStatus.Degraded, report.Checks["failure_rate"].Status);
    }

    [Fact]
    public void WindowSpansTwoHours()
    {
        var current = new DateTime(2026, 6, 7, 10, 0, 0, DateTimeKind.Utc);
        var previous = current.AddHours(-1);
        var older = current.AddHours(-2); // must be excluded by the 2-hour window
        SetupHourly(
            succeeded: new Dictionary<DateTime, long> { [current] = 30, [previous] = 30, [older] = 1000 },
            failed: new Dictionary<DateTime, long> { [current] = 20, [previous] = 20, [older] = 0 });

        var report = CreateService().CheckFull();
        var data = report.Checks["failure_rate"].Data;

        // 40 failed of 100 across the two most-recent buckets → Unhealthy; the older bucket ignored.
        Assert.Equal(100L, Convert.ToInt64(data["windowSucceeded"]) + Convert.ToInt64(data["windowFailed"]));
        Assert.Equal(HealthStatus.Unhealthy, report.Checks["failure_rate"].Status);
    }

    [Fact]
    public void NoCompletedJobs_IsHealthy()
    {
        var report = CreateService().CheckFull();
        Assert.Equal(HealthStatus.Healthy, report.Checks["failure_rate"].Status);
    }
}
