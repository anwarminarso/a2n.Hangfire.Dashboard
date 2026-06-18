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
/// Property tests for the demand-collision behaviour of <see cref="RecommendationEngine.Analyze"/> —
/// the read-only ad-hoc demand baseline (Req 19.4).
///
/// **Property 21: Demand-collision recommendations suggest the lowest-combined-load window**
/// **Validates: Requirements 19.4**
///
/// For any recommended cron cluster whose peak slot coincides with ad-hoc demand above the low-load
/// threshold (the day's mean baseline), the recommendation flags the collision and its suggested
/// alternative is the minute-of-day that minimizes the combined (ad-hoc baseline + cron concurrency)
/// load, with the engine's earliest-minute tie-break. An independent oracle recomputes the combined
/// per-slot load from the inputs and verifies both the collision flag and the suggested minute.
///
/// All fires are generated on a single calendar day so that the engine's representative cron
/// concurrency for a cluster is exactly that queue's whole-day concurrency — this lets the oracle
/// reconstruct the same combined load the engine used without reaching into the engine's internals.
/// </summary>
public class RecommendationDemandCollisionProperties
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

    /// <summary>A fixed Monday — every fire lands on this single day to keep concurrency per-queue.</summary>
    private static readonly DateTimeOffset BaseDay = new(2023, 6, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Queue alphabet including a blank so the blank → <c>default</c> normalization is exercised.</summary>
    private static readonly string[] Queues = { "alpha", "bravo", "default", "" };

    // ----------------------------------------------------------------------------------------------
    // Generators
    // ----------------------------------------------------------------------------------------------

    /// <summary>A single generated fire descriptor (all on <see cref="BaseDay"/>).</summary>
    private readonly struct FireDesc
    {
        public FireDesc(string queue, int minuteOfDay, int extraSeconds, int durationMinutes)
        {
            Queue = queue;
            MinuteOfDay = minuteOfDay;
            ExtraSeconds = extraSeconds;
            DurationMinutes = durationMinutes;
        }

        public string Queue { get; }
        public int MinuteOfDay { get; }
        public int ExtraSeconds { get; }
        public int DurationMinutes { get; }
    }

    /// <summary>
    /// A cluster seed: at least three fires that all cover a common anchor minute on the same queue,
    /// guaranteeing detectable clusters (Req 11.1) that the stagger usually breaks up.
    /// </summary>
    private static Gen<FireDesc[]> ClusterGen =>
        from queue in Gen.Elements(Queues)
        from anchor in Gen.Choose(60, 1380)
        from count in Gen.Choose(MinClusterSize, 7)
        from members in Gen.ArrayOf(count,
            from offset in Gen.Choose(0, 30)
            from extra in Gen.Choose(1, 30)
            from seconds in Gen.Choose(0, 59)
            select (offset, extra, seconds))
        select members
            .Select(m => new FireDesc(queue, anchor - m.offset, m.seconds, m.offset + m.extra))
            .ToArray();

    /// <summary>A scattered noise fire spanning the whole day and full duration range (incl. the 1-min floor).</summary>
    private static Gen<FireDesc> NoiseGen =>
        from queue in Gen.Elements(Queues)
        from minute in Gen.Choose(0, SlotsPerDay - 1)
        from seconds in Gen.Choose(0, 59)
        from duration in Gen.Choose(0, 120)
        select new FireDesc(queue, minute, seconds, duration);

    /// <summary>
    /// A per-slot ad-hoc demand baseline of length 1,440 with enough variation to trigger collisions
    /// (Req 19.4): a low non-negative background with the chance of taller spikes so that many cluster
    /// peak minutes carry above-mean demand and the combined-load minimum varies across runs.
    /// </summary>
    private static Gen<int[]> BaselineGen =>
        Gen.ArrayOf(SlotsPerDay,
            Gen.Frequency(
                Tuple.Create(6, Gen.Choose(0, 3)),
                Tuple.Create(2, Gen.Choose(4, 8)),
                Tuple.Create(1, Gen.Choose(9, 20))));

    private static List<ProjectedFire> BuildFires(FireDesc[][] clusters, FireDesc[] noise)
    {
        var fires = new List<ProjectedFire>();
        var index = 0;

        foreach (var fd in clusters.SelectMany(c => c).Concat(noise))
        {
            var minute = Math.Max(0, fd.MinuteOfDay);
            var fireTime = BaseDay.AddMinutes(minute).AddSeconds(fd.ExtraSeconds);
            fires.Add(new ProjectedFire(
                JobId: $"job-{index++}",
                Queue: fd.Queue,
                FireTimeUtc: fireTime,
                EstimatedDuration: TimeSpan.FromMinutes(fd.DurationMinutes)));
        }

        return fires;
    }

    // ----------------------------------------------------------------------------------------------
    // Property 21
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 21: Demand-collision recommendations suggest the lowest-combined-load window**
    /// **Validates: Requirements 19.4**
    ///
    /// For every recommendation produced with an ad-hoc baseline:
    /// <list type="bullet">
    /// <item>the collision flag is set if and only if the ad-hoc demand at the cluster's peak minute
    /// strictly exceeds the day's mean baseline (the low-load threshold);</item>
    /// <item>a suggested alternative minute is present exactly when the collision is flagged;</item>
    /// <item>when flagged, the suggested minute is the earliest minute-of-day minimizing the combined
    /// (ad-hoc baseline + cron concurrency) load — verified against an independent oracle and by
    /// confirming the combined load at the suggested minute equals the global minimum.</item>
    /// </list>
    /// </summary>
    [Property(MaxTest = 200)]
    public Property DemandCollision_SuggestsLowestCombinedLoadWindow()
    {
        var arb = Arb.From(
            from clusterCount in Gen.Choose(1, 6)
            from clusters in Gen.ArrayOf(clusterCount, ClusterGen)
            from noiseCount in Gen.Choose(0, 20)
            from noise in Gen.ArrayOf(noiseCount, NoiseGen)
            from capacity in Gen.Choose(0, 20)
            from baseline in BaselineGen
            select (clusters, noise, capacity, baseline));

        return Prop.ForAll(arb, input =>
        {
            var (clusters, noise, capacity, baseline) = input;
            var fires = BuildFires(clusters, noise);

            var recs = RecommendationEngine.Analyze(fires, capacity, baseline);

            var threshold = MeanBaseline(baseline);

            foreach (var rec in recs)
            {
                // Independent reconstruction of the cron concurrency the engine used: because every
                // fire is on a single day, the cluster's representative concurrency is exactly this
                // queue's whole-day per-minute concurrency.
                var cronConcurrency = QueueConcurrency(fires, rec.Queue);

                var demandAtPeak = BaselineAt(baseline, rec.PeakMinuteOfDay);
                var expectedCollides = demandAtPeak > threshold;

                if (rec.CollidesWithHighDemand != expectedCollides)
                {
                    return false.Label(
                        $"collision flag mismatch (queue='{rec.Queue}', peakMin={rec.PeakMinuteOfDay}): " +
                        $"actual={rec.CollidesWithHighDemand} expected={expectedCollides} " +
                        $"(demandAtPeak={demandAtPeak}, threshold={threshold:F4})");
                }

                if (!expectedCollides)
                {
                    // No collision → no suggested alternative window.
                    if (rec.SuggestedMinuteOfDay is not null)
                    {
                        return false.Label(
                            $"non-colliding recommendation carries a suggested minute " +
                            $"{rec.SuggestedMinuteOfDay} (queue='{rec.Queue}', peakMin={rec.PeakMinuteOfDay})");
                    }

                    continue;
                }

                // Colliding → suggested minute must be present.
                if (rec.SuggestedMinuteOfDay is null)
                {
                    return false.Label(
                        $"colliding recommendation has no suggested minute " +
                        $"(queue='{rec.Queue}', peakMin={rec.PeakMinuteOfDay})");
                }

                var suggested = rec.SuggestedMinuteOfDay.Value;

                // Oracle: earliest minute minimizing combined (cron + ad-hoc) load.
                var (expectedMinute, minCombined) = LowestCombinedLoadMinute(cronConcurrency, baseline);

                if (suggested != expectedMinute)
                {
                    return false.Label(
                        $"suggested minute mismatch (queue='{rec.Queue}', peakMin={rec.PeakMinuteOfDay}): " +
                        $"actual={suggested} expected={expectedMinute} " +
                        $"(combined@actual={Combined(cronConcurrency, baseline, suggested)}, " +
                        $"min={minCombined})");
                }

                // The suggested minute genuinely attains the global minimum combined load.
                if (Combined(cronConcurrency, baseline, suggested) != minCombined)
                {
                    return false.Label(
                        $"suggested minute {suggested} does not attain the minimum combined load " +
                        $"(queue='{rec.Queue}'): combined={Combined(cronConcurrency, baseline, suggested)} " +
                        $"min={minCombined}");
                }

                // Earliest-minute tie-break: no earlier minute has a combined load this low.
                for (var s = 0; s < suggested; s++)
                {
                    if (Combined(cronConcurrency, baseline, s) <= minCombined)
                    {
                        return false.Label(
                            $"suggested minute {suggested} is not the earliest minimizer " +
                            $"(queue='{rec.Queue}'): minute {s} also reaches the minimum {minCombined}");
                    }
                }
            }

            return true.ToProperty();
        });
    }

    // ----------------------------------------------------------------------------------------------
    // Example-based tests
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// **Property 21 (example): a cluster whose peak minute carries a tall ad-hoc spike is flagged and
    /// the suggested window is the earliest minute with the lowest combined load.** Validates Req 19.4.
    /// </summary>
    [Fact]
    public void PeakCollidingWithDemandSpike_IsFlaggedAndSuggestsLowestCombinedMinute()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero); // 09:00 → minute 540
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(5)),
            new("b", "q1", day, TimeSpan.FromMinutes(5)),
            new("c", "q1", day, TimeSpan.FromMinutes(5)),
        };

        // Cron concurrency is 3 across minutes 540..544 and 0 everywhere else.
        // Put a tall ad-hoc spike on the cluster's peak minute so demand >> mean → collision.
        // Make minute 0 carry some demand so the earliest lowest-combined minute is minute 1 (combined 0),
        // exercising the earliest-minute tie-break against a deterministic answer.
        var baseline = new int[SlotsPerDay];
        baseline[540] = 50;
        baseline[0] = 7;

        var recs = RecommendationEngine.Analyze(fires, workerCapacity: 2, baseline);

        var rec = Assert.Single(recs);
        Assert.Equal(540, rec.PeakMinuteOfDay);
        Assert.True(rec.CollidesWithHighDemand, "tall demand spike at the peak minute should flag a collision");
        Assert.NotNull(rec.SuggestedMinuteOfDay);

        // Minute 0 combined = 7, minute 1 combined = 0 → earliest minimizer is minute 1.
        Assert.Equal(1, rec.SuggestedMinuteOfDay.Value);
    }

    /// <summary>
    /// **Property 21 (example): when ad-hoc demand at the peak minute is not above the day's mean, no
    /// collision is flagged and no alternative window is suggested.** Validates Req 19.4.
    /// </summary>
    [Fact]
    public void PeakWithoutAboveMeanDemand_IsNotFlaggedAndHasNoSuggestion()
    {
        var day = new DateTimeOffset(2023, 6, 12, 9, 0, 0, TimeSpan.Zero); // minute 540
        var fires = new List<ProjectedFire>
        {
            new("a", "q1", day, TimeSpan.FromMinutes(5)),
            new("b", "q1", day, TimeSpan.FromMinutes(5)),
            new("c", "q1", day, TimeSpan.FromMinutes(5)),
        };

        // Uniform baseline: demand at the peak (5) equals the mean (5), so demand > mean is false.
        var baseline = Enumerable.Repeat(5, SlotsPerDay).ToArray();

        var recs = RecommendationEngine.Analyze(fires, workerCapacity: 2, baseline);

        var rec = Assert.Single(recs);
        Assert.Equal(540, rec.PeakMinuteOfDay);
        Assert.False(rec.CollidesWithHighDemand, "demand equal to the mean is not above the low-load threshold");
        Assert.Null(rec.SuggestedMinuteOfDay);
    }

    // ----------------------------------------------------------------------------------------------
    // Independent oracle helpers — mirror the documented engine behaviour (Req 19.4) without reusing
    // the engine's private code paths.
    // ----------------------------------------------------------------------------------------------

    /// <summary>Per-minute cron concurrency for all of a (normalized) queue's fires on the single day.</summary>
    private static int[] QueueConcurrency(IReadOnlyList<ProjectedFire> fires, string normalizedQueue)
    {
        var slots = new int[SlotsPerDay];

        foreach (var fire in fires)
        {
            if (fire is null || !string.Equals(NormalizeQueue(fire.Queue), normalizedQueue, StringComparison.Ordinal))
            {
                continue;
            }

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

    /// <summary>The earliest minute minimizing combined (cron + ad-hoc) load, with that minimum.</summary>
    private static (int Minute, int MinCombined) LowestCombinedLoadMinute(int[] cronConcurrency, IReadOnlyList<int> baseline)
    {
        var bestMinute = 0;
        var bestCombined = int.MaxValue;

        for (var s = 0; s < SlotsPerDay; s++)
        {
            var combined = Combined(cronConcurrency, baseline, s);
            if (combined < bestCombined)
            {
                bestCombined = combined;
                bestMinute = s;
            }
        }

        return (bestMinute, bestCombined);
    }

    private static int Combined(int[] cronConcurrency, IReadOnlyList<int> baseline, int slot)
    {
        var cron = cronConcurrency is not null && slot >= 0 && slot < cronConcurrency.Length ? cronConcurrency[slot] : 0;
        return cron + BaselineAt(baseline, slot);
    }

    private static double MeanBaseline(IReadOnlyList<int> baseline)
    {
        long sum = 0;
        for (var s = 0; s < SlotsPerDay; s++)
        {
            sum += BaselineAt(baseline, s);
        }

        return (double)sum / SlotsPerDay;
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
