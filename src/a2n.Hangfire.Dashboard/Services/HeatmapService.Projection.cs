#nullable enable

using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Services;

public partial class HeatmapService
{
    /// <summary>
    /// Projects the registered recurring jobs over the query's window and applies the "Hide sub-hourly"
    /// filter when enabled. The returned fires retain their absolute UTC instants; callers convert to
    /// viewer-local time via <see cref="LocalDayFires"/> / <see cref="AllLocalFires"/> as needed.
    /// </summary>
    private async Task<ProjectionContext> ProjectAsync(HeatmapQuery query, IReadOnlyList<string>? queues, CancellationToken ct)
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
}
