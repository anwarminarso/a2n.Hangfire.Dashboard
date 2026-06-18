using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure, deterministic engine that detects per-queue clusters of overlapping recurring-job fires,
/// simulates same-day staggering, and produces read-only before/after <see cref="Recommendation"/>s
/// (Requirement 11). It never mutates any cron expression and treats the optional ad-hoc demand
/// baseline strictly as read-only context (Req 11.6, 19.5).
/// </summary>
/// <remarks>
/// <para><b>Cluster detection (Req 11.1).</b> Fires are grouped by queue and then by calendar day
/// (each recurring job fires once per day per schedule, so analyzing per day avoids collapsing the
/// same daily schedule onto a single minute axis). Within a day the queue's fires are split into
/// connected components of overlapping half-open intervals <c>[start, start + max(duration, 1 min))</c>
/// — back-to-back intervals are not connected (Req 4.4). A component qualifies as a cluster when its
/// peak concurrency reaches three or more, i.e. at least three fires cover a common minute. The
/// detected peak is that component's peak concurrency and the cluster's representative minute is the
/// earliest minute reaching it (Req 4.5).</para>
/// <para><b>Repetition across weekdays (Req 11.4).</b> Clusters found on different calendar days that
/// share the same peak minute-of-day for the same queue are the same recurring cluster; their
/// weekdays are unioned and the detected peak is the maximum across those days (with the earliest
/// such day chosen as the representative for simulation).</para>
/// <para><b>Stagger simulation (Req 11.2, 11.3, 11.8).</b> The cluster's member fires (those covering
/// the peak minute) are re-placed at evenly spaced start times across the same calendar day, keeping
/// the fire count and the multiset of durations unchanged, and the resulting peak concurrency is
/// recomputed. A recommendation is presented if and only if the simulated peak is strictly less than
/// the detected peak.</para>
/// <para><b>Severity (Req 11.5, 11.9).</b> A cluster is high severity when its detected peak strictly
/// exceeds the worker capacity, otherwise standard.</para>
/// <para><b>Demand collision (Req 19.3, 19.4, 19.5).</b> Only cron clusters are analyzed; ad-hoc jobs
/// are never rescheduled. When an ad-hoc baseline is supplied and the cluster's peak minute carries
/// demand above the low-load threshold (the mean baseline across the day), the recommendation flags
/// the collision and suggests the minute-of-day that minimizes the combined (ad-hoc + cron) load.</para>
/// <para><b>Ordering (Req 11.10).</b> Recommendations are ordered by descending detected peak, then
/// ascending queue name, then ascending peak minute, so identical inputs (in any fire order) produce
/// identical output.</para>
/// <para>Validates Requirements 11.1–11.10 and 19.3–19.5 (design Properties 18–21).</para>
/// </remarks>
public static class RecommendationEngine
{
    /// <summary>The number of one-minute slots in a day (mirrors <see cref="ConcurrencyAnalyzer.SlotsPerDay"/>).</summary>
    private const int SlotsPerDay = ConcurrencyAnalyzer.SlotsPerDay;

    /// <summary>The minimum number of overlapping fires that constitutes a cluster (Req 11.1).</summary>
    private const int MinClusterSize = 3;

    /// <summary>The queue label applied to fires whose queue cannot be determined (Req 2.4).</summary>
    private const string DefaultQueue = ConcurrencyAnalyzer.DefaultQueue;

    /// <summary>The minimum interval length attributed to any fire, in minutes (Req 4.1, 4.2, 11.1).</summary>
    private const double MinimumDurationMinutes = 1d;

    /// <summary>Tolerance so whole-minute interval ends resolve as exact, keeping back-to-back fires non-concurrent (Req 4.4).</summary>
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Analyzes the supplied projected cron fires and returns the ordered set of read-only stagger
    /// recommendations.
    /// </summary>
    /// <param name="cronFires">
    /// The projected cron fires to analyze (ad-hoc jobs must not be included — Req 19.3). A fire's
    /// minute-of-day and calendar day are taken from <see cref="ProjectedFire.FireTimeUtc"/>, matching
    /// <see cref="ConcurrencyAnalyzer"/>. Order does not affect the result (Req 11.10). A null or empty
    /// list yields no recommendations.
    /// </param>
    /// <param name="workerCapacity">
    /// The active worker capacity used to classify cluster severity; a cluster is high severity when
    /// its detected peak is strictly greater than this value (Req 11.5, 11.9).
    /// </param>
    /// <param name="adHocBaselinePerSlot">
    /// An optional per-minute ad-hoc demand baseline (Req 19.4). When supplied, a cluster whose peak
    /// minute carries demand above the day's mean baseline is flagged as colliding with high demand
    /// and a lowest-combined-load alternative window is suggested. Missing or short entries are treated
    /// as zero; entries beyond <see cref="SlotsPerDay"/> are ignored.
    /// </param>
    /// <returns>The recommendations ordered deterministically (Req 11.10).</returns>
    public static IReadOnlyList<Recommendation> Analyze(
        IReadOnlyList<ProjectedFire> cronFires,
        int workerCapacity,
        IReadOnlyList<int> adHocBaselinePerSlot = null)
    {
        if (cronFires is null || cronFires.Count == 0)
        {
            return Array.Empty<Recommendation>();
        }

        // Group fires by queue (ordinal-sorted so processing is deterministic).
        var byQueue = new SortedDictionary<string, List<ProjectedFire>>(StringComparer.Ordinal);
        foreach (var fire in cronFires)
        {
            if (fire is null)
            {
                continue;
            }

            var queue = NormalizeQueue(fire.Queue);
            if (!byQueue.TryGetValue(queue, out var list))
            {
                list = new List<ProjectedFire>();
                byQueue[queue] = list;
            }

            list.Add(fire);
        }

        var recommendations = new List<Recommendation>();

        foreach (var (queue, queueFires) in byQueue)
        {
            foreach (var cluster in DetectClusters(queueFires))
            {
                var detectedPeak = cluster.DetectedPeak;
                var severity = detectedPeak > workerCapacity
                    ? RecommendationSeverity.High
                    : RecommendationSeverity.Standard;

                // Simulate same-day staggering of the member fires (count + durations preserved).
                var staggeredPeak = SimulateStaggeredPeak(cluster.MemberDurations);

                // Present only when staggering strictly reduces the peak (Req 11.3, 11.8).
                if (staggeredPeak >= detectedPeak)
                {
                    continue;
                }

                var collides = false;
                int? suggestedMinute = null;

                if (adHocBaselinePerSlot is not null)
                {
                    var threshold = MeanBaseline(adHocBaselinePerSlot);
                    var demandAtPeak = BaselineAt(adHocBaselinePerSlot, cluster.PeakMinute);

                    if (demandAtPeak > threshold)
                    {
                        collides = true;
                        suggestedMinute = LowestCombinedLoadMinute(
                            cluster.RepresentativeConcurrency, adHocBaselinePerSlot);
                    }
                }

                var weekdays = cluster.Weekdays
                    .Distinct()
                    .OrderBy(d => (int)d)
                    .ToList();

                recommendations.Add(new Recommendation(
                    queue,
                    cluster.PeakMinute,
                    weekdays,
                    detectedPeak,
                    staggeredPeak,
                    severity,
                    collides,
                    suggestedMinute));
            }
        }

        // Deterministic ordering: descending detected peak, ascending queue, ascending peak minute.
        recommendations.Sort(static (a, b) =>
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
        });

        return recommendations;
    }

    /// <summary>
    /// Detects every cluster for a single queue across all of its calendar days, merging clusters that
    /// share a peak minute-of-day into one recurring cluster spanning the corresponding weekdays.
    /// </summary>
    private static IEnumerable<MergedCluster> DetectClusters(List<ProjectedFire> queueFires)
    {
        // Key recurring clusters by peak minute-of-day.
        var merged = new Dictionary<int, MergedCluster>();

        // Group the queue's fires by calendar day, processed in ascending date order so the earliest
        // day is the representative on equal peaks.
        var byDate = queueFires
            .GroupBy(f => f.FireTimeUtc.Date)
            .OrderBy(g => g.Key);

        foreach (var dayGroup in byDate)
        {
            var dayFires = dayGroup.ToList();
            var concurrency = ComputeConcurrency(dayFires);

            foreach (var component in OverlapComponents(dayFires))
            {
                var (peakMinute, peak) = PeakWithin(concurrency, component.StartMinute, component.EndMinute);
                if (peak < MinClusterSize)
                {
                    continue;
                }

                // Cluster members are the fires that cover the common (peak) minute (Req 11.1); because
                // components are temporally disjoint only this component's fires can cover it.
                var memberDurations = dayFires
                    .Where(f => Covers(f, peakMinute))
                    .Select(DurationMinutes)
                    .ToList();

                var weekday = dayGroup.Key.DayOfWeek;

                if (!merged.TryGetValue(peakMinute, out var existing))
                {
                    merged[peakMinute] = new MergedCluster(
                        peakMinute, peak, new List<DayOfWeek> { weekday }, memberDurations, concurrency);
                }
                else
                {
                    existing.Weekdays.Add(weekday);

                    // Keep the representative from the day with the highest peak; ascending date order
                    // means the earliest such day is retained on ties.
                    if (peak > existing.DetectedPeak)
                    {
                        existing.DetectedPeak = peak;
                        existing.MemberDurations = memberDurations;
                        existing.RepresentativeConcurrency = concurrency;
                    }
                }
            }
        }

        return merged.Values;
    }

    /// <summary>
    /// Splits a day's fires into connected components of overlapping half-open intervals. Back-to-back
    /// intervals (one ending exactly when the next begins) are not connected (Req 4.4).
    /// </summary>
    private static IEnumerable<IntervalComponent> OverlapComponents(List<ProjectedFire> dayFires)
    {
        var intervals = dayFires
            .Select(f =>
            {
                var start = MinuteOfDay(f.FireTimeUtc);
                return (Start: start, End: start + DurationMinutes(f));
            })
            .OrderBy(iv => iv.Start)
            .ThenBy(iv => iv.End)
            .ToList();

        var components = new List<IntervalComponent>();
        var hasCurrent = false;
        double currentStart = 0d;
        double currentMaxEnd = 0d;

        foreach (var iv in intervals)
        {
            // Overlap iff iv.Start < currentMaxEnd. The epsilon keeps touching intervals (iv.Start ==
            // currentMaxEnd) in separate components, matching the non-concurrent back-to-back rule.
            if (!hasCurrent || iv.Start + Epsilon >= currentMaxEnd)
            {
                if (hasCurrent)
                {
                    components.Add(new IntervalComponent(currentStart, currentMaxEnd));
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
            components.Add(new IntervalComponent(currentStart, currentMaxEnd));
        }

        return components;
    }

    /// <summary>
    /// Finds the earliest minute (and its concurrency) reaching the maximum concurrency within a
    /// component's minute span (Req 4.5).
    /// </summary>
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
    /// Computes the per-minute concurrency for a single day's fires using half-open interval coverage,
    /// mirroring <see cref="ConcurrencyAnalyzer"/> so detected peaks are consistent across the feature.
    /// </summary>
    private static int[] ComputeConcurrency(List<ProjectedFire> dayFires)
    {
        var slots = new int[SlotsPerDay];

        foreach (var fire in dayFires)
        {
            var startMinute = MinuteOfDay(fire.FireTimeUtc);
            if (startMinute < 0 || startMinute >= SlotsPerDay)
            {
                continue;
            }

            var endExclusive = startMinute + DurationMinutes(fire);

            for (var s = startMinute; s < SlotsPerDay; s++)
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

    /// <summary>
    /// Simulates staggering by placing the supplied durations at evenly spaced start times across the
    /// same calendar day and returns the resulting peak concurrency. The fire count and the multiset
    /// of durations are preserved (Req 11.2).
    /// </summary>
    private static int SimulateStaggeredPeak(IReadOnlyList<double> durations)
    {
        var count = durations.Count;
        if (count == 0)
        {
            return 0;
        }

        // Largest durations first gives a deterministic, well-spread arrangement.
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
    /// Returns the earliest minute-of-day minimizing the combined (cron + ad-hoc) load (Req 19.4).
    /// </summary>
    private static int LowestCombinedLoadMinute(int[] cronConcurrency, IReadOnlyList<int> baseline)
    {
        var best = 0;
        var bestCombined = int.MaxValue;

        for (var s = 0; s < SlotsPerDay; s++)
        {
            var cron = cronConcurrency is not null && s < cronConcurrency.Length ? cronConcurrency[s] : 0;
            var combined = cron + BaselineAt(baseline, s);

            if (combined < bestCombined)
            {
                bestCombined = combined;
                best = s;
            }
        }

        return best;
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

    private static bool Covers(ProjectedFire fire, int minute)
    {
        var start = MinuteOfDay(fire.FireTimeUtc);
        var endExclusive = start + DurationMinutes(fire);
        return start <= minute && minute + Epsilon < endExclusive;
    }

    private static double DurationMinutes(ProjectedFire fire)
    {
        var minutes = fire.EstimatedDuration.TotalMinutes;
        return minutes < MinimumDurationMinutes ? MinimumDurationMinutes : minutes;
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

    /// <summary>A temporally connected group of overlapping intervals within a single day.</summary>
    private readonly struct IntervalComponent
    {
        public IntervalComponent(double startMinute, double endMinute)
        {
            StartMinute = startMinute;
            EndMinute = endMinute;
        }

        public double StartMinute { get; }

        public double EndMinute { get; }
    }

    /// <summary>
    /// A recurring cluster keyed by its peak minute-of-day, accumulating the weekdays it repeats on and
    /// retaining the representative day's member durations and concurrency for simulation/suggestion.
    /// </summary>
    private sealed class MergedCluster
    {
        public MergedCluster(
            int peakMinute,
            int detectedPeak,
            List<DayOfWeek> weekdays,
            List<double> memberDurations,
            int[] representativeConcurrency)
        {
            PeakMinute = peakMinute;
            DetectedPeak = detectedPeak;
            Weekdays = weekdays;
            MemberDurations = memberDurations;
            RepresentativeConcurrency = representativeConcurrency;
        }

        public int PeakMinute { get; }

        public int DetectedPeak { get; set; }

        public List<DayOfWeek> Weekdays { get; }

        public List<double> MemberDurations { get; set; }

        public int[] RepresentativeConcurrency { get; set; }
    }
}
