using System.Globalization;
using System.Text.Json;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Storage;

/// <summary>
/// Logic the SQL storage adapters (SQL Server, PostgreSQL) share for their analytics queries: parsing
/// Hangfire's counter keys, bucketing timestamps, and folding query rows into the dashboard's data points.
/// Only the SQL text differs per adapter.
/// </summary>
public static class StorageMetricsHelpers
{
    /// <summary>
    /// Parses a Hangfire statistics counter key, <c>stats:{category}:{yyyy-MM-dd}</c> or
    /// <c>stats:{category}:{yyyy-MM-dd-HH}</c>, into its category and UTC timestamp.
    /// </summary>
    public static bool TryParseCounterKey(
        string? key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? category,
        out DateTimeOffset timestamp)
    {
        category = null;
        timestamp = default;
        if (string.IsNullOrEmpty(key)) return false;

        var parts = key.Split(':');
        if (parts.Length < 3) return false;

        var stamp = string.Join(":", parts.Skip(2));
        if (!DateTimeOffset.TryParseExact(stamp, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out timestamp)
            && !DateTimeOffset.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out timestamp))
        {
            return false;
        }

        category = parts[1];
        return true;
    }

    /// <summary>Truncates <paramref name="timestamp"/> to the start of its <paramref name="interval"/> bucket, keeping its offset.</summary>
    public static DateTimeOffset TruncateToInterval(DateTimeOffset timestamp, MetricsInterval interval)
    {
        var minute = interval switch
        {
            MetricsInterval.OneMinute => timestamp.Minute,
            MetricsInterval.FiveMinutes => timestamp.Minute / 5 * 5,
            MetricsInterval.FifteenMinutes => timestamp.Minute / 15 * 15,
            _ => 0,
        };
        var hour = interval == MetricsInterval.OneDay ? 0 : timestamp.Hour;
        return new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, hour, minute, 0, timestamp.Offset);
    }

    /// <summary>
    /// Folds <c>stats:succeeded|failed|deleted:*</c> counter rows into throughput points: rows outside
    /// [<paramref name="from"/>, <paramref name="to"/>) or with an unparseable key are skipped, the rest
    /// are summed per <paramref name="interval"/> bucket. Ordered by time.
    /// </summary>
    public static IReadOnlyList<ThroughputDataPoint> AggregateThroughput(
        IEnumerable<(string? Key, long Value)> counters, DateTimeOffset from, DateTimeOffset to, MetricsInterval interval)
    {
        var points = new Dictionary<DateTimeOffset, ThroughputDataPoint>();
        foreach (var (key, value) in counters)
        {
            if (!TryParseCounterKey(key, out var category, out var timestamp)) continue;
            if (timestamp < from || timestamp >= to) continue;

            var bucket = TruncateToInterval(timestamp, interval);
            if (!points.TryGetValue(bucket, out var point))
                points[bucket] = point = new ThroughputDataPoint { Timestamp = bucket };

            switch (category)
            {
                case "succeeded": point.Succeeded += value; break;
                case "failed": point.Failed += value; break;
                case "deleted": point.Deleted += value; break;
            }
        }

        return points.Values.OrderBy(p => p.Timestamp).ToList();
    }

    /// <summary>
    /// Folds per-bucket state counts (bucket start as stored, read as UTC) into state transition points,
    /// ordered by time. States other than the six the chart shows are ignored.
    /// </summary>
    public static IReadOnlyList<StateTransitionDataPoint> BuildStateTransitions(
        IEnumerable<(DateTime Bucket, string? StateName, long Count)> rows)
    {
        var points = new Dictionary<DateTimeOffset, StateTransitionDataPoint>();
        foreach (var (bucketStart, stateName, count) in rows)
        {
            var bucket = new DateTimeOffset(DateTime.SpecifyKind(bucketStart, DateTimeKind.Utc), TimeSpan.Zero);
            if (!points.TryGetValue(bucket, out var point))
                points[bucket] = point = new StateTransitionDataPoint { Timestamp = bucket };

            switch (stateName)
            {
                case "Enqueued": point.Enqueued += count; break;
                case "Processing": point.Processing += count; break;
                case "Succeeded": point.Succeeded += count; break;
                case "Failed": point.Failed += count; break;
                case "Deleted": point.Deleted += count; break;
                case "Scheduled": point.Scheduled += count; break;
            }
        }

        return points.Values.OrderBy(p => p.Timestamp).ToList();
    }

    /// <summary>
    /// Reads <c>WorkerCount</c> from a server's serialized data (the <c>Server.Data</c> column). Returns 0
    /// when the data is missing, isn't JSON, or has no worker count.
    /// </summary>
    public static int ParseWorkerCount(string? serverData)
    {
        if (string.IsNullOrEmpty(serverData)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(serverData);
            if (doc.RootElement.TryGetProperty("WorkerCount", out var workerCount))
                return workerCount.GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Malformed server data only hides the worker count; the utilization view shows 0.
        }

        return 0;
    }
}
