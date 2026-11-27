using System.Globalization;
using System.Text.Json;
using a2n.Hangfire.Dashboard.Interfaces;

namespace a2n.Hangfire.Dashboard.PostgreSql;

public partial class PostgreSqlMetricsProvider
{
    #region Private Helpers

    private static (string Category, DateTimeOffset Timestamp)? ParseCounterKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        // Format: stats:{category}:{date} or stats:{category}:{date}-{hour}
        var parts = key.Split(':');
        if (parts.Length < 3) return null;

        var category = parts[1]; // succeeded, failed, deleted
        var dateStr = string.Join(":", parts.Skip(2));

        // Try hourly format: yyyy-MM-dd-HH
        if (dateStr.Length == 13 &&
            DateTime.TryParseExact(dateStr, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hourlyDate))
        {
            return (category, new DateTimeOffset(hourlyDate, TimeSpan.Zero));
        }

        // Try daily format: yyyy-MM-dd
        if (dateStr.Length == 10 &&
            DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dailyDate))
        {
            return (category, new DateTimeOffset(dailyDate, TimeSpan.Zero));
        }

        return null;
    }

    private static DateTimeOffset TruncateToInterval(DateTimeOffset timestamp, MetricsInterval interval)
    {
        return interval switch
        {
            MetricsInterval.OneMinute => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, timestamp.Offset),
            MetricsInterval.FiveMinutes => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, (timestamp.Minute / 5) * 5, 0, timestamp.Offset),
            MetricsInterval.FifteenMinutes => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, (timestamp.Minute / 15) * 15, 0, timestamp.Offset),
            MetricsInterval.OneHour => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, timestamp.Offset),
            MetricsInterval.OneDay => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                0, 0, 0, timestamp.Offset),
            _ => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, timestamp.Offset)
        };
    }

    private static int ParseWorkerCount(string? data)
    {
        if (string.IsNullOrEmpty(data)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("WorkerCount", out var workerCount))
            {
                return workerCount.GetInt32();
            }
        }
        catch
        {
            // Ignore JSON parse errors
        }

        return 0;
    }

    #endregion
}
