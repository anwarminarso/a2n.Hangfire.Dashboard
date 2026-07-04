using System.Globalization;

namespace a2n.Hangfire.Dashboard.Rollup.Internal;

/// <summary>Time bucketing helpers shared by the rollup collector and metrics store.</summary>
internal static class RollupTime
{
    public const int RetentionWeeks = 9;

    public static long WeekIndex(long utcTicks)
    {
        var daysSinceEpoch = (utcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerDay;
        return (long)Math.Floor(daysSinceEpoch / 7d);
    }

    public static long WeekIndex(DateTime utc) => WeekIndex(AsUtc(utc).Ticks);

    public static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static long AsUtcTicks(DateTime value) => AsUtc(value).Ticks;

    /// <summary>Day-of-week index 0 = Monday … 6 = Sunday (matches SQL metrics adapters).</summary>
    public static int DayIndexMondayZero(DateTime utc)
        => ((int)AsUtc(utc).DayOfWeek + 6) % 7;

    public static string ThroughputBucketKey(DateTime utc, Interfaces.MetricsInterval interval)
    {
        utc = AsUtc(utc);
        return interval switch
        {
            Interfaces.MetricsInterval.OneMinute => utc.ToString("yyyy-MM-dd-HH-mm", CultureInfo.InvariantCulture),
            Interfaces.MetricsInterval.FiveMinutes => $"{utc:yyyy-MM-dd-HH}-{(utc.Minute / 5) * 5:D2}",
            Interfaces.MetricsInterval.FifteenMinutes => $"{utc:yyyy-MM-dd-HH}-{(utc.Minute / 15) * 15:D2}",
            Interfaces.MetricsInterval.OneHour => utc.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture),
            Interfaces.MetricsInterval.OneDay => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => utc.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture)
        };
    }

    public static DateTimeOffset ParseThroughputBucket(string key, Interfaces.MetricsInterval interval)
    {
        if (string.IsNullOrEmpty(key))
            return DateTimeOffset.MinValue;

        if (interval == Interfaces.MetricsInterval.OneDay
            && DateTimeOffset.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var day))
            return day;

        if (interval == Interfaces.MetricsInterval.OneMinute
            && TryParseMinuteBucket(key, out var minute))
            return minute;

        if ((interval == Interfaces.MetricsInterval.FiveMinutes || interval == Interfaces.MetricsInterval.FifteenMinutes)
            && TryParseSubHourBucket(key, out var subHour))
            return subHour;

        if (DateTimeOffset.TryParseExact(key, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var hour))
            return hour;

        return DateTimeOffset.MinValue;
    }

    private static bool TryParseMinuteBucket(string key, out DateTimeOffset result)
    {
        result = default;
        var parts = key.Split('-');
        if (parts.Length != 5)
            return false;

        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
            return false;

        var dateHour = string.Join('-', parts.Take(4));
        if (!DateTimeOffset.TryParseExact(dateHour, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var baseHour))
            return false;

        result = baseHour.AddMinutes(minute);
        return true;
    }

    private static bool TryParseSubHourBucket(string key, out DateTimeOffset result)
    {
        result = default;
        var lastDash = key.LastIndexOf('-');
        if (lastDash <= 0)
            return false;

        var minutePart = key[(lastDash + 1)..];
        var dateHour = key[..lastDash];
        if (!int.TryParse(minutePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
            return false;

        if (!DateTimeOffset.TryParseExact(dateHour, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var baseHour))
            return false;

        result = baseHour.AddMinutes(minute);
        return true;
    }
}
