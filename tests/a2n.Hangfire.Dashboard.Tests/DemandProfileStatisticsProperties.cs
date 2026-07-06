using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Sample = a2n.Hangfire.Dashboard.Services.DemandProfileProvider.DemandRollupSample;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="DemandProfileProvider.ComputeProfile"/>, the pure aggregation that
/// summarizes per-week <c>queue × day-of-week × hour</c> ad-hoc rollup samples into a
/// <see cref="DemandProfile"/> over a lookback window.
///
/// **Property 25: Demand-profile slot statistics are correct over the lookback**
/// **Validates: Requirements 16.3, 16.4**
///
/// For any set of ad-hoc executions aggregated into per-week <c>queue × day-of-week × hour</c>
/// samples and any lookback window, each Demand Profile slot under the Average statistic equals the
/// arithmetic mean of that day-of-week-and-hour's per-week occurrences within the available lookback
/// span (a week with no sample for the slot contributing a zero occurrence), and under the p95
/// statistic equals their continuous (linear-interpolation) 95th percentile. The reported
/// <see cref="DemandProfile.AvailableSpanWeeks"/> and <see cref="DemandProfile.IsSpanReduced"/> flag
/// reflect the actual retained weeks within the lookback (Req 16.3, 16.4; design Property 25).
/// </summary>
public class DemandProfileStatisticsProperties
{
    /// <summary>Floating-point tolerance for statistic comparisons.</summary>
    private const double Tolerance = 1e-6;

    /// <summary>The default queue label applied when a sample's queue is null/empty (mirrors the provider).</summary>
    private const string DefaultQueue = "default";

    /// <summary>
    /// Candidate queue labels: real names plus an empty string so the null/empty → <c>default</c>
    /// normalization is exercised and collides with the real default slot.
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "" };

    private static Gen<AggregationStatistic> StatisticGen =>
        Gen.Elements(AggregationStatistic.Average, AggregationStatistic.P95);

    private static Gen<LoadMetric> MetricGen =>
        Gen.Elements(LoadMetric.FireCount, LoadMetric.WorkerMinutes);

    /// <summary>The selectable lookback spans (Req 16.5).</summary>
    private static Gen<int> LookbackGen => Gen.Elements(1, 4, 8);

    /// <summary>A current-week index well away from zero so week arithmetic stays positive.</summary>
    private static Gen<long> CurrentWeekGen => Gen.Choose(1_000, 3_000).Select(w => (long)w);

    /// <summary>
    /// A single rollup sample descriptor. Week offsets span a little past the longest lookback (and a
    /// couple of "future" weeks via negative offsets) so both the lower- and upper-bound lookback
    /// filtering is exercised. The slot space (queue × dow × hour) is kept small so multiple samples
    /// collide on the same (slot, week) — exercising the provider's per-(slot, week) summation — and
    /// so the same slot is present in some lookback weeks but absent in others (zero occurrence).
    /// Counts and durations include zero so all-zero slots are exercised too.
    /// </summary>
    private static Gen<(int WeekOffset, string Queue, int Dow, int Hour, long Count, double SumMs)> SampleDescGen =>
        from weekOffset in Gen.Choose(-2, 11)
        from queue in Gen.Elements(Queues)
        from dow in Gen.Choose(0, 2)
        from hour in Gen.Choose(0, 3)
        from count in Gen.Choose(0, 50)
        from sumMs in Gen.Choose(0, 600_000)
        select (weekOffset, queue, dow, hour, (long)count, (double)sumMs);

    /// <summary>
    /// **Property 25: Demand-profile slot statistics are correct over the lookback**
    /// **Validates: Requirements 16.3, 16.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property DemandProfileSlotStatistics_AreCorrect_OverTheLookback()
    {
        var arb = Arb.From(
            from currentWeek in CurrentWeekGen
            from lookback in LookbackGen
            from statistic in StatisticGen
            from metric in MetricGen
            // The set of weeks the rollup actually retained (as offsets back from the current week).
            from availOffsetCount in Gen.Choose(0, 10)
            from availOffsets in Gen.ArrayOf(availOffsetCount, Gen.Choose(-2, 11))
            from sampleCount in Gen.Choose(0, 60)
            from sampleDescs in Gen.ArrayOf(sampleCount, SampleDescGen)
            select (currentWeek, lookback, statistic, metric, availOffsets, sampleDescs));

        return Prop.ForAll(arb, input =>
        {
            var (currentWeek, lookback, statistic, metric, availOffsets, sampleDescs) = input;

            var availableWeeks = availOffsets.Select(o => currentWeek - o).Distinct().ToList();

            var samples = sampleDescs
                .Select(d => new Sample(
                    Week: currentWeek - d.WeekOffset,
                    Queue: d.Queue,
                    DayOfWeek: d.Dow,
                    Hour: d.Hour,
                    Count: d.Count,
                    SumDurationMs: d.SumMs))
                .ToList();

            var profile = DemandProfileProvider.ComputeProfile(
                samples, availableWeeks, currentWeek, lookback, statistic, metric);

            // ---- Independent reference model of the lookback semantics. ----
            var requested = lookback < 1 ? 1 : lookback;
            var minKeepWeek = currentWeek - requested + 1;
            var effectiveWeeks = availableWeeks
                .Where(w => w >= minKeepWeek && w <= currentWeek)
                .Distinct()
                .OrderBy(w => w)
                .ToList();
            var effectiveWeekSet = new HashSet<long>(effectiveWeeks);
            var availableSpan = effectiveWeeks.Count;

            // Span reporting (Req 16.8 / 17.4): the available span is the retained-week count and the
            // span is flagged reduced whenever fewer than requested weeks are available.
            if (profile.AvailableSpanWeeks != availableSpan)
            {
                return false.Label(
                    $"AvailableSpanWeeks mismatch: actual={profile.AvailableSpanWeeks} expected={availableSpan}");
            }

            if (profile.IsSpanReduced != (availableSpan < requested))
            {
                return false.Label(
                    $"IsSpanReduced mismatch: actual={profile.IsSpanReduced} expected={availableSpan < requested} " +
                    $"(span={availableSpan}, requested={requested})");
            }

            if (profile.RequestedLookbackWeeks != requested)
            {
                return false.Label(
                    $"RequestedLookbackWeeks mismatch: actual={profile.RequestedLookbackWeeks} expected={requested}");
            }

            // Build, per slot, the per-effective-week occurrence value (absent weeks contribute 0).
            // perSlot[slot][week] = summed metric value for that (slot, week).
            var perSlot = new Dictionary<DemandSlotKey, Dictionary<long, double>>();
            foreach (var s in samples)
            {
                if (!effectiveWeekSet.Contains(s.Week))
                {
                    continue;
                }

                var queue = string.IsNullOrEmpty(s.Queue) ? DefaultQueue : s.Queue;
                var key = new DemandSlotKey(queue, s.DayOfWeek, s.Hour);
                var value = metric == LoadMetric.WorkerMinutes ? s.SumDurationMs / 60000d : s.Count;

                if (!perSlot.TryGetValue(key, out var byWeek))
                {
                    byWeek = new Dictionary<long, double>();
                    perSlot[key] = byWeek;
                }

                byWeek[s.Week] = byWeek.TryGetValue(s.Week, out var existing) ? existing + value : value;
            }

            // Expected per-slot statistic over the full effective span (zero for absent weeks).
            var expected = new Dictionary<DemandSlotKey, double>();
            foreach (var entry in perSlot)
            {
                var values = effectiveWeeks
                    .Select(w => entry.Value.TryGetValue(w, out var v) ? v : 0d)
                    .ToArray();

                expected[entry.Key] = statistic == AggregationStatistic.P95
                    ? ContinuousPercentile(values, 0.95)
                    : values.Average();
            }

            // When the span is empty there can be no slots regardless of the samples supplied.
            if (availableSpan == 0 && profile.Slots.Count != 0)
            {
                return false.Label($"expected no slots for empty span, got {profile.Slots.Count}");
            }

            // The slot key-set must match exactly.
            if (profile.Slots.Count != expected.Count)
            {
                return false.Label(
                    $"slot count mismatch: actual={profile.Slots.Count} expected={expected.Count} " +
                    $"(stat={statistic}, metric={metric}, span={availableSpan})");
            }

            foreach (var kvp in expected)
            {
                if (!profile.Slots.TryGetValue(kvp.Key, out var actual))
                {
                    return false.Label($"missing slot {kvp.Key} (stat={statistic}, metric={metric})");
                }

                if (Math.Abs(actual - kvp.Value) > Tolerance)
                {
                    return false.Label(
                        $"slot {kvp.Key} value mismatch: actual={actual} expected={kvp.Value} " +
                        $"(stat={statistic}, metric={metric}, span={availableSpan})");
                }
            }

            // Queues: the distinct slot queues in ascending ordinal order.
            var expectedQueues = expected.Keys.Select(k => k.Queue).Distinct().OrderBy(q => q, StringComparer.Ordinal).ToList();
            if (!profile.Queues.SequenceEqual(expectedQueues, StringComparer.Ordinal))
            {
                return false.Label(
                    $"queues mismatch: actual=[{string.Join(",", profile.Queues)}] " +
                    $"expected=[{string.Join(",", expectedQueues)}]");
            }

            // Min/Max domain: the range of slot statistic values (0/0 when there are no slots).
            var expectedMin = expected.Count == 0 ? 0d : expected.Values.Min();
            var expectedMax = expected.Count == 0 ? 0d : expected.Values.Max();
            if (Math.Abs(profile.Min - expectedMin) > Tolerance || Math.Abs(profile.Max - expectedMax) > Tolerance)
            {
                return false.Label(
                    $"domain mismatch: actual=[{profile.Min}, {profile.Max}] expected=[{expectedMin}, {expectedMax}]");
            }

            // The echoed metric/statistic must round-trip.
            return (profile.Metric == metric).Label("metric not echoed")
                .And((profile.Statistic == statistic).Label("statistic not echoed"));
        });
    }

    /// <summary>
    /// The continuous (linear-interpolation) percentile matching SQL <c>PERCENTILE_CONT(p)</c>: with
    /// <c>n</c> sorted values the rank is <c>p·(n−1)</c> and the result is linearly interpolated
    /// between the two nearest ranks. Independent oracle for the provider's p95 statistic.
    /// </summary>
    private static double ContinuousPercentile(double[] values, double p)
    {
        if (values == null || values.Length == 0)
        {
            return 0d;
        }

        if (values.Length == 1)
        {
            return values[0];
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);

        if (lo == hi)
        {
            return sorted[lo];
        }

        var frac = rank - lo;
        return sorted[lo] + (frac * (sorted[hi] - sorted[lo]));
    }
}
