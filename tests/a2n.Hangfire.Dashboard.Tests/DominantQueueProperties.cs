using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property test for <see cref="MatrixViews.DominantQueuePerCell"/> dominant-queue selection.
///
/// **Property 10: Dominant-queue selection is the max contributor with an ascending-name tie-break**
/// **Validates: Requirements 3.6, 18.2**
///
/// For any cell's per-queue load contributions, the dominant queue is the one contributing the
/// greatest load, and when several queues tie for the greatest load the dominant queue is the
/// alphabetically smallest queue name.
/// </summary>
public class DominantQueueProperties
{
    /// <summary>The default queue label applied when a fire's queue cannot be determined (Req 2.4).</summary>
    private const string DefaultQueue = "default";

    /// <summary>
    /// A deliberately small queue alphabet — including a blank (normalized to <c>default</c>) and an
    /// explicit <c>default</c> — so multiple queues frequently collide in the same <c>(day, hour)</c>
    /// position and tie for the greatest load, exercising the ascending-name tie-break (Req 3.6, 18.2).
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "charlie", "default", "" };

    /// <summary>
    /// A single fire descriptor targeting a specific <c>(day, hour)</c> position with a chosen queue.
    /// Day and hour ranges are intentionally narrow to maximize shared buckets and load ties.
    /// </summary>
    private static Gen<(string Queue, int DayIndex, int Hour)> FireDescGen =>
        from queue in Gen.Elements(Queues)
        from day in Gen.Choose(0, 2)
        from hour in Gen.Choose(0, 3)
        select (queue, day, hour);

    /// <summary>
    /// **Property 10: Dominant-queue selection is the max contributor with an ascending-name tie-break**
    /// **Validates: Requirements 3.6, 18.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property DominantQueue_IsMaxContributor_WithAscendingNameTieBreak()
    {
        var arb = Arb.From(
            from count in Gen.Choose(0, 60)
            from descs in Gen.ArrayOf(count, FireDescGen)
            select descs);

        return Prop.ForAll(arb, descs =>
        {
            // Bucket fires by their (day, hour) using a UTC viewer time zone so each descriptor lands
            // deterministically in its intended bucket; queues then share (day, hour) positions.
            var window = HeatmapTime.BuildWindow(
                ProjectionWindowKind.IdealizedWeek,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TimeZoneInfo.Utc);

            var fires = descs
                .Select((d, i) => new ProjectedFire(
                    JobId: $"job-{i}",
                    Queue: d.Queue,
                    // Place inside the (day, hour) bucket; +1 minute keeps it within the clock hour.
                    FireTimeUtc: window.StartInclusive.AddDays(d.DayIndex).AddHours(d.Hour).AddMinutes(1),
                    EstimatedDuration: TimeSpan.FromMinutes(1)))
                .ToList();

            // Fire-count metric makes each cell value the integer count of fires for that
            // (queue, day, hour), so per-queue load ties are exact.
            var matrix = ScheduleAggregator.Aggregate(fires, LoadMetric.FireCount, TimeZoneInfo.Utc, window);

            var actual = MatrixViews.DominantQueuePerCell(matrix);

            // Independent oracle: per (day, hour), sum each queue's load, pick the greatest with an
            // ascending Ordinal-name tie-break.
            var loadByPositionQueue = new Dictionary<(int DayIndex, int Hour), Dictionary<string, double>>();
            foreach (var fire in fires)
            {
                var queue = string.IsNullOrWhiteSpace(fire.Queue) ? DefaultQueue : fire.Queue;
                var (dayIndex, hour) = HeatmapTime.GetBucket(fire.FireTimeUtc, TimeZoneInfo.Utc, window);
                var position = (dayIndex, hour);

                if (!loadByPositionQueue.TryGetValue(position, out var byQueue))
                {
                    byQueue = new Dictionary<string, double>(StringComparer.Ordinal);
                    loadByPositionQueue[position] = byQueue;
                }

                byQueue.TryGetValue(queue, out var running);
                byQueue[queue] = running + 1d;
            }

            var expected = new Dictionary<(int DayIndex, int Hour), string>();
            foreach (var entry in loadByPositionQueue)
            {
                var bestQueue = (string)null;
                var bestLoad = double.NegativeInfinity;

                // Iterate queues in ascending Ordinal order so the first queue achieving the max load
                // is the alphabetically smallest — the required tie-break.
                foreach (var pair in entry.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (pair.Value > bestLoad)
                    {
                        bestLoad = pair.Value;
                        bestQueue = pair.Key;
                    }
                }

                expected[entry.Key] = bestQueue;
            }

            // Same set of populated positions.
            if (actual.Count != expected.Count)
            {
                return false.Label($"position count mismatch: actual={actual.Count} expected={expected.Count}");
            }

            foreach (var kvp in expected)
            {
                if (!actual.TryGetValue(kvp.Key, out var dominant))
                {
                    return false.Label($"missing position (day={kvp.Key.DayIndex}, hour={kvp.Key.Hour})");
                }

                if (!string.Equals(dominant, kvp.Value, StringComparison.Ordinal))
                {
                    return false.Label(
                        $"dominant queue mismatch at (day={kvp.Key.DayIndex}, hour={kvp.Key.Hour}): " +
                        $"actual='{dominant}' expected='{kvp.Value}'");
                }
            }

            return true.ToProperty();
        });
    }
}
