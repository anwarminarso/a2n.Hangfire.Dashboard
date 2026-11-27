using System.Collections.Concurrent;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Orchestrates the Recurring Schedule Heatmap's Projected (storage-agnostic) source. It reads
/// recurring jobs through the storage-agnostic Hangfire connection, maps each
/// <see cref="RecurringJobDto"/> onto the pure-engine <see cref="RecurringJobSpec"/>, and drives the
/// deterministic <see cref="ProjectionEngine"/> + <see cref="ScheduleAggregator"/> pipeline.
/// </summary>
/// <remarks>
/// <para>The service follows the existing <see cref="AnalyticsService"/> conventions: it takes an
/// <see cref="IServiceProvider"/>, resolves <see cref="IStorageMetricsProvider"/> optionally (so the
/// Historical source degrades gracefully — Req 1.5), and never throws for missing or unreadable
/// recurring-job data, returning an empty-state matrix instead (Req 1.7).</para>
/// <para>This type implements task 9.1 (core Projected orchestration + capacity), task 9.2
/// (aggregation caching), and task 13.4 (Historical source wiring with graceful degradation). When a
/// metrics provider is registered and the operator selects the Historical source, the service queries
/// the provider's recurring-schedule buckets under a 10-second timeout
/// (<see cref="HeatmapOptions.HistoricalQueryTimeoutSeconds"/>); on failure or timeout it reverts to
/// the Projected source, retains that data, and surfaces a dismissible notice (Req 7.5). Zero-fire
/// historical buckets are treated as no-data rather than zero-valued cells (Req 7.4).</para>
/// <para>Aggregation results are cached on <see cref="IMemoryCache"/> under a key composed of the
/// active source, projection-window kind, viewer time zone, and load metric (Req 13.1). Within the
/// configured time-to-live (<see cref="HeatmapOptions.CacheTtlSeconds"/>, default 60s) a request
/// that only changes view/controls — without altering the cache key — is served from the cache
/// without recomputing projections (Req 13.2, 13.5). Concurrent cold requests for the same key
/// compute the aggregation exactly once via a per-key gate and share the single result (Req 13.4).
/// Once an entry passes its time-to-live it is served stale while a single background recomputation
/// replaces it (stale-while-revalidate, Req 13.8), mirroring the <see cref="MetricsQueryCache"/>
/// per-key single-flight style.</para>
/// <para>Validates portions of Requirements 1.2, 1.5, 1.7, 5.1, 5.3, 5.4, 13.1, 13.2, 13.4, 13.5,
/// and 13.8.</para>
/// </remarks>
public partial class HeatmapService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IStorageMetricsProvider? _metricsProvider;
    private readonly JobStorage? _storage;
    private readonly HangfireMonitorService? _monitor;
    private readonly DashboardUIOptions _options;
    private readonly ILogger<HeatmapService> _logger;

    /// <summary>
    /// Resolves each recurring job's estimated execution duration from historical p95 when a metrics
    /// provider is available, otherwise the configured default (≥ 1 minute). Null when not registered
    /// (e.g. in unit tests), in which case every job falls back to the configured default (task 14.3).
    /// </summary>
    private readonly EstimatedDurationResolver? _durationResolver;

    /// <summary>
    /// Builds the ad-hoc <c>Demand_Profile</c> from the persisted rollup. Null when not registered;
    /// callers then receive an empty profile (graceful degradation, Req 16.7).
    /// </summary>
    private readonly DemandProfileProvider? _demandProfileProvider;

    /// <summary>
    /// Short-lived memo of the resolved job-type → estimated-duration map, so the several projections
    /// performed in a single page interaction (matrix, specs, concurrency, recommendations) issue at
    /// most one duration-stats query per cache-TTL window instead of one per projection.
    /// </summary>
    private IReadOnlyDictionary<string, (TimeSpan Duration, bool IsDefault)>? _durationMemo;
    private DateTimeOffset _durationMemoAt = DateTimeOffset.MinValue;

    private readonly IMemoryCache? _cache;

    /// <summary>
    /// Per-cache-key single-flight gates. A gate serializes the cold compute and any background
    /// stale refresh for its key so the aggregation is computed exactly once at a time (Req 13.4).
    /// Gates are retained for the process lifetime (bounded by the small set of cache-key patterns),
    /// matching <see cref="MetricsQueryCache"/>: removing one after release would open a TOCTOU race.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>The logical freshness window for a cached aggregation (Req 13.5, default 60s).</summary>
    private readonly TimeSpan _cacheTtl;

    /// <summary>
    /// How long a (possibly stale) entry is physically retained in <see cref="IMemoryCache"/> so it
    /// remains available to serve while a background recomputation runs (Req 13.8). This is a
    /// generous multiple of the freshness window and bounds memory via sliding expiration.
    /// </summary>
    private readonly TimeSpan _cacheRetention;

    /// <summary>
    /// Indicates whether historical heatmap features are available (an
    /// <see cref="IStorageMetricsProvider"/> is registered). Mirrors
    /// <see cref="AnalyticsService.IsAvailable"/> so the Historical source toggle hides when no
    /// provider is present (Req 1.5, 7.2).
    /// </summary>
    public bool IsHistoricalAvailable => _metricsProvider != null;

    /// <summary>
    /// Indicates whether the ad-hoc <c>Demand_Profile</c> can be built (a metrics provider and the
    /// <see cref="DemandProfileProvider"/> are both registered). Gates the Ad-hoc / Combined demand
    /// layers on the page (Req 16.7, 16.9).
    /// </summary>
    public bool IsDemandAvailable => _demandProfileProvider?.IsAvailable ?? false;

    public HeatmapService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        // Resolve optionally — null when no metrics provider is registered (graceful degradation).
        _metricsProvider = serviceProvider.GetService<IStorageMetricsProvider>();
        _storage = serviceProvider.GetService<JobStorage>();
        _monitor = serviceProvider.GetService<HangfireMonitorService>();
        _options = serviceProvider.GetService<DashboardUIOptions>() ?? new DashboardUIOptions();
        _durationResolver = serviceProvider.GetService<EstimatedDurationResolver>();
        _demandProfileProvider = serviceProvider.GetService<DemandProfileProvider>();
        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<HeatmapService>()
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HeatmapService>.Instance;

        _cache = serviceProvider.GetService<IMemoryCache>();

        var ttlSeconds = _options.Heatmap?.CacheTtlSeconds ?? 60;
        if (ttlSeconds < 1)
        {
            ttlSeconds = 1;
        }

        _cacheTtl = TimeSpan.FromSeconds(ttlSeconds);
        // Keep stale entries around long enough to serve while a refresh runs, while bounding memory.
        _cacheRetention = TimeSpan.FromSeconds(Math.Max(ttlSeconds * 4, 120));
    }

    /// <summary>
    /// Projects the registered recurring jobs over the query's window and aggregates them into a
    /// <c>queue × day × hour</c> matrix from the Projected source.
    /// </summary>
    /// <param name="query">The heatmap request (window kind, viewer time zone, load metric, …).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// A <see cref="HeatmapResult"/> carrying the aggregated matrix plus the projection notices
    /// (unparseable crons, unknown time zones, long-period jobs). When there are no recurring jobs —
    /// or they cannot be read — the result carries an empty matrix rather than throwing (Req 1.5, 1.7).
    /// </returns>
    public async Task<HeatmapResult> GetMatrixAsync(HeatmapQuery query, CancellationToken ct)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        ct.ThrowIfCancellationRequested();

        var viewerTz = HeatmapTime.ResolveTimeZone(query.ViewerTimeZoneId);
        var window = HeatmapTime.BuildWindow(query.WindowKind, DateTimeOffset.UtcNow, viewerTz);

        // The Historical source is only honored when a metrics provider is registered. When the
        // operator asks for Historical on a storage without a provider, the toggle is hidden by the
        // page (via IsHistoricalAvailable) and any such request renders exclusively from the
        // Projected source (Req 7.2).
        if (query.Source == HeatmapSource.Historical && IsHistoricalAvailable)
        {
            return await GetHistoricalMatrixAsync(query, viewerTz, window, ct).ConfigureAwait(false);
        }

        return await GetProjectedMatrixAsync(query, viewerTz, window, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the recurring jobs that contribute to a single drilled-into heatmap cell, building
    /// the per-job drawer rows the <c>DrillDownDrawer</c> renders (Req 10.1, 10.3). The contributing
    /// jobs are found by re-projecting the registered recurring jobs over the request's window (the
    /// same <see cref="ProjectionEngine"/> pipeline that builds the matrix), then keeping only the
    /// jobs whose in-window fires land in the clicked <paramref name="key"/> bucket — matched on the
    /// normalized queue and the viewer-time-zone <c>(dayIndex, hour)</c> assignment exactly as
    /// <see cref="ScheduleAggregator"/> buckets them, so the drawer lists precisely the jobs the cell
    /// was aggregated from.
    /// </summary>
    /// <remarks>
    /// <para>Each contributing job becomes a <see cref="DrillDownJob"/> carrying its cron expression,
    /// queue, estimated duration, and next run instant (computed with Cronos in the job's configured
    /// time zone via <see cref="CronPreview"/>, normalized to UTC). The rows are returned sorted by
    /// next run ascending with jobs that have no known next run ordered last, then by id, matching the
    /// drawer's own defensive ordering (Req 10.1).</para>
    /// <para>This method never throws: any failure reading recurring jobs, projecting, or computing a
    /// next run yields a <see cref="DrillDownResult"/> whose <see cref="DrillDownResult.Error"/> is
    /// set so the drawer surfaces the error while the page retains its previously displayed heatmap
    /// data unchanged (Req 10.7). Cooperative cancellation is allowed to propagate.</para>
    /// </remarks>
    /// <param name="key">The clicked cell's <c>queue × day × hour</c> address.</param>
    /// <param name="query">The active heatmap request (window kind, viewer time zone, …).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// The contributing jobs sorted by next run, or a result whose <see cref="DrillDownResult.Error"/>
    /// is set when the lookup failed (Req 10.7). An empty job list is returned (without error) when no
    /// job contributes to the cell.
    /// </returns>
    public async Task<DrillDownResult> GetCellJobsAsync(CellKey key, HeatmapQuery query, CancellationToken ct)
    {
        if (key is null)
        {
            return new DrillDownResult(Array.Empty<DrillDownJob>(), "No cell was selected.");
        }

        if (query is null)
        {
            return new DrillDownResult(Array.Empty<DrillDownJob>(), "The heatmap request was unavailable.");
        }

        try
        {
            await Task.CompletedTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var viewerTz = HeatmapTime.ResolveTimeZone(query.ViewerTimeZoneId);
            var window = HeatmapTime.BuildWindow(query.WindowKind, DateTimeOffset.UtcNow, viewerTz);

            // Re-read the recurring jobs through the storage-agnostic connection and project them over
            // the same window the matrix was computed for. A null/empty/unreadable list yields an empty
            // (non-error) drawer rather than throwing (Req 1.7).
            var specs = await BuildSpecsAsync(ReadRecurringJobDtos(), ct).ConfigureAwait(false);
            if (specs.Count == 0)
            {
                return new DrillDownResult(Array.Empty<DrillDownJob>(), null);
            }

            // First spec per id (recurring-job ids are unique, but guard defensively).
            var specById = new Dictionary<string, RecurringJobSpec>(StringComparer.Ordinal);
            foreach (var spec in specs)
            {
                if (spec?.JobId != null && !specById.ContainsKey(spec.JobId))
                {
                    specById[spec.JobId] = spec;
                }
            }

            var projection = ProjectionEngine.Project(specs, window);
            ct.ThrowIfCancellationRequested();

            // Keep the jobs whose fires land in the clicked bucket. The queue is normalized exactly as
            // ScheduleAggregator normalizes it (blank → "default") and the (dayIndex, hour) assignment
            // uses the identical viewer-time-zone bucketing, so this matches the aggregated cell.
            var contributingJobIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fire in projection.Fires)
            {
                if (fire is null)
                {
                    continue;
                }

                var fireQueue = string.IsNullOrWhiteSpace(fire.Queue)
                    ? ScheduleAggregator.DefaultQueue
                    : fire.Queue;

                if (!string.Equals(fireQueue, key.Queue, StringComparison.Ordinal))
                {
                    continue;
                }

                var (dayIndex, hour) = HeatmapTime.GetBucket(fire.FireTimeUtc, viewerTz, window);
                if (dayIndex != key.DayIndex || hour != key.Hour)
                {
                    continue;
                }

                var id = fire.JobId ?? string.Empty;
                if (seen.Add(id))
                {
                    contributingJobIds.Add(id);
                }
            }

            // Build a drawer row per contributing job, computing its next run in the job's own time
            // zone (Req 10.3). Jobs missing from the spec map (e.g. removed mid-flight) are skipped.
            var jobs = new List<DrillDownJob>(contributingJobIds.Count);
            foreach (var jobId in contributingJobIds)
            {
                if (!specById.TryGetValue(jobId, out var spec))
                {
                    continue;
                }

                jobs.Add(new DrillDownJob(
                    JobId: spec.JobId,
                    CronExpression: spec.CronExpression,
                    Queue: spec.Queue,
                    EstimatedDuration: spec.EstimatedDuration,
                    NextRunUtc: ComputeNextRunUtc(spec)));
            }

            // Sort by next run ascending, jobs with no known next run last, then by id for a stable
            // deterministic order (Req 10.1). The drawer re-sorts defensively, but sorting here keeps
            // the service's contract self-consistent.
            var sorted = jobs
                .OrderBy(j => j.NextRunUtc.HasValue ? 0 : 1)
                .ThenBy(j => j.NextRunUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(j => j.JobId, StringComparer.Ordinal)
                .ToList();

            return new DrillDownResult(sorted, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never throw: surface the failure as a drawer notice so the page keeps its heatmap data
            // unchanged (Req 10.7).
            _logger.LogError(ex, "Failed to resolve drill-down jobs for cell {Queue}/{Day}/{Hour}",
                key.Queue, key.DayIndex, key.Hour);

            return new DrillDownResult(
                Array.Empty<DrillDownJob>(),
                "The jobs for this cell could not be loaded.");
        }
    }

    /// <summary>
    /// Computes a recurring job's next run instant in UTC using Cronos, evaluated in the job's
    /// configured time zone (UTC when none/unrecognized). Returns <c>null</c> when the cron cannot be
    /// parsed or has no future occurrence (Req 10.3).
    /// </summary>
    private static DateTimeOffset? ComputeNextRunUtc(RecurringJobSpec spec)
    {
        var zone = HeatmapTime.ResolveTimeZone(spec.TimeZoneId);
        var preview = CronPreview.NextOccurrence(spec.CronExpression, DateTime.UtcNow, zone);
        if (!preview.IsValid || preview.NextOccurrenceUtc is null)
        {
            return null;
        }

        return new DateTimeOffset(
            DateTime.SpecifyKind(preview.NextOccurrenceUtc.Value, DateTimeKind.Utc));
    }
}
