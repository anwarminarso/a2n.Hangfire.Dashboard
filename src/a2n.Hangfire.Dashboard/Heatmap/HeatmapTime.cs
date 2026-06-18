using System;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Heatmap;

/// <summary>
/// Pure, dependency-light time helpers shared by every heatmap engine. Centralizes time-zone
/// resolution (with UTC fallback), DST-safe conversion of an absolute instant to a viewer-local
/// instant, and the single deterministic <c>(dayIndex, hour)</c> bucket assignment used by the
/// aggregator. All bucketing is driven by the <em>absolute</em> (UTC-equivalent) instant, so a
/// given input always resolves to exactly one bucket even when the converted local time lands in a
/// Daylight Saving Time gap or overlap.
/// </summary>
/// <remarks>
/// Validates portions of Requirements 8.1, 8.3, 8.5, 8.7, 9.2, 9.3, and 1.4.
/// </remarks>
public static class HeatmapTime
{
    /// <summary>Number of days in any projection window.</summary>
    public const int WindowDays = 7;

    /// <summary>
    /// Resolves a time-zone identifier (IANA or Windows) to a <see cref="TimeZoneInfo"/>, falling
    /// back to <see cref="TimeZoneInfo.Utc"/> when the identifier is null, empty, or unrecognized.
    /// </summary>
    /// <param name="timeZoneId">The IANA or Windows time-zone identifier; null/empty means UTC.</param>
    /// <returns>The resolved time zone, or UTC when the identifier cannot be resolved.</returns>
    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        return TryResolveTimeZone(timeZoneId, out var timeZone) ? timeZone : TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Attempts to resolve a time-zone identifier (IANA or Windows) to a <see cref="TimeZoneInfo"/>.
    /// Cross-platform aware: when a direct lookup fails, IANA&#8596;Windows conversion is attempted
    /// so identifiers resolve on both Linux/macOS (IANA) and Windows hosts.
    /// </summary>
    /// <param name="timeZoneId">The IANA or Windows time-zone identifier to resolve.</param>
    /// <param name="timeZone">
    /// On return, the resolved time zone, or <see cref="TimeZoneInfo.Utc"/> when unresolved.
    /// </param>
    /// <returns>
    /// <c>true</c> when the identifier was recognized and resolved; otherwise <c>false</c> (and
    /// <paramref name="timeZone"/> is set to UTC).
    /// </returns>
    public static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            // No configured time zone => evaluate/display in UTC (Req 1.4, 8.3, 8.5).
            timeZone = TimeZoneInfo.Utc;
            return false;
        }

        var id = timeZoneId.Trim();

        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, TimeZoneInfo.Utc.Id, StringComparison.OrdinalIgnoreCase))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        // Direct lookup (works for the native id format of the current platform).
        if (TryFindSystemTimeZone(id, out timeZone))
        {
            return true;
        }

        // Cross-platform fallback: translate IANA -> Windows ...
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) &&
                !string.IsNullOrEmpty(windowsId) &&
                TryFindSystemTimeZone(windowsId, out timeZone))
            {
                return true;
            }
        }
        catch
        {
            // Ignore and continue to the next fallback.
        }

        // ... or Windows -> IANA.
        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) &&
                !string.IsNullOrEmpty(ianaId) &&
                TryFindSystemTimeZone(ianaId, out timeZone))
            {
                return true;
            }
        }
        catch
        {
            // Ignore and fall through to the UTC fallback.
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    private static bool TryFindSystemTimeZone(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }

    /// <summary>
    /// Converts an absolute instant to its equivalent instant in the supplied viewer time zone.
    /// Because the source is an absolute instant, the conversion is unambiguous and DST-safe: there
    /// is exactly one local representation for any UTC-equivalent instant (Req 8.7).
    /// </summary>
    /// <param name="instant">The absolute instant to convert.</param>
    /// <param name="viewerTimeZone">The viewer time zone; UTC when none is selected (Req 8.5).</param>
    /// <returns>The same instant expressed with the viewer time zone's offset.</returns>
    public static DateTimeOffset ToViewerLocal(DateTimeOffset instant, TimeZoneInfo viewerTimeZone)
    {
        var tz = viewerTimeZone ?? TimeZoneInfo.Utc;
        return TimeZoneInfo.ConvertTime(instant, tz);
    }

    /// <summary>
    /// Determines the single, deterministic <c>(dayIndex, hour)</c> bucket for an absolute instant
    /// within the given window, viewed in the supplied time zone. The day index is measured from the
    /// window's local start date and the hour is the converted local clock hour (0&#8211;23).
    /// </summary>
    /// <param name="instant">The absolute (UTC-equivalent) instant of the fire.</param>
    /// <param name="viewerTimeZone">The viewer time zone; UTC when none is selected.</param>
    /// <param name="window">The active projection window.</param>
    /// <returns>
    /// A tuple of the zero-based day index relative to the window's local start date and the local
    /// clock hour (0&#8211;23). For an in-window instant the day index is in <c>[0, 6]</c>.
    /// </returns>
    /// <remarks>
    /// Resolving the bucket from the absolute instant (rather than a wall-clock local time) means a
    /// fire whose converted local time falls within a DST gap or overlap still maps to exactly one
    /// bucket, and the same input always resolves to the same bucket (Req 8.7).
    /// </remarks>
    public static (int DayIndex, int Hour) GetBucket(
        DateTimeOffset instant, TimeZoneInfo viewerTimeZone, ProjectionWindow window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        var tz = viewerTimeZone ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(instant, tz);
        var windowStartLocal = TimeZoneInfo.ConvertTime(window.StartInclusive, tz);

        var dayIndex = (int)(local.Date - windowStartLocal.Date).TotalDays;
        return (dayIndex, local.Hour);
    }

    /// <summary>
    /// Determines whether an absolute instant falls within the window's half-open
    /// <c>[StartInclusive, EndExclusive)</c> interval.
    /// </summary>
    public static bool IsInWindow(DateTimeOffset instant, ProjectionWindow window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        return instant >= window.StartInclusive && instant < window.EndExclusive;
    }

    /// <summary>
    /// Builds the active projection window for the given kind, anchored on the supplied reference
    /// instant as observed in the viewer time zone.
    /// </summary>
    /// <param name="kind">Whether to build an idealized week or the next seven days.</param>
    /// <param name="now">The reference instant (typically "now") used to anchor the window.</param>
    /// <param name="viewerTimeZone">The viewer time zone the window is expressed in; UTC when null.</param>
    /// <returns>
    /// A <see cref="ProjectionWindow"/> whose inclusive start is local midnight (Monday for the
    /// idealized week, the current local date for next seven days) and whose exclusive end is exactly
    /// seven days later, yielding a window that covers 00:00 of day 1 through 23:59:59.999 of day 7.
    /// </returns>
    public static ProjectionWindow BuildWindow(
        ProjectionWindowKind kind, DateTimeOffset now, TimeZoneInfo viewerTimeZone)
    {
        var tz = viewerTimeZone ?? TimeZoneInfo.Utc;
        var localNow = TimeZoneInfo.ConvertTime(now, tz);

        DateTime startDate;
        if (kind == ProjectionWindowKind.IdealizedWeek)
        {
            // Anchor on the Monday of the current local week (Monday = 0 ... Sunday = 6).
            var mondayOffset = ((int)localNow.DayOfWeek + 6) % 7;
            startDate = localNow.Date.AddDays(-mondayOffset);
        }
        else
        {
            startDate = localNow.Date;
        }

        var start = ToZonedMidnight(startDate, tz);
        // Exactly seven days of absolute duration; the half-open end is 00:00 of the eighth day.
        var end = start.AddDays(WindowDays);
        return new ProjectionWindow(start, end, kind);
    }

    private static DateTimeOffset ToZonedMidnight(DateTime localDate, TimeZoneInfo timeZone)
    {
        // localDate carries the date at 00:00 with an unspecified kind; interpret it in the target
        // zone. If midnight is skipped by a (rare) DST spring-forward, GetUtcOffset still yields a
        // usable offset so the window remains well-defined and deterministic.
        var midnight = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        var offset = timeZone.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight, offset);
    }
}
