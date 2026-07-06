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
/// Property tests for <see cref="RecommendationEngine.Analyze"/> — per-queue cluster detection,
/// same-day stagger simulation, and present/suppress decisions.
///
/// **Property 18: Stagger recommendations are detected, simulated, and presented correctly**
/// **Validates: Requirements 11.1, 11.2, 11.3, 11.8, 19.3**
///
/// For any set of per-queue cron fires and a worker capacity:
/// <list type="bullet">
/// <item>every presented recommendation corresponds to a detected cluster of at least three fires
/// sharing a common covered minute within a day, so its current peak is at least three (Req 11.1);</item>
/// <item>the simulated stagger preserves the cluster's fire count and the multiset of durations
/// within the same calendar day (Req 11.2);</item>
/// <item>a recommendation is presented for a cluster if and only if its simulated post-stagger peak is
/// strictly less than its detected peak (Req 11.3, 11.8) — verified by comparing the engine output
/// against an independent brute-force oracle for the full "if and only if";</item>
/// <item>only the supplied cron fires drive the detected clusters and their stagger; supplying an
/// ad-hoc demand baseline as read-only context never adds, removes, or re-clusters a recommendation
/// (Req 19.3).</item>
/// </list>
/// </summary>
public class RecommendationStaggerProperties
{
    /// <summary>The number of one-minute slots in a day (mirrors the engine, Req 4.3 / 11.1).</summary>
    private const int SlotsPerDay = ConcurrencyAnalyzer.SlotsPerDay;

    /// <summary>The minimum number of overlapping fires that constitutes a cluster (Req 11.1).</summary>
    private const int MinClusterSize = 3;

    /// <summary>The minimum interval length attributed to any fire, in minutes (Req 11.1).</summary>
    private const double MinimumDurationMinutes = 1d;

    /// <summary>Tolerance matching the engine so whole-minute interval ends resolve as exact (Req 4.4).</summary>
    private const double Epsilon = 1e-9;

    /// <summary>The queue label applied to fires whose queue cannot be determined (Req 2.4).</summary>
    private const string DefaultQueue = "default";

    /// <summary>A fixed Monday so day offsets 0..6 map to Monday..Sunday for weekday checks.</summary>
    private static readonly DateTimeOffset BaseMonday = new(2023, 6, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Queue alphabet including a blank and an explicit "default" so the blank → <c>default</c>
    /// normalization is exercised and collides with the real default bucket.
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "default", "" };

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
    /// A cluster seed: at least three fires that all cover a common anchor minute on the same queue and
    /// day, guaranteeing detectable clusters (Req 11.1) that the stagger can usually break up.
    /// </summary>
    private static Gen<FireDesc[]> ClusterGen =>
        from queue in Gen.Elements(Queues)
        from dayOffset in Gen.Choose(0, 6)
        from anchor in Gen.Choose(60, 1380)
        from count in Gen.Choose(MinClusterSize, 7)
        from members in Gen.ArrayOf(count,
            from offset in Gen.Choose(0, 30)
            from extra in Gen.Choose(1, 30)
            from seconds in Gen.Choose(0, 59)
            select (offset, extra, seconds))
        select members
            .Select(m => new FireDesc(queue, dayOffset, anchor - m.offset, m.seconds, m.offset + m.extra))
            .ToArray();

    /// <summary>A scattered noise fire spanning the whole day and full duration range (incl. the 1-min floor).</summary>
    private static Gen<FireDesc> NoiseGen =>
        from queue in Gen.Elements(Queues)
        from dayOffset in Gen.Choose(0, 6)
        from minute in Gen.Choose(0, SlotsPerDay - 1)
        from seconds in Gen.Choose(0, 59)
        from duration in Gen.Choose(0, 120)
        select new FireDesc(queue, dayOffset, minute, seconds, duration);

    /// <summary>A per-slot ad-hoc demand baseline of length 1,440 with small non-negative values (Req 19.4).</summary>
    private static Gen<int[]> BaselineGen => Gen.ArrayOf(SlotsPerDay, Gen.Choose(0, 4));

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

    // ----------------------------------------------------------------------------------------------
    // Property 18 — engine matches the independent oracle and satisfies the invariants
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 18: Stagger recommendations are detected, simulated, and presented correctly**
    /// **Validates: Requirements 11.1, 11.2, 11.3, 11.8**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Recommendations_AreDetectedSimulatedAndPresentedCorrectly()
    {
        var arb = Arb.From(
            from clusterCount in Gen.Choose(0, 6)
            from clusters in Gen.ArrayOf(clusterCount, ClusterGen)
            from noiseCount in Gen.Choose(0, 25)
            from noise in Gen.ArrayOf(noiseCount, NoiseGen)
            from capacity in Gen.Choose(0, 20)
            select (clusters, noise, capacity));

        return Prop.ForAll(arb, input =>
        {
            var (clusters, noise, capacity) = input;
            var fires = BuildFires(clusters, noise);

            var actual = RecommendationEngine.Analyze(fires, capacity);
            var expected = OracleRecommendations(fires);

            // ---- Universal invariants on every presented recommendation (Req 11.1, 11.3, 11.8). ----
            foreach (var rec in actual)
            {
                // Req 11.1: a presented recommendation comes from a cluster of >= 3 overlapping fires.
                if (rec.CurrentPeak < MinClusterSize)
                {
                    return false.Label(
                        $"presented recommendation with CurrentPeak={rec.CurrentPeak} < {MinClusterSize} " +
                        $"(queue='{rec.Queue}', minute={rec.PeakMinuteOfDay})");
                }

                // Req 11.3 / 11.8: presented only when the stagger strictly reduces the peak.
                if (rec.StaggeredPeak >= rec.CurrentPeak)
                {
                    return false.Label(
                        $"presented recommendation does not strictly reduce the peak: " +
                        $"StaggeredPeak={rec.StaggeredPeak} >= CurrentPeak={rec.CurrentPeak} (queue='{rec.Queue}')");
                }

                if (rec.StaggeredPeak < 0)
                {
                    return false.Label($"negative StaggeredPeak={rec.StaggeredPeak} (queue='{rec.Queue}')");
                }

                // Req 11.1: the detected peak is genuinely realized — at least CurrentPeak fires of the
                // queue cover the reported peak minute on at least one of the reported weekdays.
                if (!PeakIsRealized(fires, rec))
                {
                    return false.Label(
                        $"detected peak {rec.CurrentPeak} is not realized at minute {rec.PeakMinuteOfDay} " +
                        $"for queue '{rec.Queue}' on the reported weekdays");
                }
            }

            // ---- Full "if and only if": the engine output equals the independent oracle. ----
            if (actual.Count != expected.Count)
            {
                return false.Label(
                    $"recommendation count mismatch: actual={actual.Count} expected={expected.Count} " +
                    $"(fires={fires.Count})");
            }

            for (var i = 0; i < actual.Count; i++)
            {
                var a = actual[i];
                var e = expected[i];

                if (!string.Equals(a.Queue, e.Queue, StringComparison.Ordinal)
                    || a.PeakMinuteOfDay != e.PeakMinute
                    || a.CurrentPeak != e.DetectedPeak
                    || a.StaggeredPeak != e.StaggeredPeak
                    || !a.Weekdays.SequenceEqual(e.Weekdays))
                {
                    return false.Label(
                        $"recommendation #{i} mismatch: " +
                        $"actual=({a.Queue},min={a.PeakMinuteOfDay},peak={a.CurrentPeak},stag={a.StaggeredPeak}," +
                        $"days=[{string.Join(",", a.Weekdays)}]) " +
                        $"expected=({e.Queue},min={e.PeakMinute},peak={e.DetectedPeak},stag={e.StaggeredPeak}," +
                        $"days=[{string.Join(",", e.Weekdays)}])");
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// **Property 18: Stagger recommendations are detected, simulated, and presented correctly**
    /// **Validates: Requirements 19.3**
    ///
    /// Only the cron fires drive the clusters and the stagger; supplying an ad-hoc demand baseline as
    /// read-only context (Req 19.3, 19.5) never changes which clusters are recommended, their peak
    /// minute, weekdays, detected peak, or simulated post-stagger peak — the baseline only annotates a
    /// recommendation with a demand-collision flag / alternative window.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Recommendations_DependOnlyOnCronFires_NotTheAdHocBaseline()
    {
        var arb = Arb.From(
            from clusterCount in Gen.Choose(0, 6)
            from clusters in Gen.ArrayOf(clusterCount, ClusterGen)
            from noiseCount in Gen.Choose(0, 25)
            from noise in Gen.ArrayOf(noiseCount, NoiseGen)
            from capacity in Gen.Choose(0, 20)
            from baseline in BaselineGen
            select (clusters, noise, capacity, baseline));

        return Prop.ForAll(arb, input =>
        {
            var (clusters, noise, capacity, baseline) = input;
            var fires = BuildFires(clusters, noise);

            var withoutBaseline = RecommendationEngine.Analyze(fires, capacity);
            var withBaseline = RecommendationEngine.Analyze(fires, capacity, baseline);

            if (withoutBaseline.Count != withBaseline.Count)
            {
                return false.Label(
                    $"baseline changed the recommendation count: without={withoutBaseline.Count} " +
                    $"with={withBaseline.Count}");
            }

            for (var i = 0; i < withoutBaseline.Count; i++)
            {
                var a = withoutBaseline[i];
                var b = withBaseline[i];

                // Cluster identity and stagger outcome must be identical (ad-hoc demand is read-only).
                if (!string.Equals(a.Queue, b.Queue, StringComparison.Ordinal)
                    || a.PeakMinuteOfDay != b.PeakMinuteOfDay
                    || a.CurrentPeak != b.CurrentPeak
                    || a.StaggeredPeak != b.StaggeredPeak
                    || a.Severity != b.Severity
                    || !a.Weekdays.SequenceEqual(b.Weekdays))
                {
                    return false.Label(
                        $"recommendation #{i} differs when an ad-hoc baseline is supplied " +
                        $"(queue='{a.Queue}', minute={a.PeakMinuteOfDay})");
                }
            }

            return true.ToProperty();
        });
    }

    // ----------------------------------------------------------------------------------------------
    // Example-based tests
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 18 (example): a cluster of three overlapping short fires yields exactly one
    /// recommendation whose stagger strictly reduces the peak.** Validates Req 11.1, 11.2, 11.3.
    /// </summary>
    [Fact]
    public void ThreeOverlappingShortFires_ProduceOneReducingRecommendation()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero); // 09:00, minute 540
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(5)),
            new("b", "q1", day, TimeSpan.FromMinutes(5)),
            new("c", "q1", day, TimeSpan.FromMinutes(5)),
        };

        var recs = RecommendationEngine.Analyze(fires, workerCapacity: 2);

        var rec = Assert.Single(recs);
        Assert.Equal("q1", rec.Queue);
        Assert.Equal(540, rec.PeakMinuteOfDay);
        Assert.Equal(3, rec.CurrentPeak);
        Assert.True(rec.StaggeredPeak < rec.CurrentPeak, "stagger should strictly reduce the peak");
        Assert.Equal(new[] { DayOfWeek.Monday }, rec.Weekdays);
    }

    /// <summary>
    /// **Property 18 (example): two overlapping fires are below the 3-fire cluster threshold, so no
    /// recommendation is presented.** Validates Req 11.1.
    /// </summary>
    [Fact]
    public void TwoOverlappingFires_ProduceNoRecommendation()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero);
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(10)),
            new("b", "q1", day, TimeSpan.FromMinutes(10)),
        };

        Assert.Empty(RecommendationEngine.Analyze(fires, workerCapacity: 1));
    }

    /// <summary>
    /// **Property 18 (example): three back-to-back (non-overlapping) fires never share a common minute,
    /// so no cluster is detected and no recommendation is presented.** Validates Req 11.1.
    /// </summary>
    [Fact]
    public void BackToBackFires_ProduceNoRecommendation()
    {
        var baseTime = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero);
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", baseTime, TimeSpan.FromMinutes(5)),
            new("b", "q1", baseTime.AddMinutes(5), TimeSpan.FromMinutes(5)),
            new("c", "q1", baseTime.AddMinutes(10), TimeSpan.FromMinutes(5)),
        };

        Assert.Empty(RecommendationEngine.Analyze(fires, workerCapacity: 1));
    }

    /// <summary>
    /// **Property 18 (example): supplying an ad-hoc baseline does not change the recommended cluster
    /// (Req 19.3) — it only annotates the collision flag.** Validates Req 19.3.
    /// </summary>
    [Fact]
    public void AdHocBaseline_DoesNotChangeTheRecommendedCluster()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero); // minute 540
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(5)),
            new("b", "q1", day, TimeSpan.FromMinutes(5)),
            new("c", "q1", day, TimeSpan.FromMinutes(5)),
        };

        var baseline = new int[SlotsPerDay];
        baseline[540] = 50; // heavy ad-hoc demand at the cluster's peak minute

        var withBaseline = RecommendationEngine.Analyze(fires, workerCapacity: 2, baseline);
        var withoutBaseline = RecommendationEngine.Analyze(fires, workerCapacity: 2);

        var a = Assert.Single(withBaseline);
        var b = Assert.Single(withoutBaseline);

        Assert.Equal(b.Queue, a.Queue);
        Assert.Equal(b.PeakMinuteOfDay, a.PeakMinuteOfDay);
        Assert.Equal(b.CurrentPeak, a.CurrentPeak);
        Assert.Equal(b.StaggeredPeak, a.StaggeredPeak);
    }

    // ----------------------------------------------------------------------------------------------
    // Independent oracle (brute force) — mirrors the documented engine behavior (Req 11.1, 11.2, 11.3,
    // 11.8) without reusing its private code paths.
    // ----------------------------------------------------------------------------------------------

    private sealed class ExpectedRecommendation
    {
        public string Queue { get; set; }
        public int PeakMinute { get; set; }
        public int DetectedPeak { get; set; }
        public int StaggeredPeak { get; set; }
        public List<DayOfWeek> Weekdays { get; set; }
    }

    private static List<ExpectedRecommendation> OracleRecommendations(IReadOnlyList<ProjectedFire> fires)
    {
        var recs = new List<ExpectedRecommendation>();

        // Group by queue (normalized).
        var byQueue = fires
            .Where(f => f is not null)
            .GroupBy(f => NormalizeQueue(f.Queue), StringComparer.Ordinal);

        foreach (var queueGroup in byQueue)
        {
            // peak minute-of-day -> merged cluster.
            var merged = new Dictionary<int, ExpectedCluster>();

            // Group by calendar day, ascending date so the earliest day represents on ties.
            var byDate = queueGroup
                .GroupBy(f => f.FireTimeUtc.Date)
                .OrderBy(g => g.Key);

            foreach (var dayGroup in byDate)
            {
                var dayFires = dayGroup.ToList();
                var concurrency = Concurrency(dayFires);

                foreach (var (start, end) in Components(dayFires))
                {
                    var (peakMinute, peak) = PeakWithin(concurrency, start, end);
                    if (peak < MinClusterSize)
                    {
                        continue;
                    }

                    var memberDurations = dayFires
                        .Where(f => Covers(f, peakMinute))
                        .Select(DurationMinutes)
                        .ToList();

                    var weekday = dayGroup.Key.DayOfWeek;

                    if (!merged.TryGetValue(peakMinute, out var existing))
                    {
                        merged[peakMinute] = new ExpectedCluster
                        {
                            PeakMinute = peakMinute,
                            DetectedPeak = peak,
                            Weekdays = new List<DayOfWeek> { weekday },
                            MemberDurations = memberDurations,
                        };
                    }
                    else
                    {
                        existing.Weekdays.Add(weekday);
                        if (peak > existing.DetectedPeak)
                        {
                            existing.DetectedPeak = peak;
                            existing.MemberDurations = memberDurations;
                        }
                    }
                }
            }

            foreach (var cluster in merged.Values)
            {
                var staggered = SimulateStaggeredPeak(cluster.MemberDurations);
                if (staggered >= cluster.DetectedPeak)
                {
                    continue; // Req 11.8: not strictly reduced → suppress.
                }

                recs.Add(new ExpectedRecommendation
                {
                    Queue = queueGroup.Key,
                    PeakMinute = cluster.PeakMinute,
                    DetectedPeak = cluster.DetectedPeak,
                    StaggeredPeak = staggered,
                    Weekdays = cluster.Weekdays.Distinct().OrderBy(d => (int)d).ToList(),
                });
            }
        }

        // Deterministic ordering: descending detected peak, ascending queue, ascending peak minute.
        recs.Sort((a, b) =>
        {
            var byPeak = b.DetectedPeak.CompareTo(a.DetectedPeak);
            if (byPeak != 0)
            {
                return byPeak;
            }

            var byQueueName = string.CompareOrdinal(a.Queue, b.Queue);
            if (byQueueName != 0)
            {
                return byQueueName;
            }

            return a.PeakMinute.CompareTo(b.PeakMinute);
        });

        return recs;
    }

    private sealed class ExpectedCluster
    {
        public int PeakMinute { get; set; }
        public int DetectedPeak { get; set; }
        public List<DayOfWeek> Weekdays { get; set; }
        public List<double> MemberDurations { get; set; }
    }

    /// <summary>Brute-force per-minute concurrency for a single day's fires (half-open intervals).</summary>
    private static int[] Concurrency(List<ProjectedFire> dayFires)
    {
        var slots = new int[SlotsPerDay];

        foreach (var fire in dayFires)
        {
            var start = MinuteOfDay(fire);
            if (start < 0 || start >= SlotsPerDay)
            {
                continue;
            }

            var endExclusive = start + DurationMinutes(fire);
            for (var s = start; s < SlotsPerDay; s++)
            {
                if (s + Epsilon >= endExclusive)
                {
                    break;
                }

                slots[s]++;
            }
        }

        return slots;
    }

    /// <summary>Connected components of overlapping half-open intervals (touching ends are disjoint).</summary>
    private static List<(double Start, double End)> Components(List<ProjectedFire> dayFires)
    {
        var intervals = dayFires
            .Select(f =>
            {
                double start = MinuteOfDay(f);
                return (Start: start, End: start + DurationMinutes(f));
            })
            .OrderBy(iv => iv.Start)
            .ThenBy(iv => iv.End)
            .ToList();

        var components = new List<(double Start, double End)>();
        var hasCurrent = false;
        double currentStart = 0d;
        double currentMaxEnd = 0d;

        foreach (var iv in intervals)
        {
            if (!hasCurrent || iv.Start + Epsilon >= currentMaxEnd)
            {
                if (hasCurrent)
                {
                    components.Add((currentStart, currentMaxEnd));
                }

                hasCurrent = true;
                currentStart = iv.Start;
                currentMaxEnd = iv.End;
            }
            else if (iv.End > currentMaxEnd)
            {
                currentMaxEnd = iv.End;
            }
        }

        if (hasCurrent)
        {
            components.Add((currentStart, currentMaxEnd));
        }

        return components;
    }

    /// <summary>The earliest minute (and its concurrency) reaching the maximum within a span.</summary>
    private static (int PeakMinute, int Peak) PeakWithin(int[] concurrency, double startMinute, double endMinute)
    {
        var from = Math.Max(0, (int)Math.Floor(startMinute));
        var to = Math.Min(SlotsPerDay - 1, (int)Math.Ceiling(endMinute) - 1);

        var peak = 0;
        var peakMinute = from;

        for (var s = from; s <= to; s++)
        {
            if (concurrency[s] > peak)
            {
                peak = concurrency[s];
                peakMinute = s;
            }
        }

        return (peakMinute, peak);
    }

    /// <summary>
    /// Simulates staggering: place the durations (largest first) at evenly spaced start times across
    /// the day, preserving count and the multiset of durations (Req 11.2), and return the resulting
    /// peak concurrency.
    /// </summary>
    private static int SimulateStaggeredPeak(IReadOnlyList<double> durations)
    {
        var count = durations.Count;
        if (count == 0)
        {
            return 0;
        }

        var ordered = durations.OrderByDescending(d => d).ToList();
        var spacing = (double)SlotsPerDay / count;
        var slots = new int[SlotsPerDay];

        for (var i = 0; i < count; i++)
        {
            var start = (int)Math.Round(i * spacing, MidpointRounding.AwayFromZero);
            if (start < 0)
            {
                start = 0;
            }
            else if (start >= SlotsPerDay)
            {
                start = SlotsPerDay - 1;
            }

            var duration = ordered[i] < MinimumDurationMinutes ? MinimumDurationMinutes : ordered[i];
            var endExclusive = start + duration;

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
        for (var s = 0; s < SlotsPerDay; s++)
        {
            if (slots[s] > peak)
            {
                peak = slots[s];
            }
        }

        return peak;
    }

    /// <summary>
    /// Confirms the detected peak is genuinely realized: on at least one of the recommendation's
    /// reported weekdays the queue's fires reach <see cref="Recommendation.CurrentPeak"/> concurrency at
    /// the reported peak minute (Req 11.1).
    /// </summary>
    private static bool PeakIsRealized(IReadOnlyList<ProjectedFire> fires, Recommendation rec)
    {
        var queueFires = fires
            .Where(f => f is not null && string.Equals(NormalizeQueue(f.Queue), rec.Queue, StringComparison.Ordinal))
            .GroupBy(f => f.FireTimeUtc.Date);

        foreach (var dayGroup in queueFires)
        {
            if (!rec.Weekdays.Contains(dayGroup.Key.DayOfWeek))
            {
                continue;
            }

            var concurrency = Concurrency(dayGroup.ToList());
            if (concurrency[rec.PeakMinuteOfDay] == rec.CurrentPeak)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Covers(ProjectedFire fire, int minute)
    {
        var start = MinuteOfDay(fire);
        var endExclusive = start + DurationMinutes(fire);
        return start <= minute && minute + Epsilon < endExclusive;
    }

    private static double DurationMinutes(ProjectedFire fire)
    {
        var minutes = fire.EstimatedDuration.TotalMinutes;
        return minutes < MinimumDurationMinutes ? MinimumDurationMinutes : minutes;
    }

    private static int MinuteOfDay(ProjectedFire fire)
        => (int)Math.Floor(fire.FireTimeUtc.TimeOfDay.TotalMinutes);

    private static string NormalizeQueue(string queue)
        => string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;
}
