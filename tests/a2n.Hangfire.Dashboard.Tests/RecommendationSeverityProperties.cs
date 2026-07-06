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
/// Property tests for the severity classification produced by <see cref="RecommendationEngine.Analyze"/>.
///
/// **Property 19: Recommendation severity reflects capacity**
/// **Validates: Requirements 11.5, 11.9**
///
/// For any detected cluster, its severity is <see cref="RecommendationSeverity.High"/> when its
/// detected (current) peak concurrency is strictly greater than the active worker capacity and
/// <see cref="RecommendationSeverity.Standard"/> when the detected peak is less than or equal to the
/// capacity (Req 11.5, 11.9). The engine only returns presented recommendations, so this classification
/// is asserted on every returned <see cref="Recommendation"/>:
/// <c>Severity == High  ⇔  CurrentPeak &gt; workerCapacity</c> (otherwise <c>Standard</c>).
/// Capacities are generated across a wide range — including 0 and values clustered around the
/// cluster peaks (peak-1, peak, peak+1) — so both branches are exercised.
/// </summary>
public class RecommendationSeverityProperties
{
    /// <summary>The number of one-minute slots in a day (mirrors the engine, Req 4.3 / 11.1).</summary>
    private const int SlotsPerDay = ConcurrencyAnalyzer.SlotsPerDay;

    /// <summary>The minimum number of overlapping fires that constitutes a cluster (Req 11.1).</summary>
    private const int MinClusterSize = 3;

    /// <summary>A fixed Monday so day offsets 0..6 map to Monday..Sunday.</summary>
    private static readonly DateTimeOffset BaseMonday = new(2023, 6, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Queue alphabet including a blank and an explicit "default" so the blank → <c>default</c>
    /// normalization is exercised alongside ordinary queues.
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
    /// day, guaranteeing detectable clusters (Req 11.1) whose peak varies so capacity can sit above or
    /// below it.
    /// </summary>
    private static Gen<FireDesc[]> ClusterGen =>
        from queue in Gen.Elements(Queues)
        from dayOffset in Gen.Choose(0, 6)
        from anchor in Gen.Choose(60, 1380)
        from count in Gen.Choose(MinClusterSize, 10)
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
    // Property 19 — severity reflects capacity
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 19: Recommendation severity reflects capacity**
    /// **Validates: Requirements 11.5, 11.9**
    ///
    /// For every presented recommendation, the severity is High exactly when the detected (current)
    /// peak strictly exceeds the worker capacity, and Standard otherwise. Capacities are drawn from a
    /// wide range plus values pinned around the cluster peaks so both branches are exercised across the
    /// 200 generated cases.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Severity_IsHigh_IffDetectedPeakExceedsCapacity()
    {
        var arb = Arb.From(
            from clusterCount in Gen.Choose(0, 6)
            from clusters in Gen.ArrayOf(clusterCount, ClusterGen)
            from noiseCount in Gen.Choose(0, 25)
            from noise in Gen.ArrayOf(noiseCount, NoiseGen)
            // Wide capacity range including 0 to land on both sides of typical cluster peaks (3..10).
            from capacity in Gen.Choose(0, 20)
            select (clusters, noise, capacity));

        return Prop.ForAll(arb, input =>
        {
            var (clusters, noise, capacity) = input;
            var fires = BuildFires(clusters, noise);

            var recommendations = RecommendationEngine.Analyze(fires, capacity);

            foreach (var rec in recommendations)
            {
                var expected = rec.CurrentPeak > capacity
                    ? RecommendationSeverity.High
                    : RecommendationSeverity.Standard;

                if (rec.Severity != expected)
                {
                    return false.Label(
                        $"severity mismatch: CurrentPeak={rec.CurrentPeak} capacity={capacity} " +
                        $"expected={expected} actual={rec.Severity} (queue='{rec.Queue}', minute={rec.PeakMinuteOfDay})");
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// **Property 19: Recommendation severity reflects capacity**
    /// **Validates: Requirements 11.5, 11.9**
    ///
    /// A stronger, branch-targeted variant: for a fixed set of fires, the capacity is generated
    /// relative to each cluster's detected peak (peak-1, peak, peak+1, and the boundaries 0 and a large
    /// value), guaranteeing both the High (peak &gt; capacity) and Standard (peak ≤ capacity) branches
    /// are hit deterministically rather than relying on the wide random range alone.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Severity_AtCapacityBoundaries_FollowsStrictGreaterThanRule()
    {
        // A single, reliably-detected cluster (three identical short overlapping fires) on Monday.
        var arb = Arb.From(
            from peakMinute in Gen.Choose(60, 1380)
            // Capacity values straddling the known detected peak of 3.
            from capacity in Gen.Elements(0, 1, 2, 3, 4, 50)
            select (peakMinute, capacity));

        return Prop.ForAll(arb, input =>
        {
            var (peakMinute, capacity) = input;
            var fireTime = BaseMonday.AddMinutes(peakMinute);
            var fires = new List<ProjectedFire>
            {
                new("a", "q1", fireTime, TimeSpan.FromMinutes(5)),
                new("b", "q1", fireTime, TimeSpan.FromMinutes(5)),
                new("c", "q1", fireTime, TimeSpan.FromMinutes(5)),
            };

            var recommendations = RecommendationEngine.Analyze(fires, capacity);

            foreach (var rec in recommendations)
            {
                var expected = rec.CurrentPeak > capacity
                    ? RecommendationSeverity.High
                    : RecommendationSeverity.Standard;

                if (rec.Severity != expected)
                {
                    return false.Label(
                        $"severity mismatch at boundary: CurrentPeak={rec.CurrentPeak} capacity={capacity} " +
                        $"expected={expected} actual={rec.Severity}");
                }
            }

            return true.ToProperty();
        });
    }

    // ----------------------------------------------------------------------------------------------
    // Example-based tests pinning the two branches and the strict-greater-than boundary.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 19 (example): a detected peak strictly above capacity is High severity.**
    /// Validates Req 11.5.
    /// </summary>
    [Fact]
    public void DetectedPeakAboveCapacity_IsHighSeverity()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero);
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(5)),
            new("b", "q1", day, TimeSpan.FromMinutes(5)),
            new("c", "q1", day, TimeSpan.FromMinutes(5)),
        };

        var rec = Assert.Single(RecommendationEngine.Analyze(fires, workerCapacity: 2));
        Assert.Equal(3, rec.CurrentPeak);
        Assert.Equal(RecommendationSeverity.High, rec.Severity);
    }

    /// <summary>
    /// **Property 19 (example): a detected peak equal to capacity is Standard severity (strict &gt;).**
    /// Validates Req 11.9.
    /// </summary>
    [Fact]
    public void DetectedPeakEqualToCapacity_IsStandardSeverity()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero);
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(5)),
            new("b", "q1", day, TimeSpan.FromMinutes(5)),
            new("c", "q1", day, TimeSpan.FromMinutes(5)),
        };

        var rec = Assert.Single(RecommendationEngine.Analyze(fires, workerCapacity: 3));
        Assert.Equal(3, rec.CurrentPeak);
        Assert.Equal(RecommendationSeverity.Standard, rec.Severity);
    }

    /// <summary>
    /// **Property 19 (example): a detected peak below capacity is Standard severity.**
    /// Validates Req 11.9.
    /// </summary>
    [Fact]
    public void DetectedPeakBelowCapacity_IsStandardSeverity()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero);
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(5)),
            new("b", "q1", day, TimeSpan.FromMinutes(5)),
            new("c", "q1", day, TimeSpan.FromMinutes(5)),
        };

        var rec = Assert.Single(RecommendationEngine.Analyze(fires, workerCapacity: 10));
        Assert.Equal(3, rec.CurrentPeak);
        Assert.Equal(RecommendationSeverity.Standard, rec.Severity);
    }
}
