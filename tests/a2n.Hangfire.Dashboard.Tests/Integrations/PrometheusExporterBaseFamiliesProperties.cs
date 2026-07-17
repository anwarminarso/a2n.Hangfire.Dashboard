using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Services;
using a2n.Hangfire.Dashboard.Services.Prometheus;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for the Prometheus exporter's base metric catalog
/// (<see cref="PrometheusExporter"/>).
///
/// Feature: integrations-v2-6, Property 7: Base metric families present regardless of metrics provider
///
/// **Property 7: Base metric families are present regardless of metrics provider** — for any
/// dashboard monitoring snapshot, and whether or not an <see cref="IStorageMetricsProvider"/> is
/// registered, the exporter output includes the families <c>hangfire_jobs_total</c> (counter),
/// <c>hangfire_jobs_in_state_count</c> (gauge), <c>hangfire_queue_length</c> (gauge),
/// <c>hangfire_servers_count</c> (gauge), <c>hangfire_workers_count</c> (gauge), and
/// <c>hangfire_recurring_jobs_count</c> (gauge), each declared with its expected metric type.
///
/// **Validates: Requirements 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 7.1**
///
/// <para>Construction note: <see cref="PrometheusExporter"/> resolves the concrete
/// <see cref="HangfireMonitorService"/> (which wraps <c>IMonitoringApi</c> through
/// <see cref="JobStorage"/>) and an optional <see cref="IStorageMetricsProvider"/> from the supplied
/// <see cref="IServiceProvider"/>. The tests build a <see cref="ServiceCollection"/> around a mocked
/// <see cref="JobStorage"/> whose <c>GetMonitoringApi()</c> returns a mocked
/// <see cref="IMonitoringApi"/> serving the generated <see cref="StatisticsDto"/>, queues, and
/// servers, and whose read-only connection is a mocked <see cref="JobStorageConnection"/> (so the
/// <c>GetRecurringJobCount</c> storage extension resolves to 0 without throwing). The metrics
/// provider is registered only in the provider-present case.</para>
/// </summary>
public class PrometheusExporterBaseFamiliesProperties
{
    /// <summary>A generated dashboard monitoring snapshot plus the metrics-provider toggle.</summary>
    public sealed record Snapshot(
        bool ProviderPresent,
        long Succeeded,
        long Enqueued,
        long Scheduled,
        long Processing,
        long Failed,
        long Deleted,
        IReadOnlyList<(string Name, long Length)> Queues,
        IReadOnlyList<int> ServerWorkerCounts);

    private static Gen<long> NonNegativeLongGen =>
        Gen.Choose(0, 1_000_000).Select(i => (long)i);

    private static Gen<string> QueueNameGen =>
        Gen.Elements("default", "critical", "low", "emails", "reports", "queue-1", "queue-2");

    private static Gen<Snapshot> SnapshotGen =>
        from provider in Arb.Generate<bool>()
        from succeeded in NonNegativeLongGen
        from enqueued in NonNegativeLongGen
        from scheduled in NonNegativeLongGen
        from processing in NonNegativeLongGen
        from failed in NonNegativeLongGen
        from deleted in NonNegativeLongGen
        from queueCount in Gen.Choose(0, 5)
        from queueNames in Gen.ArrayOf(queueCount, QueueNameGen)
        from queueLengths in Gen.ArrayOf(queueCount, NonNegativeLongGen)
        from serverCount in Gen.Choose(0, 5)
        from workerCounts in Gen.ArrayOf(serverCount, Gen.Choose(0, 200))
        select new Snapshot(
            provider,
            succeeded, enqueued, scheduled, processing, failed, deleted,
            queueNames.Zip(queueLengths, (n, l) => (n, l)).ToArray(),
            workerCounts);

    private static Arbitrary<Snapshot> SnapshotArb => Arb.From(SnapshotGen);

    private static PrometheusExporter BuildExporter(Snapshot snapshot)
    {
        var stats = new StatisticsDto
        {
            Succeeded = snapshot.Succeeded,
            Enqueued = snapshot.Enqueued,
            Scheduled = snapshot.Scheduled,
            Processing = snapshot.Processing,
            Failed = snapshot.Failed,
            Deleted = snapshot.Deleted,
        };

        var queues = snapshot.Queues
            .Select(q => new QueueWithTopEnqueuedJobsDto { Name = q.Name, Length = q.Length })
            .ToList();

        var servers = snapshot.ServerWorkerCounts
            .Select((w, i) => new ServerDto
            {
                Name = $"server-{i}",
                WorkersCount = w,
                StartedAt = DateTime.UtcNow,
                Heartbeat = DateTime.UtcNow,
                Queues = new List<string>(),
            })
            .ToList();

        var api = new Mock<IMonitoringApi>();
        api.Setup(m => m.GetStatistics()).Returns(stats);
        api.Setup(m => m.Queues()).Returns(queues);
        api.Setup(m => m.Servers()).Returns(servers);

        // A loose JobStorageConnection mock: the GetRecurringJobCount extension calls storage
        // primitives (e.g. GetSetCount) that default to 0 on a loose mock, so it never throws.
        var connection = new Mock<JobStorageConnection>();

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(api.Object);
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        var monitor = new HangfireMonitorService(storage.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(monitor);
        if (snapshot.ProviderPresent)
        {
            services.AddSingleton(new Mock<IStorageMetricsProvider>().Object);
        }

        var sp = services.BuildServiceProvider();
        return new PrometheusExporter(sp);
    }

    private static readonly (string Name, MetricType Type)[] ExpectedBaseFamilies =
    {
        ("hangfire_jobs_total", MetricType.Counter),
        ("hangfire_jobs_in_state_count", MetricType.Gauge),
        ("hangfire_queue_length", MetricType.Gauge),
        ("hangfire_servers_count", MetricType.Gauge),
        ("hangfire_workers_count", MetricType.Gauge),
        ("hangfire_recurring_jobs_count", MetricType.Gauge),
    };

    [Property(MaxTest = 100)]
    public Property BaseFamilies_ArePresent_WithExpectedTypes_RegardlessOfProvider()
    {
        return Prop.ForAll(SnapshotArb, snapshot =>
        {
            var exporter = BuildExporter(snapshot);
            var result = exporter.CollectAsync().GetAwaiter().GetResult();

            var byName = result.Families.ToDictionary(f => f.Name);

            foreach (var (name, type) in ExpectedBaseFamilies)
            {
                if (!byName.TryGetValue(name, out var family))
                {
                    return false
                        .Label($"providerPresent={snapshot.ProviderPresent}: " +
                               $"missing base family '{name}'. Present: [" +
                               string.Join(", ", byName.Keys) + "]");
                }

                if (family.Type != type)
                {
                    return false
                        .Label($"providerPresent={snapshot.ProviderPresent}: family '{name}' " +
                               $"has type {family.Type}, expected {type}");
                }
            }

            return true.ToProperty();
        });
    }
}
