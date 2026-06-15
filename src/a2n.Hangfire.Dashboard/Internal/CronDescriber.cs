using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Internal;

/// <summary>
/// Pure helper for the Schedule Builder's Cron Builder. It turns a <see cref="CronFields"/>
/// selection into a 5-field Hangfire-compatible cron expression (Req 10.2) and produces a
/// human-readable, word description of a cron expression (Req 10.3), degrading to echoing the
/// raw fields when a pattern is not recognized.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="CronFieldSpec"/> records both *how* a field is expressed
/// (<c>Every / Specific / Range / Step</c>) and the concrete value(s) for that mode.
/// <see cref="Build"/> renders one token per field, clamping every value into that cron field's
/// own legal value domain (minute 0-59, hour 0-23, day-of-month 1-31, month 1-12, day-of-week 0-6)
/// so the result is always a valid 5-field expression whose fields match the selected values.
/// </para>
/// </remarks>
internal static class CronDescriber
{
    /// <summary>Per-field legal value domain, indexed positionally: minute, hour, day-of-month, month, day-of-week.</summary>
    private static readonly (int Min, int Max)[] FieldDomains =
    {
        (0, 59), // minute
        (0, 23), // hour
        (1, 31), // day-of-month
        (1, 12), // month
        (0, 6),  // day-of-week
    };

    /// <summary>
    /// Builds a 5-field (minute hour day-of-month month day-of-week) Hangfire-compatible cron
    /// expression whose fields match the selections in <paramref name="fields"/> (Req 10.2).
    /// </summary>
    public static string Build(CronFields fields)
    {
        if (fields is null) throw new ArgumentNullException(nameof(fields));

        return string.Join(" ", new[]
        {
            RenderField(fields.Minute, FieldDomains[0]),
            RenderField(fields.Hour, FieldDomains[1]),
            RenderField(fields.DayOfMonth, FieldDomains[2]),
            RenderField(fields.Month, FieldDomains[3]),
            RenderField(fields.DayOfWeek, FieldDomains[4]),
        });
    }

    /// <summary>
    /// Produces a human-readable description of a cron expression (Req 10.3). Recognizes the common
    /// field shapes (every minute, every N minutes, hourly, daily, weekly, monthly) and degrades to
    /// echoing the raw fields when the pattern is not recognized or the expression is not 5 fields.
    /// </summary>
    public static string Describe(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return string.Empty;

        var raw = cron.Trim();
        var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        // Only the canonical 5-field shape is described in words; anything else echoes the raw fields.
        if (parts.Length != 5) return EchoRaw(parts, raw);

        var minute = parts[0];
        var hour = parts[1];
        var dom = parts[2];
        var month = parts[3];
        var dow = parts[4];

        var domAny = IsWildcard(dom);
        var monthAny = IsWildcard(month);
        var dowAny = IsWildcard(dow);

        // Every minute: * * * * *
        if (IsWildcard(minute) && IsWildcard(hour) && domAny && monthAny && dowAny)
            return "Every minute";

        // Every N minutes: */N * * * *
        if (IsStep(minute, out var minuteStep) && IsWildcard(hour) && domAny && monthAny && dowAny)
            return minuteStep == 1 ? "Every minute" : $"Every {minuteStep} minutes";

        // Every N hours at a given minute: M */N * * *
        if (IsSingleInt(minute, out var hsMinute) && IsStep(hour, out var hourStep) && domAny && monthAny && dowAny)
            return hourStep == 1
                ? $"Every hour at minute {hsMinute}"
                : $"Every {hourStep} hours at minute {hsMinute}";

        // Hourly at a given minute: M * * * *
        if (IsSingleInt(minute, out var hourlyMinute) && IsWildcard(hour) && domAny && monthAny && dowAny)
            return $"At minute {hourlyMinute} of every hour";

        if (IsSingleInt(minute, out var tMinute) && IsSingleInt(hour, out var tHour))
        {
            var time = FormatTime(tHour, tMinute);

            // Daily: M H * * *
            if (domAny && monthAny && dowAny)
                return $"Every day at {time}";

            // Weekly: M H * * D
            if (domAny && monthAny && IsSingleInt(dow, out var weekday))
                return $"Every {DayOfWeekName(weekday)} at {time}";

            // Monthly on a day: M H D * *
            if (IsSingleInt(dom, out var monthDay) && monthAny && dowAny)
                return $"On day {monthDay} of every month at {time}";

            // Specific month and day: M H D Mon *
            if (IsSingleInt(dom, out var ymDay) && IsSingleInt(month, out var ymMonth) && dowAny)
                return $"On {MonthName(ymMonth)} {ymDay} at {time}";
        }

        // Unrecognized combination: echo the raw fields.
        return EchoRaw(parts, raw);
    }

    private static string RenderField(CronFieldSpec spec, (int Min, int Max) domain)
    {
        if (spec is null) return "*";

        switch (spec.Mode)
        {
            case CronFieldMode.Specific:
                return Clamp(spec.Value, domain).ToString(CultureInfo.InvariantCulture);

            case CronFieldMode.Range:
            {
                var start = Clamp(spec.RangeStart, domain);
                var end = Clamp(spec.RangeEnd, domain);
                if (end < start) (start, end) = (end, start);

                // A range covering a single point collapses to that single value.
                return start == end
                    ? start.ToString(CultureInfo.InvariantCulture)
                    : start.ToString(CultureInfo.InvariantCulture)
                        + "-" + end.ToString(CultureInfo.InvariantCulture);
            }

            case CronFieldMode.Step:
            {
                var step = spec.Step < 1 ? 1 : spec.Step;
                return "*/" + step.ToString(CultureInfo.InvariantCulture);
            }

            case CronFieldMode.Every:
            default:
                return "*";
        }
    }

    private static int Clamp(int value, (int Min, int Max) domain)
    {
        if (value < domain.Min) return domain.Min;
        if (value > domain.Max) return domain.Max;
        return value;
    }

    private static bool IsWildcard(string field) => field == "*" || field == "?";

    private static bool IsSingleInt(string field, out int value)
        => int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool IsStep(string field, out int step)
    {
        step = 0;
        if (field is null || !field.StartsWith("*/", StringComparison.Ordinal)) return false;
        return int.TryParse(field.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out step) && step > 0;
    }

    private static string FormatTime(int hour, int minute) => $"{hour:00}:{minute:00}";

    private static string DayOfWeekName(int dow)
    {
        // Cron day-of-week: 0 and 7 are both Sunday.
        var normalized = dow == 7 ? 0 : dow;
        return normalized switch
        {
            0 => "Sunday",
            1 => "Monday",
            2 => "Tuesday",
            3 => "Wednesday",
            4 => "Thursday",
            5 => "Friday",
            6 => "Saturday",
            _ => $"day {dow}"
        };
    }

    private static string MonthName(int month)
    {
        if (month >= 1 && month <= 12)
            return CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);
        return $"month {month}";
    }

    private static string EchoRaw(IReadOnlyList<string> parts, string raw)
    {
        if (parts is null || parts.Count != 5)
            return $"Cron expression: {raw}";

        var sb = new StringBuilder();
        sb.Append("Minute ").Append(parts[0]);
        sb.Append(", hour ").Append(parts[1]);
        sb.Append(", day-of-month ").Append(parts[2]);
        sb.Append(", month ").Append(parts[3]);
        sb.Append(", day-of-week ").Append(parts[4]);
        return sb.ToString();
    }
}
