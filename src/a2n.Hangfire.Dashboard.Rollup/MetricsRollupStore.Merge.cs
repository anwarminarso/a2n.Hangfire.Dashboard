using System.Globalization;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Rollup.Internal;
using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Rollup;

public sealed partial class MetricsRollupStore
{
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
        var jobs = existing.Select(ParseSlowest).OfType<SlowestJobDto>().Concat(acc.SlowestJobs.Select(s => new SlowestJobDto
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
}
