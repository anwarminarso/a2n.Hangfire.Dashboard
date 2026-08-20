using System;
using Xunit;
using a2n.Hangfire.Dashboard.Heatmap;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Deterministic regression tests for the projection window across DST transitions.
/// </summary>
/// <remarks>
/// <para>
/// <c>BuildWindow</c> used to compute its end as <c>start.AddDays(7)</c>. On a
/// <see cref="DateTimeOffset"/> that adds 168 hours of absolute time while keeping the start's UTC
/// offset, so in a week containing a DST transition the end drifted away from local midnight:
/// </para>
/// <list type="bullet">
///   <item>
///     spring-forward — the window ran an hour past local midnight, so an in-window fire could bucket
///     at day index 7. Consumers build cells for days 0-6 only (see <c>PlannerHelpers</c>), so those
///     fires were dropped from the grid with no indication.
///   </item>
///   <item>
///     fall-back — the window ended an hour early, so the last local hour of day 6 was never
///     projected at all.
///   </item>
/// </list>
/// <para>
/// The first case was found by <c>ViewerTimeZoneBucketingProperties</c> under a random seed, which
/// made it show up only intermittently. These cases pin it down.
/// </para>
/// </remarks>
public class HeatmapDstWindowTests
{
    private static TimeZoneInfo Sydney =>
        HeatmapTime.TryResolveTimeZone("Australia/Sydney", out var tz)
            ? tz
            : throw new InvalidOperationException("Australia/Sydney could not be resolved on this host.");

    /// <summary>
    /// The exact counterexample from CI: <c>StdGen (2018328598,297663684)</c> reduced to
    /// 2018-10-04T18:46:00Z, an idealized week in Sydney, with the fire 10066 minutes into the window.
    /// Sydney moved to UTC+11 on 2018-10-07, inside that window.
    /// </summary>
    [Fact]
    public void SpringForwardWeek_TheCounterexampleCanNoLongerBucketPastTheLastDay()
    {
        var window = HeatmapTime.BuildWindow(
            ProjectionWindowKind.IdealizedWeek,
            DateTimeOffset.Parse("2018-10-04T18:46:00Z"),
            Sydney);

        // Seven local days across the transition is 167 hours, so the window admits 10020 minutes.
        // The counterexample's 10066-minute offset assumed a fixed 168, and now falls outside the
        // window — which is the point: it can no longer be admitted and then bucketed onto a day the
        // grid does not have.
        Assert.Equal(10020, (int)(window.EndExclusive - window.StartInclusive).TotalMinutes);
        Assert.False(
            HeatmapTime.IsInWindow(window.StartInclusive.AddMinutes(10066).ToUniversalTime(), window),
            "10066 minutes is past seven local days in this week");

        // The last minute the window does admit sits on the final grid day, not an eighth one.
        var lastAdmitted = window.EndExclusive.AddMinutes(-1);
        Assert.True(HeatmapTime.IsInWindow(lastAdmitted, window));

        var (dayIndex, hour) = HeatmapTime.GetBucket(lastAdmitted, Sydney, window);

        Assert.Equal(HeatmapTime.WindowDays - 1, dayIndex);
        Assert.Equal(23, hour);
    }

    /// <summary>
    /// Every instant the window admits must bucket inside the grid, in both transition directions and
    /// in an ordinary week.
    /// </summary>
    [Theory]
    [InlineData("2018-10-04T18:46:00Z", 167)] // DST starts 2018-10-07: seven local days = 167 hours
    [InlineData("2018-03-28T00:00:00Z", 169)] // DST ends 2018-04-01: seven local days = 169 hours
    [InlineData("2018-04-11T00:00:00Z", 168)] // no transition
    public void Window_SpansSevenLocalDays_AndEveryAdmittedInstantIsInTheGrid(
        string nowIso, int expectedHours)
    {
        var window = HeatmapTime.BuildWindow(
            ProjectionWindowKind.IdealizedWeek,
            DateTimeOffset.Parse(nowIso),
            Sydney);

        Assert.Equal(expectedHours, (int)(window.EndExclusive - window.StartInclusive).TotalHours);

        // Local midnight at both ends.
        var startLocal = TimeZoneInfo.ConvertTime(window.StartInclusive, Sydney);
        var endLocal = TimeZoneInfo.ConvertTime(window.EndExclusive, Sydney);

        Assert.Equal(TimeSpan.Zero, startLocal.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, endLocal.TimeOfDay);
        Assert.Equal(HeatmapTime.WindowDays, (int)(endLocal.Date - startLocal.Date).TotalDays);

        // Walk the whole window: no admitted instant may fall outside days 0..6.
        for (var minutes = 0; minutes < expectedHours * 60; minutes++)
        {
            var instant = window.StartInclusive.AddMinutes(minutes);
            var (dayIndex, hour) = HeatmapTime.GetBucket(instant, Sydney, window);

            Assert.True(HeatmapTime.IsInWindow(instant, window), $"minute {minutes} should be in window");
            Assert.InRange(dayIndex, 0, HeatmapTime.WindowDays - 1);
            Assert.InRange(hour, 0, 23);
        }

        // The first instant outside the window is local midnight of the eighth day.
        Assert.False(HeatmapTime.IsInWindow(window.EndExclusive, window));
    }
}
