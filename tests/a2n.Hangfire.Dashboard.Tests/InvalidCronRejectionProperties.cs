using System;
using System.Linq;
using Cronos;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Internal;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Property tests for <see cref="CronPreview"/> invalid-cron rejection (Job Builder, Phase 4).
///
/// **Property 19: Invalid cron rejection**
/// **Validates: Requirements 10.7, 10.9**
///
/// For any cron string that cannot be parsed, the schedule preview reports an invalid result with
/// no next-occurrence preview (<see cref="CronPreview.NextOccurrence"/> returns
/// <c>IsValid=false</c> and <c>NextOccurrenceUtc=null</c>, and <see cref="CronPreview.TryParse"/>
/// returns <c>false</c>) — this is the pure-logic basis for Requirement 10.7 (the Schedule_Builder
/// shows an invalid-cron error and no preview) and Requirement 10.9 (the Job_Builder rejects
/// submission when the Cron_Expression is absent or invalid, because submission validity derives
/// from this same parse outcome). The converse is asserted for representative valid crons so the
/// property is meaningful.
/// </summary>
public class InvalidCronRejectionProperties
{
    /// <summary>
    /// Representative, unambiguously-valid 5-field Hangfire-compatible cron expressions used to
    /// assert the converse: these MUST be reported valid, otherwise an over-eager "everything is
    /// invalid" implementation would vacuously satisfy the rejection property.
    /// </summary>
    private static readonly string[] ValidCronExpressions =
    [
        "* * * * *",   // every minute
        "0 0 * * *",   // daily at midnight
        "*/5 * * * *", // every 5 minutes
        "0 12 * * 1",  // noon on Mondays
        "30 9 1 * *",  // 09:30 on the 1st of each month
    ];

    /// <summary>Empty / whitespace-only expressions (no fields at all). Always invalid.</summary>
    private static Gen<string> EmptyOrWhitespaceGen =>
        Gen.Elements("", " ", "   ", "\t", "\t ");

    /// <summary>
    /// Too few fields: 1–4 star fields. A standard cron needs 5 fields and a with-seconds cron
    /// needs 6, so any count in 1..4 fails both formats <see cref="CronPreview.TryParse"/> tries.
    /// </summary>
    private static Gen<string> TooFewFieldsGen =>
        from count in Gen.Choose(1, 4)
        select string.Join(' ', Enumerable.Repeat("*", count));

    /// <summary>
    /// Too many fields: 7–12 star fields. Exceeds both the 5-field standard and 6-field
    /// with-seconds layouts, so it can never parse.
    /// </summary>
    private static Gen<string> TooManyFieldsGen =>
        from count in Gen.Choose(7, 12)
        select string.Join(' ', Enumerable.Repeat("*", count));

    /// <summary>
    /// A valid 5-field expression with exactly one field replaced by a clearly out-of-range numeric
    /// token (e.g. minute 99, hour 88, day-of-month 77, month 66, day-of-week 55). All other fields
    /// are <c>*</c>, so the out-of-range token is the sole reason the expression fails to parse.
    /// </summary>
    private static Gen<string> OutOfRangeFieldGen =>
        from position in Gen.Choose(0, 4)
        select BuildOutOfRange(position);

    private static string BuildOutOfRange(int position)
    {
        // Values chosen to exceed each field's maximum (min 0-59, hour 0-23, dom 1-31, month 1-12,
        // dow 0-7) by a wide margin so none can ever be a legal value.
        var outOfRange = new[] { "99", "88", "77", "66", "55" };
        var fields = new[] { "*", "*", "*", "*", "*" };
        fields[position] = outOfRange[position];
        return string.Join(' ', fields);
    }

    /// <summary>
    /// Five fields of non-numeric gibberish that are not valid cron tokens, month names, or
    /// day-of-week names. Such strings are syntactically meaningless and always fail to parse.
    /// </summary>
    private static Gen<string> GibberishGen
    {
        get
        {
            // Deliberately avoids any substring that Cronos accepts (JAN..DEC, SUN..SAT, L, W, #).
            var tokens = new[] { "zzz", "qqq", "xyzzy", "blah", "nope", "kkk", "vvv", "ppp" };
            return from a in Gen.Elements(tokens)
                   from b in Gen.Elements(tokens)
                   from c in Gen.Elements(tokens)
                   from d in Gen.Elements(tokens)
                   from e in Gen.Elements(tokens)
                   select string.Join(' ', a, b, c, d, e);
        }
    }

    /// <summary>The union of all invalid-expression categories.</summary>
    private static Gen<string> InvalidCronGen =>
        Gen.OneOf(
            EmptyOrWhitespaceGen,
            TooFewFieldsGen,
            TooManyFieldsGen,
            OutOfRangeFieldGen,
            GibberishGen);

    /// <summary>Base UTC instants spread across ~28 years, one minute apart.</summary>
    private static Gen<DateTime> BaseTimeUtcGen =>
        Gen.Choose(0, 15_000_000)
            .Select(minutes => new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutes));

    /// <summary>A fixed-offset (no DST) time zone used as a representative non-UTC zone.</summary>
    private static readonly TimeZoneInfo OffsetZone = TimeZoneInfo.CreateCustomTimeZone(
        "Test+05:30", new TimeSpan(5, 30, 0), "Test +05:30", "Test +05:30");

    private static Gen<TimeZoneInfo> TimeZoneGen =>
        Gen.Elements<TimeZoneInfo>(null, TimeZoneInfo.Utc, OffsetZone);

    /// <summary>
    /// **Property 19: Invalid cron rejection**
    /// **Validates: Requirements 10.7, 10.9**
    ///
    /// For any generated invalid cron string, regardless of base instant and time zone,
    /// <see cref="CronPreview.NextOccurrence"/> reports <c>IsValid=false</c> with no preview
    /// (<c>NextOccurrenceUtc=null</c>) and <see cref="CronPreview.TryParse"/> returns <c>false</c>.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property InvalidCron_IsRejectedWithNoPreview()
    {
        var arb = Arb.From(
            from cron in InvalidCronGen
            from baseUtc in BaseTimeUtcGen
            from zone in TimeZoneGen
            select (cron, baseUtc, zone));

        return Prop.ForAll(arb, input =>
        {
            var (cron, baseUtc, zone) = input;

            var result = CronPreview.NextOccurrence(cron, baseUtc, zone);
            var tryParseSucceeded = CronPreview.TryParse(cron, out _);

            var rejected = !result.IsValid
                && result.NextOccurrenceUtc is null
                && !tryParseSucceeded;

            return rejected.Label(
                $"Expected invalid cron '{cron}' to be rejected, but got " +
                $"IsValid={result.IsValid}, NextOccurrenceUtc={result.NextOccurrenceUtc:o}, " +
                $"TryParse={tryParseSucceeded}");
        });
    }

    /// <summary>
    /// **Property 19: Invalid cron rejection (converse)**
    /// **Validates: Requirements 10.7**
    ///
    /// Representative valid cron expressions are reported valid with a preview, ensuring the
    /// rejection property above is not vacuously satisfied by an implementation that rejects
    /// everything.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ValidCron_IsAccepted()
    {
        var arb = Arb.From(
            from cron in Gen.Elements(ValidCronExpressions)
            from baseUtc in BaseTimeUtcGen
            from zone in TimeZoneGen
            select (cron, baseUtc, zone));

        return Prop.ForAll(arb, input =>
        {
            var (cron, baseUtc, zone) = input;

            var result = CronPreview.NextOccurrence(cron, baseUtc, zone);
            var tryParseSucceeded = CronPreview.TryParse(cron, out _);

            var accepted = result.IsValid
                && result.NextOccurrenceUtc is not null
                && tryParseSucceeded;

            return accepted.Label(
                $"Expected valid cron '{cron}' to be accepted, but got " +
                $"IsValid={result.IsValid}, NextOccurrenceUtc={result.NextOccurrenceUtc:o}, " +
                $"TryParse={tryParseSucceeded}");
        });
    }

    /// <summary>
    /// **Property 19: Invalid cron rejection**
    /// **Validates: Requirements 10.7**
    ///
    /// Anchors the property with concrete, hand-checked invalid expressions across every category,
    /// independent of the randomized generator.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* * *")]            // too few fields
    [InlineData("* * * *")]          // too few fields
    [InlineData("* * * * * * *")]    // too many fields
    [InlineData("60 * * * *")]       // minute out of range
    [InlineData("* 24 * * *")]       // hour out of range
    [InlineData("* * 32 * *")]       // day-of-month out of range
    [InlineData("* * * 13 *")]       // month out of range
    [InlineData("nonsense words here please")] // gibberish
    public void NextOccurrence_KnownInvalidExpressions_AreRejected(string cron)
    {
        var result = CronPreview.NextOccurrence(cron, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);

        Assert.False(result.IsValid);
        Assert.Null(result.NextOccurrenceUtc);
        Assert.False(CronPreview.TryParse(cron, out _));
    }
}
