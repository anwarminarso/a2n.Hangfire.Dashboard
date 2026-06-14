using Cronos;

namespace a2n.Hangfire.Dashboard.Internal;

/// <summary>
/// Outcome of a cron next-occurrence computation.
/// </summary>
/// <param name="IsValid">
/// <c>true</c> when the cron expression parsed successfully; <c>false</c> when it could not be
/// parsed (in which case no preview is available, per Requirement 10.7).
/// </param>
/// <param name="NextOccurrenceUtc">
/// The next scheduled occurrence expressed in UTC, or <c>null</c> when the expression is invalid
/// or has no future occurrence.
/// </param>
internal sealed record CronPreviewResult(bool IsValid, DateTime? NextOccurrenceUtc)
{
    /// <summary>A shared result representing an unparseable cron expression with no preview.</summary>
    public static CronPreviewResult Invalid { get; } = new(false, null);
}

/// <summary>
/// Pure helper that parses Hangfire-compatible cron expressions (via the Cronos library bundled
/// transitively by Hangfire.Core) and computes the next scheduled occurrence for a time zone.
/// </summary>
/// <remarks>
/// Implements the preview portion of Requirement 10:
/// <list type="bullet">
///   <item>10.5 — when no time zone is selected, previews are computed in UTC.</item>
///   <item>10.6 — when a valid cron expression is present, the next occurrence is computed for the
///   selected time zone.</item>
///   <item>10.7 — an unparseable cron expression is reported as invalid with no preview.</item>
/// </list>
/// </remarks>
internal static class CronPreview
{
    /// <summary>
    /// Attempts to parse <paramref name="cronExpression"/> into a <see cref="CronExpression"/>.
    /// Five-field expressions are parsed with <see cref="CronFormat.Standard"/> and six-field
    /// expressions with <see cref="CronFormat.IncludeSeconds"/>; the alternate format is tried as a
    /// fallback so well-formed expressions of either shape parse successfully.
    /// </summary>
    /// <param name="cronExpression">The candidate cron expression.</param>
    /// <param name="parsed">The parsed expression when this method returns <c>true</c>; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when the expression parsed successfully; otherwise <c>false</c>.</returns>
    public static bool TryParse(string cronExpression, out CronExpression parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            return false;
        }

        var expression = cronExpression.Trim();
        var fieldCount = expression.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
        var primaryFormat = fieldCount >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        if (TryParseWithFormat(expression, primaryFormat, out parsed))
        {
            return true;
        }

        var fallbackFormat = primaryFormat == CronFormat.Standard
            ? CronFormat.IncludeSeconds
            : CronFormat.Standard;

        return TryParseWithFormat(expression, fallbackFormat, out parsed);
    }

    /// <summary>
    /// Computes the next scheduled occurrence of <paramref name="cronExpression"/> after
    /// <paramref name="baseTimeUtc"/>, expressed in UTC.
    /// </summary>
    /// <param name="cronExpression">The cron expression to evaluate.</param>
    /// <param name="baseTimeUtc">
    /// The reference point after which the next occurrence is sought. Interpreted as UTC; any
    /// non-UTC <see cref="DateTime.Kind"/> is treated as UTC.
    /// </param>
    /// <param name="timeZone">
    /// The time zone in which the schedule is evaluated. When <c>null</c>, UTC is used (Requirement 10.5).
    /// </param>
    /// <returns>
    /// A <see cref="CronPreviewResult"/> whose <see cref="CronPreviewResult.IsValid"/> is <c>false</c>
    /// with no occurrence when the expression cannot be parsed (Requirement 10.7); otherwise a valid
    /// result carrying the next occurrence in UTC (or <c>null</c> when none exists).
    /// </returns>
    public static CronPreviewResult NextOccurrence(string cronExpression, DateTime baseTimeUtc, TimeZoneInfo timeZone)
    {
        if (!TryParse(cronExpression, out var parsed))
        {
            return CronPreviewResult.Invalid;
        }

        var zone = timeZone ?? TimeZoneInfo.Utc;
        var fromUtc = baseTimeUtc.Kind == DateTimeKind.Utc
            ? baseTimeUtc
            : DateTime.SpecifyKind(baseTimeUtc, DateTimeKind.Utc);

        var next = parsed.GetNextOccurrence(fromUtc, zone);
        return new CronPreviewResult(true, next);
    }

    private static bool TryParseWithFormat(string expression, CronFormat format, out CronExpression parsed)
    {
        try
        {
            parsed = CronExpression.Parse(expression, format);
            return true;
        }
        catch (CronFormatException)
        {
            parsed = null;
            return false;
        }
    }
}
