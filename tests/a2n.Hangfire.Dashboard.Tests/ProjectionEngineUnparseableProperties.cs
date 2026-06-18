using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Internal;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="ProjectionEngine.Project"/> graceful per-job degradation when a
/// recurring job carries a cron expression Cronos cannot parse.
///
/// **Property 2: Unparseable crons are excluded without affecting other jobs**
/// **Validates: Requirements 1.6**
///
/// For any mix of parseable and unparseable cron specs, the projection contains exactly the fires
/// of the parseable subset (identical to projecting that subset alone) and records exactly the
/// unparseable job ids in the exclusion notice.
/// </summary>
public class ProjectionEngineUnparseableProperties
{
    /// <summary>
    /// Representative valid 5-field cron expressions producing a moderate, well-bounded number of
    /// in-window fires (every-minute / every-5-minute are deliberately excluded to keep the 100+
    /// iteration run fast while still exercising hourly, daily, weekly, and monthly cadences).
    /// </summary>
    private static readonly string[] ValidCrons =
    [
        "0 * * * *",        // top of every hour
        "0 0 * * *",        // daily at midnight
        "30 9 * * *",       // daily at 09:30
        "0 12 * * 1",       // noon on Mondays
        "15 14 1 * *",      // 14:15 on the 1st of each month
        "0 22 * * 1-5",     // 22:00 on weekdays
        "0 0 * * 0",        // Sundays at midnight
    ];

    /// <summary>Clearly-invalid cron expressions that Cronos cannot parse.</summary>
    private static readonly string[] InvalidCrons =
    [
        "not a cron",
        "99 99 99",
        "60 24 * * *",          // minute 60 / hour 24 are out of range
        "foo bar baz qux quux", // five tokens, none valid
        "* * *",                // too few fields
        "abc",
        "70 * * * *",           // minute 70 out of range
    ];

    /// <summary>Time-zone identifiers attached to jobs (null/empty means UTC per Req 1.4/8.3).</summary>
    private static readonly string[] TimeZoneIds = [null, "", "America/New_York", "Australia/Sydney"];

    /// <summary>A single valid 7-day window the projection is evaluated over.</summary>
    private static readonly ProjectionWindow Window = HeatmapTime.BuildWindow(
        ProjectionWindowKind.IdealizedWeek,
        new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc);

    /// <summary>A single job's generated shape: whether its cron is valid plus the cron string and tz id.</summary>
    private static Gen<(bool IsValid, string Cron, string TimeZoneId)> JobShapeGen =>
        from tz in Gen.Elements(TimeZoneIds)
        from shape in Gen.OneOf(
            Gen.Elements(ValidCrons).Select(c => (true, c)),
            Gen.Elements(InvalidCrons).Select(c => (false, c)))
        select (shape.Item1, shape.Item2, tz);

    /// <summary>
    /// Guards the oracle: every expression in <see cref="ValidCrons"/> must parse and every
    /// expression in <see cref="InvalidCrons"/> must fail to parse under the same parser the engine
    /// uses, otherwise the partition the property relies on would be meaningless.
    /// </summary>
    [Fact]
    public void CronCorpus_PartitionsCleanlyIntoParseableAndUnparseable()
    {
        Assert.All(ValidCrons, c => Assert.True(CronPreview.TryParse(c, out _), $"expected parseable: '{c}'"));
        Assert.All(InvalidCrons, c => Assert.False(CronPreview.TryParse(c, out _), $"expected unparseable: '{c}'"));
    }

    /// <summary>
    /// **Property 2: Unparseable crons are excluded without affecting other jobs**
    /// **Validates: Requirements 1.6**
    ///
    /// For any mix of parseable and unparseable jobs, projecting the full set yields fires identical
    /// to projecting only the parseable subset, and the exclusion notice contains exactly the ids of
    /// the unparseable jobs.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property UnparseableJobs_ExcludedWithoutAffectingOthers()
    {
        var arb = Arb.From(Gen.ListOf(JobShapeGen));

        return Prop.ForAll(arb, shapes =>
        {
            // Assign each job a unique id so the exclusion-notice comparison is unambiguous.
            var jobs = shapes
                .Select((s, i) => new RecurringJobSpec(
                    JobId: $"job-{i}",
                    CronExpression: s.Cron,
                    TimeZoneId: s.TimeZoneId,
                    Queue: $"queue-{i % 3}",
                    EstimatedDuration: TimeSpan.FromMinutes(5),
                    EstimatedDurationIsDefault: false))
                .ToList();

            var parseableSubset = jobs.Where((_, i) => shapes[i].IsValid).ToList();
            var expectedUnparseableIds = jobs
                .Where((_, i) => !shapes[i].IsValid)
                .Select(j => j.JobId)
                .ToList();

            var full = ProjectionEngine.Project(jobs, Window);
            var subsetOnly = ProjectionEngine.Project(parseableSubset, Window);

            // The full projection's fires are identical (and identically ordered, since the parseable
            // jobs retain their relative order) to projecting the parseable subset alone.
            var firesMatch = full.Fires.SequenceEqual(subsetOnly.Fires);

            // The exclusion notice records exactly the unparseable job ids (set equality).
            var noticeMatches =
                full.UnparseableJobIds.Count == expectedUnparseableIds.Count &&
                new HashSet<string>(full.UnparseableJobIds).SetEquals(expectedUnparseableIds);

            // No fire ever originates from an excluded job.
            var noFiresFromExcluded = !full.Fires.Any(f => expectedUnparseableIds.Contains(f.JobId));

            return (firesMatch && noticeMatches && noFiresFromExcluded).Label(
                $"jobs={jobs.Count}, unparseable={expectedUnparseableIds.Count}: " +
                $"firesMatch={firesMatch} (full={full.Fires.Count}, subset={subsetOnly.Fires.Count}), " +
                $"noticeMatches={noticeMatches} (notice=[{string.Join(",", full.UnparseableJobIds)}], " +
                $"expected=[{string.Join(",", expectedUnparseableIds)}]), " +
                $"noFiresFromExcluded={noFiresFromExcluded}");
        });
    }
}
