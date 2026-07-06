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
/// Property tests for <see cref="ScheduleAggregator.Aggregate"/> bucketing correctness.
///
/// **Property 6: Aggregation is exact, complete, and order-independent**
/// **Validates: Requirements 2.1, 2.2, 2.3, 2.5, 2.6**
///
/// For any list of fires, a load metric, and a viewer time zone, the aggregated matrix assigns each
/// fire to exactly one <c>queue × day × hour</c> bucket (so the total Fire-count over all cells
/// equals the number of fires); under the Fire-count metric each cell equals the number of fires in
/// its bucket (0 when empty); under the Worker-minutes metric each cell equals the sum of
/// <c>max(duration, 1 minute)</c> in minutes of its fires (0 when empty); and aggregating any
/// permutation of the same fires yields an identical matrix.
/// </summary>
public class AggregationCorrectnessProperties
{
    /// <summary>The default queue label applied when a fire's queue cannot be determined (Req 2.4).</summary>
    private const string DefaultQueue = "default";

    /// <summary>The number of one-minute offsets inside a seven-day window.</summary>
    private const int WindowMinutes = 7 * 24 * 60;

    /// <summary>Floating-point tolerance for Worker-minutes comparisons.</summary>
    private const double Tolerance = 1e-9;

    /// <summary>
    /// Candidate queue labels: real names plus blank/whitespace and an explicit "default" so the
    /// unknown-queue normalization (blank → <c>default</c>) is exercised and collides with the real
    /// default bucket (Req 2.4).
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "charlie", "default", "", "   " };

    /// <summary>
    /// Representative viewer time zones: UTC, fixed offsets (including a half-hour and a +13 offset),
    /// and any real DST zones that resolve on this host (via the project's cross-platform resolver).
    /// </summary>
    private static readonly TimeZoneInfo[] TimeZones = BuildTimeZones();

    private static TimeZoneInfo[] BuildTimeZones()
    {
        var zones = new List<TimeZoneInfo>
        {
            TimeZoneInfo.Utc,
            TimeZoneInfo.CreateCustomTimeZone("Test+05:30", new TimeSpan(5, 30, 0), "Test +05:30", "Test +05:30"),
            TimeZoneInfo.CreateCustomTimeZone("Test-08:00", new TimeSpan(-8, 0, 0), "Test -08:00", "Test -08:00"),
            TimeZoneInfo.CreateCustomTimeZone("Test+13:00", new TimeSpan(13, 0, 0), "Test +13:00", "Test +13:00"),
        };

        foreach (var id in new[] { "America/New_York", "Europe/London", "Australia/Sydney" })
        {
            if (HeatmapTime.TryResolveTimeZone(id, out var tz) && !zones.Contains(tz))
            {
                zones.Add(tz);
            }
        }

        return zones.ToArray();
    }

    private static Gen<TimeZoneInfo> TimeZoneGen => Gen.Elements(TimeZones);

    private static Gen<LoadMetric> MetricGen => Gen.Elements(LoadMetric.FireCount, LoadMetric.WorkerMinutes);

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>Base "now" UTC instants spread across ~30 years at one-minute resolution.</summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 16_000_000)
            .Select(minutes => new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    /// <summary>
    /// A single fire descriptor: a queue label (including blank/default variants), a one-minute
    /// offset into the window, and an estimated-duration expressed in <em>seconds</em> so that
    /// sub-minute durations (which must be raised to a 1-minute floor — Req 2.3, 2.6) are exercised.
    /// </summary>
    private static Gen<(string Queue, int MinuteOffset, int DurationSeconds)> FireDescGen =>
        from queue in Gen.Elements(Queues)
        from offset in Gen.Choose(0, WindowMinutes - 1)
        from durationSeconds in Gen.Choose(0, 600)
        select (queue, offset, durationSeconds);

    /// <summary>
    /// **Property 6: Aggregation is exact, complete, and order-independent**
    /// **Validates: Requirements 2.1, 2.2, 2.3, 2.5, 2.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Aggregation_IsExact_Complete_AndOrderIndependent()
    {
        var arb = Arb.From(
            from baseNow in BaseNowGen
            from kind in KindGen
            from tz in TimeZoneGen
            from metric in MetricGen
            from count in Gen.Choose(0, 80)
            from descs in Gen.ArrayOf(count, FireDescGen)
            select (baseNow, kind, tz, metric, descs));

        return Prop.ForAll(arb, input =>
        {
            var (baseNow, kind, tz, metric, descs) = input;

            // Build the window in UTC so the descriptor offsets land inside [start, end); the viewer
            // time zone only affects bucket assignment, which is exactly what we want to test.
            var window = HeatmapTime.BuildWindow(kind, baseNow, TimeZoneInfo.Utc);

            var fires = descs
                .Select((d, i) => new ProjectedFire(
                    JobId: $"job-{i}",
                    Queue: d.Queue,
                    FireTimeUtc: window.StartInclusive.AddMinutes(d.MinuteOffset),
                    EstimatedDuration: TimeSpan.FromSeconds(d.DurationSeconds)))
                .ToList();

            var matrix = ScheduleAggregator.Aggregate(fires, metric, tz, window);

            // Independent reference model: each fire → exactly one (queue, dayIndex, hour) bucket.
            var expected = new Dictionary<CellKey, double>();
            foreach (var fire in fires)
            {
                var queue = string.IsNullOrWhiteSpace(fire.Queue) ? DefaultQueue : fire.Queue;
                var (dayIndex, hour) = HeatmapTime.GetBucket(fire.FireTimeUtc, tz, window);
                var key = new CellKey(queue, dayIndex, hour);

                double contribution;
                if (metric == LoadMetric.WorkerMinutes)
                {
                    var duration = fire.EstimatedDuration < TimeSpan.FromMinutes(1)
                        ? TimeSpan.FromMinutes(1)
                        : fire.EstimatedDuration;
                    contribution = duration.TotalMinutes;
                }
                else
                {
                    contribution = 1d;
                }

                expected.TryGetValue(key, out var running);
                expected[key] = running + contribution;
            }

            // Completeness: the set of populated cells equals the set of non-empty buckets exactly
            // (no extra cells, no missing cells — empty buckets are simply absent).
            if (matrix.Cells.Count != expected.Count)
            {
                return false.Label(
                    $"cell count mismatch: matrix={matrix.Cells.Count} expected={expected.Count} " +
                    $"(metric={metric}, fires={fires.Count}, tz={tz.Id})");
            }

            foreach (var kvp in expected)
            {
                if (!matrix.Cells.TryGetValue(kvp.Key, out var cell))
                {
                    return false.Label($"missing cell {kvp.Key} (metric={metric}, tz={tz.Id})");
                }

                if (Math.Abs(cell.Value - kvp.Value) > Tolerance)
                {
                    return false.Label(
                        $"cell {kvp.Key} value mismatch: actual={cell.Value} expected={kvp.Value} " +
                        $"(metric={metric}, tz={tz.Id})");
                }
            }

            // Exactness of Fire-count: the total over all cells equals the number of fires (each fire
            // is assigned to exactly one bucket — Req 2.1, 2.2).
            if (metric == LoadMetric.FireCount)
            {
                var total = matrix.Cells.Values.Sum(c => c.Value);
                if (Math.Abs(total - fires.Count) > Tolerance)
                {
                    return false.Label(
                        $"fire-count total != number of fires: total={total} fires={fires.Count} (tz={tz.Id})");
                }
            }

            // Order-independence: aggregating any permutation yields an identical matrix (Req 2.5).
            var reversed = fires.AsEnumerable().Reverse().ToList();
            var shuffled = ShuffleDeterministic(fires, baseNow.Ticks);

            return MatrixEquals(matrix, ScheduleAggregator.Aggregate(reversed, metric, tz, window))
                .Label("matrix differs for reversed input")
                .And(MatrixEquals(matrix, ScheduleAggregator.Aggregate(shuffled, metric, tz, window))
                    .Label("matrix differs for shuffled input"));
        });
    }

    /// <summary>
    /// Compares two matrices for structural equality: identical cell sets (keys and values), the
    /// same queue list (in order), and the same value domain (<c>Min</c>/<c>Max</c>). Used to assert
    /// permutation-invariance since <see cref="HeatmapMatrix"/>'s dictionary member has no value
    /// equality.
    /// </summary>
    private static Property MatrixEquals(HeatmapMatrix a, HeatmapMatrix b)
    {
        if (a.Cells.Count != b.Cells.Count)
        {
            return false.Label($"cell count differs: {a.Cells.Count} vs {b.Cells.Count}");
        }

        foreach (var kvp in a.Cells)
        {
            if (!b.Cells.TryGetValue(kvp.Key, out var other))
            {
                return false.Label($"cell {kvp.Key} present in one matrix only");
            }

            if (Math.Abs(kvp.Value.Value - other.Value) > Tolerance)
            {
                return false.Label(
                    $"cell {kvp.Key} value differs: {kvp.Value.Value} vs {other.Value}");
            }

            if (kvp.Value.ContributingJobCount != other.ContributingJobCount)
            {
                return false.Label(
                    $"cell {kvp.Key} contributing count differs: " +
                    $"{kvp.Value.ContributingJobCount} vs {other.ContributingJobCount}");
            }

            if (!string.Equals(kvp.Value.DominantQueue, other.DominantQueue, StringComparison.Ordinal))
            {
                return false.Label(
                    $"cell {kvp.Key} dominant queue differs: " +
                    $"'{kvp.Value.DominantQueue}' vs '{other.DominantQueue}'");
            }

            if (!kvp.Value.JobIds.SequenceEqual(other.JobIds, StringComparer.Ordinal))
            {
                return false.Label($"cell {kvp.Key} job-id list differs");
            }
        }

        if (!a.Queues.SequenceEqual(b.Queues, StringComparer.Ordinal))
        {
            return false.Label("queue lists differ");
        }

        if (Math.Abs(a.Min - b.Min) > Tolerance || Math.Abs(a.Max - b.Max) > Tolerance)
        {
            return false.Label($"domain differs: [{a.Min}, {a.Max}] vs [{b.Min}, {b.Max}]");
        }

        return true.ToProperty();
    }

    /// <summary>Deterministically permutes a list using a seeded Fisher–Yates shuffle.</summary>
    private static List<ProjectedFire> ShuffleDeterministic(IReadOnlyList<ProjectedFire> fires, long seed)
    {
        var list = fires.ToList();
        var rng = new System.Random(unchecked((int)seed));
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
