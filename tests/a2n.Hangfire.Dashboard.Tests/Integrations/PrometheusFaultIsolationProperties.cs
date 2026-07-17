using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using a2n.Hangfire.Dashboard.Services.Prometheus;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for the <see cref="PrometheusExporter"/>'s per-family fault isolation.
///
/// Feature: integrations-v2-6, Property 9: Per-family fault isolation
///
/// **Property 9: Per-family fault isolation** — for any single metric-family data source whose
/// computation throws, the exporter output omits ONLY the family (or families) that depend on the
/// throwing source, while still emitting every other family that computed successfully.
///
/// The exporter sources every family from <see cref="HangfireMonitorService"/> (which wraps the core
/// <c>IMonitoringApi</c>) and the optional <see cref="IStorageMetricsProvider"/>. The mapping from a
/// data source to the families it feeds is:
/// <list type="bullet">
///   <item><c>IMonitoringApi.GetStatistics()</c> → <c>hangfire_jobs_total</c> AND
///     <c>hangfire_jobs_in_state_count</c> (both share the one statistics read via
///     <c>SafeGetStatistics</c>, so both are omitted together).</item>
///   <item><c>IMonitoringApi.Queues()</c> → <c>hangfire_queue_length</c>.</item>
///   <item><c>IMonitoringApi.Servers()</c> → <c>hangfire_servers_count</c> AND
///     <c>hangfire_workers_count</c> (both derive from the one servers read).</item>
///   <item><c>JobStorageConnection.GetRecurringJobCount()</c> → <c>hangfire_recurring_jobs_count</c>.</item>
///   <item><c>IStorageMetricsProvider.GetJobDurationStatsAsync()</c> → the
///     <c>hangfire_job_duration_seconds</c> histogram.</item>
/// </list>
///
/// **Validates: Requirements 7.3**
/// </summary>
public class PrometheusFaultIsolationProperties
{
    /// <summary>The data source configured to throw for a given iteration (exactly one per run).</summary>
    private enum FaultSource
    {
        Statistics,
        Queues,
        Servers,
        Recurring,
        Metrics,
    }

    // Base (counter/gauge) family names the exporter always attempts to emit.
    private const string JobsTotal = "hangfire_jobs_total";
    private const string JobsInState = "hangfire_jobs_in_state_count";
    private const string QueueLength = "hangfire_queue_length";
    private const string ServersCount = "hangfire_servers_count";
    private const string WorkersCount = "hangfire_workers_count";
    private const string RecurringCount = "hangfire_recurring_jobs_count";

    private static readonly IReadOnlyList<string> AllBaseFamilies = new[]
    {
        JobsTotal, JobsInState, QueueLength, ServersCount, WorkersCount, RecurringCount,
    };

    private static Arbitrary<FaultSource> FaultSourceArb =>
        Arb.From(Gen.Elements(
            FaultSource.Statistics,
            FaultSource.Queues,
            FaultSource.Servers,
            FaultSource.Recurring,
            FaultSource.Metrics));

    [Property(MaxTest = 100)]
    public Property SingleFailingSource_OmitsOnlyItsFamilies_AndEmitsTheRest()
    {
        return Prop.ForAll(FaultSourceArb, fault =>
        {
            var snapshot = Collect(fault);

            var presentBase = snapshot.Families.Select(f => f.Name).ToHashSet();
            var expectedBase = ExpectedBaseFamilies(fault);
            var histogramExpected = fault != FaultSource.Metrics;
            var histogramPresent = snapshot.Histograms.Count > 0;

            var basesMatch = presentBase.SetEquals(expectedBase);
            var histogramMatches = histogramPresent == histogramExpected;

            return (basesMatch && histogramMatches)
                .Label(
                    $"fault={fault} " +
                    $"expectedBase=[{string.Join(",", expectedBase.OrderBy(x => x))}] " +
                    $"actualBase=[{string.Join(",", presentBase.OrderBy(x => x))}] " +
                    $"histogram expected={histogramExpected} actual={histogramPresent}");
        });
    }

    /// <summary>The base families that should survive when <paramref name="fault"/> is the throwing source.</summary>
    private static HashSet<string> ExpectedBaseFamilies(FaultSource fault)
    {
        var omitted = fault switch
        {
            // GetStatistics feeds both job-count families (shared read via SafeGetStatistics).
            FaultSource.Statistics => new[] { JobsTotal, JobsInState },
            FaultSource.Queues => new[] { QueueLength },
            // Servers() feeds both the server-count and worker-count gauges.
            FaultSource.Servers => new[] { ServersCount, WorkersCount },
            FaultSource.Recurring => new[] { RecurringCount },
            // The metrics provider only feeds the histogram; no base family is omitted.
            FaultSource.Metrics => Array.Empty<string>(),
            _ => Array.Empty<string>(),
        };

        return AllBaseFamilies.Except(omitted).ToHashSet();
    }

    /// <summary>
    /// Builds a <see cref="PrometheusExporter"/> over mocked data sources where exactly one source
    /// throws (per <paramref name="fault"/>) and every other source returns valid data, then runs a
    /// collection.
    /// </summary>
    private static PrometheusSnapshot Collect(FaultSource fault)
    {
        // ── IMonitoringApi (statistics, queues, servers) ────────────────────────────────────────
        var monitoringApi = new Mock<IMonitoringApi>(MockBehavior.Loose);

        if (fault == FaultSource.Statistics)
            monitoringApi.Setup(m => m.GetStatistics()).Throws(new InvalidOperationException("stats boom"));
        else
            monitoringApi.Setup(m => m.GetStatistics()).Returns(SampleStatistics());

        if (fault == FaultSource.Queues)
            monitoringApi.Setup(m => m.Queues()).Throws(new InvalidOperationException("queues boom"));
        else
            monitoringApi.Setup(m => m.Queues()).Returns(SampleQueues());

        if (fault == FaultSource.Servers)
            monitoringApi.Setup(m => m.Servers()).Throws(new InvalidOperationException("servers boom"));
        else
            monitoringApi.Setup(m => m.Servers()).Returns(SampleServers());

        // ── recurring-job count comes from a JobStorageConnection ───────────────────────────────
        // HangfireMonitorService.GetRecurringJobCount() calls the StorageConnectionExtensions
        // extension, which delegates to the overridable GetSetCount("recurring-jobs").
        var connection = new Mock<JobStorageConnection>(MockBehavior.Loose);
        if (fault == FaultSource.Recurring)
            connection.Setup(c => c.GetSetCount("recurring-jobs")).Throws(new InvalidOperationException("recurring boom"));
        else
            connection.Setup(c => c.GetSetCount("recurring-jobs")).Returns(7);

        // ── JobStorage wiring for HangfireMonitorService ────────────────────────────────────────
        var storage = new Mock<JobStorage>(MockBehavior.Loose);
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoringApi.Object);
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        var monitorService = new HangfireMonitorService(storage.Object);

        // ── metrics provider (histogram source) — always registered so the histogram is testable ─
        var metricsProvider = new Mock<IStorageMetricsProvider>(MockBehavior.Loose);
        if (fault == FaultSource.Metrics)
        {
            metricsProvider
                .Setup(p => p.GetJobDurationStatsAsync(
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("metrics boom"));
        }
        else
        {
            metricsProvider
                .Setup(p => p.GetJobDurationStatsAsync(
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<JobDurationStatsDto>)Array.Empty<JobDurationStatsDto>());
        }

        var services = new ServiceCollection();
        services.AddSingleton(monitorService);
        services.AddSingleton<IStorageMetricsProvider>(metricsProvider.Object);
        using var sp = services.BuildServiceProvider();

        var exporter = new PrometheusExporter(sp);
        return exporter.CollectAsync().GetAwaiter().GetResult();
    }

    private static StatisticsDto SampleStatistics() => new()
    {
        Servers = 2,
        Recurring = 3,
        Enqueued = 4,
        Queues = 1,
        Scheduled = 5,
        Processing = 6,
        Succeeded = 100,
        Failed = 2,
        Deleted = 1,
    };

    private static IList<QueueWithTopEnqueuedJobsDto> SampleQueues() => new List<QueueWithTopEnqueuedJobsDto>
    {
        new() { Name = "default", Length = 10 },
        new() { Name = "critical", Length = 3 },
    };

    private static IList<ServerDto> SampleServers() => new List<ServerDto>
    {
        new() { Name = "server-1", WorkersCount = 5, Heartbeat = DateTime.UtcNow, StartedAt = DateTime.UtcNow },
        new() { Name = "server-2", WorkersCount = 3, Heartbeat = DateTime.UtcNow, StartedAt = DateTime.UtcNow },
    };
}
