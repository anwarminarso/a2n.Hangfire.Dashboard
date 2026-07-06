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
/// Property tests for <see cref="MatrixViews.SliceDay"/> and <see cref="MatrixViews.SumWeek"/>, the
/// two Queue×Hour view derivations over an aggregated <see cref="HeatmapMatrix"/>.
///
/// **Property 9: Day slicing and weekly summation are consistent with the matrix**
/// **Validates: Requirements 3.2, 3.3**
///
/// For any matrix, the Queue×Hour values for a selected day equal the matrix cells with that day
/// index (keyed by <c>(queue, hour)</c> with their values — Req 3.2), and the whole-week values for
/// each <c>(queue, hour)</c> equal the sum of that cell across all seven days (Req 3.3).
/// </summary>
public class DaySliceWeekSumProperties
{
    /// <summary>The default queue label applied when a fire's queue cannot be determined (Req 2.4).</summary>
    private const string DefaultQueue = "default";

    /// <summary>The number of one-minute offsets inside a seven-day window.</summary>
    private const int WindowMinutes = 7 * 24 * 60;

    /// <summary>The number of day rows in a projection window's week.</summary>
    private const int DaysInWeek = 7;

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
    /// offset into the window, and an estimated-duration expressed in seconds (so sub-minute
    /// durations exercise the 1-minute Worker-minutes floor).
    /// </summary>
    private static Gen<(string Queue, int MinuteOffset, int DurationSeconds)> FireDescGen =>
        from queue in Gen.Elements(Queues)
        from offset in Gen.Choose(0, WindowMinutes - 1)
        from durationSeconds in Gen.Choose(0, 600)
        select (queue, offset, durationSeconds);

    /// <summary>
    /// **Property 9: Day slicing and weekly summation are consistent with the matrix**
    /// **Validates: Requirements 3.2, 3.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property DaySlice_And_WeekSum_AreConsistentWithMatrix()
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

            // Build the window in UTC so descriptor offsets land inside [start, end); the viewer time
            // zone only affects bucket assignment.
            var window = HeatmapTime.BuildWindow(kind, baseNow, TimeZoneInfo.Utc);

            var fires = descs
                .Select((d, i) => new ProjectedFire(
                    JobId: $"job-{i}",
                    Queue: d.Queue,
                    FireTimeUtc: window.StartInclusive.AddMinutes(d.MinuteOffset),
                    EstimatedDuration: TimeSpan.FromSeconds(d.DurationSeconds)))
                .ToList();

            var matrix = ScheduleAggregator.Aggregate(fires, metric, tz, window);

            // ---- Req 3.2: SliceDay(matrix, d) == matrix cells with DayIndex == d, keyed by (queue, hour). ----
            // Cover the canonical week (0..6) plus any day index actually present (viewer-time-zone
            // conversion can shift a fire onto an adjacent day index).
            var daysToCheck = Enumerable.Range(0, DaysInWeek)
                .Concat(matrix.Cells.Values.Select(c => c.Key.DayIndex))
                .Distinct();
            foreach (var day in daysToCheck)
            {
                var slice = MatrixViews.SliceDay(matrix, day);

                var expectedDay = matrix.Cells.Values
                    .Where(c => c.Key.DayIndex == day)
                    .ToDictionary(c => new QueueHourKey(c.Key.Queue, c.Key.Hour), c => c.Value);

                if (slice.Count != expectedDay.Count)
                {
                    return false.Label(
                        $"day {day} slice count mismatch: slice={slice.Count} expected={expectedDay.Count} " +
                        $"(metric={metric}, tz={tz.Id})");
                }

                foreach (var kvp in expectedDay)
                {
                    if (!slice.TryGetValue(kvp.Key, out var sliced))
                    {
                        return false.Label($"day {day} slice missing key {kvp.Key} (metric={metric}, tz={tz.Id})");
                    }

                    if (Math.Abs(sliced - kvp.Value) > Tolerance)
                    {
                        return false.Label(
                            $"day {day} slice value mismatch at {kvp.Key}: actual={sliced} expected={kvp.Value} " +
                            $"(metric={metric}, tz={tz.Id})");
                    }
                }
            }

            // ---- Req 3.3: SumWeek(matrix)[(queue, hour)] == sum of that cell over all days. ----
            var week = MatrixViews.SumWeek(matrix);

            var expectedWeek = new Dictionary<QueueHourKey, double>();
            foreach (var cell in matrix.Cells.Values)
            {
                var key = new QueueHourKey(cell.Key.Queue, cell.Key.Hour);
                expectedWeek.TryGetValue(key, out var running);
                expectedWeek[key] = running + cell.Value;
            }

            if (week.Count != expectedWeek.Count)
            {
                return false.Label(
                    $"week sum count mismatch: week={week.Count} expected={expectedWeek.Count} " +
                    $"(metric={metric}, tz={tz.Id})");
            }

            foreach (var kvp in expectedWeek)
            {
                if (!week.TryGetValue(kvp.Key, out var summed))
                {
                    return false.Label($"week sum missing key {kvp.Key} (metric={metric}, tz={tz.Id})");
                }

                if (Math.Abs(summed - kvp.Value) > Tolerance)
                {
                    return false.Label(
                        $"week sum value mismatch at {kvp.Key}: actual={summed} expected={kvp.Value} " +
                        $"(metric={metric}, tz={tz.Id})");
                }
            }

            // Cross-check: the whole-week value equals the sum of the per-day slices over every day
            // index present in the matrix, so the two derivations are mutually consistent
            // (Req 3.2 + 3.3). Day indices are taken from the matrix itself rather than assumed to be
            // 0..6, because viewer-time-zone conversion can shift a fire onto an adjacent day index.
            var dayIndices = matrix.Cells.Values.Select(c => c.Key.DayIndex).Distinct();
            var sliceTotals = new Dictionary<QueueHourKey, double>();
            foreach (var day in dayIndices)
            {
                foreach (var kvp in MatrixViews.SliceDay(matrix, day))
                {
                    sliceTotals.TryGetValue(kvp.Key, out var running);
                    sliceTotals[kvp.Key] = running + kvp.Value;
                }
            }

            if (sliceTotals.Count != week.Count)
            {
                return false.Label(
                    $"slice-sum vs week-sum key-set mismatch: slices={sliceTotals.Count} week={week.Count}");
            }

            foreach (var kvp in week)
            {
                sliceTotals.TryGetValue(kvp.Key, out var fromSlices);
                if (Math.Abs(fromSlices - kvp.Value) > Tolerance)
                {
                    return false.Label(
                        $"slice-sum vs week-sum value mismatch at {kvp.Key}: slices={fromSlices} week={kvp.Value}");
                }
            }

            return true.ToProperty();
        });
    }
}
