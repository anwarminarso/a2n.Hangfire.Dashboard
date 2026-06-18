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
/// Property tests for <see cref="SubHourly.DetectSubHourlyJobIds"/> and <see cref="SubHourly.Filter"/>.
///
/// **Property 28: Sub-hourly detection and filtering are consistent**
/// **Validates: Requirements 20.1, 20.2, 20.3**
///
/// For any list of projected fires, a viewer time zone, and a projection window: a job is detected
/// as sub-hourly iff some <c>(dayIndex, hour)</c> bucket (computed with <see cref="HeatmapTime.GetBucket"/>)
/// contains more than one of that job's fires (Req 20.1); the filter removes exactly the fires of the
/// sub-hourly jobs and only those, preserving the original relative order of the retained fires
/// (Req 20.2); and fires belonging to jobs that are not sub-hourly are always retained (Req 20.3).
/// </summary>
public class SubHourlyProperties
{
    /// <summary>The number of one-minute offsets inside a seven-day window.</summary>
    private const int WindowMinutes = 7 * 24 * 60;

    /// <summary>The number of distinct clock-hour offsets inside a seven-day window.</summary>
    private const int WindowHours = 7 * 24;

    /// <summary>
    /// Candidate queue labels (the queue is incidental to sub-hourly classification but is varied so
    /// fires across jobs are realistic).
    /// </summary>
    private static readonly string[] Queues = { "alpha", "bravo", "charlie", "default" };

    /// <summary>
    /// Representative viewer time zones: UTC, fixed offsets (including half-hour and +13), and any
    /// real DST zones that resolve on this host via the project's cross-platform resolver. The
    /// reference model uses the same <see cref="HeatmapTime.GetBucket"/>, so DST folds (which can
    /// collapse two distinct UTC hours into one local bucket) are handled consistently.
    /// </summary>
    private static readonly TimeZoneInfo[] TimeZones = BuildTimeZones();

    private static TimeZoneInfo[] BuildTimeZones()
    {
        var zones = new List<TimeZoneInfo>
        {
            TimeZoneInfo.Utc,
            TimeZoneInfo.CreateCustomTimeZone("Test+05:30", new TimeSpan(5, 30, 0), "Test +05:30", "Test +05:30"),
            TimeZoneInfo.CreateCustomTimeZone("Test-08:00", new TimeSpan(-8, 0, 0), "Test -08:00", "Test -08:00"),
            TimeZoneInfo.CreateCustomTimeZone("Test+13:00", new TimeSpan(13, 0, 0), "Test +13:00", "Test +13:00"),
        };

        foreach (var id in new[] { "America/New_York", "Europe/London", "Australia/Sydney" })
        {
            if (HeatmapTime.TryResolveTimeZone(id, out var tz) && !zones.Contains(tz))
            {
                zones.Add(tz);
            }
        }

        return zones.ToArray();
    }

    private static Gen<TimeZoneInfo> TimeZoneGen => Gen.Elements(TimeZones);

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>Base "now" UTC instants spread across ~30 years at one-minute resolution.</summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 16_000_000)
            .Select(minutes => new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    /// <summary>
    /// A single job's fire layout. <c>Dense</c> jobs place every fire inside one randomly chosen
    /// clock hour of the window (so a fire count &gt; 1 deliberately yields a sub-hourly job under
    /// the UTC bucketing), while non-dense jobs scatter their fires across the whole window. This
    /// guarantees the generated inputs exercise both sub-hourly and non-sub-hourly jobs.
    /// </summary>
    private static Gen<(string Queue, bool Dense, int FireCount, int HourStart, int[] Offsets)> JobGen =>
        from queue in Gen.Elements(Queues)
        from dense in Gen.Elements(true, false)
        from fireCount in Gen.Choose(0, 6)
        from hourStart in Gen.Choose(0, WindowHours - 1)
        from scatter in Gen.ArrayOf(fireCount, Gen.Choose(0, WindowMinutes - 1))
        from withinHour in Gen.ArrayOf(fireCount, Gen.Choose(0, 59))
        let offsets = dense
            ? withinHour.Select(m => hourStart * 60 + m).ToArray()
            : scatter
        select (queue, dense, fireCount, hourStart, offsets);

    /// <summary>
    /// **Property 28: Sub-hourly detection and filtering are consistent**
    /// **Validates: Requirements 20.1, 20.2, 20.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property SubHourly_DetectionAndFiltering_AreConsistent()
    {
        var arb = Arb.From(
            from baseNow in BaseNowGen
            from kind in KindGen
            from tz in TimeZoneGen
            from jobCount in Gen.Choose(0, 6)
            from jobs in Gen.ArrayOf(jobCount, JobGen)
            select (baseNow, kind, tz, jobs));

        return Prop.ForAll(arb, input =>
        {
            var (baseNow, kind, tz, jobs) = input;

            // Build the window in UTC so the per-job offsets land inside [start, end); the viewer
            // time zone only affects bucket assignment, which is exactly what we want to test.
            var window = HeatmapTime.BuildWindow(kind, baseNow, TimeZoneInfo.Utc);

            // Flatten all jobs into a single fire list, preserving a stable, interleaving-free order
            // (job 0's fires, then job 1's, ...). Each job gets a distinct id.
            var fires = new List<ProjectedFire>();
            for (var j = 0; j < jobs.Length; j++)
            {
                var jobId = $"job-{j}";
                var (queue, _, _, _, offsets) = jobs[j];
                foreach (var offset in offsets)
                {
                    fires.Add(new ProjectedFire(
                        JobId: jobId,
                        Queue: queue,
                        FireTimeUtc: window.StartInclusive.AddMinutes(offset),
                        EstimatedDuration: TimeSpan.FromMinutes(1)));
                }
            }

            // Independent reference: count, per (jobId, dayIndex, hour) bucket, how many fires the job
            // contributes using the same GetBucket oracle. A job is sub-hourly iff any bucket holds > 1.
            var perBucketCounts = new Dictionary<(string JobId, int DayIndex, int Hour), int>();
            foreach (var fire in fires)
            {
                var (dayIndex, hour) = HeatmapTime.GetBucket(fire.FireTimeUtc, tz, window);
                var key = (fire.JobId, dayIndex, hour);
                perBucketCounts.TryGetValue(key, out var c);
                perBucketCounts[key] = c + 1;
            }

            var expectedSubHourly = perBucketCounts
                .Where(kvp => kvp.Value > 1)
                .Select(kvp => kvp.Key.JobId)
                .ToHashSet(StringComparer.Ordinal);

            var detected = SubHourly.DetectSubHourlyJobIds(fires, tz, window);
            var detectedSet = detected.ToHashSet(StringComparer.Ordinal);

            // Req 20.1: detection equals the set of jobs with any (day, hour) bucket count > 1.
            if (!detectedSet.SetEquals(expectedSubHourly))
            {
                return false.Label(
                    $"detection mismatch: detected={{{string.Join(",", detectedSet.OrderBy(x => x, StringComparer.Ordinal))}}} " +
                    $"expected={{{string.Join(",", expectedSubHourly.OrderBy(x => x, StringComparer.Ordinal))}}} " +
                    $"(fires={fires.Count}, tz={tz.Id})");
            }

            var filtered = SubHourly.Filter(fires, tz, window);

            // Req 20.2 + 20.3: the filter keeps exactly the fires of non-sub-hourly jobs, in original
            // relative order, and drops exactly the sub-hourly fires (and only those).
            var expectedRetained = fires
                .Where(f => !expectedSubHourly.Contains(f.JobId))
                .ToList();

            if (!filtered.SequenceEqual(expectedRetained))
            {
                return false.Label(
                    $"filter mismatch: kept={filtered.Count} expected={expectedRetained.Count} " +
                    $"(subHourly={{{string.Join(",", expectedSubHourly.OrderBy(x => x, StringComparer.Ordinal))}}}, tz={tz.Id})");
            }

            // Strengthen 20.2: no retained fire belongs to a sub-hourly job; and no sub-hourly fire
            // survives. (Implied by the exact equality above, but asserted explicitly for clarity.)
            var keptHasSubHourly = filtered.Any(f => expectedSubHourly.Contains(f.JobId));
            var droppedNonSubHourly = expectedRetained.Count != filtered.Count;

            return (!keptHasSubHourly)
                .Label("a sub-hourly fire survived the filter")
                .And((!droppedNonSubHourly).Label("a non-sub-hourly fire was dropped by the filter"));
        });
    }
}
