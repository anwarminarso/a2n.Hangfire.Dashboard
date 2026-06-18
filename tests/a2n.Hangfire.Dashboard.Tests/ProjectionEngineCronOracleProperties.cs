using System;
using System.Collections.Generic;
using System.Linq;
using Cronos;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="ProjectionEngine.Project"/> against a direct Cronos oracle.
///
/// **Property 1: Projected fires match the cron oracle in the job's time zone and stay in-window**
/// **Validates: Requirements 1.1, 1.3, 1.4, 8.1, 8.3**
///
/// For any set of recurring job specs (each with a valid cron expression and an optional time-zone
/// id) and any valid projection window, every projected fire falls within the window's
/// <c>[StartInclusive, EndExclusive)</c> interval and equals the occurrence computed directly by
/// Cronos for that job's configured time zone — using UTC when the job has no time zone (Req 1.4,
/// 8.3) — and the set of fires equals the union over jobs of their in-window Cronos occurrences
/// (Req 1.1, 1.3, 8.1).
/// </summary>
public class ProjectionEngineCronOracleProperties
{
    /// <summary>
    /// A pairing of the time-zone id placed on a <see cref="RecurringJobSpec"/> and the
    /// <see cref="TimeZoneInfo"/> the schedule is expected to be evaluated in. Both the engine and
    /// the oracle resolve the id through the same shared mechanism, so the pair is consistent; this
    /// test isolates the Cronos-occurrence property rather than re-testing time-zone resolution.
    /// </summary>
    private sealed record TzChoice(string Id, TimeZoneInfo Zone);

    /// <summary>
    /// Representative, valid 5-field cron expressions mixing hourly, daily, and weekly cadences.
    /// All are parsed with <see cref="CronFormat.Standard"/> by both the engine and the oracle.
    /// </summary>
    private static readonly string[] ValidCronExpressions =
    [
        "0 * * * *",        // hourly, top of the hour
        "30 * * * *",       // hourly, half past
        "0,30 * * * *",     // twice hourly
        "0 0 * * *",        // daily at midnight
        "30 9 * * *",       // daily at 09:30
        "0 12 * * *",       // daily at noon
        "0 0 * * 1",        // weekly: Monday midnight
        "0 9 * * 1-5",      // weekdays at 09:00
        "0 22 * * 0",       // weekly: Sunday 22:00
        "15 6 * * 6",       // weekly: Saturday 06:15
    ];

    /// <summary>
    /// Time-zone choices exercised by the property: the UTC fallbacks (null/empty id and an explicit
    /// "UTC"), plus any real IANA zones that resolve on this host (cross-platform via the project's
    /// own resolver). Each non-trivial id is paired with the exact zone it resolves to.
    /// </summary>
    private static readonly TzChoice[] TimeZoneChoices = BuildTimeZoneChoices();

    private static TzChoice[] BuildTimeZoneChoices()
    {
        var choices = new List<TzChoice>
        {
            new(null, TimeZoneInfo.Utc),   // no configured zone => UTC (Req 1.4, 8.3)
            new(string.Empty, TimeZoneInfo.Utc),
            new("UTC", TimeZoneInfo.Utc),
        };

        foreach (var id in new[] { "America/New_York", "Europe/London", "Asia/Kolkata", "Australia/Sydney" })
        {
            if (HeatmapTime.TryResolveTimeZone(id, out var zone))
            {
                choices.Add(new TzChoice(id, zone));
            }
        }

        return choices.ToArray();
    }

    private static Gen<string> CronGen => Gen.Elements(ValidCronExpressions);

    private static Gen<TzChoice> TzGen => Gen.Elements(TimeZoneChoices);

    private static Gen<string> QueueGen => Gen.Elements("default", "critical", "emails", "reports");

    /// <summary>Base "now" UTC instants spread across ~30 years at one-minute resolution.</summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 16_000_000)
            .Select(minutes => new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>A single job descriptor: cron expression, time-zone choice, and queue.</summary>
    private static Gen<(string Cron, TzChoice Tz, string Queue)> JobDescGen =>
        from cron in CronGen
        from tz in TzGen
        from queue in QueueGen
        select (cron, tz, queue);

    /// <summary>
    /// **Property 1: Projected fires match the cron oracle in the job's time zone and stay in-window**
    /// **Validates: Requirements 1.1, 1.3, 1.4, 8.1, 8.3**
    ///
    /// The multiset of fires returned by <see cref="ProjectionEngine.Project"/> equals the union,
    /// over every job, of the in-window occurrences computed directly by Cronos in that job's
    /// configured time zone (UTC when none); every returned fire is normalized to UTC and lies in
    /// the window's half-open interval.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ProjectedFires_MatchCronOracle_InJobTimeZone_AndStayInWindow()
    {
        var arb = Arb.From(
            from baseNow in BaseNowGen
            from kind in KindGen
            from count in Gen.Choose(0, 5)
            from descs in Gen.ArrayOf(count, JobDescGen)
            select (baseNow, kind, descs));

        return Prop.ForAll(arb, input =>
        {
            var (baseNow, kind, descs) = input;

            // A valid 7-day window (duration within [1 hour, 366 days]) anchored on baseNow.
            var window = HeatmapTime.BuildWindow(kind, baseNow, TimeZoneInfo.Utc);

            // Assign stable, unique job ids so the union is unambiguous per job.
            var jobs = descs
                .Select((d, i) => new RecurringJobSpec(
                    JobId: $"job-{i}",
                    CronExpression: d.Cron,
                    TimeZoneId: d.Tz.Id,
                    Queue: d.Queue,
                    EstimatedDuration: TimeSpan.FromMinutes(1),
                    EstimatedDurationIsDefault: false))
                .ToList();

            var result = ProjectionEngine.Project(jobs, window);

            // No representative cron is unparseable, so nothing should be excluded (Req 1.1).
            if (result.UnparseableJobIds.Count != 0)
            {
                return false.Label(
                    $"unexpected unparseable jobs: {string.Join(",", result.UnparseableJobIds)}");
            }

            // Every returned fire must be UTC-normalized and inside the half-open window (Req 1.1).
            foreach (var fire in result.Fires)
            {
                if (fire.FireTimeUtc.Offset != TimeSpan.Zero)
                {
                    return false.Label($"fire not UTC-normalized: {fire.JobId} @ {fire.FireTimeUtc:o}");
                }

                if (fire.FireTimeUtc < window.StartInclusive || fire.FireTimeUtc >= window.EndExclusive)
                {
                    return false.Label(
                        $"fire out of window: {fire.JobId} @ {fire.FireTimeUtc:o} " +
                        $"not in [{window.StartInclusive:o}, {window.EndExclusive:o})");
                }
            }

            // Oracle: the union over all jobs of their in-window Cronos occurrences, evaluated in the
            // job's configured time zone (UTC when none) and normalized to UTC (Req 1.3, 1.4, 8.1, 8.3).
            var expected = new List<(string JobId, string Queue, long UtcTicks)>();
            foreach (var (desc, i) in descs.Select((d, i) => (d, i)))
            {
                var parsed = CronExpression.Parse(desc.Cron, CronFormat.Standard);
                foreach (var occurrence in parsed.GetOccurrences(
                             window.StartInclusive,
                             window.EndExclusive,
                             desc.Tz.Zone,
                             fromInclusive: true,
                             toInclusive: false))
                {
                    expected.Add(($"job-{i}", desc.Queue, occurrence.ToUniversalTime().UtcTicks));
                }
            }

            var actual = result.Fires
                .Select(f => (f.JobId, f.Queue, f.FireTimeUtc.UtcTicks))
                .ToList();

            // Compare as multisets (sorted), so order of jobs/fires is irrelevant.
            var expectedSorted = expected
                .OrderBy(e => e.JobId, StringComparer.Ordinal)
                .ThenBy(e => e.Queue, StringComparer.Ordinal)
                .ThenBy(e => e.UtcTicks)
                .ToList();
            var actualSorted = actual
                .OrderBy(a => a.JobId, StringComparer.Ordinal)
                .ThenBy(a => a.Queue, StringComparer.Ordinal)
                .ThenBy(a => a.UtcTicks)
                .ToList();

            if (actualSorted.Count != expectedSorted.Count)
            {
                return false.Label(
                    $"fire count mismatch: engine={actualSorted.Count} oracle={expectedSorted.Count} " +
                    $"(window [{window.StartInclusive:o},{window.EndExclusive:o}))");
            }

            for (var i = 0; i < expectedSorted.Count; i++)
            {
                if (!actualSorted[i].Equals(expectedSorted[i]))
                {
                    return false.Label(
                        $"fire mismatch at {i}: engine={actualSorted[i]} oracle={expectedSorted[i]}");
                }
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// **Property 1 (anchor): hand-checked example tying a daily job's UTC fires to its time zone.**
    /// **Validates: Requirements 1.3, 8.1**
    ///
    /// A "daily at midnight" job evaluated in a +05:30 zone fires at 18:30 UTC of the previous
    /// calendar day, demonstrating that the engine honors the configured time zone before
    /// normalizing to UTC rather than treating the cron as UTC.
    /// </summary>
    [Fact]
    public void DailyJob_HonorsConfiguredTimeZone_BeforeUtcNormalization()
    {
        if (!HeatmapTime.TryResolveTimeZone("Asia/Kolkata", out var kolkata))
        {
            return; // zone unavailable on this host; the property test covers the general case
        }

        // A fixed seven-day UTC window starting Monday 2024-03-04 00:00Z.
        var start = new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero);
        var window = new ProjectionWindow(start, start.AddDays(7), ProjectionWindowKind.IdealizedWeek);

        var job = new RecurringJobSpec(
            JobId: "daily-midnight",
            CronExpression: "0 0 * * *",
            TimeZoneId: "Asia/Kolkata",
            Queue: "default",
            EstimatedDuration: TimeSpan.FromMinutes(1),
            EstimatedDurationIsDefault: false);

        var result = ProjectionEngine.Project(new[] { job }, window);

        var expected = CronExpression.Parse("0 0 * * *", CronFormat.Standard)
            .GetOccurrences(window.StartInclusive, window.EndExclusive, kolkata, true, false)
            .Select(o => o.ToUniversalTime())
            .ToList();

        Assert.Equal(expected, result.Fires.Select(f => f.FireTimeUtc).ToList());

        // Midnight in +05:30 is 18:30 UTC the day before, so every fire's UTC time-of-day is 18:30.
        Assert.All(result.Fires, f => Assert.Equal(new TimeSpan(18, 30, 0), f.FireTimeUtc.TimeOfDay));
    }
}
