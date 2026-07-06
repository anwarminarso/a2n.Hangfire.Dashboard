using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure, deterministic aggregation of projected (or historical) fire times into a
/// <c>queue × day × hour</c> matrix with a derived per-cell load value. Each fire is converted to
/// the viewer time zone via <see cref="HeatmapTime"/> and assigned to exactly one bucket identified
/// by its queue, the zero-based day index within the active window, and the local clock hour
/// (0&#8211;23).
/// </summary>
/// <remarks>
/// <para>The two load metrics are computed as follows (Req 2.2, 2.3, 2.6):</para>
/// <list type="bullet">
/// <item><see cref="LoadMetric.FireCount"/> — the integer count of fires in the bucket.</item>
/// <item><see cref="LoadMetric.WorkerMinutes"/> — the sum, in minutes, of each fire's estimated
/// duration treated as at least one minute, with the whole duration attributed to the single bucket
/// containing the fire time.</item>
/// </list>
/// <para>A fire whose queue is null, empty, or whitespace is attributed to the <c>default</c> queue
/// (Req 2.4). Within each cell the dominant queue is the queue contributing the greatest load, with
/// ascending queue-name tie-break (Req 3.6, 18.2). Bucketing is driven by the absolute instant so a
/// DST gap/overlap still resolves to exactly one deterministic bucket (Req 8.7), and the output is
/// identical for any permutation of the same input fires (Req 2.5).</para>
/// <para>Validates portions of Requirements 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 8.2, 8.4, and 8.7.</para>
/// </remarks>
public static class ScheduleAggregator
{
    /// <summary>The queue label applied to fires whose queue cannot be determined (Req 2.4).</summary>
    public const string DefaultQueue = "default";

    /// <summary>The minimum estimated duration attributed to any fire (Req 2.3, 2.6).</summary>
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Aggregates the supplied fires into a deterministic <c>queue × day × hour</c> matrix.
    /// </summary>
    /// <param name="fires">The fires to bucket; order does not affect the result (Req 2.5).</param>
    /// <param name="metric">The load metric used to compute each cell value.</param>
    /// <param name="viewerTimeZone">The viewer time zone fires are converted to; UTC when null (Req 8.5).</param>
    /// <param name="window">The active projection window the matrix is computed over.</param>
    /// <returns>
    /// A <see cref="HeatmapMatrix"/> whose populated cells carry the load value, contributing job
    /// count, dominant queue, and contributing job ids, together with the distinct queues (ascending)
    /// and the matrix value domain (<c>Min</c>/<c>Max</c>).
    /// </returns>
    public static HeatmapMatrix Aggregate(
        IReadOnlyList<ProjectedFire> fires,
        LoadMetric metric,
        TimeZoneInfo viewerTimeZone,
        ProjectionWindow window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var timeZone = viewerTimeZone ?? TimeZoneInfo.Utc;
        var accumulators = new Dictionary<CellKey, CellAccumulator>();

        if (fires is not null)
        {
            foreach (var fire in fires)
            {
                if (fire is null)
                {
                    continue;
                }

                var queue = NormalizeQueue(fire.Queue);
                var (dayIndex, hour) = HeatmapTime.GetBucket(fire.FireTimeUtc, timeZone, window);
                var key = new CellKey(queue, dayIndex, hour);

                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    accumulator = new CellAccumulator();
                    accumulators[key] = accumulator;
                }

                accumulator.Add(queue, fire.JobId, ContributionFor(metric, fire.EstimatedDuration));
            }
        }

        // Materialize cells in a deterministic order (queue asc, then day, then hour) so the output
        // is independent of input ordering (Req 2.5).
        var cells = new Dictionary<CellKey, HeatmapCell>();
        double min = 0;
        double max = 0;
        var hasCells = false;

        foreach (var entry in accumulators.OrderBy(e => e.Key.Queue, StringComparer.Ordinal)
                                          .ThenBy(e => e.Key.DayIndex)
                                          .ThenBy(e => e.Key.Hour))
        {
            var cell = entry.Value.ToCell(entry.Key);
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

        return new HeatmapMatrix(cells, queues, window, metric, min, max);
    }

    private static string NormalizeQueue(string queue)
        => string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

    private static double ContributionFor(LoadMetric metric, TimeSpan estimatedDuration)
    {
        if (metric == LoadMetric.WorkerMinutes)
        {
            var duration = estimatedDuration < MinimumDuration ? MinimumDuration : estimatedDuration;
            return duration.TotalMinutes;
        }

        // Fire count: each fire contributes exactly one.
        return 1d;
    }

    /// <summary>
    /// Mutable per-cell accumulator that tracks the total load, per-queue load contributions, and
    /// the contributing job ids while preserving deterministic ordering of the final outputs.
    /// </summary>
    private sealed class CellAccumulator
    {
        private readonly Dictionary<string, double> _loadByQueue = new(StringComparer.Ordinal);
        private readonly List<string> _jobIds = new();
        private readonly HashSet<string> _seenJobIds = new(StringComparer.Ordinal);

        private double _total;

        public void Add(string queue, string jobId, double contribution)
        {
            _total += contribution;

            _loadByQueue.TryGetValue(queue, out var existing);
            _loadByQueue[queue] = existing + contribution;

            var id = jobId ?? string.Empty;
            if (_seenJobIds.Add(id))
            {
                _jobIds.Add(id);
            }
        }

        public HeatmapCell ToCell(CellKey key)
        {
            // Dominant queue = greatest load contributor with ascending-name tie-break (Req 3.6, 18.2).
            var dominantQueue = key.Queue;
            var dominantLoad = double.NegativeInfinity;

            foreach (var pair in _loadByQueue.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (pair.Value > dominantLoad)
                {
                    dominantLoad = pair.Value;
                    dominantQueue = pair.Key;
                }
            }

            var jobIds = _jobIds.OrderBy(id => id, StringComparer.Ordinal).ToList();

            return new HeatmapCell(
                key,
                _total,
                jobIds.Count,
                dominantQueue,
                jobIds);
        }
    }
}
