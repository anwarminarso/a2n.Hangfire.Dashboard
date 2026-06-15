using System;
using Cronos;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="CronPreview"/> next-occurrence correctness (Job Builder, Phase 4).
///
/// **Property 18: Cron next-occurrence correctness**
/// **Validates: Requirements 10.5, 10.6**
///
/// For any valid cron expression, base instant, and time zone (UTC when none is selected), the
/// next scheduled occurrence reported by <see cref="CronPreview.NextOccurrence"/> equals the
/// occurrence computed deterministically from that same cron expression and time zone via
/// <see cref="CronExpression.GetNextOccurrence(DateTime, TimeZoneInfo)"/> (Req 10.6), and when no
/// time zone is supplied the computation falls back to UTC (Req 10.5).
/// </summary>
public class CronPreviewProperties
{
    /// <summary>
    /// A small set of representative, valid 5-field Hangfire-compatible cron expressions exercising
    /// a range of field shapes (every-minute, ranges, steps, lists, day-of-week/month constraints).
    /// </summary>
    private static readonly string[] ValidCronExpressions =
    [
        "* * * * *",        // every minute
        "0 * * * *",        // top of every hour
        "*/5 * * * *",      // every 5 minutes
        "0 0 * * *",        // daily at midnight
        "30 9 * * *",       // daily at 09:30
        "0 12 * * 1",       // noon on Mondays
        "15 14 1 * *",      // 14:15 on the 1st of each month
        "0 22 * * 1-5",     // 22:00 on weekdays
        "23 0-20/2 * * *",  // minute 23 of even hours up to 20
        "0 0 1 1 *",        // once a year (Jan 1 midnight)
        "5,35 8,18 * * *",  // 08:05/08:35 and 18:05/18:35
        "0 0 * * 0",        // Sundays at midnight
    ];

    /// <summary>A fixed-offset (no DST) time zone used as a representative non-UTC zone.</summary>
    private static readonly TimeZoneInfo OffsetZone = TimeZoneInfo.CreateCustomTimeZone(
        "Test+05:30", new TimeSpan(5, 30, 0), "Test +05:30", "Test +05:30");

    private static Gen<string> CronGen => Gen.Elements(ValidCronExpressions);

    /// <summary>
    /// Base UTC instants spread across ~28 years (2010-01-01 onward), one minute apart, so the
    /// generated time always has <see cref="DateTimeKind.Utc"/> and a valid calendar value.
    /// </summary>
    private static Gen<DateTime> BaseTimeUtcGen =>
        Gen.Choose(0, 15_000_000)
            .Select(minutes => new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutes));

    /// <summary>Time zones under test: null (UTC fallback, Req 10.5), explicit UTC, and an offset zone.</summary>
    private static Gen<TimeZoneInfo> TimeZoneGen =>
        Gen.Elements<TimeZoneInfo>(null, TimeZoneInfo.Utc, OffsetZone);

    /// <summary>
    /// **Property 18: Cron next-occurrence correctness**
    /// **Validates: Requirements 10.5, 10.6**
    ///
    /// For a valid cron expression, arbitrary base UTC instant, and a time zone (or null), the
    /// preview is valid and its next occurrence equals the Cronos-computed occurrence for the same
    /// zone (UTC when null). When an occurrence exists it is in UTC and strictly after the base.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NextOccurrence_MatchesCronosForSameZone()
    {
        var arb = Arb.From(
            from cron in CronGen
            from baseUtc in BaseTimeUtcGen
            from zone in TimeZoneGen
            select (cron, baseUtc, zone));

        return Prop.ForAll(arb, input =>
        {
            var (cron, baseUtc, zone) = input;

            var result = CronPreview.NextOccurrence(cron, baseUtc, zone);

            // A representative valid cron must always be reported as valid (Req 10.6).
            if (!result.IsValid)
            {
                return false.Label($"Expected IsValid=true for valid cron '{cron}'");
            }

            // Oracle: parse the same 5-field expression directly and compute for the same zone,
            // falling back to UTC when no zone is selected (Req 10.5).
            var effectiveZone = zone ?? TimeZoneInfo.Utc;
            var expected = CronExpression.Parse(cron, CronFormat.Standard)
                .GetNextOccurrence(baseUtc, effectiveZone);

            if (result.NextOccurrenceUtc != expected)
            {
                return false.Label(
                    $"cron='{cron}', base={baseUtc:o}, zone={(zone?.Id ?? "<null=UTC>")}: " +
                    $"expected {expected:o} but got {result.NextOccurrenceUtc:o}");
            }

            if (result.NextOccurrenceUtc is { } occurrence)
            {
                var strictlyAfter = occurrence > baseUtc;
                var isUtc = occurrence.Kind == DateTimeKind.Utc;
                return (strictlyAfter && isUtc).Label(
                    $"cron='{cron}', base={baseUtc:o}: occurrence={occurrence:o} " +
                    $"(strictlyAfter={strictlyAfter}, isUtc={isUtc})");
            }

            return true.ToProperty();
        });
    }

    /// <summary>
    /// **Property 18: Cron next-occurrence correctness**
    /// **Validates: Requirements 10.5**
    ///
    /// When no time zone is selected, the preview is computed in UTC: the null-zone result is
    /// identical to passing <see cref="TimeZoneInfo.Utc"/> explicitly.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NextOccurrence_NullZone_UsesUtc()
    {
        var arb = Arb.From(
            from cron in CronGen
            from baseUtc in BaseTimeUtcGen
            select (cron, baseUtc));

        return Prop.ForAll(arb, input =>
        {
            var (cron, baseUtc) = input;

            var nullZoneResult = CronPreview.NextOccurrence(cron, baseUtc, null);
            var utcResult = CronPreview.NextOccurrence(cron, baseUtc, TimeZoneInfo.Utc);

            return (nullZoneResult == utcResult).Label(
                $"cron='{cron}', base={baseUtc:o}: null-zone {nullZoneResult.NextOccurrenceUtc:o} " +
                $"!= UTC {utcResult.NextOccurrenceUtc:o}");
        });
    }

    /// <summary>
    /// **Property 18: Cron next-occurrence correctness**
    /// **Validates: Requirements 10.6**
    ///
    /// The selected time zone affects the computation consistently with Cronos: an every-minute
    /// expression is zone-agnostic, while a daily-at-a-fixed-time expression shifts with the zone
    /// offset. This anchors the property with concrete, hand-checked examples.
    /// </summary>
    [Fact]
    public void NextOccurrence_DailySchedule_ShiftsWithTimeZone()
    {
        // Base: 2024-03-10 00:00:00 UTC. "0 0 * * *" = midnight local.
        var baseUtc = new DateTime(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        var utc = CronPreview.NextOccurrence("0 0 * * *", baseUtc, TimeZoneInfo.Utc);
        var offset = CronPreview.NextOccurrence("0 0 * * *", baseUtc, OffsetZone);

        Assert.True(utc.IsValid);
        Assert.True(offset.IsValid);

        // Midnight UTC is exactly the base, so the next UTC midnight is 24h later.
        Assert.Equal(new DateTime(2024, 3, 11, 0, 0, 0, DateTimeKind.Utc), utc.NextOccurrenceUtc);

        // Midnight at +05:30 corresponds to 18:30 UTC the previous calendar day; the next such
        // instant after the base is 2024-03-10 18:30 UTC.
        Assert.Equal(new DateTime(2024, 3, 10, 18, 30, 0, DateTimeKind.Utc), offset.NextOccurrenceUtc);

        // The two zones genuinely produce different occurrences (Req 10.6).
        Assert.NotEqual(utc.NextOccurrenceUtc, offset.NextOccurrenceUtc);
    }

    /// <summary>
    /// **Property 18: Cron next-occurrence correctness**
    /// **Validates: Requirements 10.6**
    ///
    /// The reported occurrence actually satisfies the cron: there is no earlier occurrence between
    /// the base instant and the reported one (the result is the first matching instant).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NextOccurrence_IsTheFirstMatchingInstant()
    {
        var arb = Arb.From(
            from cron in CronGen
            from baseUtc in BaseTimeUtcGen
            from zone in TimeZoneGen
            select (cron, baseUtc, zone));

        return Prop.ForAll(arb, input =>
        {
            var (cron, baseUtc, zone) = input;
            var effectiveZone = zone ?? TimeZoneInfo.Utc;

            var result = CronPreview.NextOccurrence(cron, baseUtc, zone);
            if (result.NextOccurrenceUtc is not { } occurrence)
            {
                return true.ToProperty(); // no future occurrence; nothing to check
            }

            // The occurrence just before `occurrence` (from the base) must be `occurrence` itself,
            // i.e. nothing matches strictly between base and occurrence.
            var parsed = CronExpression.Parse(cron, CronFormat.Standard);
            var firstFromBase = parsed.GetNextOccurrence(baseUtc, effectiveZone);

            return (firstFromBase == occurrence).Label(
                $"cron='{cron}', base={baseUtc:o}: first match {firstFromBase:o} != reported {occurrence:o}");
        });
    }
}
