using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure, deterministic duration-aware concurrency analysis over a single day. The day is partitioned
/// into 1,440 one-minute slots; each fire is treated as occupying the half-open interval
/// <c>[start, start + max(duration, 1 min))</c>, where <c>start</c> is the fire's minute-of-day. A
/// slot's concurrency is the number of fire intervals that cover that slot's start instant.
/// </summary>
/// <remarks>
/// <para>
/// Because the intervals are half-open, two back-to-back fires (one ending exactly when the next
/// begins) are never counted as concurrent (Req 4.4). The peak concurrency is the maximum slot
/// concurrency across the day and ties for the peak resolve to the earliest slot (Req 4.5). When the
/// analyzed day contains no fires the analyzer reports a peak of 0, no peak time, and zero
/// over-capacity slots (Req 4.10).
/// </para>
/// <para>
/// An optional ad-hoc baseline (Req 19.1) supplies a per-slot concurrency floor that is added to the
/// cron concurrency before the peak and over-capacity comparisons. The baseline is not attributed to
/// any queue and therefore does not appear in the per-queue stacked series. Output is identical for
/// any permutation of the same input fires (Req 4.7).
/// </para>
/// <para>Validates Requirements 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.10, and 19.1.</para>
/// </remarks>
public static class ConcurrencyAnalyzer
{
    /// <summary>The number of one-minute slots in a day (Req 4.3).</summary>
    public const int SlotsPerDay = 1440;

    /// <summary>The queue label applied to fires whose queue cannot be determined (Req 2.4).</summary>
    public const string DefaultQueue = "default";

    /// <summary>The minimum interval length attributed to any fire, in minutes (Req 4.1, 4.2).</summary>
    private const double MinimumDurationMinutes = 1d;

    /// <summary>Tolerance (in minutes) so that whole-minute interval ends resolve as exact, keeping back-to-back fires non-concurrent (Req 4.4).</summary>
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Analyzes the supplied day's fires and returns the duration-aware concurrency result.
    /// </summary>
    /// <param name="dayFires">
    /// The fires for the analyzed day; their minute-of-day is derived from each fire's time-of-day.
    /// Order does not affect the result (Req 4.7). A null or empty list yields the empty-day result
    /// (Req 4.10).
    /// </param>
    /// <param name="workerCapacity">
    /// The active worker capacity; a slot is over capacity when its total concurrency (including the
    /// ad-hoc baseline) is strictly greater than this value (Req 4.6).
    /// </param>
    /// <param name="adHocBaselinePerSlot">
    /// An optional per-slot ad-hoc concurrency baseline added before the capacity comparison
    /// (Req 19.1). Missing or short entries are treated as zero; entries beyond
    /// <see cref="SlotsPerDay"/> are ignored.
    /// </param>
    /// <returns>
    /// The peak concurrency, the earliest minute at which it occurs (or <c>null</c> for an empty
    /// day), the over-capacity slot count, and the per-queue stacked concurrency series.
    /// </returns>
    public static ConcurrencyResult Analyze(
        IReadOnlyList<ProjectedFire> dayFires,
        int workerCapacity,
        IReadOnlyList<int> adHocBaselinePerSlot = null)
    {
        // Empty day: peak 0, no peak time, no over-capacity slots (Req 4.10).
        if (dayFires is null || dayFires.Count == 0)
        {
            return new ConcurrencyResult(0, null, 0, Array.Empty<QueueConcurrencySeries>());
        }

        // Accumulate per-queue per-slot concurrency. Sorted so the output order is deterministic.
        var perQueue = new SortedDictionary<string, int[]>(StringComparer.Ordinal);

        foreach (var fire in dayFires)
        {
            if (fire is null)
            {
                continue;
            }

            var queue = NormalizeQueue(fire.Queue);
            var startMinute = MinuteOfDay(fire.FireTimeUtc);

            // Defensive clip: minute-of-day is always within [0, 1440), but guard regardless.
            if (startMinute < 0 || startMinute >= SlotsPerDay)
            {
                continue;
            }

            var durationMinutes = fire.EstimatedDuration.TotalMinutes;
            if (durationMinutes < MinimumDurationMinutes)
            {
                durationMinutes = MinimumDurationMinutes;
            }

            var endExclusive = startMinute + durationMinutes;

            if (!perQueue.TryGetValue(queue, out var slots))
            {
                slots = new int[SlotsPerDay];
                perQueue[queue] = slots;
            }

            // A slot's start instant at integer minute s is covered when start <= s < end. The
            // half-open upper bound (with epsilon tolerance) keeps back-to-back fires from being
            // counted as concurrent (Req 4.4) and clips the interval to the day (Req 4.1, 4.3).
            for (var s = startMinute; s < SlotsPerDay; s++)
            {
                if (s + Epsilon >= endExclusive)
                {
                    break;
                }

                slots[s]++;
            }
        }

        // Total per slot = sum across queues + ad-hoc baseline (Req 19.1). Peak and over-capacity are
        // computed against this total; the baseline is not part of any per-queue series.
        var peak = 0;
        int? peakMinute = null;
        var overCapacity = 0;

        for (var s = 0; s < SlotsPerDay; s++)
        {
            var total = BaselineAt(adHocBaselinePerSlot, s);
            foreach (var slots in perQueue.Values)
            {
                total += slots[s];
            }

            // Strictly-greater update keeps the earliest slot that reaches the peak (Req 4.5).
            if (total > peak)
            {
                peak = total;
                peakMinute = s;
            }

            if (total > workerCapacity)
            {
                overCapacity++;
            }
        }

        var perQueueSeries = perQueue
            .Select(pair => new QueueConcurrencySeries(pair.Key, pair.Value))
            .ToList();

        return new ConcurrencyResult(peak, peakMinute, overCapacity, perQueueSeries);
    }

    private static string NormalizeQueue(string queue)
        => string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

    private static int MinuteOfDay(DateTimeOffset fireTime)
        => (int)Math.Floor(fireTime.TimeOfDay.TotalMinutes);

    private static int BaselineAt(IReadOnlyList<int> baseline, int slot)
    {
        if (baseline is null || slot < 0 || slot >= baseline.Count)
        {
            return 0;
        }

        var value = baseline[slot];
        return value > 0 ? value : 0;
    }
}
