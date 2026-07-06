using System;
using System.Collections.Generic;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// The address of a single planner cell in <c>day-index × hour</c> space. Unlike
/// <see cref="CellKey"/> it carries no queue: the planner overlays the projected cron load on top of
/// the ad-hoc <c>Demand_Profile</c> across the whole active queue selection, so its unit of analysis
/// is a single <c>day × hour</c> slot of the projection window rather than a per-queue cell.
/// </summary>
/// <param name="DayIndex">The zero-based day index within the projection window (0..6).</param>
/// <param name="Hour">The clock hour of the slot, in the range 0..23.</param>
public sealed record PlannerCellKey(int DayIndex, int Hour);

/// <summary>
/// A single planner cell: the combined picture of ad-hoc demand and projected cron load for one
/// <c>day × hour</c> slot, together with its <see cref="Safe_Window"/> classification.
/// </summary>
/// <param name="Key">The <c>day-index × hour</c> address of the slot.</param>
/// <param name="AdHocDemand">The ad-hoc <c>Demand_Profile</c> load summed across the active queues for this slot.</param>
/// <param name="CronLoad">The projected cron <see cref="LoadMetric"/> summed across the active queues for this slot.</param>
/// <param name="CombinedLoad">The sum of <paramref name="AdHocDemand"/> and <paramref name="CronLoad"/>.</param>
/// <param name="IsSafeWindow">
/// <c>true</c> when the slot is a <c>Safe_Window</c> — its ad-hoc demand is at or below the low-load
/// threshold and its projected cron load is zero (Req 18.3).
/// </param>
public sealed record PlannerCell(
    PlannerCellKey Key,
    double AdHocDemand,
    double CronLoad,
    double CombinedLoad,
    bool IsSafeWindow);

/// <summary>
/// The result of a planner overlay: every <c>day × hour</c> slot of the projection window with its
/// ad-hoc/cron breakdown and <c>Safe_Window</c> flag, plus the recommended "best window to schedule"
/// (the slot with the lowest combined load).
/// </summary>
/// <param name="Cells">
/// Every planner cell of the window keyed by its <see cref="PlannerCellKey"/>. The grid is always
/// complete (7 days × 24 hours), so a slot with no cron fires and no ad-hoc demand is present with
/// zero values rather than absent.
/// </param>
/// <param name="LowLoadThreshold">The low-load threshold used to classify <c>Safe_Window</c> cells.</param>
/// <param name="BestWindow">
/// The <c>day × hour</c> slot with the lowest combined (ad-hoc + cron) load; ties are resolved by the
/// earliest <c>(DayIndex, Hour)</c> (Req 18.6).
/// </param>
public sealed record PlannerResult(
    IReadOnlyDictionary<PlannerCellKey, PlannerCell> Cells,
    double LowLoadThreshold,
    PlannerCellKey BestWindow);

/// <summary>
/// Pure, deterministic helpers backing the Combined / Planner view (Requirement 18). They overlay
/// the projected cron <see cref="HeatmapMatrix"/> on top of the ad-hoc <see cref="DemandProfile"/>
/// and derive, for every <c>day × hour</c> slot of the active projection window:
/// <list type="bullet">
/// <item>its <c>Safe_Window</c> classification — demand at or below a low-load threshold AND no
/// projected cron load (Requirement 18.3); and</item>
/// <item>the recommended "best window to schedule" — the slot minimizing combined (ad-hoc plus cron)
/// load across the active selection (Requirement 18.6).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Day-index ↔ day-of-week mapping.</b> The cron matrix keys cells by a window-relative
/// <see cref="CellKey.DayIndex"/> (0 = the window's first day), whereas the demand profile keys slots
/// by a calendar <see cref="DemandSlotKey.DayOfWeek"/> (0 = Sunday … 6 = Saturday). The two are
/// aligned by <see cref="MapDayIndexToDayOfWeek"/>, which derives each day index's day-of-week from
/// the window's local start date: for <see cref="ProjectionWindowKind.IdealizedWeek"/> day 0 is
/// Monday, and for <see cref="ProjectionWindowKind.Next7Days"/> day 0 is the current local date's
/// day-of-week.</para>
/// <para><b>Hour and queue alignment.</b> Both sources are summed across whatever queues are present
/// in the supplied matrix / profile, so callers should pre-filter the inputs to the active queue
/// selection. The hour axis is used directly; callers are responsible for supplying a demand profile
/// expressed on the same hour basis as the matrix.</para>
/// <para>All operations are order-independent and produce deterministic output. Validates
/// Requirements 18.3 and 18.6 (Property 27).</para>
/// </remarks>
public static class PlannerHelpers
{
    /// <summary>The number of clock hours in a planner day.</summary>
    public const int HoursPerDay = 24;

    /// <summary>
    /// Maps a window-relative day index to its calendar day-of-week (0 = Sunday … 6 = Saturday,
    /// matching <see cref="System.DayOfWeek"/> and <see cref="DemandSlotKey.DayOfWeek"/>), derived
    /// from the window's local start date.
    /// </summary>
    /// <param name="window">The active projection window.</param>
    /// <param name="viewerTimeZone">The viewer time zone the window is expressed in; UTC when null.</param>
    /// <param name="dayIndex">The zero-based day index within the window.</param>
    /// <returns>The day-of-week of the given day index, in the range 0..6.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is <c>null</c>.</exception>
    public static int MapDayIndexToDayOfWeek(
        ProjectionWindow window, TimeZoneInfo viewerTimeZone, int dayIndex)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var tz = viewerTimeZone ?? TimeZoneInfo.Utc;
        var startLocal = HeatmapTime.ToViewerLocal(window.StartInclusive, tz);
        var startDayOfWeek = (int)startLocal.DayOfWeek;

        // Normalize into [0, 6] for any (possibly negative) day index.
        var dow = ((startDayOfWeek + dayIndex) % 7 + 7) % 7;
        return dow;
    }

    /// <summary>
    /// Classifies a slot as a <c>Safe_Window</c> (Req 18.3): its ad-hoc demand must be at or below
    /// the low-load threshold AND its projected cron load must be exactly zero.
    /// </summary>
    /// <param name="adHocDemand">The slot's ad-hoc demand value.</param>
    /// <param name="cronLoad">The slot's projected cron load value.</param>
    /// <param name="lowLoadThreshold">The low-load threshold (inclusive upper bound on demand).</param>
    /// <returns><c>true</c> when the slot is a safe window; otherwise <c>false</c>.</returns>
    public static bool IsSafeWindow(double adHocDemand, double cronLoad, double lowLoadThreshold)
        => cronLoad == 0d && adHocDemand <= lowLoadThreshold;

    /// <summary>
    /// Builds the planner overlay for every <c>day × hour</c> slot of the window, summing the
    /// projected cron load (from <paramref name="cronMatrix"/>) and the ad-hoc demand (from
    /// <paramref name="demand"/>) per slot, classifying each as a <c>Safe_Window</c> (Req 18.3), and
    /// reporting the best window to schedule (Req 18.6).
    /// </summary>
    /// <param name="cronMatrix">The projected cron matrix; <c>null</c> is treated as no cron load.</param>
    /// <param name="demand">The ad-hoc demand profile; <c>null</c> is treated as no demand.</param>
    /// <param name="window">The active projection window (drives the day-index ↔ day-of-week mapping).</param>
    /// <param name="viewerTimeZone">The viewer time zone the window is expressed in; UTC when null.</param>
    /// <param name="lowLoadThreshold">
    /// The low-load threshold a slot's demand must be at or below to qualify as a safe window.
    /// </param>
    /// <returns>The complete planner result over the window's 7 × 24 slot grid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is <c>null</c>.</exception>
    public static PlannerResult BuildPlanner(
        HeatmapMatrix cronMatrix,
        DemandProfile demand,
        ProjectionWindow window,
        TimeZoneInfo viewerTimeZone,
        double lowLoadThreshold)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var tz = viewerTimeZone ?? TimeZoneInfo.Utc;

        // Cron load summed across queues per (dayIndex, hour).
        var cronByCell = new Dictionary<(int DayIndex, int Hour), double>();
        if (cronMatrix?.Cells is not null)
        {
            foreach (var cell in cronMatrix.Cells.Values)
            {
                if (cell is null)
                {
                    continue;
                }

                var position = (cell.Key.DayIndex, cell.Key.Hour);
                cronByCell.TryGetValue(position, out var existing);
                cronByCell[position] = existing + cell.Value;
            }
        }

        // Ad-hoc demand summed across queues per (dayOfWeek, hour).
        var demandByDow = new Dictionary<(int DayOfWeek, int Hour), double>();
        if (demand?.Slots is not null)
        {
            foreach (var slot in demand.Slots)
            {
                var position = (slot.Key.DayOfWeek, slot.Key.Hour);
                demandByDow.TryGetValue(position, out var existing);
                demandByDow[position] = existing + slot.Value;
            }
        }

        var cells = new Dictionary<PlannerCellKey, PlannerCell>(HeatmapTime.WindowDays * HoursPerDay);
        PlannerCellKey bestWindow = null;
        var bestCombined = double.PositiveInfinity;

        for (var dayIndex = 0; dayIndex < HeatmapTime.WindowDays; dayIndex++)
        {
            var dayOfWeek = MapDayIndexToDayOfWeek(window, tz, dayIndex);

            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                cronByCell.TryGetValue((dayIndex, hour), out var cronLoad);
                demandByDow.TryGetValue((dayOfWeek, hour), out var adHocDemand);

                var combined = adHocDemand + cronLoad;
                var key = new PlannerCellKey(dayIndex, hour);

                cells[key] = new PlannerCell(
                    key,
                    adHocDemand,
                    cronLoad,
                    combined,
                    IsSafeWindow(adHocDemand, cronLoad, lowLoadThreshold));

                // Lowest combined load wins; the iteration order (ascending day then hour) makes the
                // earliest slot the deterministic tie-break.
                if (combined < bestCombined)
                {
                    bestCombined = combined;
                    bestWindow = key;
                }
            }
        }

        return new PlannerResult(cells, lowLoadThreshold, bestWindow);
    }

    /// <summary>
    /// Finds the recommended best window to schedule (Req 18.6): the planner cell with the lowest
    /// combined (ad-hoc + cron) load, resolving ties to the earliest <c>(DayIndex, Hour)</c>.
    /// </summary>
    /// <param name="cells">The planner cells to scan.</param>
    /// <returns>
    /// The <see cref="PlannerCellKey"/> of the lowest-combined-load slot, or <c>null</c> when
    /// <paramref name="cells"/> is null or empty.
    /// </returns>
    public static PlannerCellKey FindBestWindow(IEnumerable<PlannerCell> cells)
    {
        if (cells is null)
        {
            return null;
        }

        PlannerCellKey best = null;
        var bestCombined = double.PositiveInfinity;

        foreach (var cell in cells)
        {
            if (cell is null)
            {
                continue;
            }

            var isLower = cell.CombinedLoad < bestCombined;

            // Deterministic ascending (DayIndex, Hour) tie-break on equal combined load.
            var isEarlierTie = cell.CombinedLoad == bestCombined
                && best is not null
                && (cell.Key.DayIndex < best.DayIndex
                    || (cell.Key.DayIndex == best.DayIndex && cell.Key.Hour < best.Hour));

            if (isLower || isEarlierTie)
            {
                bestCombined = cell.CombinedLoad;
                best = cell.Key;
            }
        }

        return best;
    }
}
