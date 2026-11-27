using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

public partial class HeatmapService
{
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

        try
        {
            var buckets = await QueryHistoricalBucketsAsync(query.LookbackWeeks, ct).ConfigureAwait(false);

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
}
