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
/// Property tests for <see cref="ProjectionEngine.Project"/> long-period-job detection.
///
/// **Property 4: Long-period jobs are detected regardless of the selected window**
/// **Validates: Requirements 9.5, 9.6**
///
/// For any set of jobs, a job whose recurrence period exceeds seven days appears in the
/// long-period-jobs list for both the Idealized-week and Next-7-days windows, and a job whose
/// period is at most seven days never appears in that list.
/// </summary>
public class ProjectionEngineLongPeriodProperties
{
    /// <summary>
    /// Catalog of cron expressions paired with whether their recurrence period exceeds seven days.
    /// Short-period crons fire repeatedly with a consecutive-fire gap of at most seven days (weekly
    /// is the exact boundary at seven days, which is NOT long-period). Long-period crons (monthly,
    /// yearly, every-two-months) always have a consecutive-fire gap strictly greater than seven days.
    /// </summary>
    private static readonly (string Cron, bool IsLong)[] CronCatalog =
    {
        ("0 * * * *", false),    // hourly      — gap 1 hour
        ("0 0 * * *", false),    // daily       — gap 1 day
        ("0 0 */2 * *", false),  // every 2 days — gap <= 3 days
        ("0 0 */3 * *", false),  // every 3 days — gap <= 3 days
        ("0 0 * * 1", false),    // weekly      — gap exactly 7 days (boundary, not long)
        ("0 0 1 * *", true),     // monthly     — gap 28..31 days
        ("0 0 1 1 *", true),     // yearly      — gap ~365 days
        ("0 0 1 */2 *", true),   // every 2 months — gap ~59 days
    };

    /// <summary>
    /// Representative viewer time zones used only to anchor the window construction: UTC, fixed
    /// offsets (including a half-hour and a +13 offset past the date line), and any real DST zones
    /// the host can resolve through the project's own cross-platform resolver.
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

    /// <summary>
    /// Base "now" UTC instants spread across ~30 years at one-minute resolution so the anchored
    /// window start (the Monday of the local week, or the local current date) varies across every
    /// weekday, month, and DST transition.
    /// </summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 16_000_000)
            .Select(minutes =>
                new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    /// <summary>A non-empty list of indices into <see cref="CronCatalog"/>.</summary>
    private static Gen<List<int>> CatalogIndicesGen =>
        from n in Gen.Choose(1, 6)
        from indices in Gen.ListOf(n, Gen.Choose(0, CronCatalog.Length - 1))
        select indices.ToList();

    /// <summary>
    /// **Property 4: Long-period jobs are detected regardless of the selected window**
    /// **Validates: Requirements 9.5, 9.6**
    ///
    /// For any mix of short- and long-period recurring jobs, projecting over both the Idealized-week
    /// and Next-7-days windows records exactly the long-period jobs (period &gt; 7 days) in the
    /// long-period-jobs list and never records a short-period job (period &lt;= 7 days).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property LongPeriodJobs_DetectedRegardlessOfWindow()
    {
        var arb = Arb.From(
            from indices in CatalogIndicesGen
            from now in BaseNowGen
            from tz in TimeZoneGen
            select (indices, now, tz));

        return Prop.ForAll(arb, input =>
        {
            var (indices, now, tz) = input;

            // Build one spec per drawn catalog entry, each with a unique job id and remembering
            // whether it is expected to be classified as long-period. Jobs are evaluated in UTC so
            // their recurrence period is independent of the viewer time zone used for the window.
            var specs = indices
                .Select((catalogIndex, position) =>
                {
                    var (cron, isLong) = CronCatalog[catalogIndex];
                    var spec = new RecurringJobSpec(
                        JobId: $"job-{position}",
                        CronExpression: cron,
                        TimeZoneId: "UTC",
                        Queue: "default",
                        EstimatedDuration: TimeSpan.FromMinutes(1),
                        EstimatedDurationIsDefault: true);
                    return (Spec: spec, ExpectedLong: isLong);
                })
                .ToList();

            var jobs = specs.Select(s => s.Spec).ToList();

            // The classification must hold identically for both window kinds (Req 9.5, 9.6).
            foreach (var kind in new[] { ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days })
            {
                var window = HeatmapTime.BuildWindow(kind, now, tz);
                var result = ProjectionEngine.Project(jobs, window);
                var longPeriod = new HashSet<string>(result.LongPeriodJobIds);

                foreach (var (spec, expectedLong) in specs)
                {
                    var present = longPeriod.Contains(spec.JobId);

                    if (expectedLong && !present)
                    {
                        return false.Label(
                            $"long-period job not detected: id={spec.JobId} cron='{spec.CronExpression}' " +
                            $"kind={kind} tz={tz.Id}");
                    }

                    if (!expectedLong && present)
                    {
                        return false.Label(
                            $"short-period job wrongly flagged long: id={spec.JobId} cron='{spec.CronExpression}' " +
                            $"kind={kind} tz={tz.Id}");
                    }
                }
            }

            return true.ToProperty();
        });
    }
}
