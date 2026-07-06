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
/// Property tests for <see cref="ProjectionEngine.Project"/> unknown-time-zone fallback behavior.
///
/// **Property 3: Unknown time-zone ids fall back to UTC and are recorded, leaving others unchanged**
/// **Validates: Requirements 8.6**
///
/// For any mix of jobs with valid and unrecognized time-zone ids, each job with an unrecognized id
/// is evaluated in UTC and its id is recorded in the unknown-time-zone notice, while every job with
/// a valid id produces fires identical to projecting it in isolation. A job with no configured time
/// zone (null/empty) evaluates in UTC but is NOT recorded as an unknown time zone (Req 1.4, 8.3).
/// </summary>
public class ProjectionEngineUnknownTimeZoneProperties
{
    /// <summary>
    /// Time-zone ids that are genuinely unrecognized on every platform and therefore must fall back
    /// to UTC and be recorded in the unknown-time-zone notice (Req 8.6). Each entry is asserted at
    /// type-init time to be unresolvable through the project's own cross-platform resolver, so the
    /// test never accidentally classifies a real zone as "unknown".
    /// </summary>
    private static readonly string[] UnknownTimeZoneIds = BuildUnknownTimeZoneIds();

    /// <summary>
    /// Time-zone ids that resolve through <see cref="HeatmapTime.TryResolveTimeZone"/> on this host.
    /// Built cross-platform from a pool of common IANA/Windows ids plus UTC; only the ones that
    /// actually resolve are kept, so the generator is identical in meaning on Linux and Windows.
    /// </summary>
    private static readonly string[] ValidTimeZoneIds = BuildValidTimeZoneIds();

    /// <summary>
    /// Valid 5-field cron expressions exercising a range of cadences, all of which fire one or more
    /// times within a seven-day window so the fallback comparison covers non-empty fire sets.
    /// </summary>
    private static readonly string[] ValidCronExpressions =
    [
        "0 * * * *",        // top of every hour
        "*/30 * * * *",     // every 30 minutes
        "0 0 * * *",        // daily at midnight
        "30 9 * * *",       // daily at 09:30
        "0 12 * * 1",       // noon on Mondays
        "0 22 * * 1-5",     // 22:00 on weekdays
        "15 14 * * 0",      // 14:15 on Sundays
    ];

    private static string[] BuildUnknownTimeZoneIds()
    {
        var candidates = new[] { "Mars/Phobos", "Not/AZone", "Foo/Bar", "Invalid/Zone", "Nowhere/Land" };
        var unknown = candidates
            .Where(id => !HeatmapTime.TryResolveTimeZone(id, out _))
            .ToArray();

        // Guard: the suite is meaningless if none of the chosen ids are actually unrecognized.
        Assert.NotEmpty(unknown);
        return unknown;
    }

    private static string[] BuildValidTimeZoneIds()
    {
        var pool = new[]
        {
            "UTC",
            "America/New_York",
            "Europe/London",
            "Australia/Sydney",
            "Asia/Kolkata",
            "America/Los_Angeles",
        };

        var valid = pool
            .Where(id => HeatmapTime.TryResolveTimeZone(id, out _))
            .ToArray();

        // UTC always resolves, so the set is never empty on any host.
        Assert.NotEmpty(valid);
        return valid;
    }

    /// <summary>The category a generated job's time-zone id belongs to.</summary>
    private enum TimeZoneKind
    {
        Valid,
        Unknown,
        NullOrEmpty
    }

    private sealed record JobTemplate(string Cron, TimeZoneKind Kind, string TimeZoneId);

    private static Gen<JobTemplate> JobTemplateGen =>
        from cron in Gen.Elements(ValidCronExpressions)
        from template in Gen.OneOf(
            from id in Gen.Elements(ValidTimeZoneIds)
            select new JobTemplate(cron, TimeZoneKind.Valid, id),
            from id in Gen.Elements(UnknownTimeZoneIds)
            select new JobTemplate(cron, TimeZoneKind.Unknown, id),
            from id in Gen.Elements<string>(null, string.Empty, "   ")
            select new JobTemplate(cron, TimeZoneKind.NullOrEmpty, id))
        select template;

    /// <summary>Base "now" instants spread across ~20 years at one-minute resolution.</summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 10_000_000)
            .Select(minutes => new DateTimeOffset(2012, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>
    /// **Property 3: Unknown time-zone ids fall back to UTC and are recorded, leaving others unchanged**
    /// **Validates: Requirements 8.6**
    ///
    /// For any mix of jobs (each with a valid, unrecognized, or absent time-zone id) and any valid
    /// window, projecting them together yields: (a) every unrecognized-tz job recorded in
    /// <see cref="ProjectionResult.UnknownTimeZoneJobIds"/> with fires identical to evaluating that
    /// job in UTC; (b) every valid-tz job's fires identical to projecting it in isolation and never
    /// recorded as unknown; (c) jobs with no time zone evaluated in UTC but never recorded as
    /// unknown.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property UnknownTimeZones_FallBackToUtc_AndAreRecorded()
    {
        var arb = Arb.From(
            from templates in Gen.NonEmptyListOf(JobTemplateGen)
            from now in BaseNowGen
            from kind in KindGen
            select (templates, now, kind));

        return Prop.ForAll(arb, input =>
        {
            var (templates, now, kind) = input;

            // Build the window in UTC; projection honors each job's own zone, not the viewer's.
            var window = HeatmapTime.BuildWindow(kind, now, TimeZoneInfo.Utc);

            // Assign every job a unique id so combined fires can be partitioned per job.
            var jobs = templates
                .Select((t, i) => new RecurringJobSpec(
                    JobId: $"job-{i}",
                    CronExpression: t.Cron,
                    TimeZoneId: t.TimeZoneId,
                    Queue: "default",
                    EstimatedDuration: TimeSpan.FromMinutes(1),
                    EstimatedDurationIsDefault: true))
                .ToList();

            var kindByJobId = jobs
                .Zip(templates, (job, template) => (job.JobId, template.Kind))
                .ToDictionary(x => x.JobId, x => x.Kind);

            var combined = ProjectionEngine.Project(jobs, window);
            var unknownSet = combined.UnknownTimeZoneJobIds.ToHashSet();

            // Fires partitioned by originating job (combined fires for a job are contiguous and in
            // occurrence order, so a stable filter preserves that order).
            var combinedByJob = jobs.ToDictionary(
                j => j.JobId,
                j => combined.Fires.Where(f => f.JobId == j.JobId).ToList());

            foreach (var job in jobs)
            {
                var jobKind = kindByJobId[job.JobId];

                // (1) Each job's fires match projecting that same job in isolation, regardless of
                // its tz category — the engine never lets one job's tz handling perturb another's.
                var isolated = ProjectionEngine.Project(new[] { job }, window).Fires.ToList();
                var actual = combinedByJob[job.JobId];
                if (!actual.SequenceEqual(isolated))
                {
                    return false.Label(
                        $"job '{job.JobId}' ({jobKind}, tz='{job.TimeZoneId ?? "<null>"}'): combined " +
                        $"fires ({actual.Count}) != isolated fires ({isolated.Count})");
                }

                if (jobKind == TimeZoneKind.Unknown)
                {
                    // (2) Unrecognized-tz jobs are recorded in the unknown notice (Req 8.6).
                    if (!unknownSet.Contains(job.JobId))
                    {
                        return false.Label(
                            $"unknown-tz job '{job.JobId}' (tz='{job.TimeZoneId}') not recorded in " +
                            "UnknownTimeZoneJobIds");
                    }

                    // (3) The unrecognized-tz job is evaluated in UTC: its fires equal those of the
                    // same job projected with an explicit UTC zone.
                    var utcJob = job with { TimeZoneId = "UTC" };
                    var utcFires = ProjectionEngine.Project(new[] { utcJob }, window).Fires
                        .Select(f => f with { JobId = job.JobId })
                        .ToList();
                    if (!actual.SequenceEqual(utcFires))
                    {
                        return false.Label(
                            $"unknown-tz job '{job.JobId}' (tz='{job.TimeZoneId}') not evaluated in UTC: " +
                            $"{actual.Count} fires != {utcFires.Count} UTC fires");
                    }
                }
                else
                {
                    // (4) Valid-tz and no-tz jobs are never recorded as unknown (Req 1.4, 8.3, 8.6).
                    if (unknownSet.Contains(job.JobId))
                    {
                        return false.Label(
                            $"{jobKind} job '{job.JobId}' (tz='{job.TimeZoneId ?? "<null>"}') wrongly " +
                            "recorded in UnknownTimeZoneJobIds");
                    }
                }
            }

            // (5) The unknown notice contains exactly the unknown-tz jobs and nothing else.
            var expectedUnknown = jobs
                .Where(j => kindByJobId[j.JobId] == TimeZoneKind.Unknown)
                .Select(j => j.JobId)
                .ToHashSet();
            if (!unknownSet.SetEquals(expectedUnknown))
            {
                return false.Label(
                    $"UnknownTimeZoneJobIds {{{string.Join(",", unknownSet)}}} != expected " +
                    $"{{{string.Join(",", expectedUnknown)}}}");
            }

            return true.ToProperty();
        });
    }
}
