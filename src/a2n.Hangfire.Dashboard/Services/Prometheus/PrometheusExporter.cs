#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// The result of a single Prometheus collection: the provider-independent metric families and,
/// when an <see cref="IStorageMetricsProvider"/> is registered, the job-duration histogram(s).
/// </summary>
/// <param name="Families">The counter/gauge metric families that were computed successfully.</param>
/// <param name="Histograms">
/// The histogram families. Empty when no <see cref="IStorageMetricsProvider"/> is registered
/// (Req 7.2) or when the histogram computation failed (Req 7.3).
/// </param>
public sealed record PrometheusSnapshot(
    IReadOnlyList<MetricFamily> Families,
    IReadOnlyList<HistogramFamily> Histograms);

/// <summary>
/// Builds Prometheus metric families from the dashboard's existing data sources only: the core
/// <c>IMonitoringApi</c> (through <see cref="HangfireMonitorService"/>) and the optional
/// <see cref="IStorageMetricsProvider"/>. It issues no new storage-specific queries (Req 6.8).
/// </summary>
/// <remarks>
/// <para>Follows the established dashboard service convention (see <c>AnalyticsService</c>,
/// <c>HeatmapService</c>): it takes an <see cref="IServiceProvider"/>, resolves
/// <see cref="IStorageMetricsProvider"/> optionally (so the histogram lights up only on storages
/// that support it — Req 7.2), and resolves a logger for diagnostics.</para>
/// <para>Each metric family is computed independently in a try/catch: a family that throws is
/// omitted while every other family is still emitted, and a diagnostic is logged (Req 7.3).</para>
/// </remarks>
public sealed class PrometheusExporter
{
    /// <summary>
    /// The default histogram bucket upper bounds (in seconds) used when the host configures none.
    /// Mirrors the Prometheus client-library default buckets (0.005 s … 10 s).
    /// </summary>
    public static readonly IReadOnlyList<double> DefaultDurationBucketsSeconds = new[]
    {
        0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0
    };

    // The look-back window over which job-duration statistics are aggregated for the histogram.
    private static readonly TimeSpan DurationWindow = TimeSpan.FromDays(1);

    private readonly HangfireMonitorService _monitor;
    private readonly IStorageMetricsProvider? _metricsProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the exporter, resolving its data sources from the supplied service provider.
    /// </summary>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceProvider"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="HangfireMonitorService"/> is registered.</exception>
    public PrometheusExporter(IServiceProvider serviceProvider)
    {
        if (serviceProvider is null) throw new ArgumentNullException(nameof(serviceProvider));

        _monitor = serviceProvider.GetService<HangfireMonitorService>()
                   ?? throw new InvalidOperationException(
                       "HangfireMonitorService is not registered; the Prometheus exporter requires it.");

        // Resolve optionally — null when no metrics provider is registered (graceful degradation, Req 7.2).
        _metricsProvider = serviceProvider.GetService<IStorageMetricsProvider>();

        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<PrometheusExporter>()
                  ?? NullLogger<PrometheusExporter>.Instance;
    }

    /// <summary>
    /// Indicates whether the job-duration histogram can be produced, i.e. whether an
    /// <see cref="IStorageMetricsProvider"/> is registered (Req 7.2).
    /// </summary>
    public bool HasMetricsProvider => _metricsProvider is not null;

    /// <summary>
    /// Collects the current metric families and (when a metrics provider is registered) the
    /// job-duration histogram. Never throws for a single failing family — that family is omitted
    /// and a diagnostic is logged while the rest are still returned (Req 7.3).
    /// </summary>
    /// <param name="durationBucketsSeconds">
    /// Optional histogram bucket upper bounds in seconds; defaults to
    /// <see cref="DefaultDurationBucketsSeconds"/> when null or empty.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PrometheusSnapshot> CollectAsync(
        IReadOnlyList<double>? durationBucketsSeconds = null,
        CancellationToken ct = default)
    {
        var families = new List<MetricFamily>();

        // Read statistics once; the two job-count families share it. If it throws, both are omitted.
        StatisticsDto? stats = SafeGetStatistics();

        AddFamily(families, "hangfire_jobs_total", () => BuildJobsTotal(stats));
        AddFamily(families, "hangfire_jobs_in_state_count", () => BuildJobsInState(stats));
        AddFamily(families, "hangfire_queue_length", BuildQueueLength);
        AddFamily(families, "hangfire_servers_count", BuildServersCount);
        AddFamily(families, "hangfire_workers_count", BuildWorkersCount);
        AddFamily(families, "hangfire_recurring_jobs_count", BuildRecurringJobsCount);

        var histograms = new List<HistogramFamily>();

        // Histogram is produced only when a metrics provider is registered (Req 7.2).
        if (_metricsProvider is not null)
        {
            var bounds = (durationBucketsSeconds is { Count: > 0 })
                ? durationBucketsSeconds
                : DefaultDurationBucketsSeconds;

            try
            {
                var histogram = await BuildDurationHistogramAsync(bounds, ct).ConfigureAwait(false);
                if (histogram is not null)
                {
                    histograms.Add(histogram);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Prometheus: failed to compute the {Family} histogram; omitting it.",
                    "hangfire_job_duration_seconds");
            }
        }

        return new PrometheusSnapshot(families, histograms);
    }

    // ── family orchestration ──────────────────────────────────────────────────────────────────

    private void AddFamily(List<MetricFamily> families, string name, Func<MetricFamily> build)
    {
        try
        {
            families.Add(build());
        }
        catch (Exception ex)
        {
            // Per-family fault isolation: omit this family, keep the rest, log a diagnostic (Req 7.3).
            _logger.LogWarning(
                ex,
                "Prometheus: failed to compute the {Family} metric family; omitting it.",
                name);
        }
    }

    private StatisticsDto? SafeGetStatistics()
    {
        try
        {
            return _monitor.GetStatistics();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Prometheus: failed to read job statistics; job-count families will be omitted.");
            return null;
        }
    }

    // ── metric family builders ──────────────────────────────────────────────────────────────────

    private static MetricFamily BuildJobsTotal(StatisticsDto? stats)
    {
        var s = stats ?? throw new InvalidOperationException("Statistics unavailable.");
        return new MetricFamily(
            "hangfire_jobs_total",
            MetricType.Counter,
            "Total number of jobs that have succeeded.",
            new[] { Sample(s.Succeeded) });
    }

    private static MetricFamily BuildJobsInState(StatisticsDto? stats)
    {
        var s = stats ?? throw new InvalidOperationException("Statistics unavailable.");
        var samples = new List<MetricSample>
        {
            Sample(s.Enqueued, "state", "Enqueued"),
            Sample(s.Scheduled, "state", "Scheduled"),
            Sample(s.Processing, "state", "Processing"),
            Sample(s.Succeeded, "state", "Succeeded"),
            Sample(s.Failed, "state", "Failed"),
            Sample(s.Deleted, "state", "Deleted"),
        };
        return new MetricFamily(
            "hangfire_jobs_in_state_count",
            MetricType.Gauge,
            "Current number of jobs in each Hangfire state.",
            samples);
    }

    private MetricFamily BuildQueueLength()
    {
        var queues = _monitor.GetQueues() ?? new List<QueueWithTopEnqueuedJobsDto>();
        var samples = new List<MetricSample>();
        foreach (var queue in queues)
        {
            if (string.IsNullOrEmpty(queue.Name)) continue;
            samples.Add(Sample(queue.Length, "queue", queue.Name));
        }
        return new MetricFamily(
            "hangfire_queue_length",
            MetricType.Gauge,
            "Number of enqueued jobs per queue.",
            samples);
    }

    private MetricFamily BuildServersCount()
    {
        var servers = _monitor.GetServers() ?? new List<ServerDto>();
        return new MetricFamily(
            "hangfire_servers_count",
            MetricType.Gauge,
            "Number of active Hangfire servers.",
            new[] { Sample(servers.Count) });
    }

    private MetricFamily BuildWorkersCount()
    {
        var servers = _monitor.GetServers() ?? new List<ServerDto>();
        long totalWorkers = 0;
        foreach (var server in servers)
        {
            totalWorkers += server.WorkersCount;
        }
        return new MetricFamily(
            "hangfire_workers_count",
            MetricType.Gauge,
            "Total number of workers across all Hangfire servers.",
            new[] { Sample(totalWorkers) });
    }

    private MetricFamily BuildRecurringJobsCount()
    {
        var count = _monitor.GetRecurringJobCount();
        return new MetricFamily(
            "hangfire_recurring_jobs_count",
            MetricType.Gauge,
            "Number of registered recurring jobs.",
            new[] { Sample(count) });
    }

    private async Task<HistogramFamily?> BuildDurationHistogramAsync(
        IReadOnlyList<double> bucketBoundsSeconds,
        CancellationToken ct)
    {
        var provider = _metricsProvider;
        if (provider is null) return null;

        var to = DateTimeOffset.UtcNow;
        var from = to - DurationWindow;

        var stats = await provider.GetJobDurationStatsAsync(from, to, ct).ConfigureAwait(false)
                    ?? (IReadOnlyList<JobDurationStatsDto>)Array.Empty<JobDurationStatsDto>();

        // Sorted, de-duplicated ascending bounds so bucket counts stay cumulative and monotonic.
        var bounds = bucketBoundsSeconds
            .Where(b => !double.IsNaN(b))
            .Distinct()
            .OrderBy(b => b)
            .ToArray();

        var bucketCounts = new long[bounds.Length];
        long totalCount = 0;
        double totalSum = 0;

        // Only aggregate per-job-type statistics are available (not raw observations), so each job
        // type contributes its Count observations at its average duration. This yields a valid,
        // internally-consistent histogram: buckets are cumulative and monotonic, the implicit +Inf
        // bucket equals the total count, and _sum/_count are derivable.
        foreach (var t in stats)
        {
            if (t is null || t.Count <= 0) continue;

            var avgSeconds = t.AverageMs / 1000.0;
            if (double.IsNaN(avgSeconds) || double.IsInfinity(avgSeconds) || avgSeconds < 0)
            {
                avgSeconds = 0;
            }

            totalCount += t.Count;
            totalSum += avgSeconds * t.Count;

            for (var i = 0; i < bounds.Length; i++)
            {
                if (avgSeconds <= bounds[i])
                {
                    bucketCounts[i] += t.Count;
                }
            }
        }

        return new HistogramFamily(
            "hangfire_job_duration_seconds",
            "Observed Hangfire job execution durations in seconds.",
            bounds,
            bucketCounts,
            totalSum,
            totalCount);
    }

    // ── sample helpers ──────────────────────────────────────────────────────────────────────────

    private static MetricSample Sample(double value)
        => new(Array.Empty<KeyValuePair<string, string>>(), value);

    private static MetricSample Sample(double value, string labelKey, string labelValue)
        => new(new[] { new KeyValuePair<string, string>(labelKey, labelValue) }, value);
}
