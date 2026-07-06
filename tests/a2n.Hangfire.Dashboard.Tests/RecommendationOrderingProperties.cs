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
/// Property tests for the deterministic ordering of <see cref="RecommendationEngine.Analyze"/> output.
///
/// **Property 20: Recommendation output is deterministically ordered**
/// **Validates: Requirements 11.10**
///
/// For any set of per-queue cron fires and a worker capacity, the engine produces the same set of
/// recommendations in the same order for identical inputs. Two complementary checks establish this:
/// <list type="bullet">
/// <item><b>(a) Sorted output.</b> The returned list is sorted by the documented comparator —
/// descending detected peak (<see cref="Recommendation.CurrentPeak"/>), then ascending queue name
/// (ordinal), then ascending peak time (<see cref="Recommendation.PeakMinuteOfDay"/>) — so adjacent
/// recommendations never violate the ordering.</item>
/// <item><b>(b) Order-independence.</b> Feeding the SAME fires in a shuffled/permuted order yields an
/// identical sequence of recommendations (same length, same fields in the same positions). Because the
/// only thing that changed is input order, an identical output sequence proves the ordering is
/// deterministic.</item>
/// </list>
/// Inputs are biased to produce multiple recommendations whose peaks, queues, and peak minutes collide,
/// exercising every tie-break level of the comparator.
/// </summary>
public class RecommendationOrderingProperties
{
    /// <summary>The number of one-minute slots in a day (mirrors the engine, Req 4.3 / 11.1).</summary>
    private const int SlotsPerDay = ConcurrencyAnalyzer.SlotsPerDay;

    /// <summary>The minimum number of overlapping fires that constitutes a cluster (Req 11.1).</summary>
    private const int MinClusterSize = 3;

    /// <summary>The queue label applied to fires whose queue cannot be determined (Req 2.4).</summary>
    private const string DefaultQueue = "default";

    /// <summary>A fixed Monday so day offsets 0..6 map to Monday..Sunday.</summary>
    private static readonly DateTimeOffset BaseMonday = new(2023, 6, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A small queue alphabet (including blank → <c>default</c> normalization) so several queues
    /// frequently share a detected peak, exercising the ascending-queue tie-break.
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "charlie", "default", "" };

    /// <summary>
    /// A small alphabet of anchor minutes so distinct queues frequently peak at the SAME minute-of-day,
    /// exercising the ascending peak-minute tie-break after the queue tie-break.
    /// </summary>
    private static readonly int[] Anchors = { 120, 480, 540, 600, 1020 };

    // ----------------------------------------------------------------------------------------------
    // Generators
    // ----------------------------------------------------------------------------------------------

    /// <summary>A single generated fire descriptor.</summary>
    private readonly struct FireDesc
    {
        public FireDesc(string queue, int dayOffset, int minuteOfDay, int extraSeconds, int durationMinutes)
        {
            Queue = queue;
            DayOffset = dayOffset;
            MinuteOfDay = minuteOfDay;
            ExtraSeconds = extraSeconds;
            DurationMinutes = durationMinutes;
        }

        public string Queue { get; }
        public int DayOffset { get; }
        public int MinuteOfDay { get; }
        public int ExtraSeconds { get; }
        public int DurationMinutes { get; }
    }

    /// <summary>
    /// A cluster seed: a set of short fires that all cover a common anchor minute on one queue/day,
    /// guaranteeing a detectable, staggerable cluster (Req 11.1). Anchor minutes and member counts are
    /// drawn from small sets so peaks/minutes collide across queues and tie-breaks are exercised.
    /// </summary>
    private static Gen<FireDesc[]> ClusterGen =>
        from queue in Gen.Elements(Queues)
        from dayOffset in Gen.Choose(0, 6)
        from anchor in Gen.Elements(Anchors)
        from count in Gen.Choose(MinClusterSize, 6)
        from members in Gen.ArrayOf(count,
            from offset in Gen.Choose(0, 5)
            from extra in Gen.Choose(1, 8)
            from seconds in Gen.Choose(0, 59)
            select (offset, extra, seconds))
        select members
            .Select(m => new FireDesc(queue, dayOffset, anchor - m.offset, m.seconds, m.offset + m.extra))
            .ToArray();

    /// <summary>A scattered noise fire spanning the whole day and the full duration range.</summary>
    private static Gen<FireDesc> NoiseGen =>
        from queue in Gen.Elements(Queues)
        from dayOffset in Gen.Choose(0, 6)
        from minute in Gen.Choose(0, SlotsPerDay - 1)
        from seconds in Gen.Choose(0, 59)
        from duration in Gen.Choose(0, 60)
        select new FireDesc(queue, dayOffset, minute, seconds, duration);

    private static List<ProjectedFire> BuildFires(FireDesc[][] clusters, FireDesc[] noise)
    {
        var fires = new List<ProjectedFire>();
        var index = 0;

        foreach (var fd in clusters.SelectMany(c => c).Concat(noise))
        {
            var minute = Math.Max(0, fd.MinuteOfDay);
            var fireTime = BaseMonday.AddDays(fd.DayOffset).AddMinutes(minute).AddSeconds(fd.ExtraSeconds);
            fires.Add(new ProjectedFire(
                JobId: $"job-{index++}",
                Queue: fd.Queue,
                FireTimeUtc: fireTime,
                EstimatedDuration: TimeSpan.FromMinutes(fd.DurationMinutes)));
        }

        return fires;
    }

    /// <summary>Deterministic Fisher–Yates permutation driven by a generated seed.</summary>
    private static List<ProjectedFire> Shuffle(List<ProjectedFire> fires, int seed)
    {
        var copy = new List<ProjectedFire>(fires);
        var rng = new System.Random(seed);
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }

    /// <summary>The documented ordering comparator (descending peak, ascending queue, ascending minute).</summary>
    private static int Compare(Recommendation a, Recommendation b)
    {
        var byPeak = b.CurrentPeak.CompareTo(a.CurrentPeak);
        if (byPeak != 0)
        {
            return byPeak;
        }

        var byQueueName = string.CompareOrdinal(a.Queue, b.Queue);
        if (byQueueName != 0)
        {
            return byQueueName;
        }

        return a.PeakMinuteOfDay.CompareTo(b.PeakMinuteOfDay);
    }

    // ----------------------------------------------------------------------------------------------
    // Property 20(a) — the output is sorted by the documented comparator
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 20: Recommendation output is deterministically ordered**
    /// **Validates: Requirements 11.10**
    ///
    /// The returned recommendations are sorted by descending detected peak, then ascending queue name
    /// (ordinal), then ascending peak minute-of-day.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Recommendations_AreSortedByTheDocumentedComparator()
    {
        var arb = Arb.From(
            from clusterCount in Gen.Choose(0, 10)
            from clusters in Gen.ArrayOf(clusterCount, ClusterGen)
            from noiseCount in Gen.Choose(0, 20)
            from noise in Gen.ArrayOf(noiseCount, NoiseGen)
            from capacity in Gen.Choose(0, 20)
            select (clusters, noise, capacity));

        return Prop.ForAll(arb, input =>
        {
            var (clusters, noise, capacity) = input;
            var fires = BuildFires(clusters, noise);

            var recs = RecommendationEngine.Analyze(fires, capacity);

            for (var i = 0; i + 1 < recs.Count; i++)
            {
                var a = recs[i];
                var b = recs[i + 1];

                if (Compare(a, b) > 0)
                {
                    return false.Label(
                        $"ordering violated at #{i}: " +
                        $"({a.Queue},peak={a.CurrentPeak},min={a.PeakMinuteOfDay}) precedes " +
                        $"({b.Queue},peak={b.CurrentPeak},min={b.PeakMinuteOfDay}) but should not");
                }

                // The comparator's keys must be unique per recommendation (queue, peak minute) so the
                // order is total and unambiguous — no two recommendations share the same cluster slot.
                if (string.Equals(a.Queue, b.Queue, StringComparison.Ordinal)
                    && a.PeakMinuteOfDay == b.PeakMinuteOfDay)
                {
                    return false.Label(
                        $"duplicate recommendation key at #{i}: queue='{a.Queue}', minute={a.PeakMinuteOfDay}");
                }
            }

            return true.ToProperty();
        });
    }

    // ----------------------------------------------------------------------------------------------
    // Property 20(b) — order-independence proves deterministic ordering
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 20: Recommendation output is deterministically ordered**
    /// **Validates: Requirements 11.10**
    ///
    /// Feeding the SAME fires in a shuffled order yields an identical sequence of recommendations
    /// (same count and identical fields in the same positions), so the ordering does not depend on
    /// input order.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Recommendations_AreIdentical_RegardlessOfInputOrder()
    {
        var arb = Arb.From(
            from clusterCount in Gen.Choose(0, 10)
            from clusters in Gen.ArrayOf(clusterCount, ClusterGen)
            from noiseCount in Gen.Choose(0, 20)
            from noise in Gen.ArrayOf(noiseCount, NoiseGen)
            from capacity in Gen.Choose(0, 20)
            from seed in Gen.Choose(0, 1_000_000)
            select (clusters, noise, capacity, seed));

        return Prop.ForAll(arb, input =>
        {
            var (clusters, noise, capacity, seed) = input;
            var fires = BuildFires(clusters, noise);
            var shuffled = Shuffle(fires, seed);

            var original = RecommendationEngine.Analyze(fires, capacity);
            var permuted = RecommendationEngine.Analyze(shuffled, capacity);

            if (original.Count != permuted.Count)
            {
                return false.Label(
                    $"shuffling changed the recommendation count: original={original.Count} " +
                    $"permuted={permuted.Count}");
            }

            for (var i = 0; i < original.Count; i++)
            {
                var a = original[i];
                var b = permuted[i];

                if (!string.Equals(a.Queue, b.Queue, StringComparison.Ordinal)
                    || a.PeakMinuteOfDay != b.PeakMinuteOfDay
                    || a.CurrentPeak != b.CurrentPeak
                    || a.StaggeredPeak != b.StaggeredPeak
                    || a.Severity != b.Severity
                    || a.CollidesWithHighDemand != b.CollidesWithHighDemand
                    || a.SuggestedMinuteOfDay != b.SuggestedMinuteOfDay
                    || !a.Weekdays.SequenceEqual(b.Weekdays))
                {
                    return false.Label(
                        $"recommendation #{i} differs after shuffling input: " +
                        $"original=({a.Queue},min={a.PeakMinuteOfDay},peak={a.CurrentPeak}) " +
                        $"permuted=({b.Queue},min={b.PeakMinuteOfDay},peak={b.CurrentPeak})");
                }
            }

            return true.ToProperty();
        });
    }

    // ----------------------------------------------------------------------------------------------
    // Example-based tests — explicit tie-break coverage
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 20 (example): descending detected peak orders recommendations first.**
    /// A 4-fire cluster outranks a 3-fire cluster regardless of queue name. Validates Req 11.10.
    /// </summary>
    [Fact]
    public void HigherDetectedPeak_IsOrderedFirst()
    {
        var day = new DateTimeOffset(2023, 6, 12, 8, 0, 0, TimeSpan.Zero); // minute 480
        var fires = new List<ProjectedFire>();

        // Queue "zzz": peak 4 (should come first despite the later queue name).
        for (var i = 0; i < 4; i++)
        {
            fires.Add(new($"z{i}", "zzz", day, TimeSpan.FromMinutes(5)));
        }

        // Queue "aaa": peak 3.
        for (var i = 0; i < 3; i++)
        {
            fires.Add(new($"a{i}", "aaa", day, TimeSpan.FromMinutes(5)));
        }

        var recs = RecommendationEngine.Analyze(fires, workerCapacity: 1);

        Assert.Equal(2, recs.Count);
        Assert.Equal("zzz", recs[0].Queue);
        Assert.Equal(4, recs[0].CurrentPeak);
        Assert.Equal("aaa", recs[1].Queue);
        Assert.Equal(3, recs[1].CurrentPeak);
    }

    /// <summary>
    /// **Property 20 (example): equal peaks tie-break on ascending ordinal queue name.**
    /// Validates Req 11.10.
    /// </summary>
    [Fact]
    public void EqualPeak_TieBreaksOnAscendingQueueName()
    {
        var day = new DateTimeOffset(2023, 6, 12, 8, 0, 0, TimeSpan.Zero);
        var fires = new List<ProjectedFire>();

        foreach (var queue in new[] { "bravo", "alpha", "charlie" })
        {
            for (var i = 0; i < 3; i++)
            {
                fires.Add(new($"{queue}-{i}", queue, day, TimeSpan.FromMinutes(5)));
            }
        }

        var recs = RecommendationEngine.Analyze(fires, workerCapacity: 1);

        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, recs.Select(r => r.Queue).ToArray());
        Assert.All(recs, r => Assert.Equal(3, r.CurrentPeak));
    }

    /// <summary>
    /// **Property 20 (example): equal peak and same queue tie-break on ascending peak minute.**
    /// Two separate clusters on the same queue at 08:00 and 10:00 order earliest-minute first.
    /// Validates Req 11.10.
    /// </summary>
    [Fact]
    public void EqualPeakSameQueue_TieBreaksOnAscendingPeakMinute()
    {
        var morning = new DateTimeOffset(2023, 6, 12, 8, 0, 0, TimeSpan.Zero); // minute 480
        var later = new DateTimeOffset(2023, 6, 12, 10, 0, 0, TimeSpan.Zero);  // minute 600
        var fires = new List<ProjectedFire>();

        for (var i = 0; i < 3; i++)
        {
            fires.Add(new($"late-{i}", "q1", later, TimeSpan.FromMinutes(5)));
            fires.Add(new($"early-{i}", "q1", morning, TimeSpan.FromMinutes(5)));
        }

        var recs = RecommendationEngine.Analyze(fires, workerCapacity: 1);

        Assert.Equal(2, recs.Count);
        Assert.Equal(480, recs[0].PeakMinuteOfDay);
        Assert.Equal(600, recs[1].PeakMinuteOfDay);
    }
}
