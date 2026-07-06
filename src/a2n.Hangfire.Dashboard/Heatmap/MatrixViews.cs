using System;
using System.Collections.Generic;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// The address of a single Queue×Hour cell, independent of any particular day. Used as the key for
/// both the per-day slice and the whole-week summation produced by <see cref="MatrixViews"/>.
/// </summary>
/// <param name="Queue">The queue the cell belongs to.</param>
/// <param name="Hour">The clock hour of the cell, in the range 0..23.</param>
public sealed record QueueHourKey(string Queue, int Hour);

/// <summary>
/// Pure, deterministic derivations over an aggregated <see cref="HeatmapMatrix"/> that back the
/// Queue×Hour view's two display modes:
/// <list type="bullet">
/// <item>a per-day slice that exposes each cell's load value for a single selected day
/// (Requirement 3.2); and</item>
/// <item>a whole-week summation that collapses all seven days into a single value per
/// <c>(queue, hour)</c> (Requirement 3.3).</item>
/// </list>
/// Both views key their results by <see cref="QueueHourKey"/> and include only the matrix's
/// populated cells (the matrix never materializes empty buckets), so an absent key denotes a zero
/// value.
/// </summary>
public static class MatrixViews
{
    /// <summary>
    /// Produces the Queue×Hour values for a single day of the projection window (Requirement 3.2).
    /// The result contains exactly the matrix's populated cells whose
    /// <see cref="CellKey.DayIndex"/> equals <paramref name="dayIndex"/>, keyed by their
    /// <c>(queue, hour)</c> and carrying each cell's load value.
    /// </summary>
    /// <param name="matrix">The aggregated matrix to slice.</param>
    /// <param name="dayIndex">The zero-based day index within the projection window to select.</param>
    /// <returns>A map of <see cref="QueueHourKey"/> to the cell's load value for the selected day.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="matrix"/> is <c>null</c>.</exception>
    public static IReadOnlyDictionary<QueueHourKey, double> SliceDay(HeatmapMatrix matrix, int dayIndex)
    {
        if (matrix is null)
            throw new ArgumentNullException(nameof(matrix));

        var result = new Dictionary<QueueHourKey, double>();

        if (matrix.Cells is null)
            return result;

        foreach (var cell in matrix.Cells.Values)
        {
            if (cell.Key.DayIndex != dayIndex)
                continue;

            result[new QueueHourKey(cell.Key.Queue, cell.Key.Hour)] = cell.Value;
        }

        return result;
    }

    /// <summary>
    /// Produces the whole-week Queue×Hour values by summing every populated cell's load value across
    /// all seven days for each <c>(queue, hour)</c> (Requirement 3.3).
    /// </summary>
    /// <param name="matrix">The aggregated matrix to collapse.</param>
    /// <returns>
    /// A map of <see cref="QueueHourKey"/> to the sum of that cell's load value over all day indices
    /// present in the matrix.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="matrix"/> is <c>null</c>.</exception>
    public static IReadOnlyDictionary<QueueHourKey, double> SumWeek(HeatmapMatrix matrix)
    {
        if (matrix is null)
            throw new ArgumentNullException(nameof(matrix));

        var result = new Dictionary<QueueHourKey, double>();

        if (matrix.Cells is null)
            return result;

        foreach (var cell in matrix.Cells.Values)
        {
            var key = new QueueHourKey(cell.Key.Queue, cell.Key.Hour);
            result.TryGetValue(key, out var existing);
            result[key] = existing + cell.Value;
        }

        return result;
    }

    /// <summary>
    /// Computes, for every <c>(day, hour)</c> position that has at least one populated cell, the
    /// <em>dominant queue</em> across all queues contributing load to that position
    /// (Requirements 3.6, 18.2). The dominant queue is the one contributing the greatest summed load
    /// at that <c>(day, hour)</c>; when several queues tie for the greatest load the alphabetically
    /// smallest queue name (Ordinal comparison) is chosen.
    /// </summary>
    /// <remarks>
    /// Because <see cref="ScheduleAggregator"/> keys each cell by <c>(queue, day, hour)</c>, a single
    /// matrix cell already carries one queue's total load for its <c>(day, hour)</c>. This helper
    /// therefore groups the matrix's cells by <c>(day, hour)</c> and, within each group, selects the
    /// queue whose cell value is greatest (ascending-name tie-break). Positions with no populated
    /// cell are absent from the result. The selection is deterministic and independent of the order
    /// in which cells are enumerated.
    /// </remarks>
    /// <param name="matrix">The aggregated matrix to derive dominant queues from.</param>
    /// <returns>
    /// A map of <c>(DayIndex, Hour)</c> to the dominant queue name for that position; empty when the
    /// matrix has no populated cells.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="matrix"/> is <c>null</c>.</exception>
    public static IReadOnlyDictionary<(int DayIndex, int Hour), string> DominantQueuePerCell(HeatmapMatrix matrix)
    {
        if (matrix is null)
            throw new ArgumentNullException(nameof(matrix));

        var result = new Dictionary<(int DayIndex, int Hour), string>();

        if (matrix.Cells is null)
            return result;

        // Track the best (greatest load, then alphabetically smallest queue) seen per (day, hour).
        var bestLoad = new Dictionary<(int DayIndex, int Hour), double>();

        foreach (var cell in matrix.Cells.Values)
        {
            var position = (cell.Key.DayIndex, cell.Key.Hour);
            var queue = cell.Key.Queue;
            var load = cell.Value;

            if (!bestLoad.TryGetValue(position, out var currentLoad))
            {
                bestLoad[position] = load;
                result[position] = queue;
                continue;
            }

            // Greater load wins; on an exact tie the alphabetically smaller queue name wins.
            if (load > currentLoad ||
                (load == currentLoad && string.CompareOrdinal(queue, result[position]) < 0))
            {
                bestLoad[position] = load;
                result[position] = queue;
            }
        }

        return result;
    }
}
