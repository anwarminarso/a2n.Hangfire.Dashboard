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
/// Property tests for the Prometheus job-duration histogram produced by
/// <see cref="PrometheusExporter"/>.
///
/// Feature: integrations-v2-6, Property 8: Histogram is provider-gated and internally consistent
///
/// **Property 8** — for any dashboard state, the <c>hangfire_job_duration_seconds</c> histogram
/// family SHALL be present <b>iff</b> an <see cref="IStorageMetricsProvider"/> is registered; and
/// whenever it is present its series SHALL be internally consistent: <c>_bucket</c> counts are
/// cumulative and monotonically non-decreasing, the <c>+Inf</c> bucket equals <c>_count</c>, and
/// both <c>_sum</c> and <c>_count</c> are emitted.
///
/// **Validates: Requirements 6.7, 7.2**
/// </summary>
public class PrometheusHistogramProperties
{
    // ── Generators ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A single per-job-type duration statistic with a non-negative count and average.</summary>
    private static Gen<JobDurationStatsDto> DurationStatGen =>
        from count in Gen.Choose(0, 5000)
        from avgMs in Gen.Choose(0, 60_000).Select(x => (double)x)
        from typeIx in Gen.Choose(0, 100)
        select new JobDurationStatsDto
        {
            JobType = "JobType" + typeIx,
            Count = count,
            AverageMs = avgMs,
        };

    /// <summary>A (possibly empty) list of per-job-type duration statistics.</summary>
    private static Gen<List<JobDurationStatsDto>> DurationStatsGen =>
        Gen.ListOf(DurationStatGen).Select(xs => xs.ToList());

    /// <summary>
    /// A generated scenario: whether an <see cref="IStorageMetricsProvider"/> is registered, and
    /// the duration statistics the mocked provider returns.
    /// </summary>
    private static Arbitrary<(bool providerPresent, List<JobDurationStatsDto> stats)> ScenarioArb =>
        Arb.From(
            from providerPresent in Gen.Elements(true, false)
            from stats in DurationStatsGen
            select (providerPresent, stats));

    // ── Service-provider assembly ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a service provider carrying a <see cref="HangfireMonitorService"/> (backed by a mocked
    /// <c>IMonitoringApi</c>) and, when <paramref name="providerPresent"/> is true, a mocked
    /// <see cref="IStorageMetricsProvider"/> whose <c>GetJobDurationStatsAsync</c> returns
    /// <paramref name="stats"/>.
    /// </summary>
    private static IServiceProvider BuildProvider(bool providerPresent, List<JobDurationStatsDto> stats)
    {
        var monitoringApi = new Mock<IMonitoringApi>();
        // The base families are irrelevant to this property; leave the monitoring API's members at
        // their default behavior. Any family that cannot be computed is simply omitted by the
        // exporter, so CollectAsync never throws regardless of these values.

        var connection = new Mock<JobStorageConnection>();

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoringApi.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        var services = new ServiceCollection();
        services.AddSingleton<JobStorage>(storage.Object);
        services.AddSingleton(new HangfireMonitorService(storage.Object));

        if (providerPresent)
        {
            var metrics = new Mock<IStorageMetricsProvider>();
            metrics
                .Setup(m => m.GetJobDurationStatsAsync(
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<JobDurationStatsDto>)stats);
            services.AddSingleton<IStorageMetricsProvider>(metrics.Object);
        }

        return services.BuildServiceProvider();
    }

    // ── Property ────────────────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property Histogram_IsProviderGated_AndInternallyConsistent()
    {
        return Prop.ForAll(ScenarioArb, scenario =>
        {
            var (providerPresent, stats) = scenario;

            var sp = BuildProvider(providerPresent, stats);
            var exporter = new PrometheusExporter(sp);
            var snapshot = exporter.CollectAsync().GetAwaiter().GetResult();

            var histograms = snapshot.Histograms;

            // Provider-gated: the histogram family is present iff a metrics provider is registered.
            if (!providerPresent)
            {
                return (histograms.Count == 0)
                    .Label($"expected no histogram when provider absent, found {histograms.Count}");
            }

            // Present iff provider registered — exactly one duration histogram family.
            if (histograms.Count != 1)
            {
                return false.Label(
                    $"expected exactly one histogram when provider present, found {histograms.Count}");
            }

            var h = histograms[0];

            bool named = h.Name == "hangfire_job_duration_seconds";

            // _bucket counts are cumulative and monotonically non-decreasing.
            bool monotonic = true;
            for (var i = 1; i < h.BucketCounts.Count; i++)
            {
                if (h.BucketCounts[i] < h.BucketCounts[i - 1])
                {
                    monotonic = false;
                    break;
                }
            }

            // Every bucket count is bounded by the total (+Inf) count; the implicit +Inf bucket
            // equals _count (Count >= the last/greatest cumulative bucket count).
            long maxBucket = h.BucketCounts.Count > 0 ? h.BucketCounts.Max() : 0;
            bool infEqualsCount = h.Count >= maxBucket && h.BucketCounts.All(c => c <= h.Count);

            // _sum and _count are both emitted (the family carries them) and are non-negative.
            bool sumCountValid = h.Count >= 0 && h.Sum >= 0 && !double.IsNaN(h.Sum);

            // Cross-check against the oracle computed directly from the generated statistics: the
            // total count equals the sum of positive per-type observation counts.
            long expectedCount = stats.Where(t => t is { Count: > 0 }).Sum(t => t.Count);
            bool countMatchesOracle = h.Count == expectedCount;

            return (named && monotonic && infEqualsCount && sumCountValid && countMatchesOracle)
                .Label(
                    $"name={h.Name} buckets=[{string.Join(",", h.BucketCounts)}] " +
                    $"count={h.Count} sum={h.Sum} expectedCount={expectedCount} " +
                    $"monotonic={monotonic} infEqualsCount={infEqualsCount} " +
                    $"sumCountValid={sumCountValid} countMatchesOracle={countMatchesOracle}");
        });
    }
}
