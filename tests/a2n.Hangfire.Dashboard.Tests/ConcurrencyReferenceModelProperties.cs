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
/// Property tests for <see cref="ConcurrencyAnalyzer.Analyze"/> against an independent brute-force
/// reference model.
///
/// **Property 14: Concurrency equals the reference model and is deterministic**
/// **Validates: Requirements 4.1, 4.3, 4.5, 4.6, 4.7, 19.1**
///
/// For any generated set of single-day fires, an optional ad-hoc baseline, and a worker capacity,
/// the analyzer's per-slot concurrency equals a straightforward reference model over 1,440 one-minute
/// slots: each fire occupies the half-open interval <c>[start, start + max(duration, 1 min))</c>
/// where <c>start</c> is the fire's minute-of-day (floored), a slot's concurrency is the number of
/// intervals covering the slot start (so back-to-back fires are not concurrent), the peak concurrency
/// resolves ties to the earliest slot, and a slot is over capacity when its total — including the
/// ad-hoc baseline — strictly exceeds the worker capacity. The result is order-independent
/// (permuting the input fires yields an identical result) and an empty day reports peak 0, no peak
/// minute, and zero over-capacity slots.
/// </summary>
public class ConcurrencyReferenceModelProperties
{
    /// <summary>The number of one-minute slots in a day (Req 4.3).</summary>
    private const int SlotsPerDay = ConcurrencyAnalyzer.SlotsPerDay;

    /// <summary>The default queue label applied to fires with no resolvable queue (Req 2.4).</summary>
    private const string DefaultQueue = "default";

    /// <summary>The minimum interval length attributed to any fire, in minutes (Req 4.1, 4.2).</summary>
    private const double MinimumDurationMinutes = 1d;

    /// <summary>Tolerance matching the analyzer so whole-minute interval ends resolve as exact (Req 4.4).</summary>
    private const double Epsilon = 1e-9;

    /// <summary>An arbitrary fixed day; only the time-of-day component drives minute-of-day.</summary>
    private static readonly DateTimeOffset Day = new(2023, 6, 15, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Candidate queue labels including blank/whitespace and an explicit "default" so the
    /// blank → <c>default</c> normalization is exercised and collides with the real default bucket.
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "charlie", "default", "", "   " };

    /// <summary>
    /// A single fire descriptor: a queue label, a minute-of-day in <c>[0, 1439]</c>, extra seconds in
    /// <c>[0, 59]</c> (so the analyzer's floor-to-minute is exercised), and a duration in seconds
    /// spanning sub-minute durations (raised to a one-minute floor — Req 4.1, 4.2) up to durations
    /// that clip past the end of the day (Req 4.3).
    /// </summary>
    private static Gen<(string Queue, int MinuteOfDay, int ExtraSeconds, int DurationSeconds)> FireDescGen =>
        from queue in Gen.Elements(Queues)
        from minute in Gen.Choose(0, SlotsPerDay - 1)
        from extraSeconds in Gen.Choose(0, 59)
        from durationSeconds in Gen.Choose(0, 100_000)
        select (queue, minute, extraSeconds, durationSeconds);

    /// <summary>A per-slot ad-hoc baseline array of length 1,440 with small non-negative values (Req 19.1).</summary>
    private static Gen<int[]> BaselineGen => Gen.ArrayOf(SlotsPerDay, Gen.Choose(0, 3));

    /// <summary>An optional ad-hoc baseline: most cases supply one, some pass <c>null</c>.</summary>
    private static Gen<int[]> OptionalBaselineGen =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant<int[]>(null)),
            Tuple.Create(2, BaselineGen));

    /// <summary>
    /// **Property 14: Concurrency equals the reference model and is deterministic**
    /// **Validates: Requirements 4.1, 4.3, 4.5, 4.6, 4.7, 19.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Concurrency_EqualsReferenceModel_AndIsDeterministic()
    {
        var arb = Arb.From(
            from count in Gen.Choose(0, 40)
            from descs in Gen.ArrayOf(count, FireDescGen)
            from capacity in Gen.Choose(0, 30)
            from baseline in OptionalBaselineGen
            from seed in Gen.Choose(0, 1_000_000)
            select (descs, capacity, baseline, seed));

        return Prop.ForAll(arb, input =>
        {
            var (descs, capacity, baseline, seed) = input;

            var fires = descs
                .Select((d, i) => new ProjectedFire(
                    JobId: $"job-{i}",
                    Queue: d.Queue,
                    FireTimeUtc: Day.AddMinutes(d.MinuteOfDay).AddSeconds(d.ExtraSeconds),
                    EstimatedDuration: TimeSpan.FromSeconds(d.DurationSeconds)))
                .ToList();

            var result = ConcurrencyAnalyzer.Analyze(fires, capacity, baseline);
            var reference = Reference(fires, capacity, baseline);

            // Peak concurrency, the earliest peak minute, and the over-capacity slot count all match
            // the brute-force reference (Req 4.1, 4.3, 4.5, 4.6, 19.1).
            if (result.PeakConcurrency != reference.Peak)
            {
                return false.Label(
                    $"peak mismatch: actual={result.PeakConcurrency} expected={reference.Peak} " +
                    $"(fires={fires.Count}, capacity={capacity})");
            }

            if (result.PeakMinuteOfDay != reference.PeakMinute)
            {
                return false.Label(
                    $"peak minute mismatch: actual={Show(result.PeakMinuteOfDay)} " +
                    $"expected={Show(reference.PeakMinute)} (fires={fires.Count})");
            }

            if (result.OverCapacitySlotCount != reference.OverCapacity)
            {
                return false.Label(
                    $"over-capacity mismatch: actual={result.OverCapacitySlotCount} " +
                    $"expected={reference.OverCapacity} (capacity={capacity})");
            }

            // The per-queue stacked series (and therefore their per-slot sum) match the reference.
            var seriesCheck = SeriesEquals(reference.PerQueue, result.PerQueueSeries)
                .Label("per-queue series differ from the reference");

            // Order-independence: permuting the input fires yields an identical result (Req 4.7).
            var reversed = ConcurrencyAnalyzer.Analyze(
                fires.AsEnumerable().Reverse().ToList(), capacity, baseline);
            var shuffled = ConcurrencyAnalyzer.Analyze(
                Shuffle(fires, seed), capacity, baseline);

            var orderCheck = ResultsEqual(result, reversed)
                .Label("result differs for reversed input")
                .And(ResultsEqual(result, shuffled).Label("result differs for shuffled input"));

            return seriesCheck.And(orderCheck);
        });
    }

    /// <summary>
    /// **Property 14: Concurrency equals the reference model and is deterministic**
    /// **Validates: Requirements 4.1, 4.3, 4.5, 4.6, 4.7, 19.1**
    ///
    /// An explicit empty-day check: a day with no fires reports peak 0, no peak minute, no
    /// over-capacity slots, and an empty per-queue series even when an ad-hoc baseline is present.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property EmptyDay_ReportsZeroPeakAndNoPeakMinute()
    {
        var arb = Arb.From(
            from capacity in Gen.Choose(0, 30)
            from baseline in OptionalBaselineGen
            select (capacity, baseline));

        return Prop.ForAll(arb, input =>
        {
            var (capacity, baseline) = input;

            var result = ConcurrencyAnalyzer.Analyze(Array.Empty<ProjectedFire>(), capacity, baseline);

            return (result.PeakConcurrency == 0).Label("empty day peak should be 0")
                .And((result.PeakMinuteOfDay == null).Label("empty day should have no peak minute"))
                .And((result.OverCapacitySlotCount == 0).Label("empty day should have 0 over-capacity slots"))
                .And((result.PerQueueSeries.Count == 0).Label("empty day should have no per-queue series"));
        });
    }

    /// <summary>
    /// The brute-force reference model: builds per-queue per-slot concurrency over 1,440 one-minute
    /// slots and derives the peak (earliest on ties), the over-capacity count (total incl. baseline
    /// strictly greater than capacity), and the per-queue series. Mirrors the empty-day short-circuit
    /// of <see cref="ConcurrencyAnalyzer.Analyze"/> so the baseline is ignored when there are no fires.
    /// </summary>
    private static (int Peak, int? PeakMinute, int OverCapacity, SortedDictionary<string, int[]> PerQueue)
        Reference(IReadOnlyList<ProjectedFire> fires, int capacity, IReadOnlyList<int> baseline)
    {
        var perQueue = new SortedDictionary<string, int[]>(StringComparer.Ordinal);

        if (fires.Count == 0)
        {
            return (0, null, 0, perQueue);
        }

        foreach (var fire in fires)
        {
            var queue = string.IsNullOrWhiteSpace(fire.Queue) ? DefaultQueue : fire.Queue;
            var start = (int)Math.Floor(fire.FireTimeUtc.TimeOfDay.TotalMinutes);
            if (start < 0 || start >= SlotsPerDay)
            {
                continue;
            }

            var durationMinutes = fire.EstimatedDuration.TotalMinutes;
            if (durationMinutes < MinimumDurationMinutes)
            {
                durationMinutes = MinimumDurationMinutes;
            }

            var endExclusive = start + durationMinutes;

            if (!perQueue.TryGetValue(queue, out var slots))
            {
                slots = new int[SlotsPerDay];
                perQueue[queue] = slots;
            }

            for (var s = start; s < SlotsPerDay; s++)
            {
                if (s + Epsilon >= endExclusive)
                {
                    break;
                }

                slots[s]++;
            }
        }

        var peak = 0;
        int? peakMinute = null;
        var overCapacity = 0;

        for (var s = 0; s < SlotsPerDay; s++)
        {
            var total = BaselineAt(baseline, s);
            foreach (var slots in perQueue.Values)
            {
                total += slots[s];
            }

            if (total > peak)
            {
                peak = total;
                peakMinute = s;
            }

            if (total > capacity)
            {
                overCapacity++;
            }
        }

        return (peak, peakMinute, overCapacity, perQueue);
    }

    private static int BaselineAt(IReadOnlyList<int> baseline, int slot)
    {
        if (baseline is null || slot < 0 || slot >= baseline.Count)
        {
            return 0;
        }

        var value = baseline[slot];
        return value > 0 ? value : 0;
    }

    /// <summary>
    /// Asserts the analyzer's per-queue series exactly match the reference: identical queue set (in
    /// the same deterministic ordinal order) and identical per-slot concurrency for every queue.
    /// </summary>
    private static Property SeriesEquals(
        SortedDictionary<string, int[]> expected,
        IReadOnlyList<QueueConcurrencySeries> actual)
    {
        if (actual.Count != expected.Count)
        {
            return false.Label($"series count differs: actual={actual.Count} expected={expected.Count}");
        }

        var expectedKeys = expected.Keys.ToList();
        for (var i = 0; i < actual.Count; i++)
        {
            if (!string.Equals(actual[i].Queue, expectedKeys[i], StringComparison.Ordinal))
            {
                return false.Label(
                    $"series #{i} queue differs: actual='{actual[i].Queue}' expected='{expectedKeys[i]}'");
            }

            var expectedSlots = expected[expectedKeys[i]];
            var actualSlots = actual[i].ConcurrencyPerSlot;

            if (actualSlots.Count != SlotsPerDay)
            {
                return false.Label(
                    $"series '{actual[i].Queue}' length={actualSlots.Count} expected {SlotsPerDay}");
            }

            for (var s = 0; s < SlotsPerDay; s++)
            {
                if (actualSlots[s] != expectedSlots[s])
                {
                    return false.Label(
                        $"series '{actual[i].Queue}' slot {s}: actual={actualSlots[s]} expected={expectedSlots[s]}");
                }
            }
        }

        return true.ToProperty();
    }

    /// <summary>Asserts two analyzer results are identical in every observable field.</summary>
    private static Property ResultsEqual(ConcurrencyResult a, ConcurrencyResult b)
    {
        if (a.PeakConcurrency != b.PeakConcurrency)
        {
            return false.Label($"peak differs: {a.PeakConcurrency} vs {b.PeakConcurrency}");
        }

        if (a.PeakMinuteOfDay != b.PeakMinuteOfDay)
        {
            return false.Label($"peak minute differs: {Show(a.PeakMinuteOfDay)} vs {Show(b.PeakMinuteOfDay)}");
        }

        if (a.OverCapacitySlotCount != b.OverCapacitySlotCount)
        {
            return false.Label($"over-capacity differs: {a.OverCapacitySlotCount} vs {b.OverCapacitySlotCount}");
        }

        if (a.PerQueueSeries.Count != b.PerQueueSeries.Count)
        {
            return false.Label($"series count differs: {a.PerQueueSeries.Count} vs {b.PerQueueSeries.Count}");
        }

        for (var i = 0; i < a.PerQueueSeries.Count; i++)
        {
            if (!string.Equals(a.PerQueueSeries[i].Queue, b.PerQueueSeries[i].Queue, StringComparison.Ordinal))
            {
                return false.Label($"series #{i} queue differs");
            }

            if (!a.PerQueueSeries[i].ConcurrencyPerSlot.SequenceEqual(b.PerQueueSeries[i].ConcurrencyPerSlot))
            {
                return false.Label($"series '{a.PerQueueSeries[i].Queue}' slots differ");
            }
        }

        return true.ToProperty();
    }

    /// <summary>Deterministically permutes a list using a seeded Fisher–Yates shuffle.</summary>
    private static List<ProjectedFire> Shuffle(IReadOnlyList<ProjectedFire> fires, int seed)
    {
        var list = fires.ToList();
        var rng = new System.Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private static string Show(int? value) => value?.ToString() ?? "null";
}
