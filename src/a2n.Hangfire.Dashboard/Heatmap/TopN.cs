using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure helper for the <c>Top-N</c> queue selector. This minimal type currently exposes only the
/// accepted input range and a strict validation routine for the operator-supplied <c>Top-N</c>
/// value; the actual Top-N <em>selection</em> logic (choosing the N highest-load queues with an
/// ascending-name tie-break at the Nth position) is added by task 4.1 and will reuse these bounds.
/// </summary>
/// <remarks>
/// Validates Requirement 13.7 (invalid Top-N inputs are rejected so the previously active value is
/// retained). The accepted range mirrors Requirement 13.3, which constrains the Top-N selector to
/// an integer in the inclusive range <c>[1, 100]</c>.
/// </remarks>
public static class TopN
{
    /// <summary>The smallest accepted Top-N value (inclusive).</summary>
    public const int MinValue = 1;

    /// <summary>The largest accepted Top-N value (inclusive).</summary>
    public const int MaxValue = 100;

    /// <summary>
    /// Validates a raw Top-N input. The input must parse strictly as an integer within the
    /// inclusive range <c>[1, 100]</c>. Empty/whitespace, non-integer, and out-of-range inputs are
    /// rejected; on rejection the caller should retain the previously active Top-N value
    /// (Requirement 13.7).
    /// </summary>
    /// <param name="raw">The raw operator input (e.g. from a text field).</param>
    /// <param name="value">The parsed value when the input is valid; otherwise 0.</param>
    /// <returns><c>true</c> when the input is a valid Top-N value; otherwise <c>false</c>.</returns>
    public static bool TryValidate(string raw, out int value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();

        if (!int.TryParse(
                trimmed,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        if (parsed < MinValue || parsed > MaxValue)
            return false;

        value = parsed;
        return true;
    }

    /// <summary>
    /// Selects the <c>Top-N</c> queues to display from an aggregated matrix: the
    /// <c>min(n, queueCount)</c> queues with the greatest total load across the whole matrix,
    /// ordered by descending total load and, where totals tie (including at the <c>n</c>th
    /// position), by ascending queue name (Requirement 13.3).
    /// </summary>
    /// <param name="matrix">The aggregated heatmap matrix to select queues from.</param>
    /// <param name="n">
    /// The requested number of queues. The result contains <c>min(n, queueCount)</c> queues; values
    /// of <c>n</c> at or above the queue count return every queue (in selection order). Callers
    /// should validate operator input with <see cref="TryValidate"/> first.
    /// </param>
    /// <returns>
    /// The selected queue names in selection order (descending total load, then ascending name).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="matrix"/> is <c>null</c>.</exception>
    public static IReadOnlyList<string> SelectTopQueues(HeatmapMatrix matrix, int n)
    {
        if (matrix is null)
            throw new ArgumentNullException(nameof(matrix));

        // Total load per queue across every populated cell. Seed from the matrix's declared queues
        // so queues with no populated cells still participate (with a total of zero).
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);

        if (matrix.Queues is not null)
        {
            foreach (var queue in matrix.Queues)
            {
                if (queue is not null)
                    totals[queue] = 0d;
            }
        }

        if (matrix.Cells is not null)
        {
            foreach (var cell in matrix.Cells.Values)
            {
                var queue = cell.Key.Queue;
                totals.TryGetValue(queue, out var existing);
                totals[queue] = existing + cell.Value;
            }
        }

        var take = Math.Max(0, Math.Min(n, totals.Count));

        return totals
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(pair => pair.Key)
            .ToList();
    }
}
