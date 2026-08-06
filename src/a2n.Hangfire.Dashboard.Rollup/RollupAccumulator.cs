namespace a2n.Hangfire.Dashboard.Rollup;

/// <summary>A single processed job execution fed into the rollup accumulator.</summary>
public sealed class ProcessedExecution
{
    public string JobId { get; init; }
    public DateTime ExecutedAtUtc { get; init; }
    public bool Succeeded { get; init; }
    public string JobType { get; init; }
    public string Queue { get; init; }
    public string RecurringJobId { get; init; }
    public double DurationMs { get; init; }
    public double LatencyMs { get; init; }
    public string ExceptionType { get; set; }
    public int RetryCount { get; init; }
    public string JobName { get; init; }
    public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurringJobId);
    public bool IsAdHoc => !IsRecurring;
}

/// <summary>In-memory deltas produced by one collector poll.</summary>
public sealed class RollupAccumulator
{
    public Dictionary<(long Week, string Queue, int DayOfWeek, int Hour), DemandSample> DemandBuckets { get; } = new();
    public Dictionary<string, ThroughputDelta> ThroughputBuckets { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> StateTransitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DurationDelta> DurationByJobType { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DurationDelta> LatencyByQueue { get; } = new(StringComparer.Ordinal);
    public Dictionary<(long Week, string Queue, int DayIndex, int Hour), ScheduleDelta> ScheduleBuckets { get; } = new();
    public Dictionary<string, long> Exceptions { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, long> RetryBuckets { get; } = new();
    public Dictionary<string, long> VolumeByJobType { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> FailedVolumeByJobType { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Recent executions per recurring job id, used to serve execution history and the
    /// last-N result strip without scanning the succeeded/failed lists.
    /// </summary>
    public Dictionary<string, List<RecurringExecutionEntry>> RecurringExecutions { get; } = new(StringComparer.Ordinal);

    public Dictionary<int, long> HourlyActivity { get; } = new();
    public Dictionary<string, Dictionary<string, long>> QueueThroughput { get; } = new(StringComparer.Ordinal);
    public List<SlowestJobEntry> SlowestJobs { get; } = new();
    public HashSet<string> TouchedQueues { get; } = new(StringComparer.Ordinal);
    public HashSet<long> TouchedWeeks { get; } = new();

    public readonly struct DemandSample
    {
        public DemandSample(long count, double sumDurationMs)
        {
            Count = count;
            SumDurationMs = sumDurationMs;
        }

        public long Count { get; }
        public double SumDurationMs { get; }
    }

    public sealed class ThroughputDelta
    {
        public long Succeeded { get; set; }
        public long Failed { get; set; }
        public long Deleted { get; set; }
    }

    public sealed class DurationDelta
    {
        public long Count { get; set; }
        public double Sum { get; set; }
        public double Min { get; set; } = double.MaxValue;
        public double Max { get; set; }
        public List<double> Samples { get; } = new();
    }

    public sealed class ScheduleDelta
    {
        public long FireCount { get; set; }
        public long FailureCount { get; set; }
        public long DurationCount { get; set; }
        public double DurationSum { get; set; }
        public double MinMs { get; set; } = double.MaxValue;
        public double MaxMs { get; set; }
        public List<double> DurationSamples { get; } = new();
    }

    public sealed class SlowestJobEntry
    {
        public string JobId { get; init; }
        public string JobName { get; init; }
        public double DurationMs { get; init; }
        public DateTime CompletedAt { get; init; }
    }

    /// <summary>One recorded execution of a recurring job, kept in a bounded per-job ring.</summary>
    public sealed class RecurringExecutionEntry
    {
        public string JobId { get; init; }
        public DateTime ExecutedAtUtc { get; init; }
        public bool Succeeded { get; init; }
        public double DurationMs { get; init; }
    }

    public void Record(ProcessedExecution exec)
    {
        var utc = exec.ExecutedAtUtc;
        var week = Internal.RollupTime.WeekIndex(utc);
        TouchedWeeks.Add(week);

        var queue = string.IsNullOrWhiteSpace(exec.Queue) ? "default" : exec.Queue;
        TouchedQueues.Add(queue);

        var hour = utc.Hour;
        var tpKey = Internal.RollupTime.ThroughputBucketKey(utc, Interfaces.MetricsInterval.OneHour);
        if (!ThroughputBuckets.TryGetValue(tpKey, out var tp))
            ThroughputBuckets[tpKey] = tp = new ThroughputDelta();

        var stateKey = $"h:{tpKey}:{(exec.Succeeded ? "Succeeded" : "Failed")}";
        StateTransitions.TryGetValue(stateKey, out var stCount);
        StateTransitions[stateKey] = stCount + 1;

        if (exec.Succeeded)
        {
            tp.Succeeded++;
            if (!string.IsNullOrEmpty(exec.JobType))
            {
                RecordDuration(DurationByJobType, exec.JobType, exec.DurationMs);
                VolumeByJobType.TryGetValue(exec.JobType, out var vol);
                VolumeByJobType[exec.JobType] = vol + 1;
            }

            if (exec.LatencyMs > 0)
                RecordDuration(LatencyByQueue, queue, exec.LatencyMs);

            HourlyActivity.TryGetValue(hour, out var hc);
            HourlyActivity[hour] = hc + 1;

            if (!QueueThroughput.TryGetValue(queue, out var qtp))
                QueueThroughput[queue] = qtp = new Dictionary<string, long>(StringComparer.Ordinal);
            qtp.TryGetValue(tpKey, out var qCount);
            qtp[tpKey] = qCount + 1;

            if (exec.DurationMs > 0)
            {
                SlowestJobs.Add(new SlowestJobEntry
                {
                    JobId = exec.JobId,
                    JobName = exec.JobName ?? exec.JobType ?? "Unknown",
                    DurationMs = exec.DurationMs,
                    CompletedAt = utc
                });
            }
        }
        else
        {
            tp.Failed++;
            if (!string.IsNullOrEmpty(exec.ExceptionType))
            {
                Exceptions.TryGetValue(exec.ExceptionType, out var exCount);
                Exceptions[exec.ExceptionType] = exCount + 1;
            }

            if (!string.IsNullOrEmpty(exec.JobType))
            {
                VolumeByJobType.TryGetValue(exec.JobType, out var vol);
                VolumeByJobType[exec.JobType] = vol + 1;
                FailedVolumeByJobType.TryGetValue(exec.JobType, out var fv);
                FailedVolumeByJobType[exec.JobType] = fv + 1;
            }
        }

        RetryBuckets.TryGetValue(exec.RetryCount, out var rb);
        RetryBuckets[exec.RetryCount] = rb + 1;

        if (exec.IsAdHoc)
        {
            var dayOfWeek = (int)utc.DayOfWeek;
            var demandKey = (week, queue, dayOfWeek, hour);
            DemandBuckets.TryGetValue(demandKey, out var demand);
            DemandBuckets[demandKey] = new DemandSample(
                demand.Count + 1,
                demand.SumDurationMs + (exec.Succeeded ? exec.DurationMs : 0d));
        }

        if (exec.IsRecurring)
        {
            if (!RecurringExecutions.TryGetValue(exec.RecurringJobId, out var executions))
                RecurringExecutions[exec.RecurringJobId] = executions = new List<RecurringExecutionEntry>();

            executions.Add(new RecurringExecutionEntry
            {
                JobId = exec.JobId,
                ExecutedAtUtc = utc,
                Succeeded = exec.Succeeded,
                DurationMs = exec.Succeeded ? exec.DurationMs : 0d
            });

            var dayIndex = Internal.RollupTime.DayIndexMondayZero(utc);
            var schedKey = (week, queue, dayIndex, hour);
            if (!ScheduleBuckets.TryGetValue(schedKey, out var sched))
                ScheduleBuckets[schedKey] = sched = new ScheduleDelta();

            sched.FireCount++;
            if (!exec.Succeeded)
                sched.FailureCount++;

            if (exec.Succeeded && exec.DurationMs > 0)
            {
                sched.DurationCount++;
                sched.DurationSum += exec.DurationMs;
                if (exec.DurationMs < sched.MinMs) sched.MinMs = exec.DurationMs;
                if (exec.DurationMs > sched.MaxMs) sched.MaxMs = exec.DurationMs;
                sched.DurationSamples.Add(exec.DurationMs);
            }
        }
    }

    private static void RecordDuration(Dictionary<string, DurationDelta> map, string key, double ms)
    {
        if (!map.TryGetValue(key, out var delta))
            map[key] = delta = new DurationDelta();

        delta.Count++;
        delta.Sum += ms;
        if (ms < delta.Min) delta.Min = ms;
        if (ms > delta.Max) delta.Max = ms;
        delta.Samples.Add(ms);
    }
}
