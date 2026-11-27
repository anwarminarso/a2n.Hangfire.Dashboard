using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

public partial class HeatmapService
{
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
            var buckets = await QueryHistoricalBucketsAsync(query.LookbackWeeks, ct).ConfigureAwait(false);
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
    /// Queries the registered metrics provider for the recurring-schedule buckets, enforcing a hard
    /// timeout (<see cref="HeatmapOptions.HistoricalQueryTimeoutSeconds"/>, default 10s). The buckets
    /// are keyed by day-of-week × hour, so the query aggregates the trailing
    /// <paramref name="lookbackWeeks"/> weeks of executions (now − N×7 days … now) into those
    /// day-of-week buckets — this is what makes the Lookback control change the Historical heatmap.
    /// The timeout is enforced even when a provider ignores its cancellation token, throwing
    /// <see cref="TimeoutException"/> so the caller reverts to the Projected source (Req 7.5).
    /// </summary>
    private async Task<IReadOnlyList<HistoricalScheduleBucket>> QueryHistoricalBucketsAsync(
        int lookbackWeeks,
        CancellationToken ct)
    {
        var timeoutSeconds = _options.Heatmap?.HistoricalQueryTimeoutSeconds ?? 10;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = 1;
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        // The bucket query aggregates by day-of-week × hour over the lookback window ending now, so a
        // wider Lookback produces more robust day-of-week averages (Req: Lookback drives Historical).
        var weeks = lookbackWeeks < 1 ? 1 : lookbackWeeks;
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-7 * weeks);

        // Linked source lets us best-effort cancel the in-flight query on timeout; the WhenAny race
        // guarantees we stop waiting at the deadline even if the provider never observes the token.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var queryTask = _metricsProvider.GetRecurringScheduleBucketsAsync(from, to, timeoutCts.Token);

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
}
