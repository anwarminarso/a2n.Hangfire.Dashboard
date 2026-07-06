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
/// Property test for <see cref="PlannerHelpers.BuildPlanner"/>, <see cref="PlannerHelpers.IsSafeWindow"/>,
/// and <see cref="PlannerHelpers.FindBestWindow"/> — the pure helpers backing the Combined / Planner view.
///
/// <para>// Feature: recurring-schedule-heatmap, Property 27: Safe-window and best-window planner classifications</para>
/// <para>// Validates: Requirements 18.3, 18.6</para>
///
/// For any cron matrix + ad-hoc demand profile + low-load threshold, a planner cell is classified a
/// Safe_Window if and only if its ad-hoc demand (summed across queues) is at or below the low-load
/// threshold AND its projected cron load (summed across queues) is exactly zero (Req 18.3); and the
/// reported best window is the slot minimizing combined (ad-hoc + cron) load, with the documented
/// earliest-(DayIndex, Hour) tie-break (Req 18.6).
/// </summary>
public class PlannerClassificationProperties
{
    /// <summary>
    /// A small queue alphabet — including a blank and an explicit "default" — so multiple queues
    /// frequently land in the same <c>(day, hour)</c> / <c>(day-of-week, hour)</c> position and their
    /// loads are summed by the planner regardless of queue name.
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "default", "" };

    /// <summary>Floating-point tolerance for load comparisons (values are integer-derived, so exact).</summary>
    private const double Tolerance = 1e-9;

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
    /// A cron contribution: a queue, a window-relative day index (0..6), an hour (0..23), and a small
    /// non-negative load value. Zero values are included so cron-free (potentially safe) slots arise.
    /// </summary>
    private static Gen<(string Queue, int DayIndex, int Hour, double Value)> CronDescGen =>
        from queue in Gen.Elements(Queues)
        from day in Gen.Choose(0, HeatmapTime.WindowDays - 1)
        from hour in Gen.Choose(0, 23)
        from value in Gen.Choose(0, 4)
        select (queue, day, hour, (double)value);

    /// <summary>
    /// An ad-hoc demand contribution: a queue, a calendar day-of-week (0 = Sunday … 6 = Saturday), an
    /// hour (0..23), and a small non-negative demand value chosen to straddle the threshold.
    /// </summary>
    private static Gen<(string Queue, int DayOfWeek, int Hour, double Value)> DemandDescGen =>
        from queue in Gen.Elements(Queues)
        from dow in Gen.Choose(0, 6)
        from hour in Gen.Choose(0, 23)
        from value in Gen.Choose(0, 5)
        select (queue, dow, hour, (double)value);

    /// <summary>
    /// **Property 27: Safe-window and best-window planner classifications**
    /// **Validates: Requirements 18.3, 18.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Planner_SafeWindow_And_BestWindow_AreCorrect()
    {
        var arb = Arb.From(
            from baseNow in BaseNowGen
            from kind in KindGen
            from tz in TimeZoneGen
            from metric in MetricGen
            from cronCount in Gen.Choose(0, 60)
            from cronDescs in Gen.ArrayOf(cronCount, CronDescGen)
            from demandCount in Gen.Choose(0, 60)
            from demandDescs in Gen.ArrayOf(demandCount, DemandDescGen)
            // Threshold straddles the demand value range (0..5) so the inclusive `<=` boundary is hit.
            from threshold in Gen.Choose(0, 6)
            select (baseNow, kind, tz, metric, cronDescs, demandDescs, (double)threshold));

        return Prop.ForAll(arb, input =>
        {
            var (baseNow, kind, tz, metric, cronDescs, demandDescs, threshold) = input;

            var window = HeatmapTime.BuildWindow(kind, baseNow, tz);

            // ---- Build the cron matrix (dedupe by CellKey, summing collisions into the cell value). ----
            var cellValues = new Dictionary<CellKey, double>();
            foreach (var d in cronDescs)
            {
                var key = new CellKey(d.Queue, d.DayIndex, d.Hour);
                cellValues.TryGetValue(key, out var running);
                cellValues[key] = running + d.Value;
            }

            var cells = cellValues.ToDictionary(
                kvp => kvp.Key,
                kvp => new HeatmapCell(kvp.Key, kvp.Value, 1, kvp.Key.Queue, new[] { "job" }));

            var min = cells.Count == 0 ? 0d : cells.Values.Min(c => c.Value);
            var max = cells.Count == 0 ? 0d : cells.Values.Max(c => c.Value);
            var matrix = new HeatmapMatrix(
                cells,
                cells.Keys.Select(k => k.Queue).Distinct().OrderBy(q => q, StringComparer.Ordinal).ToList(),
                window,
                metric,
                min,
                max);

            // ---- Build the demand profile (dedupe by DemandSlotKey, summing collisions). ----
            var slotValues = new Dictionary<DemandSlotKey, double>();
            foreach (var d in demandDescs)
            {
                var key = new DemandSlotKey(d.Queue, d.DayOfWeek, d.Hour);
                slotValues.TryGetValue(key, out var running);
                slotValues[key] = running + d.Value;
            }

            var demandMin = slotValues.Count == 0 ? 0d : slotValues.Values.Min();
            var demandMax = slotValues.Count == 0 ? 0d : slotValues.Values.Max();
            var demand = new DemandProfile(
                slotValues,
                slotValues.Keys.Select(k => k.Queue).Distinct().OrderBy(q => q, StringComparer.Ordinal).ToList(),
                metric,
                AggregationStatistic.Average,
                RequestedLookbackWeeks: 4,
                AvailableSpanWeeks: 4,
                IsSpanReduced: false,
                Min: demandMin,
                Max: demandMax);

            // ---- Independent oracle ----
            // Cron load summed across queues per (dayIndex, hour).
            var cronByCell = new Dictionary<(int DayIndex, int Hour), double>();
            foreach (var cell in cells.Values)
            {
                var pos = (cell.Key.DayIndex, cell.Key.Hour);
                cronByCell.TryGetValue(pos, out var running);
                cronByCell[pos] = running + cell.Value;
            }

            // Ad-hoc demand summed across queues per (dayOfWeek, hour).
            var demandByDow = new Dictionary<(int DayOfWeek, int Hour), double>();
            foreach (var slot in slotValues)
            {
                var pos = (slot.Key.DayOfWeek, slot.Key.Hour);
                demandByDow.TryGetValue(pos, out var running);
                demandByDow[pos] = running + slot.Value;
            }

            // Day-index → day-of-week alignment, computed from the window's local start date using the
            // documented rule (the same ToViewerLocal contract the helper relies on).
            var startLocal = HeatmapTime.ToViewerLocal(window.StartInclusive, tz);
            var startDow = (int)startLocal.DayOfWeek;

            var actual = PlannerHelpers.BuildPlanner(matrix, demand, window, tz, threshold);

            // The grid is always complete: 7 days × 24 hours.
            var expectedCellCount = HeatmapTime.WindowDays * PlannerHelpers.HoursPerDay;
            if (actual.Cells.Count != expectedCellCount)
            {
                return false.Label($"cell count mismatch: actual={actual.Cells.Count} expected={expectedCellCount}");
            }

            if (Math.Abs(actual.LowLoadThreshold - threshold) > Tolerance)
            {
                return false.Label($"threshold not echoed: actual={actual.LowLoadThreshold} expected={threshold}");
            }

            PlannerCellKey expectedBest = null;
            var bestCombined = double.PositiveInfinity;

            for (var dayIndex = 0; dayIndex < HeatmapTime.WindowDays; dayIndex++)
            {
                var dayOfWeek = ((startDow + dayIndex) % 7 + 7) % 7;

                for (var hour = 0; hour < PlannerHelpers.HoursPerDay; hour++)
                {
                    cronByCell.TryGetValue((dayIndex, hour), out var expectedCron);
                    demandByDow.TryGetValue((dayOfWeek, hour), out var expectedDemand);
                    var expectedCombined = expectedCron + expectedDemand;
                    var expectedSafe = expectedCron == 0d && expectedDemand <= threshold;

                    var key = new PlannerCellKey(dayIndex, hour);
                    if (!actual.Cells.TryGetValue(key, out var cell))
                    {
                        return false.Label($"missing planner cell (day={dayIndex}, hour={hour})");
                    }

                    if (Math.Abs(cell.CronLoad - expectedCron) > Tolerance)
                    {
                        return false.Label(
                            $"cron load mismatch at (day={dayIndex}, hour={hour}): actual={cell.CronLoad} expected={expectedCron}");
                    }

                    if (Math.Abs(cell.AdHocDemand - expectedDemand) > Tolerance)
                    {
                        return false.Label(
                            $"demand mismatch at (day={dayIndex}, hour={hour}): actual={cell.AdHocDemand} expected={expectedDemand}");
                    }

                    if (Math.Abs(cell.CombinedLoad - expectedCombined) > Tolerance)
                    {
                        return false.Label(
                            $"combined mismatch at (day={dayIndex}, hour={hour}): actual={cell.CombinedLoad} expected={expectedCombined}");
                    }

                    // Req 18.3: Safe_Window iff demand ≤ threshold AND cron load is zero.
                    if (cell.IsSafeWindow != expectedSafe)
                    {
                        return false.Label(
                            $"safe-window mismatch at (day={dayIndex}, hour={hour}): actual={cell.IsSafeWindow} " +
                            $"expected={expectedSafe} (cron={expectedCron}, demand={expectedDemand}, threshold={threshold})");
                    }

                    // Cross-check the standalone classifier against the same formula.
                    if (PlannerHelpers.IsSafeWindow(expectedDemand, expectedCron, threshold) != expectedSafe)
                    {
                        return false.Label(
                            $"IsSafeWindow standalone mismatch (cron={expectedCron}, demand={expectedDemand}, threshold={threshold})");
                    }

                    // Req 18.6: lowest combined load, earliest (DayIndex, Hour) tie-break (strict <).
                    if (expectedCombined < bestCombined - Tolerance)
                    {
                        bestCombined = expectedCombined;
                        expectedBest = key;
                    }
                }
            }

            // Req 18.6: BuildPlanner reports the best window.
            if (!Equals(actual.BestWindow, expectedBest))
            {
                return false.Label($"BuildPlanner best window mismatch: actual={actual.BestWindow} expected={expectedBest}");
            }

            // FindBestWindow over the (unordered) cell collection must agree, exercising its explicit
            // tie-break logic independent of enumeration order.
            var foundBest = PlannerHelpers.FindBestWindow(actual.Cells.Values);
            if (!Equals(foundBest, expectedBest))
            {
                return false.Label($"FindBestWindow mismatch: actual={foundBest} expected={expectedBest}");
            }

            return true.ToProperty();
        });
    }
}
