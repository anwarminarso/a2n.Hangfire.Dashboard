using System;
using System.Collections.Generic;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="HeatmapTime.BuildWindow"/> projection-window construction.
///
/// **Property 5: Projection window construction is well-formed**
/// **Validates: Requirements 9.2, 9.3**
///
/// For any base date, the Next-7-days window spans exactly seven calendar days starting at 00:00
/// of the local current date, and the Idealized-week window spans exactly seven days starting at
/// Monday 00:00; both windows have an inclusive start, an exclusive end, and a duration of seven
/// days.
/// </summary>
public class ProjectionWindowProperties
{
    /// <summary>The fixed number of days every projection window must span.</summary>
    private const int WindowDays = 7;

    /// <summary>
    /// Representative viewer time zones: UTC, fixed-offset zones (including a half-hour and a
    /// past-the-date-line +13 offset), and any real DST zones that resolve on this host (resolved
    /// through the project's own cross-platform resolver so the set is identical on Linux/Windows).
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

        // Include real DST zones when the host can resolve them, to exercise spring-forward /
        // fall-back dates. Cross-platform: TryResolveTimeZone handles IANA<->Windows translation.
        foreach (var id in new[] { "America/New_York", "Europe/London", "Australia/Sydney" })
        {
            if (HeatmapTime.TryResolveTimeZone(id, out var tz) && !zones.Contains(tz))
            {
                zones.Add(tz);
            }
        }

        return zones.ToArray();
    }

    /// <summary>Viewer time zones under test (never null; UTC is included explicitly).</summary>
    private static Gen<TimeZoneInfo> TimeZoneGen => Gen.Elements(TimeZones);

    /// <summary>
    /// Base "now" UTC instants spread across ~30 years at one-minute resolution, so the local
    /// current date (and the Monday it belongs to) varies across every weekday, month, and DST
    /// transition.
    /// </summary>
    private static Gen<DateTimeOffset> BaseNowGen =>
        Gen.Choose(0, 16_000_000)
            .Select(minutes =>
                new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private static Gen<ProjectionWindowKind> KindGen =>
        Gen.Elements(ProjectionWindowKind.IdealizedWeek, ProjectionWindowKind.Next7Days);

    /// <summary>
    /// **Property 5: Projection window construction is well-formed**
    /// **Validates: Requirements 9.2, 9.3**
    ///
    /// For any base instant, viewer time zone, and window kind, the constructed window starts at
    /// local midnight (Monday for Idealized week, the current local date for Next 7 days), has an
    /// exclusive end exactly seven days after the inclusive start, and spans exactly seven days.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property BuildWindow_IsWellFormed()
    {
        var arb = Arb.From(
            from now in BaseNowGen
            from kind in KindGen
            from tz in TimeZoneGen
            select (now, kind, tz));

        return Prop.ForAll(arb, input =>
        {
            var (now, kind, tz) = input;

            var window = HeatmapTime.BuildWindow(kind, now, tz);

            // The window records the requested kind.
            if (window.Kind != kind)
            {
                return false.Label($"kind: expected {kind} but got {window.Kind}");
            }

            // Inclusive start is local midnight: the wall-clock time-of-day at the start offset is 0.
            if (window.StartInclusive.TimeOfDay != TimeSpan.Zero)
            {
                return false.Label(
                    $"start not at midnight: {window.StartInclusive:o} (kind={kind}, tz={tz.Id})");
            }

            // Exclusive end is exactly seven days after the inclusive start.
            if (window.EndExclusive != window.StartInclusive.AddDays(WindowDays))
            {
                return false.Label(
                    $"end != start+7d: start={window.StartInclusive:o} end={window.EndExclusive:o}");
            }

            // Inclusive start strictly precedes the exclusive end.
            if (window.StartInclusive >= window.EndExclusive)
            {
                return false.Label(
                    $"start !< end: start={window.StartInclusive:o} end={window.EndExclusive:o}");
            }

            // Absolute duration is exactly seven days (168 hours), DST notwithstanding, because
            // AddDays on a DateTimeOffset adds exact elapsed time.
            var duration = window.EndExclusive - window.StartInclusive;
            if (duration != TimeSpan.FromDays(WindowDays))
            {
                return false.Label($"duration != 7d: {duration} (kind={kind}, tz={tz.Id})");
            }

            var localNow = TimeZoneInfo.ConvertTime(now, tz);

            if (kind == ProjectionWindowKind.IdealizedWeek)
            {
                // Idealized week starts on Monday (Req 9.3).
                if (window.StartInclusive.DayOfWeek != DayOfWeek.Monday)
                {
                    return false.Label(
                        $"idealized start not Monday: {window.StartInclusive:o} " +
                        $"({window.StartInclusive.DayOfWeek}, tz={tz.Id})");
                }

                // The start is the Monday of the local current week: 0..6 days before localNow's date.
                var daysBack = (localNow.Date - window.StartInclusive.Date).TotalDays;
                if (daysBack < 0 || daysBack > 6)
                {
                    return false.Label(
                        $"idealized start not within current week: start={window.StartInclusive.Date:d} " +
                        $"localNow={localNow.Date:d} daysBack={daysBack}");
                }
            }
            else
            {
                // Next 7 days starts at 00:00 of the local current date (Req 9.2).
                if (window.StartInclusive.Date != localNow.Date)
                {
                    return false.Label(
                        $"next7 start date != local now date: start={window.StartInclusive.Date:d} " +
                        $"localNow={localNow.Date:d} (tz={tz.Id})");
                }
            }

            return true.ToProperty();
        });
    }
}
