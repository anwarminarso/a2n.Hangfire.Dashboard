using System.Globalization;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Rollup.Internal;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Rollup;

public sealed partial class MetricsRollupStore
{
    public IReadOnlyList<ThroughputDataPoint> ReadThroughput(
        IStorageConnection connection, DateTimeOffset from, DateTimeOffset to, MetricsInterval interval)
    {
        var hash = SafeReadHash(connection, ThroughputHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<ThroughputDataPoint>();

        var points = new Dictionary<DateTimeOffset, ThroughputDataPoint>();
        foreach (var entry in hash)
        {
            var ts = RollupTime.ParseThroughputBucket(entry.Key, interval);
            if (ts < from || ts >= to)
                continue;

            var parts = entry.Value?.Split('|') ?? Array.Empty<string>();
            if (parts.Length < 2)
                continue;

            long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var succeeded);
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var failed);
            long deleted = parts.Length > 2 && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0;

            points[ts] = new ThroughputDataPoint
            {
                Timestamp = ts,
                Succeeded = succeeded,
                Failed = failed,
                Deleted = deleted
            };
        }

        return points.Values.OrderBy(p => p.Timestamp).ToList();
    }

    public IReadOnlyList<StateTransitionDataPoint> ReadStateTransitions(
        IStorageConnection connection, DateTimeOffset from, DateTimeOffset to, MetricsInterval interval)
    {
        var hash = SafeReadHash(connection, StatesHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<StateTransitionDataPoint>();

        var buckets = new Dictionary<DateTimeOffset, StateTransitionDataPoint>();
        foreach (var entry in hash)
        {
            if (!entry.Key.StartsWith("h:", StringComparison.Ordinal))
                continue;

            var rest = entry.Key[2..];
            var colon = rest.LastIndexOf(':');
            if (colon <= 0)
                continue;

            var bucketKey = rest[..colon];
            var stateName = rest[(colon + 1)..];
            var ts = RollupTime.ParseThroughputBucket(bucketKey, MetricsInterval.OneHour);
            if (ts < from || ts >= to)
                continue;

            if (!long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                continue;

            if (!buckets.TryGetValue(ts, out var point))
            {
                point = new StateTransitionDataPoint { Timestamp = ts };
                buckets[ts] = point;
            }

            switch (stateName)
            {
                case "Succeeded": point.Succeeded += count; break;
                case "Failed": point.Failed += count; break;
                case "Deleted": point.Deleted += count; break;
                case "Enqueued": point.Enqueued += count; break;
                case "Processing": point.Processing += count; break;
                case "Scheduled": point.Scheduled += count; break;
            }
        }

        return buckets.Values.OrderBy(p => p.Timestamp).ToList();
    }

    public IReadOnlyList<JobDurationStatsDto> ReadJobDurationStats(IStorageConnection connection)
    {
        var hash = SafeReadHash(connection, DurationHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<JobDurationStatsDto>();

        var results = new List<JobDurationStatsDto>();
        foreach (var jobType in EnumerateDurationPrefixes(hash))
        {
            var stats = ParseDurationFields(hash, jobType);
            if (stats == null)
                continue;

            results.Add(stats);
        }

        return results;
    }

    public IReadOnlyList<QueueLatencyStatsDto> ReadQueueLatencyStats(IStorageConnection connection)
    {
        var all = SafeReadHash(connection, LatencyHashKey);
        if (all == null || all.Count == 0)
            return Array.Empty<QueueLatencyStatsDto>();

        var queues = EnumerateDurationPrefixes(all);

        var results = new List<QueueLatencyStatsDto>();
        foreach (var queue in queues)
        {
            var stats = ParseDurationFields(all, queue, isQueue: true);
            if (stats == null)
                continue;

            results.Add(new QueueLatencyStatsDto
            {
                QueueName = queue,
                AverageMs = stats.AverageMs,
                P50Ms = stats.P50Ms,
                P95Ms = stats.P95Ms,
                P99Ms = stats.P99Ms
            });
        }

        return results;
    }

    /// <summary>
    /// Derives lifecycle state timings from the rollup aggregates: the enqueued phase is the
    /// count-weighted mean of the per-queue latency rollup and the processing phase the count-weighted
    /// mean of the per-job-type duration rollup. The scheduled phase is not tracked by the rollup
    /// (it needs the pre-enqueue history, which non-SQL storages do not expose cheaply) and stays zero.
    /// </summary>
    public AverageStateTimingsDto ReadAverageStateTimings(IStorageConnection connection)
    {
        return new AverageStateTimingsDto
        {
            AvgScheduledMs = 0d,
            AvgEnqueuedMs = WeightedMean(SafeReadHash(connection, LatencyHashKey)),
            AvgProcessingMs = WeightedMean(SafeReadHash(connection, DurationHashKey))
        };
    }

    /// <summary>Count-weighted mean over every entity stored in a duration-style hash.</summary>
    private static double WeightedMean(IReadOnlyDictionary<string, string>? hash)
    {
        if (hash == null || hash.Count == 0)
            return 0d;

        var totalCount = 0L;
        var totalSum = 0d;
        foreach (var prefix in EnumerateDurationPrefixes(hash))
        {
            var fields = ReadDurationFields(hash, prefix);
            if (fields == null)
                continue;

            totalCount += fields.Count;
            totalSum += fields.Sum;
        }

        return totalCount > 0 ? totalSum / totalCount : 0d;
    }

    public IReadOnlyList<SlowestJobDto> ReadSlowestJobs(IStorageConnection connection, int count)
    {
        var hash = SafeReadHash(connection, SlowestHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<SlowestJobDto>();

        return hash
            .Select(ParseSlowest)
            .OfType<SlowestJobDto>()
            .OrderByDescending(j => j.DurationMs)
            .Take(count)
            .ToList();
    }

    public IReadOnlyList<JobTypeFailureRateDto> ReadFailureRates(IStorageConnection connection)
    {
        var volume = SafeReadHash(connection, VolumeHashKey);
        if (volume == null || volume.Count == 0)
            return Array.Empty<JobTypeFailureRateDto>();

        var failed = SafeReadHash(connection, $"{KeyPrefix}failed_volume") ?? new Dictionary<string, string>();

        return volume.Select(entry =>
        {
            failed.TryGetValue(entry.Key, out var failedRaw);
            long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total);
            long.TryParse(failedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var failedCount);
            return new JobTypeFailureRateDto
            {
                JobType = entry.Key,
                TotalCount = total,
                FailedCount = failedCount,
                FailureRate = total > 0 ? (double)failedCount / total : 0d
            };
        }).OrderByDescending(r => r.FailureRate).ToList();
    }

    public IReadOnlyList<ExceptionSummaryDto> ReadTopExceptions(IStorageConnection connection, int count)
    {
        var hash = SafeReadHash(connection, ExceptionsHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<ExceptionSummaryDto>();

        return hash
            .Select(e =>
            {
                long.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c);
                return new ExceptionSummaryDto { ExceptionType = e.Key, Count = c };
            })
            .OrderByDescending(e => e.Count)
            .Take(count)
            .ToList();
    }

    public IReadOnlyList<RetryBucketDto> ReadRetryDistribution(IStorageConnection connection)
    {
        var hash = SafeReadHash(connection, RetryHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<RetryBucketDto>();

        return hash
            .Select(e =>
            {
                int.TryParse(e.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retry);
                long.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jobs);
                return new RetryBucketDto { RetryCount = retry, JobCount = jobs };
            })
            .OrderBy(r => r.RetryCount)
            .ToList();
    }

    public IReadOnlyList<QueueThroughputDataPoint> ReadQueueThroughput(
        IStorageConnection connection, DateTimeOffset from, DateTimeOffset to, MetricsInterval interval)
    {
        var hash = SafeReadHash(connection, QueueTpHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<QueueThroughputDataPoint>();

        var results = new List<QueueThroughputDataPoint>();
        foreach (var entry in hash)
        {
            // Fields are '{queue}:{bucketKey}'. The bucket key never contains a colon (it is a
            // yyyy-MM-dd-HH stamp) but a queue name may, so split on the last separator — splitting on
            // the first one silently dropped data points for such queues.
            var sep = entry.Key.LastIndexOf(':');
            if (sep <= 0)
                continue;

            var queue = entry.Key[..sep];
            var bucketKey = entry.Key[(sep + 1)..];
            var ts = RollupTime.ParseThroughputBucket(bucketKey, MetricsInterval.OneHour);
            if (ts < from || ts >= to)
                continue;

            long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);
            results.Add(new QueueThroughputDataPoint
            {
                Timestamp = ts,
                QueueName = queue,
                SucceededCount = count
            });
        }

        return results.OrderBy(r => r.Timestamp).ToList();
    }

    public IReadOnlyList<HourlyActivityDto> ReadHourlyActivity(IStorageConnection connection)
    {
        var hash = SafeReadHash(connection, HourlyHashKey);
        return Enumerable.Range(0, 24)
            .Select(h =>
            {
                var raw = hash != null && hash.TryGetValue(h.ToString(CultureInfo.InvariantCulture), out var found)
                    ? found
                    : null;
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);
                return new HourlyActivityDto { Hour = h, JobCount = count };
            })
            .ToList();
    }

    public IReadOnlyList<JobTypeVolumeDto> ReadJobTypeVolume(IStorageConnection connection, int count)
    {
        var hash = SafeReadHash(connection, VolumeHashKey);
        if (hash == null || hash.Count == 0)
            return Array.Empty<JobTypeVolumeDto>();

        return hash
            .Select(e =>
            {
                long.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c);
                return new JobTypeVolumeDto { JobType = e.Key, ExecutionCount = c };
            })
            .OrderByDescending(v => v.ExecutionCount)
            .Take(count)
            .ToList();
    }

    public IReadOnlyList<HistoricalScheduleBucket> ReadRecurringScheduleBuckets(
        IStorageConnection connection, DateTimeOffset from, DateTimeOffset to)
    {
        var weeks = SafeReadSet(connection, WeeksSetKey);
        var queues = new HashSet<string>(StringComparer.Ordinal);

        foreach (var weekRaw in weeks)
        {
            if (!long.TryParse(weekRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var week))
                continue;

            var weekStart = DateTimeOffset.FromUnixTimeSeconds(0).AddDays(week * 7);
            if (weekStart >= to)
                continue;

            var prefix = $"{ScheduleHashKey}:{weekRaw}:";
            foreach (var key in ListScheduleKeys(connection, weekRaw))
            {
                var queue = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : null;
                if (!string.IsNullOrEmpty(queue))
                    queues.Add(queue);
            }
        }

        var aggregated = new Dictionary<(string Queue, int Day, int Hour), ScheduleAggregate>();
        foreach (var weekRaw in weeks)
        {
            if (!long.TryParse(weekRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                continue;

            var queueSet = SafeReadSet(connection, DemandRollupKeys.QueuesSetKey);
            foreach (var queue in queueSet)
            {
                var hash = SafeReadHash(connection, $"{ScheduleHashKey}:{weekRaw}:{queue}");
                if (hash == null)
                    continue;

                var fieldPrefixes = hash.Keys
                    .Select(k =>
                    {
                        var lastColon = k.LastIndexOf(':');
                        return lastColon > 0 ? k[..lastColon] : null;
                    })
                    .OfType<string>()
                    .Where(k => k.Count(c => c == ':') == 1)
                    .Distinct(StringComparer.Ordinal);

                foreach (var field in fieldPrefixes)
                {
                    if (!TryParseScheduleField(field, out var day, out var hour))
                        continue;

                    var fire = ParseLong(hash, $"{field}:fires");
                    if (fire <= 0)
                        continue;

                    var key = (queue, day, hour);
                    if (!aggregated.TryGetValue(key, out var agg))
                        aggregated[key] = agg = new ScheduleAggregate();

                    agg.FireCount += fire;
                    agg.FailureCount += ParseLong(hash, $"{field}:failures");
                    var min = ParseDouble(hash, $"{field}:min");
                    if (min > 0)
                        agg.MinMs = agg.MinMs == 0 ? min : Math.Min(agg.MinMs, min);
                    agg.MaxMs = Math.Max(agg.MaxMs, ParseDouble(hash, $"{field}:max"));
                    agg.SumMs += ParseDouble(hash, $"{field}:sum");
                    agg.DurationCount += ParseLong(hash, $"{field}:dcount");
                    agg.Samples.AddRange(RollupMath.ParseSamples(hash.GetValueOrDefault($"{field}:samples")));
                }
            }
        }

        return aggregated
            .Select(e => BuildScheduleBucket(e.Key.Queue, e.Key.Day, e.Key.Hour, e.Value))
            .OrderBy(b => b.Queue, StringComparer.Ordinal)
            .ThenBy(b => b.DayIndex)
            .ThenBy(b => b.Hour)
            .ToList();
    }

    private IEnumerable<string> ListScheduleKeys(IStorageConnection connection, string weekRaw)
    {
        var queues = SafeReadSet(connection, DemandRollupKeys.QueuesSetKey);
        foreach (var queue in queues)
            yield return $"{ScheduleHashKey}:{weekRaw}:{queue}";
    }

    private static HistoricalScheduleBucket BuildScheduleBucket(string queue, int day, int hour, ScheduleAggregate agg)
    {
        var avg = agg.DurationCount > 0 ? agg.SumMs / agg.DurationCount : 0d;
        var p95 = agg.Samples.Count > 0 ? RollupMath.ContinuousPercentile(agg.Samples.ToArray(), 0.95) : 0d;
        return new HistoricalScheduleBucket
        {
            Queue = queue,
            DayIndex = day,
            Hour = hour,
            FireCount = agg.FireCount,
            FailureCount = agg.FailureCount,
            MinMs = agg.MinMs,
            AvgMs = avg,
            MaxMs = agg.MaxMs,
            P95Ms = p95
        };
    }

    private sealed class ScheduleAggregate
    {
        public long FireCount { get; set; }
        public long FailureCount { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }
        public double SumMs { get; set; }
        public long DurationCount { get; set; }
        public List<double> Samples { get; } = new();
    }
}
