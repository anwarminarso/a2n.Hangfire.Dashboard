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
/// Property tests for <see cref="ScheduleAggregator.Aggregate"/> viewer-time-zone bucketing.
///
/// **Property 7: Fires are bucketed by their viewer-time-zone local time**
/// **Validates: Requirements 8.2, 8.4, 8.5**
///
/// For any fire and any viewer time zone (UTC when none is selected), the fire is assigned to the
/// calendar day and clock hour of its time-zone-converted local time — including when conversion
/// moves it across a day boundary — and never to the day or hour of its pre-conversion instant.
/// </summary>
public class ViewerTimeZoneBucketingProperties
{
    /// <summary>The fixed number of days every projection window spans.</summary>
    private const int WindowDays = 7;

    /// <summary>The number of minutes in a seven-day window (exclusive upper bound for fire offsets).</summary>
    private const int WindowMinutes = WindowDays * 24 * 60;

    /// <summary>
    /// Viewer time zones under test. A <c>null</c> entry models "no viewer time zone selected", which
    /// the aggregator treats as UTC (Req 8.5). The non-zero fixed offsets (including a half-hour and a
    /// past-the-date-line +13 offset) guarantee that conversion shifts the clock hour and, near
    /// midnight, the calendar day (Req 8.4). Real DST zones are added when the host can resolve them.
    /// </summary>
    private static readonly TimeZoneInfo[] ViewerZones = BuildViewerZones();

    private static TimeZoneInfo[] BuildViewerZones()
    {
        var zones = new List<TimeZoneInfo>
        {
            null,                 // "no viewer selected" => UTC (Req 8.5)
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

    private static Gen<TimeZoneInfo> ViewerZoneGen => Gen.Elements(ViewerZones);

    /// <summary>A handful of distinct, non-whitespace queue names (avoids the default-queue rule).</summary>
    private static Gen<string> QueueGen => Gen.Elements("alpha", "queue-1", "Reports", "zeta");

    /// <summary>
    /// Base "now" UTC instants spread across ~16 years at one-minute resolution so the window anchor
    /// varies across every weekday, month, and DST transition.
    /// </summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 8_000_000)
            .Select(minutes => new DateTimeOffset(2012, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>
    /// Offset (in minutes from the window start) of the single fire. Reduced modulo the window's
    /// actual span before use: seven local days is 168 hours only when no DST transition falls inside
    /// the window, so a fixed <see cref="WindowMinutes"/> bound would place fires outside it.
    /// </summary>
    private static Gen<int> FireOffsetMinutesGen => Gen.Choose(0, WindowMinutes - 1);

    /// <summary>
    /// **Property 7: Fires are bucketed by their viewer-time-zone local time**
    /// **Validates: Requirements 8.2, 8.4, 8.5**
    ///
    /// A single fire is aggregated under the Fire-count metric so the matrix has exactly one
    /// populated cell, making the bucket assignment precise. The expected <c>(dayIndex, hour)</c> is
    /// computed directly from the fire's viewer-time-zone-converted local time (the oracle). The test
    /// asserts the populated cell carries that expected day and hour, and — whenever conversion moves
    /// the fire to a different day or hour than its pre-conversion UTC instant — that the cell is NOT
    /// placed at the pre-conversion UTC day or hour.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Fire_IsBucketedByViewerLocalTime()
    {
        var arb = Arb.From(
            from now in BaseNowGen
            from kind in KindGen
            from viewer in ViewerZoneGen
            from queue in QueueGen
            from offsetMinutes in FireOffsetMinutesGen
            select (now, kind, viewer, queue, offsetMinutes));

        return Prop.ForAll(arb, input =>
        {
            var (now, kind, viewer, queue, offsetMinutes) = input;
            var effectiveZone = viewer ?? TimeZoneInfo.Utc;

            // Build the window in the viewer zone, then place a single absolute fire instant inside
            // the half-open window and normalize it to UTC (as ProjectedFire.FireTimeUtc is stored).
            var window = HeatmapTime.BuildWindow(kind, now, effectiveZone);

            // The window spans seven local days, which is 167, 168 or 169 hours depending on whether
            // a DST transition falls inside it, so the offset is reduced modulo the real span to keep
            // the fire in the half-open interval the property is about.
            var spanMinutes = (int)(window.EndExclusive - window.StartInclusive).TotalMinutes;
            var fireInstantUtc = window.StartInclusive
                .AddMinutes(offsetMinutes % spanMinutes)
                .ToUniversalTime();

            var fire = new ProjectedFire(
                JobId: "job-1",
                Queue: queue,
                FireTimeUtc: fireInstantUtc,
                EstimatedDuration: TimeSpan.FromMinutes(1));

            var matrix = ScheduleAggregator.Aggregate(
                new[] { fire }, LoadMetric.FireCount, viewer, window);

            // --- Oracle: the bucket computed directly from the viewer-local converted time. ---
            var viewerLocal = TimeZoneInfo.ConvertTime(fireInstantUtc, effectiveZone);
            var windowStartLocal = TimeZoneInfo.ConvertTime(window.StartInclusive, effectiveZone);
            var expectedDay = (int)(viewerLocal.Date - windowStartLocal.Date).TotalDays;
            var expectedHour = viewerLocal.Hour;

            // --- Pre-conversion (UTC) instant: the day/hour the fire must NEVER be placed at when
            //     conversion changes them (Req 8.4). ---
            var utcLocal = fireInstantUtc.UtcDateTime;
            var windowStartUtc = window.StartInclusive.UtcDateTime;
            var utcDay = (int)(utcLocal.Date - windowStartUtc.Date).TotalDays;
            var utcHour = utcLocal.Hour;

            // Exactly one populated cell for a single fire.
            if (matrix.Cells.Count != 1)
            {
                return false.Label(
                    $"expected exactly 1 cell but got {matrix.Cells.Count} (tz={Describe(viewer)})");
            }

            var key = matrix.Cells.Keys.Single();

            if (key.Queue != queue)
            {
                return false.Label($"queue: expected '{queue}' but got '{key.Queue}'");
            }

            // The fire is bucketed at the viewer-local day and hour (Req 8.2, 8.4, 8.5).
            if (key.DayIndex != expectedDay || key.Hour != expectedHour)
            {
                return false.Label(
                    $"bucket mismatch: expected (day={expectedDay}, hour={expectedHour}) but got " +
                    $"(day={key.DayIndex}, hour={key.Hour}); fireUtc={fireInstantUtc:o} " +
                    $"tz={Describe(viewer)} localHour={expectedHour} utcHour={utcHour}");
            }

            // The day index for an in-window fire is always within [0, 6].
            if (key.DayIndex < 0 || key.DayIndex > WindowDays - 1)
            {
                return false.Label($"day index out of range: {key.DayIndex} (tz={Describe(viewer)})");
            }

            // When conversion changes the hour, the cell must NOT sit at the pre-conversion UTC hour.
            if (utcHour != expectedHour && key.Hour == utcHour)
            {
                return false.Label(
                    $"placed at pre-conversion UTC hour {utcHour} instead of local hour {expectedHour} " +
                    $"(tz={Describe(viewer)})");
            }

            // When conversion crosses a day boundary, the cell must NOT sit at the pre-conversion day.
            if (utcDay != expectedDay && key.DayIndex == utcDay)
            {
                return false.Label(
                    $"placed at pre-conversion UTC day {utcDay} instead of local day {expectedDay} " +
                    $"(tz={Describe(viewer)})");
            }

            return true.ToProperty();
        });
    }

    private static string Describe(TimeZoneInfo tz) => tz is null ? "<none/UTC>" : tz.Id;
}
