using System.Globalization;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Rollup.Internal;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Rollup;

/// <summary>
/// Persists and reads metrics rollup data via Hangfire storage-agnostic Hash/Set primitives.
/// </summary>
public sealed class MetricsRollupStore
{
    private const string KeyPrefix = "dashboard:rollup:";
    private const string StateHashKey = KeyPrefix + "state";
    private const string WeeksSetKey = KeyPrefix + "weeks";
    private const string ThroughputHashKey = KeyPrefix + "throughput";
    private const string StatesHashKey = KeyPrefix + "states";
    private const string DurationHashKey = KeyPrefix + "duration";
    private const string LatencyHashKey = KeyPrefix + "latency";
    private const string ScheduleHashKey = KeyPrefix + "schedule";
    private const string ExceptionsHashKey = KeyPrefix + "exceptions";
    private const string RetryHashKey = KeyPrefix + "retry";
    private const string VolumeHashKey = KeyPrefix + "volume";
    private const string HourlyHashKey = KeyPrefix + "hourly";
    private const string QueueTpHashKey = KeyPrefix + "qtp";
    private const string SlowestHashKey = KeyPrefix + "slowest";
    private const string RecurringHashKey = KeyPrefix + "recurring";

    /// <summary>How many recent executions are retained per recurring job.</summary>
    private const int MaxRecurringExecutions = 20;

    /// <summary>Suffix of the field that marks an entity prefix inside a duration-style hash.</summary>
    private const string CountSuffix = ":count";

    private const string SucceededWatermarkField = "succeededWatermarkTicks";
    private const string FailedWatermarkField = "failedWatermarkTicks";

    // Boundaries of the range a capped pass already covered, so the executions it could not reach are
    // drained by later polls instead of being skipped past (issue #29). Absent for state written by
    // earlier versions, which parses back as "no gap open".
    private const string SucceededCoveredFloorField = "succeededCoveredFloorTicks";
    private const string SucceededCoveredCeilingField = "succeededCoveredCeilingTicks";
    private const string FailedCoveredFloorField = "failedCoveredFloorTicks";
    private const string FailedCoveredCeilingField = "failedCoveredCeilingTicks";

    private readonly ILogger<MetricsRollupStore> _logger;

    public MetricsRollupStore(ILogger<MetricsRollupStore> logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MetricsRollupStore>.Instance;
    }

    public (long Succeeded, long Failed, bool HasState) ReadWatermarks(IStorageConnection connection)
    {
        var (succeeded, failed, hasState) = ReadCheckpoints(connection);
        return (succeeded.WatermarkTicks, failed.WatermarkTicks, hasState);
    }

    /// <summary>
    /// Reads the full scan position of both state lists, including the range a capped pass covered.
    /// </summary>
    internal (ScanCheckpoint Succeeded, ScanCheckpoint Failed, bool HasState) ReadCheckpoints(
        IStorageConnection connection)
    {
        var state = SafeReadHash(connection, StateHashKey);
        if (state == null || state.Count == 0)
            return (default, default, false);

        return (
            new ScanCheckpoint(
                ParseTicks(state, SucceededWatermarkField),
                ParseTicks(state, SucceededCoveredFloorField),
                ParseTicks(state, SucceededCoveredCeilingField)),
            new ScanCheckpoint(
                ParseTicks(state, FailedWatermarkField),
                ParseTicks(state, FailedCoveredFloorField),
                ParseTicks(state, FailedCoveredCeilingField)),
            true);
    }

    public void Commit(
        IStorageConnection connection,
        long succeededWatermark,
        long failedWatermark,
        RollupAccumulator accumulator,
        long currentWeek)
        => Commit(
            connection,
            ScanCheckpoint.Collapsed(succeededWatermark),
            ScanCheckpoint.Collapsed(failedWatermark),
            accumulator,
            currentWeek);

    internal void Commit(
        IStorageConnection connection,
        ScanCheckpoint succeeded,
        ScanCheckpoint failed,
        RollupAccumulator accumulator,
        long currentWeek)
    {
        var demandHashUpdates = BuildDemandHashUpdates(connection, accumulator);
        var metricsUpdates = BuildMetricsHashUpdates(connection, accumulator);

        var minKeepWeek = currentWeek - RollupTime.RetentionWeeks + 1;
        var knownWeeks = SafeReadSet(connection, WeeksSetKey);
        var knownDemandWeeks = SafeReadSet(connection, DemandRollupKeys.WeeksSetKey);
        var knownDemandQueues = SafeReadSet(connection, DemandRollupKeys.QueuesSetKey);

        var weeksToTrim = knownWeeks
            .Select(w => long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : long.MaxValue)
            .Where(w => w < minKeepWeek)
            .ToList();

        using var transaction = connection.CreateWriteTransaction();

        foreach (var update in demandHashUpdates)
            transaction.SetRangeInHash(update.Key, update.Value);

        foreach (var queue in accumulator.TouchedQueues)
            transaction.AddToSet(DemandRollupKeys.QueuesSetKey, queue);

        foreach (var week in accumulator.TouchedWeeks)
            transaction.AddToSet(DemandRollupKeys.WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));

        foreach (var update in metricsUpdates)
            transaction.SetRangeInHash(update.Key, update.Value);

        foreach (var week in accumulator.TouchedWeeks)
            transaction.AddToSet(WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));

        foreach (var week in weeksToTrim)
        {
            foreach (var queue in accumulator.TouchedQueues.Union(knownDemandQueues))
            {
                transaction.RemoveHash($"{ScheduleHashKey}:{week}:{queue}");
                transaction.RemoveHash(DemandRollupKeys.BucketHashKey(week, queue));
            }

            transaction.RemoveFromSet(WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));
            transaction.RemoveFromSet(DemandRollupKeys.WeeksSetKey, week.ToString(CultureInfo.InvariantCulture));
        }

        transaction.SetRangeInHash(StateHashKey, new[]
        {
            new KeyValuePair<string, string>(SucceededWatermarkField, Ticks(succeeded.WatermarkTicks)),
            new KeyValuePair<string, string>(SucceededCoveredFloorField, Ticks(succeeded.CoveredFloorTicks)),
            new KeyValuePair<string, string>(SucceededCoveredCeilingField, Ticks(succeeded.CoveredCeilingTicks)),
            new KeyValuePair<string, string>(FailedWatermarkField, Ticks(failed.WatermarkTicks)),
            new KeyValuePair<string, string>(FailedCoveredFloorField, Ticks(failed.CoveredFloorTicks)),
            new KeyValuePair<string, string>(FailedCoveredCeilingField, Ticks(failed.CoveredCeilingTicks)),
        });

        // The demand rollup is maintained by this collector or by DemandRollupService, never both, and
        // that service only understands the plain watermark — mirror it so a switch between adapters
        // resumes from a sane position.
        transaction.SetRangeInHash(DemandRollupKeys.StateHashKey, new[]
        {
            new KeyValuePair<string, string>(DemandRollupKeys.SucceededWatermarkField, Ticks(succeeded.WatermarkTicks)),
            new KeyValuePair<string, string>(DemandRollupKeys.FailedWatermarkField, Ticks(failed.WatermarkTicks)),
        });

        transaction.Commit();
    }

    private static string Ticks(long value) => value.ToString(CultureInfo.InvariantCulture);

    public void SeedWatermarks(IStorageConnection connection, long nowTicks)
    {
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(StateHashKey, new[]
        {
            new KeyValuePair<string, string>(SucceededWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(FailedWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
        });
        transaction.SetRangeInHash(DemandRollupKeys.StateHashKey, new[]
        {
            new KeyValuePair<string, string>(DemandRollupKeys.SucceededWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(DemandRollupKeys.FailedWatermarkField, nowTicks.ToString(CultureInfo.InvariantCulture)),
        });
        transaction.Commit();
    }

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
    private static double WeightedMean(IReadOnlyDictionary<string, string> hash)
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
            .Where(j => j != null)
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
                    .Where(k => k != null && k.Count(c => c == ':') == 1)
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

    private Dictionary<string, List<KeyValuePair<string, string>>> BuildDemandHashUpdates(
        IStorageConnection connection, RollupAccumulator accumulator)
    {
        var byHash = new Dictionary<string, Dictionary<string, RollupAccumulator.DemandSample>>(StringComparer.Ordinal);
        foreach (var kv in accumulator.DemandBuckets)
        {
            var hashKey = DemandRollupKeys.BucketHashKey(kv.Key.Week, kv.Key.Queue);
            if (!byHash.TryGetValue(hashKey, out var fields))
                byHash[hashKey] = fields = new Dictionary<string, RollupAccumulator.DemandSample>(StringComparer.Ordinal);

            fields[DemandRollupKeys.FieldName(kv.Key.DayOfWeek, kv.Key.Hour)] = kv.Value;
        }

        var updates = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        foreach (var hash in byHash)
        {
            var existing = SafeReadHash(connection, hash.Key);
            var merged = new List<KeyValuePair<string, string>>();
            foreach (var field in hash.Value)
            {
                var prior = existing != null && existing.TryGetValue(field.Key, out var raw)
                    ? RollupMath.ParseCountSum(raw)
                    : (0L, 0d);
                var combined = new RollupAccumulator.DemandSample(
                    prior.Item1 + field.Value.Count,
                    prior.Item2 + field.Value.SumDurationMs);
                merged.Add(new KeyValuePair<string, string>(
                    field.Key,
                    DemandRollupKeys.PackDemandSample(combined.Count, combined.SumDurationMs)));
            }

            updates[hash.Key] = merged;
        }

        return updates;
    }

    private Dictionary<string, List<KeyValuePair<string, string>>> BuildMetricsHashUpdates(
        IStorageConnection connection, RollupAccumulator accumulator)
    {
        var updates = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);

        MergeThroughput(connection, updates, accumulator);
        MergeStates(connection, updates, accumulator);
        MergeDurationMap(connection, updates, DurationHashKey, accumulator.DurationByJobType);
        MergeDurationMap(connection, updates, LatencyHashKey, accumulator.LatencyByQueue);
        MergeSchedule(connection, updates, accumulator);
        MergeCounterHash(connection, updates, ExceptionsHashKey, accumulator.Exceptions);
        MergeCounterHash(connection, updates, RetryHashKey,
            accumulator.RetryBuckets.ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => k.Value));
        MergeCounterHash(connection, updates, VolumeHashKey, accumulator.VolumeByJobType);
        MergeFailedVolume(connection, updates, accumulator);
        MergeHourly(connection, updates, accumulator);
        MergeQueueThroughput(connection, updates, accumulator);
        MergeSlowest(connection, updates, accumulator);
        MergeRecurringExecutions(connection, updates, accumulator);

        return updates;
    }

    private void MergeThroughput(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        var existing = SafeReadHashStatic(connection, ThroughputHashKey) ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);

        foreach (var entry in acc.ThroughputBuckets)
        {
            merged.TryGetValue(entry.Key, out var raw);
            var parts = raw?.Split('|') ?? Array.Empty<string>();
            long.TryParse(parts.ElementAtOrDefault(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s);
            long.TryParse(parts.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f);
            long.TryParse(parts.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d);

            merged[entry.Key] = $"{s + entry.Value.Succeeded}|{f + entry.Value.Failed}|{d + entry.Value.Deleted}";
        }

        updates[ThroughputHashKey] = merged.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    private void MergeStates(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        var existing = SafeReadHashStatic(connection, StatesHashKey) ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var entry in acc.StateTransitions)
        {
            merged.TryGetValue(entry.Key, out var raw);
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prior);
            merged[entry.Key] = (prior + entry.Value).ToString(CultureInfo.InvariantCulture);
        }

        updates[StatesHashKey] = merged.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    private static void MergeDurationMap(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        string hashKey,
        Dictionary<string, RollupAccumulator.DurationDelta> deltas)
    {
        if (deltas.Count == 0)
            return;

        var existing = SafeReadHashStatic(connection, hashKey) ?? new Dictionary<string, string>();
        var fields = new List<KeyValuePair<string, string>>();

        foreach (var entry in deltas)
        {
            var prefix = entry.Key;
            var prior = ReadDurationFields(existing, prefix);
            var merged = MergeDurationDelta(prior, entry.Value);
            fields.AddRange(WriteDurationFields(prefix, merged));
        }

        updates[hashKey] = fields;
    }

    private void MergeSchedule(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        foreach (var entry in acc.ScheduleBuckets)
        {
            var hashKey = $"{ScheduleHashKey}:{entry.Key.Week}:{entry.Key.Queue}";
            var existing = SafeReadHashStatic(connection, hashKey) ?? new Dictionary<string, string>();
            var field = $"{entry.Key.DayIndex}:{entry.Key.Hour}";
            updates[hashKey] = MergeScheduleField(existing, field, entry.Value);
        }
    }

    private static List<KeyValuePair<string, string>> MergeScheduleField(
        IReadOnlyDictionary<string, string> existing, string field, RollupAccumulator.ScheduleDelta delta)
    {
        var fires = ParseLong(existing, $"{field}:fires") + delta.FireCount;
        var failures = ParseLong(existing, $"{field}:failures") + delta.FailureCount;
        var dcount = ParseLong(existing, $"{field}:dcount") + delta.DurationCount;
        var sum = ParseDouble(existing, $"{field}:sum") + delta.DurationSum;
        var min = ParseDouble(existing, $"{field}:min");
        if (delta.DurationCount > 0)
            min = min == 0 ? delta.MinMs : Math.Min(min, delta.MinMs);
        var max = Math.Max(ParseDouble(existing, $"{field}:max"), delta.MaxMs);
        var samples = RollupMath.MergeReservoir(
            RollupMath.ParseSamples(existing.GetValueOrDefault($"{field}:samples")),
            delta.DurationSamples.Count > 0 ? delta.DurationSamples[^1] : 0,
            RollupMath.MaxReservoirSamples);
        foreach (var sample in delta.DurationSamples.Skip(1))
            samples = RollupMath.MergeReservoir(samples, sample);

        return new List<KeyValuePair<string, string>>
        {
            new($"{field}:fires", fires.ToString(CultureInfo.InvariantCulture)),
            new($"{field}:failures", failures.ToString(CultureInfo.InvariantCulture)),
            new($"{field}:dcount", dcount.ToString(CultureInfo.InvariantCulture)),
            new($"{field}:sum", sum.ToString("R", CultureInfo.InvariantCulture)),
            new($"{field}:min", min.ToString("R", CultureInfo.InvariantCulture)),
            new($"{field}:max", max.ToString("R", CultureInfo.InvariantCulture)),
            new($"{field}:samples", RollupMath.PackSamples(samples)),
        };
    }

    /// <summary>
    /// Merges newly observed recurring-job executions into a bounded, newest-first ring per recurring
    /// job id. Keeping the history here is what lets the provider answer per-job history with a single
    /// hash read instead of paging the succeeded/failed lists and probing a job parameter per entry.
    /// </summary>
    private static void MergeRecurringExecutions(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        if (acc.RecurringExecutions.Count == 0)
            return;

        var existing = SafeReadHashStatic(connection, RecurringHashKey) ?? new Dictionary<string, string>();
        var fields = new List<KeyValuePair<string, string>>();

        foreach (var entry in acc.RecurringExecutions)
        {
            var merged = RecurringExecutionCodec
                .Parse(existing.GetValueOrDefault(entry.Key))
                .Concat(entry.Value)
                .GroupBy(e => e.JobId ?? string.Empty, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderByDescending(e => e.ExecutedAtUtc)
                .Take(MaxRecurringExecutions)
                .ToList();

            fields.Add(new KeyValuePair<string, string>(entry.Key, RecurringExecutionCodec.Pack(merged)));
        }

        updates[RecurringHashKey] = fields;
    }

    /// <summary>
    /// Reads the retained execution history for one recurring job, newest first. A single hash read;
    /// no scanning of the succeeded/failed lists.
    /// </summary>
    public IReadOnlyList<RecurringJobExecutionDto> ReadRecurringExecutions(
        IStorageConnection connection, string recurringJobId, int count)
    {
        if (string.IsNullOrEmpty(recurringJobId))
            return Array.Empty<RecurringJobExecutionDto>();

        var hash = SafeReadHash(connection, RecurringHashKey);
        if (hash == null || !hash.TryGetValue(recurringJobId, out var packed))
            return Array.Empty<RecurringJobExecutionDto>();

        return RecurringExecutionCodec.Parse(packed)
            .OrderByDescending(e => e.ExecutedAtUtc)
            .Take(count)
            .Select(e => new RecurringJobExecutionDto
            {
                JobId = e.JobId,
                ExecutedAt = new DateTimeOffset(DateTime.SpecifyKind(e.ExecutedAtUtc, DateTimeKind.Utc)),
                DurationMs = e.DurationMs,
                Succeeded = e.Succeeded
            })
            .ToList();
    }

    /// <summary>
    /// Reads the retained execution history for every recurring job in one hash read, newest first per
    /// job. Used to fill the health view's last-results strip and average duration.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RollupAccumulator.RecurringExecutionEntry>> ReadAllRecurringExecutions(
        IStorageConnection connection)
    {
        var result = new Dictionary<string, IReadOnlyList<RollupAccumulator.RecurringExecutionEntry>>(StringComparer.Ordinal);
        var hash = SafeReadHash(connection, RecurringHashKey);
        if (hash == null || hash.Count == 0)
            return result;

        foreach (var entry in hash)
        {
            var parsed = RecurringExecutionCodec.Parse(entry.Value)
                .OrderByDescending(e => e.ExecutedAtUtc)
                .ToList();

            if (parsed.Count > 0)
                result[entry.Key] = parsed;
        }

        return result;
    }

    private static void MergeCounterHash(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        string hashKey,
        Dictionary<string, long> deltas)
    {
        if (deltas.Count == 0)
            return;

        var existing = SafeReadHashStatic(connection, hashKey) ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var entry in deltas)
        {
            merged.TryGetValue(entry.Key, out var raw);
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prior);
            merged[entry.Key] = (prior + entry.Value).ToString(CultureInfo.InvariantCulture);
        }

        updates[hashKey] = merged.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    private static void MergeFailedVolume(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        if (acc.FailedVolumeByJobType.Count == 0)
            return;

        const string hashKey = KeyPrefix + "failed_volume";
        var existing = SafeReadHashStatic(connection, hashKey) ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var entry in acc.FailedVolumeByJobType)
        {
            merged.TryGetValue(entry.Key, out var raw);
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prior);
            merged[entry.Key] = (prior + entry.Value).ToString(CultureInfo.InvariantCulture);
        }

        updates[hashKey] = merged.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    private static void MergeHourly(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        if (acc.HourlyActivity.Count == 0)
            return;

        var existing = SafeReadHashStatic(connection, HourlyHashKey) ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var entry in acc.HourlyActivity)
        {
            merged.TryGetValue(entry.Key.ToString(CultureInfo.InvariantCulture), out var raw);
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prior);
            merged[entry.Key.ToString(CultureInfo.InvariantCulture)] = (prior + entry.Value).ToString(CultureInfo.InvariantCulture);
        }

        updates[HourlyHashKey] = merged.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    private static void MergeQueueThroughput(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        if (acc.QueueThroughput.Count == 0)
            return;

        var existing = SafeReadHashStatic(connection, QueueTpHashKey) ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var queue in acc.QueueThroughput)
        {
            foreach (var bucket in queue.Value)
            {
                var key = $"{queue.Key}:{bucket.Key}";
                merged.TryGetValue(key, out var raw);
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prior);
                merged[key] = (prior + bucket.Value).ToString(CultureInfo.InvariantCulture);
            }
        }

        updates[QueueTpHashKey] = merged.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    private void MergeSlowest(
        IStorageConnection connection,
        Dictionary<string, List<KeyValuePair<string, string>>> updates,
        RollupAccumulator acc)
    {
        if (acc.SlowestJobs.Count == 0)
            return;

        var existing = SafeReadHashStatic(connection, SlowestHashKey) ?? new Dictionary<string, string>();
        var jobs = existing.Select(ParseSlowest).Where(j => j != null).Concat(acc.SlowestJobs.Select(s => new SlowestJobDto
        {
            JobId = s.JobId,
            JobName = s.JobName,
            DurationMs = s.DurationMs,
            CompletedAt = s.CompletedAt
        })).OrderByDescending(j => j.DurationMs).Take(100).ToList();

        updates[SlowestHashKey] = jobs.Select(j => new KeyValuePair<string, string>(
            j.JobId,
            $"{j.DurationMs.ToString("R", CultureInfo.InvariantCulture)}|{j.CompletedAt?.Ticks.ToString(CultureInfo.InvariantCulture)}|{j.JobName}")).ToList();
    }

    private static SlowestJobDto ParseSlowest(KeyValuePair<string, string> entry)
    {
        var parts = entry.Value?.Split('|') ?? Array.Empty<string>();
        if (parts.Length < 2)
            return null;

        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration);
        long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks);
        return new SlowestJobDto
        {
            JobId = entry.Key,
            DurationMs = duration,
            CompletedAt = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null,
            JobName = parts.Length > 2 ? parts[2] : "Unknown"
        };
    }

    /// <summary>
    /// Recovers the distinct entity prefixes (job type or queue name) stored in a duration-style hash.
    /// Fields are persisted by <see cref="WriteDurationFields"/> as <c>{prefix}:count</c>,
    /// <c>{prefix}:sum</c>, … so the prefix is the part before the trailing <c>:count</c> marker.
    /// Splitting on the first colon is not safe: prefixes such as <c>MyJob.Run</c> are free to contain
    /// separators of their own.
    /// </summary>
    private static List<string> EnumerateDurationPrefixes(IReadOnlyDictionary<string, string> hash)
        => hash.Keys
            .Where(k => k != null && k.EndsWith(CountSuffix, StringComparison.Ordinal) && k.Length > CountSuffix.Length)
            .Select(k => k[..^CountSuffix.Length])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    private static JobDurationStatsDto ParseDurationFields(
        IReadOnlyDictionary<string, string> hash, string prefix, bool isQueue = false)
    {
        var stats = ReadDurationFields(hash, prefix);
        if (stats == null || stats.Count == 0)
            return null;

        var samples = RollupMath.ParseSamples(hash.GetValueOrDefault($"{prefix}:samples"));
        var dto = new JobDurationStatsDto
        {
            JobType = prefix,
            AverageMs = stats.Sum / stats.Count,
            MinMs = stats.Min,
            MaxMs = stats.Max,
            Count = stats.Count,
            P50Ms = RollupMath.ContinuousPercentile(samples, 0.50),
            P95Ms = RollupMath.ContinuousPercentile(samples, 0.95),
            P99Ms = RollupMath.ContinuousPercentile(samples, 0.99)
        };
        return dto;
    }

    private sealed class DurationFields
    {
        public long Count { get; set; }
        public double Sum { get; set; }
        public double Min { get; set; } = double.MaxValue;
        public double Max { get; set; }
        public List<double> Samples { get; set; } = new();
    }

    private static DurationFields ReadDurationFields(IReadOnlyDictionary<string, string> hash, string prefix)
    {
        var count = ParseLong(hash, $"{prefix}:count");
        if (count == 0)
            return null;

        return new DurationFields
        {
            Count = count,
            Sum = ParseDouble(hash, $"{prefix}:sum"),
            Min = ParseDouble(hash, $"{prefix}:min"),
            Max = ParseDouble(hash, $"{prefix}:max"),
            Samples = RollupMath.ParseSamples(hash.GetValueOrDefault($"{prefix}:samples")).ToList()
        };
    }

    private static DurationFields MergeDurationDelta(DurationFields prior, RollupAccumulator.DurationDelta delta)
    {
        prior ??= new DurationFields();
        var samples = prior.Samples;
        foreach (var sample in delta.Samples)
            samples = RollupMath.MergeReservoir(samples, sample);

        return new DurationFields
        {
            Count = prior.Count + delta.Count,
            Sum = prior.Sum + delta.Sum,
            Min = prior.Count == 0 ? delta.Min : Math.Min(prior.Min, delta.Min),
            Max = Math.Max(prior.Max, delta.Max),
            Samples = samples
        };
    }

    private static IEnumerable<KeyValuePair<string, string>> WriteDurationFields(string prefix, DurationFields stats)
    {
        yield return new KeyValuePair<string, string>($"{prefix}:count", stats.Count.ToString(CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:sum", stats.Sum.ToString("R", CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:min", stats.Min.ToString("R", CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:max", stats.Max.ToString("R", CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:samples", RollupMath.PackSamples(stats.Samples));
    }

    private static bool TryParseScheduleField(string field, out int day, out int hour)
    {
        day = 0;
        hour = 0;
        var sep = field.IndexOf(':');
        if (sep <= 0)
            return false;

        return int.TryParse(field[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out day)
               && int.TryParse(field[(sep + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour);
    }

    private static long ParseLong(IReadOnlyDictionary<string, string> hash, string key)
    {
        if (hash != null && hash.TryGetValue(key, out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return v;
        return 0;
    }

    private static double ParseDouble(IReadOnlyDictionary<string, string> hash, string key)
    {
        if (hash != null && hash.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return 0d;
    }

    private static long ParseTicks(IReadOnlyDictionary<string, string> state, string field)
        => state.TryGetValue(field, out var raw)
           && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? ticks
            : 0L;

    private static Dictionary<string, string> SafeReadHashStatic(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllEntriesFromHash(key);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, string> SafeReadHash(IStorageConnection connection, string key)
        => SafeReadHashStatic(connection, key);

    private HashSet<string> SafeReadSet(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllItemsFromSet(key) ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read rollup set {Key}.", key);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
