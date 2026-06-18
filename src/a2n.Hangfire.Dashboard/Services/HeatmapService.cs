using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
public class HeatmapService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IStorageMetricsProvider _metricsProvider;
    private readonly JobStorage _storage;
    private readonly HangfireMonitorService _monitor;
    private readonly DashboardUIOptions _options;
    private readonly ILogger<HeatmapService> _logger;

    /// <summary>
    /// Resolves each recurring job's estimated execution duration from historical p95 when a metrics
    /// provider is available, otherwise the configured default (≥ 1 minute). Null when not registered
    /// (e.g. in unit tests), in which case every job falls back to the configured default (task 14.3).
    /// </summary>
    private readonly EstimatedDurationResolver _durationResolver;

    /// <summary>
    /// Builds the ad-hoc <c>Demand_Profile</c> from the persisted rollup. Null when not registered;
    /// callers then receive an empty profile (graceful degradation, Req 16.7).
    /// </summary>
    private readonly DemandProfileProvider _demandProfileProvider;

    /// <summary>
    /// Short-lived memo of the resolved job-type → estimated-duration map, so the several projections
    /// performed in a single page interaction (matrix, specs, concurrency, recommendations) issue at
    /// most one duration-stats query per cache-TTL window instead of one per projection.
    /// </summary>
    private IReadOnlyDictionary<string, (TimeSpan Duration, bool IsDefault)> _durationMemo;
    private DateTimeOffset _durationMemoAt = DateTimeOffset.MinValue;

    private readonly IMemoryCache _cache;

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

    /// <summary>
    /// Serves the Projected-source matrix for <paramref name="query"/> with the cache/single-flight/
    /// stale-while-revalidate policy. This is also the fallback path the Historical source reverts to
    /// on failure or timeout (Req 7.5).
    /// </summary>
    private async Task<HeatmapResult> GetProjectedMatrixAsync(
        HeatmapQuery query,
        TimeZoneInfo viewerTz,
        ProjectionWindow window,
        CancellationToken ct)
    {
        // The aggregation depends only on the source, window kind, viewer time zone, and load metric,
        // so it is cacheable under that key (Req 13.1). View/control changes that do not alter the key
        // are served from the cache without recomputing (Req 13.2).
        Task<HeatmapResult> Factory(CancellationToken token) => ComputeProjectedMatrixAsync(query, viewerTz, window, token);

        // Defensive: if no memory cache is registered, compute directly without caching.
        if (_cache is null)
        {
            return await Factory(ct).ConfigureAwait(false);
        }

        var cacheKey = BuildCacheKey(HeatmapSource.Projected, query);
        return await GetOrCreateWithStaleAsync(cacheKey, Factory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves the Historical-source matrix for <paramref name="query"/>, degrading gracefully when
    /// the metrics provider fails or does not respond within the configured timeout. A fresh cached
    /// historical result is returned immediately; otherwise the recurring-schedule buckets are queried
    /// under a 10-second timeout (<see cref="HeatmapOptions.HistoricalQueryTimeoutSeconds"/>). On
    /// success the historical matrix is cached and returned; on failure or timeout the service reverts
    /// to the Projected source, retaining that data and surfacing a non-blocking, dismissible notice
    /// (Req 7.5). Zero-fire buckets are treated as no-data rather than zero-valued cells (Req 7.4).
    /// </summary>
    private async Task<HeatmapResult> GetHistoricalMatrixAsync(
        HeatmapQuery query,
        TimeZoneInfo viewerTz,
        ProjectionWindow window,
        CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(HeatmapSource.Historical, query);

        // Serve a fresh, previously-successful historical aggregation without re-querying (Req 13.2).
        if (_cache is not null
            && _cache.TryGetValue(cacheKey, out CacheEntry cached)
            && cached is not null
            && DateTimeOffset.UtcNow < cached.ExpiresAtUtc)
        {
            return cached.Value;
        }

        try
        {
            var buckets = await QueryHistoricalBucketsAsync(window, ct).ConfigureAwait(false);
            var result = BuildHistoricalResult(buckets, query.LoadMetric, window);

            // Cache only successful historical results; transient failures are not cached so the next
            // request re-attempts the provider and recovers automatically.
            if (_cache is not null)
            {
                Store(cacheKey, result);
            }

            return result;
        }
        catch (Exception ex)
        {
            // Revert to the Projected source, retain that data, and surface a dismissible notice
            // (Req 7.5). The notice text is rendered verbatim by Heatmap.razor.
            _logger.LogWarning(ex, "Historical heatmap query failed or timed out; reverting to the Projected source");

            var projected = await GetProjectedMatrixAsync(query, viewerTz, window, ct).ConfigureAwait(false);
            return projected with
            {
                HistoricalError = "Historical data could not be loaded; showing the projected schedule instead."
            };
        }
    }

    /// <summary>
    /// Queries the registered metrics provider for the window's recurring-schedule buckets, enforcing
    /// a hard timeout (<see cref="HeatmapOptions.HistoricalQueryTimeoutSeconds"/>, default 10s). The
    /// timeout is enforced even when a provider ignores its cancellation token, throwing
    /// <see cref="TimeoutException"/> so the caller reverts to the Projected source (Req 7.5).
    /// </summary>
    private async Task<IReadOnlyList<HistoricalScheduleBucket>> QueryHistoricalBucketsAsync(
        ProjectionWindow window,
        CancellationToken ct)
    {
        var timeoutSeconds = _options.Heatmap?.HistoricalQueryTimeoutSeconds ?? 10;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = 1;
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        // Linked source lets us best-effort cancel the in-flight query on timeout; the WhenAny race
        // guarantees we stop waiting at the deadline even if the provider never observes the token.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var queryTask = _metricsProvider.GetRecurringScheduleBucketsAsync(
            window.StartInclusive, window.EndExclusive, timeoutCts.Token);

        var completed = await Task.WhenAny(queryTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (completed != queryTask)
        {
            timeoutCts.Cancel();
            throw new TimeoutException(
                $"Historical recurring-schedule query did not complete within {timeoutSeconds} seconds.");
        }

        // Observe the query's result (or surface its exception to trigger the projected fallback).
        var buckets = await queryTask.ConfigureAwait(false);
        return buckets ?? (IReadOnlyList<HistoricalScheduleBucket>)Array.Empty<HistoricalScheduleBucket>();
    }

    /// <summary>
    /// Builds a <see cref="HeatmapResult"/> from historical recurring-schedule buckets. Buckets with a
    /// fire count of zero are treated as no-data and produce no cell (Req 7.4); the remaining buckets
    /// are summed per <c>queue × day × hour</c> cell and the matrix value domain is derived. Historical
    /// results carry no projection notices (the projected diagnostics do not apply).
    /// </summary>
    private static HeatmapResult BuildHistoricalResult(
        IReadOnlyList<HistoricalScheduleBucket> buckets,
        LoadMetric metric,
        ProjectionWindow window)
    {
        var valueByKey = new Dictionary<CellKey, double>();

        if (buckets is not null)
        {
            foreach (var bucket in buckets)
            {
                if (bucket is null)
                {
                    continue;
                }

                // Zero-fire buckets are no-data, not a zero value (Req 7.4): skip them entirely.
                if (bucket.FireCount <= 0)
                {
                    continue;
                }

                if (bucket.DayIndex < 0 || bucket.DayIndex > 6 || bucket.Hour < 0 || bucket.Hour > 23)
                {
                    continue;
                }

                var queue = string.IsNullOrWhiteSpace(bucket.Queue)
                    ? ScheduleAggregator.DefaultQueue
                    : bucket.Queue;

                var key = new CellKey(queue, bucket.DayIndex, bucket.Hour);
                valueByKey.TryGetValue(key, out var existing);
                valueByKey[key] = existing + HistoricalContributionFor(metric, bucket);
            }
        }

        var cells = new Dictionary<CellKey, HeatmapCell>();
        double min = 0;
        double max = 0;
        var hasCells = false;

        // Deterministic ordering (queue asc, then day, then hour) mirrors ScheduleAggregator.
        foreach (var entry in valueByKey.OrderBy(e => e.Key.Queue, StringComparer.Ordinal)
                                        .ThenBy(e => e.Key.DayIndex)
                                        .ThenBy(e => e.Key.Hour))
        {
            var cell = new HeatmapCell(
                entry.Key,
                entry.Value,
                ContributingJobCount: 0,
                DominantQueue: entry.Key.Queue,
                JobIds: Array.Empty<string>());

            cells[entry.Key] = cell;

            if (!hasCells)
            {
                min = cell.Value;
                max = cell.Value;
                hasCells = true;
            }
            else
            {
                if (cell.Value < min) min = cell.Value;
                if (cell.Value > max) max = cell.Value;
            }
        }

        var queues = cells.Keys
            .Select(k => k.Queue)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(q => q, StringComparer.Ordinal)
            .ToList();

        var matrix = new HeatmapMatrix(cells, queues, window, metric, min, max);

        return new HeatmapResult(
            matrix,
            UnparseableJobIds: Array.Empty<string>(),
            UnknownTimeZoneJobIds: Array.Empty<string>(),
            LongPeriodJobIds: Array.Empty<string>(),
            HistoricalError: null);
    }

    /// <summary>
    /// Computes a historical bucket's contribution under the active load metric. <c>FireCount</c>
    /// contributes the recurring-execution count; <c>WorkerMinutes</c> contributes the count scaled by
    /// the average execution duration in minutes, treated as at least one minute per fire (Req 2.3).
    /// </summary>
    private static double HistoricalContributionFor(LoadMetric metric, HistoricalScheduleBucket bucket)
    {
        if (metric == LoadMetric.WorkerMinutes)
        {
            var avgMinutes = bucket.AvgMs / 60000d;
            if (avgMinutes < 1d)
            {
                avgMinutes = 1d;
            }

            return bucket.FireCount * avgMinutes;
        }

        return bucket.FireCount;
    }

    /// <summary>
    /// Computes the aggregated <c>queue × day × hour</c> matrix from the Projected source. This is
    /// the uncached core invoked by <see cref="GetProjectedMatrixAsync"/> and by background refreshes;
    /// the caching/single-flight policy lives in <see cref="GetOrCreateWithStaleAsync"/>.
    /// </summary>
    private async Task<HeatmapResult> ComputeProjectedMatrixAsync(
        HeatmapQuery query,
        TimeZoneInfo viewerTz,
        ProjectionWindow window,
        CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Read recurring jobs through the storage-agnostic connection. A null/empty list (no jobs)
        // or an unreadable storage both yield the empty-state matrix (Req 1.5, 1.7).
        var dtos = ReadRecurringJobDtos();
        if (dtos.Count == 0)
        {
            return EmptyResult(window, query.LoadMetric);
        }

        var specs = await BuildSpecsAsync(dtos, ct).ConfigureAwait(false);

        var projection = ProjectionEngine.Project(specs, window);

        // Honor "Hide sub-hourly" by removing every fire belonging to a sub-hourly job before
        // aggregation, matching the concurrency/recommendation inputs (Req 20.2).
        var fires = query.HideSubHourly
            ? SubHourly.Filter(projection.Fires, viewerTz, window)
            : projection.Fires;

        var matrix = ScheduleAggregator.Aggregate(fires, query.LoadMetric, viewerTz, window);

        return new HeatmapResult(
            matrix,
            projection.UnparseableJobIds,
            projection.UnknownTimeZoneJobIds,
            projection.LongPeriodJobIds,
            HistoricalError: null);
    }

    /// <summary>
    /// Builds the cache key for an aggregation request from the active source, projection-window
    /// kind, viewer time zone, and load metric (Req 13.1). The source is passed explicitly so the
    /// Projected fallback served on a historical failure reuses the stable Projected key rather than
    /// the requesting Historical key. The load metric is part of the key because each cell stores only
    /// the value for the active metric, so switching metrics requires a recompute.
    /// </summary>
    private static string BuildCacheKey(HeatmapSource source, HeatmapQuery query)
    {
        var tz = string.IsNullOrWhiteSpace(query.ViewerTimeZoneId) ? "UTC" : query.ViewerTimeZoneId.Trim();
        return $"heatmap:{source}:{query.WindowKind}:{tz}:{query.LoadMetric}";
    }

    /// <summary>
    /// Serves the aggregation for <paramref name="key"/> with single-flight + stale-while-revalidate
    /// semantics over <see cref="IMemoryCache"/>:
    /// <list type="bullet">
    /// <item>A fresh cached entry (within its time-to-live) is returned immediately (Req 13.2).</item>
    /// <item>An expired (stale) entry is returned immediately while a single background recomputation
    /// replaces it (Req 13.8).</item>
    /// <item>On a cold key, concurrent callers serialize on a per-key gate so the aggregation is
    /// computed exactly once and the single result is shared (Req 13.4).</item>
    /// </list>
    /// </summary>
    private async Task<HeatmapResult> GetOrCreateWithStaleAsync(
        string key,
        Func<CancellationToken, Task<HeatmapResult>> factory,
        CancellationToken ct)
    {
        // Fast path: an entry exists. Serve it (fresh or stale), kicking off a refresh when stale.
        if (_cache.TryGetValue(key, out CacheEntry entry) && entry is not null)
        {
            if (DateTimeOffset.UtcNow < entry.ExpiresAtUtc)
            {
                return entry.Value; // Fresh hit (Req 13.2).
            }

            // Stale: trigger a single background recomputation and serve the stale value (Req 13.8).
            TriggerBackgroundRefresh(key, factory);
            return entry.Value;
        }

        // Cold path: serialize concurrent callers so the aggregation is computed once (Req 13.4).
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check: another caller may have populated the cache while we waited on the gate.
            if (_cache.TryGetValue(key, out entry) && entry is not null)
            {
                if (DateTimeOffset.UtcNow < entry.ExpiresAtUtc)
                {
                    return entry.Value;
                }
                // Stale entry found after waiting; we own the gate, so recompute synchronously below.
            }

            var value = await factory(ct).ConfigureAwait(false);
            Store(key, value);
            return value;
        }
        finally
        {
            gate.Release();
            // Do not remove the gate from _locks: a TryRemove after Release creates a TOCTOU race
            // where a concurrent caller can GetOrAdd a fresh gate and bypass mutual exclusion.
        }
    }

    /// <summary>
    /// Starts at most one background recomputation for <paramref name="key"/> using the per-key gate.
    /// If a compute/refresh is already in progress the call is a no-op and the caller keeps serving
    /// the stale value, ensuring the aggregation is recomputed exactly once at a time (Req 13.4, 13.8).
    /// </summary>
    private void TriggerBackgroundRefresh(string key, Func<CancellationToken, Task<HeatmapResult>> factory)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            // A compute or refresh is already running for this key; keep serving stale.
            return;
        }

        // Detach from the caller's request lifetime so cancelling the request does not abort the
        // refresh; the stale value remains served until this completes (Req 13.8).
        _ = Task.Run(async () =>
        {
            try
            {
                var value = await factory(CancellationToken.None).ConfigureAwait(false);
                Store(key, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background heatmap aggregation refresh failed for cache key {CacheKey}", key);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <summary>
    /// Stores an aggregation result under <paramref name="key"/>, stamping its freshness expiry at
    /// now + the configured time-to-live (Req 13.5) and retaining the physical entry long enough to
    /// serve stale while a refresh runs (Req 13.8). Sliding expiration bounds memory over time.
    /// </summary>
    private void Store(string key, HeatmapResult value)
    {
        var cacheEntry = new CacheEntry(value, DateTimeOffset.UtcNow.Add(_cacheTtl));
        _cache.Set(key, cacheEntry, new MemoryCacheEntryOptions
        {
            SlidingExpiration = _cacheRetention
        });
    }

    /// <summary>
    /// Resolves the active worker capacity from the running servers and an optional manual override.
    /// The detected capacity is the sum of the worker counts reported by
    /// <see cref="HangfireMonitorService.GetServers"/>; zero servers resolve to a capacity of 1
    /// (Req 5.1, 5.4). A valid manual override wins and is reported as
    /// <see cref="CapacitySource.ManualOverride"/> (Req 5.3).
    /// </summary>
    /// <param name="manualOverride">An optional, pre-validated manual capacity override.</param>
    /// <returns>The resolved capacity together with its source.</returns>
    public CapacityResult ResolveCapacity(int? manualOverride)
    {
        var detectedWorkerSum = 0;

        try
        {
            var servers = _monitor?.GetServers();
            if (servers != null)
            {
                foreach (var server in servers)
                {
                    if (server != null)
                    {
                        detectedWorkerSum += server.WorkersCount;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Treat an unreadable server list as "no servers"; CapacityResolver maps that to 1.
            _logger.LogError(ex, "Failed to read servers for heatmap capacity resolution");
            detectedWorkerSum = 0;
        }

        return CapacityResolver.Resolve(detectedWorkerSum, manualOverride);
    }

    /// <summary>
    /// Serializes the currently displayed <paramref name="matrix"/> to a self-describing RFC 4180 CSV
    /// document, scoped by the active source, projection window, viewer time zone, queue selection, and
    /// load metric carried on <paramref name="ctx"/> (Req 12.1). This is a thin delegation onto the
    /// pure, deterministic <see cref="CsvExporter.Export(HeatmapMatrix, CsvExportContext)"/> so the
    /// page has a single orchestration entry point for export, matching the design's
    /// <c>HeatmapService.ExportCsv</c> surface.
    /// </summary>
    /// <param name="matrix">The matrix whose populated cells are exported.</param>
    /// <param name="ctx">The contextual metadata written into the export so it is self-describing.</param>
    /// <returns>The complete CSV document as a single string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="matrix"/> or <paramref name="ctx"/> is null.</exception>
    public string ExportCsv(HeatmapMatrix matrix, CsvExportContext ctx)
        => CsvExporter.Export(matrix, ctx);

    // ─── Demand-aware analysis surface (concurrency, recommendations, demand, historical cells) ──
    // These methods complete the design's HeatmapService surface so the Planner, Concurrency,
    // Recommendations, Punchcard, and Calendar views can be fed real data. Each projects the
    // recurring jobs through the same ProjectionEngine pipeline the matrix uses, then delegates to
    // the pure engines. Fires are converted into the viewer's local clock so the day grouping,
    // minute-of-day, and weekday seen by the concurrency/recommendation engines match the rendered
    // grid exactly (Req 8.2/8.4).

    /// <summary>
    /// Computes the duration-aware concurrency for a single window day (<paramref name="dayIndex"/>,
    /// 0..6) from the projected cron fires, optionally adding a per-minute ad-hoc demand baseline
    /// before the capacity comparison (Req 4.x, 19.1). Honors "Hide sub-hourly" (Req 20.2). Returns
    /// the empty-day result when no fires fall on the day.
    /// </summary>
    public async Task<ConcurrencyResult> GetConcurrencyAsync(
        HeatmapQuery query,
        int dayIndex,
        int workerCapacity,
        IReadOnlyList<int> adHocBaselinePerSlot,
        CancellationToken ct,
        IReadOnlyList<string> queues = null)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        ct.ThrowIfCancellationRequested();

        var context = await ProjectAsync(query, queues, ct).ConfigureAwait(false);
        var dayFires = LocalDayFires(context, dayIndex);
        return ConcurrencyAnalyzer.Analyze(dayFires, workerCapacity, adHocBaselinePerSlot);
    }

    /// <summary>
    /// Finds the window day with the highest cron peak concurrency (ties resolve to the earliest
    /// day), so the Concurrency view can default to the busiest day exactly like the v4 mockup. Uses
    /// the cron-only peak for selection; the demand baseline (if any) is applied when the chosen day
    /// is subsequently rendered via <see cref="GetConcurrencyAsync"/>.
    /// </summary>
    public async Task<int> GetWorstConcurrencyDayAsync(HeatmapQuery query, int workerCapacity, CancellationToken ct, IReadOnlyList<string> queues = null)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        ct.ThrowIfCancellationRequested();

        var context = await ProjectAsync(query, queues, ct).ConfigureAwait(false);

        var worstDay = 0;
        var worstPeak = -1;
        for (var day = 0; day < HeatmapTime.WindowDays; day++)
        {
            var result = ConcurrencyAnalyzer.Analyze(LocalDayFires(context, day), workerCapacity, null);
            if (result.PeakConcurrency > worstPeak)
            {
                worstPeak = result.PeakConcurrency;
                worstDay = day;
            }
        }

        return worstDay;
    }

    /// <summary>
    /// Produces the read-only stagger recommendations for the projected cron fires across the window,
    /// optionally demand-aware via <paramref name="adHocBaselinePerSlot"/> (Req 11, 19.3–19.5). Honors
    /// "Hide sub-hourly" (Req 20.2). Recommendations are deterministically ordered by the engine.
    /// </summary>
    public async Task<IReadOnlyList<Recommendation>> GetRecommendationsAsync(
        HeatmapQuery query,
        int workerCapacity,
        IReadOnlyList<int> adHocBaselinePerSlot,
        CancellationToken ct,
        IReadOnlyList<string> queues = null)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        ct.ThrowIfCancellationRequested();

        var context = await ProjectAsync(query, queues, ct).ConfigureAwait(false);
        var fires = AllLocalFires(context);
        return RecommendationEngine.Analyze(fires, workerCapacity, adHocBaselinePerSlot);
    }

    /// <summary>
    /// Builds the ad-hoc <c>Demand_Profile</c> for the query's lookback / aggregation statistic /
    /// load metric (Req 16.3–16.5). Returns an empty profile when no demand provider is registered
    /// (Req 16.7); never throws.
    /// </summary>
    public DemandProfile GetDemandProfile(HeatmapQuery query)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var statistic = ParseAggregationStatistic(query.AggregationStatistic);

        if (_demandProfileProvider is null)
        {
            return DemandProfile.Empty(query.LoadMetric, statistic, query.LookbackWeeks);
        }

        var profile = _demandProfileProvider.GetProfile(query.LookbackWeeks, statistic, query.LoadMetric);

        // The rollup stores demand in native UTC (day-of-week × hour) coordinates, while the cron
        // matrix is bucketed in viewer-local time. Rotate the demand into the viewer's local
        // coordinates so the Planner shading, insights, and the demand-aware concurrency baseline all
        // align on the same clock hours as the cron overlay (Req 8.2 parity for ad-hoc demand).
        var viewerTz = HeatmapTime.ResolveTimeZone(query.ViewerTimeZoneId);
        var window = HeatmapTime.BuildWindow(query.WindowKind, DateTimeOffset.UtcNow, viewerTz);
        var viewerOffset = viewerTz.GetUtcOffset(window.StartInclusive);

        return DemandProfileProvider.ShiftToViewerLocal(profile, viewerOffset);
    }

    /// <summary>
    /// Returns the per-<c>(dayIndex, hour)</c> historical execution statistics (fire/failure counts
    /// and p95 duration) for the active window, collapsed across queues, so the Punchcard and Calendar
    /// views can recolor cells by failure rate or duration under the Historical source (Req 6.3, 6.4,
    /// 7.6). Returns an empty list when no metrics provider is registered or the query fails — callers
    /// then render the Color_Ramp's empty shade (Req 7.4). Never throws.
    /// </summary>
    public async Task<IReadOnlyList<HeatmapHistoricalCell>> GetHistoricalCellsAsync(
        HeatmapQuery query, CancellationToken ct)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (!IsHistoricalAvailable)
        {
            return Array.Empty<HeatmapHistoricalCell>();
        }

        var viewerTz = HeatmapTime.ResolveTimeZone(query.ViewerTimeZoneId);
        var window = HeatmapTime.BuildWindow(query.WindowKind, DateTimeOffset.UtcNow, viewerTz);

        try
        {
            var buckets = await QueryHistoricalBucketsAsync(window, ct).ConfigureAwait(false);

            // Collapse the per-queue buckets onto (dayIndex, hour): sum fire/failure counts and take
            // the maximum p95 across the position (matching the planner's CollapseHistorical).
            var byCell = new Dictionary<(int Day, int Hour), (long Fire, long Fail, double P95)>();
            foreach (var bucket in buckets)
            {
                if (bucket is null || bucket.FireCount <= 0)
                {
                    continue;
                }

                if (bucket.DayIndex < 0 || bucket.DayIndex > 6 || bucket.Hour < 0 || bucket.Hour > 23)
                {
                    continue;
                }

                var key = (bucket.DayIndex, bucket.Hour);
                byCell.TryGetValue(key, out var cur);
                byCell[key] = (cur.Fire + bucket.FireCount, cur.Fail + bucket.FailureCount, Math.Max(cur.P95, bucket.P95Ms));
            }

            return byCell
                .Select(e => new HeatmapHistoricalCell(e.Key.Day, e.Key.Hour, e.Value.Fire, e.Value.Fail, e.Value.P95))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read historical cells for the heatmap; rendering empty.");
            return Array.Empty<HeatmapHistoricalCell>();
        }
    }

    /// <summary>
    /// Returns the recurring-job specs backing the projection (id, cron, queue, time zone, resolved
    /// estimated duration) so the recurring-job table can list every job — including long-period jobs
    /// that contribute zero cells (Req 9.7). Sub-hourly jobs are retained here; the "Hide sub-hourly"
    /// control only filters the matrix/concurrency inputs, not the job inventory.
    /// </summary>
    public async Task<IReadOnlyList<RecurringJobSpec>> GetRecurringJobSpecsAsync(
        HeatmapQuery query, CancellationToken ct)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        ct.ThrowIfCancellationRequested();
        return await BuildSpecsAsync(ReadRecurringJobDtos(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects the registered recurring jobs over the query's window and applies the "Hide sub-hourly"
    /// filter when enabled. The returned fires retain their absolute UTC instants; callers convert to
    /// viewer-local time via <see cref="LocalDayFires"/> / <see cref="AllLocalFires"/> as needed.
    /// </summary>
    private async Task<ProjectionContext> ProjectAsync(HeatmapQuery query, IReadOnlyList<string> queues, CancellationToken ct)
    {
        var viewerTz = HeatmapTime.ResolveTimeZone(query.ViewerTimeZoneId);
        var window = HeatmapTime.BuildWindow(query.WindowKind, DateTimeOffset.UtcNow, viewerTz);

        var specs = await BuildSpecsAsync(ReadRecurringJobDtos(), ct).ConfigureAwait(false);
        var projection = ProjectionEngine.Project(specs, window);

        var fires = query.HideSubHourly
            ? SubHourly.Filter(projection.Fires, viewerTz, window)
            : projection.Fires;

        // Optional queue filter (from the queue chips): keep only fires on the selected queues,
        // normalizing a blank queue to the default exactly as the aggregator does.
        if (queues is { Count: > 0 })
        {
            var allow = new HashSet<string>(queues, StringComparer.Ordinal);
            fires = fires
                .Where(f => f is not null
                    && allow.Contains(string.IsNullOrWhiteSpace(f.Queue) ? ScheduleAggregator.DefaultQueue : f.Queue))
                .ToList();
        }

        return new ProjectionContext(fires, window, viewerTz);
    }

    /// <summary>
    /// Selects the fires falling on a single window day (by their viewer-local bucket) and converts
    /// each to its viewer-local clock so the concurrency engine's minute-of-day matches the grid.
    /// </summary>
    private static List<ProjectedFire> LocalDayFires(ProjectionContext context, int dayIndex)
    {
        var list = new List<ProjectedFire>();
        foreach (var fire in context.Fires)
        {
            if (fire is null)
            {
                continue;
            }

            var (day, _) = HeatmapTime.GetBucket(fire.FireTimeUtc, context.ViewerTz, context.Window);
            if (day != dayIndex)
            {
                continue;
            }

            list.Add(fire with { FireTimeUtc = HeatmapTime.ToViewerLocal(fire.FireTimeUtc, context.ViewerTz) });
        }

        return list;
    }

    /// <summary>
    /// Converts every fire to its viewer-local clock so the recommendation engine's day grouping,
    /// weekday, and minute-of-day are all expressed in the viewer time zone.
    /// </summary>
    private static List<ProjectedFire> AllLocalFires(ProjectionContext context)
    {
        var list = new List<ProjectedFire>(context.Fires.Count);
        foreach (var fire in context.Fires)
        {
            if (fire is null)
            {
                continue;
            }

            list.Add(fire with { FireTimeUtc = HeatmapTime.ToViewerLocal(fire.FireTimeUtc, context.ViewerTz) });
        }

        return list;
    }

    /// <summary>Maps the query's aggregation-statistic string ("Average"/"p95") onto the enum.</summary>
    private static AggregationStatistic ParseAggregationStatistic(string raw)
        => string.Equals(raw, "p95", StringComparison.OrdinalIgnoreCase)
            ? AggregationStatistic.P95
            : AggregationStatistic.Average;

    /// <summary>The projected fires plus the window and viewer time zone they were computed against.</summary>
    private sealed record ProjectionContext(
        IReadOnlyList<ProjectedFire> Fires,
        ProjectionWindow Window,
        TimeZoneInfo ViewerTz);

    /// <summary>
    /// Reads the recurring jobs via the storage-agnostic Hangfire connection and maps each
    /// <see cref="RecurringJobDto"/> onto a pure-engine <see cref="RecurringJobSpec"/>. Any storage
    /// failure is swallowed and yields an empty list so the page can render its empty state without
    /// raising an error (Req 1.7).
    /// </summary>
    /// <summary>
    /// Reads the recurring jobs via the storage-agnostic Hangfire connection. Any storage failure is
    /// swallowed and yields an empty list so the page can render its empty state without raising an
    /// error (Req 1.7).
    /// </summary>
    private List<RecurringJobDto> ReadRecurringJobDtos()
    {
        if (_storage == null)
        {
            return new List<RecurringJobDto>();
        }

        try
        {
            using var connection = _storage.GetConnection();
            var jobs = connection.GetRecurringJobs();
            if (jobs == null || jobs.Count == 0)
            {
                return new List<RecurringJobDto>();
            }

            var dtos = new List<RecurringJobDto>(jobs.Count);
            foreach (var dto in jobs)
            {
                if (dto != null)
                {
                    dtos.Add(dto);
                }
            }

            return dtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read recurring jobs for heatmap projection");
            return new List<RecurringJobDto>();
        }
    }

    /// <summary>
    /// Maps the supplied <see cref="RecurringJobDto"/> list onto pure-engine <see cref="RecurringJobSpec"/>s,
    /// resolving each job's estimated duration from the historical p95 supplied by the
    /// <see cref="EstimatedDurationResolver"/> when a metrics provider is registered (Req 21.2),
    /// otherwise the configured default treated as at least one minute (Req 21.3, 21.4). A single
    /// batched duration query is issued for the whole list; a missing resolver or any failure degrades
    /// every job to the flagged default, so the Projected source keeps working on any storage.
    /// </summary>
    private async Task<List<RecurringJobSpec>> BuildSpecsAsync(
        List<RecurringJobDto> dtos, CancellationToken ct)
    {
        var defaultDuration = ResolveDefaultDuration();

        if (dtos == null || dtos.Count == 0)
        {
            return new List<RecurringJobSpec>();
        }

        // Resolve historical p95 durations in one batched call keyed by the metrics provider's
        // "{ClassName}.{MethodName}" job-type key. Null resolver / no provider → empty map → defaults.
        var durations = await ResolveDurationsAsync(dtos, ct).ConfigureAwait(false);

        var specs = new List<RecurringJobSpec>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (dto == null)
            {
                continue;
            }

            var queue = ResolveQueue(dto);
            var duration = defaultDuration;
            var isDefault = true;

            var key = JobTypeKey(dto);
            if (durations != null && key != null && durations.TryGetValue(key, out var resolved))
            {
                duration = resolved.Duration;
                isDefault = resolved.IsDefault;
            }

            specs.Add(new RecurringJobSpec(
                JobId: dto.Id,
                CronExpression: dto.Cron,
                TimeZoneId: dto.TimeZoneId,
                Queue: queue,
                EstimatedDuration: duration,
                EstimatedDurationIsDefault: isDefault));
        }

        return specs;
    }

    /// <summary>
    /// Resolves the job-type → estimated-duration map for the supplied jobs, memoized for the
    /// configured cache-TTL window so repeated projections within one interaction reuse a single
    /// duration-stats query. Returns <c>null</c> when no metrics provider is registered (every job
    /// then falls back to the configured default).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, (TimeSpan Duration, bool IsDefault)>> ResolveDurationsAsync(
        List<RecurringJobDto> dtos, CancellationToken ct)
    {
        if (_durationResolver == null || !_durationResolver.IsAvailable)
        {
            return null;
        }

        if (_durationMemo != null && DateTimeOffset.UtcNow - _durationMemoAt < _cacheTtl)
        {
            return _durationMemo;
        }

        var keys = dtos.Select(JobTypeKey).Where(k => !string.IsNullOrEmpty(k));
        var (from, to) = DurationStatsWindow();

        try
        {
            var map = await _durationResolver.ResolveBatchAsync(keys, from, to, ct).ConfigureAwait(false);
            _durationMemo = map;
            _durationMemoAt = DateTimeOffset.UtcNow;
            return map;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve historical durations for the heatmap; using defaults");
            // Serve the previous memo when available; otherwise fall back to defaults (null).
            return _durationMemo;
        }
    }

    /// <summary>
    /// Derives the metrics provider's job-type key (<c>{ClassName}.{MethodName}</c>) from a recurring
    /// job, matching <c>IStorageMetricsProvider.GetJobDurationStatsAsync</c>'s <c>JobType</c> format.
    /// Returns <c>null</c> when the job's type or method cannot be determined.
    /// </summary>
    private static string JobTypeKey(RecurringJobDto dto)
    {
        var type = dto?.Job?.Type;
        var method = dto?.Job?.Method;
        if (type == null || method == null)
        {
            return null;
        }

        return $"{type.Name}.{method.Name}";
    }

    /// <summary>
    /// The historical window over which job-type durations are sampled for the p95 estimate. A short,
    /// fixed 7-day lookback keeps the duration query bounded and independent of the projection window.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset To) DurationStatsWindow()
    {
        var to = DateTimeOffset.UtcNow;
        return (to.AddDays(-7), to);
    }

    /// <summary>
    /// Determines a recurring job's effective queue. Precedence: the job's own InvocationData queue
    /// (set by the modern <c>RecurringJob.AddOrUpdate(id, queue, …)</c> API and used by Hangfire when
    /// the job is enqueued), then the recurring job's top-level stored queue (older API), then a
    /// <see cref="QueueAttribute"/> on the method (or its declaring type), and finally
    /// <see cref="ScheduleAggregator.DefaultQueue"/> when none is determinable (Req 2.4).
    /// </summary>
    private static string ResolveQueue(RecurringJobDto dto)
    {
        var job = dto.Job;

        // Prefer the job's own (InvocationData) queue: the modern Hangfire API
        // `RecurringJob.AddOrUpdate(id, queue, …)` stores the target queue here (serialized as "q"),
        // while the top-level recurring "Queue" hash field stays "default". The job is enqueued to
        // this InvocationData queue at trigger time, so it is the authoritative effective queue.
        if (job != null && !string.IsNullOrWhiteSpace(job.Queue))
        {
            return job.Queue;
        }

        // Older API path: the queue was stored in the recurring job's top-level "Queue" field.
        if (!string.IsNullOrWhiteSpace(dto.Queue))
        {
            return dto.Queue;
        }

        if (job != null)
        {
            try
            {
                var attr = job.Method?.GetCustomAttribute<QueueAttribute>(inherit: true)
                           ?? job.Method?.DeclaringType?.GetCustomAttribute<QueueAttribute>(inherit: true);

                if (attr != null && !string.IsNullOrWhiteSpace(attr.Queue) && !attr.Queue.Contains('{'))
                {
                    return attr.Queue;
                }
            }
            catch
            {
                // Reflection failure → fall through to the default queue.
            }
        }

        return ScheduleAggregator.DefaultQueue;
    }

    /// <summary>
    /// Returns the configured default estimated duration, treated as at least one minute (Req 21.3).
    /// </summary>
    private TimeSpan ResolveDefaultDuration()
    {
        var configured = _options.Heatmap?.DefaultEstimatedDuration ?? TimeSpan.FromMinutes(1);
        return configured < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : configured;
    }

    /// <summary>
    /// Builds the empty-state result: an empty matrix over the active window with no notices (Req 1.5, 1.7).
    /// </summary>
    private static HeatmapResult EmptyResult(ProjectionWindow window, LoadMetric metric)
    {
        var matrix = new HeatmapMatrix(
            Cells: new Dictionary<CellKey, HeatmapCell>(),
            Queues: Array.Empty<string>(),
            Window: window,
            Metric: metric,
            Min: 0,
            Max: 0);

        return new HeatmapResult(
            matrix,
            UnparseableJobIds: Array.Empty<string>(),
            UnknownTimeZoneJobIds: Array.Empty<string>(),
            LongPeriodJobIds: Array.Empty<string>(),
            HistoricalError: null);
    }

    /// <summary>
    /// A cached aggregation together with the instant at which it stops being fresh. The entry is
    /// retained physically beyond this instant so it can be served stale while a background refresh
    /// recomputes it (Req 13.5, 13.8).
    /// </summary>
    private sealed class CacheEntry
    {
        public CacheEntry(HeatmapResult value, DateTimeOffset expiresAtUtc)
        {
            Value = value;
            ExpiresAtUtc = expiresAtUtc;
        }

        /// <summary>The cached aggregation result.</summary>
        public HeatmapResult Value { get; }

        /// <summary>The UTC instant after which the entry is considered stale (Req 13.5).</summary>
        public DateTimeOffset ExpiresAtUtc { get; }
    }
}
