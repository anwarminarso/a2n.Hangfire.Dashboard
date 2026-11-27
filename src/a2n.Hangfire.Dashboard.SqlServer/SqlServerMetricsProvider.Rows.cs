
namespace a2n.Hangfire.Dashboard.SqlServer;

public partial class SqlServerMetricsProvider
{
    #region Internal Row Types (Dapper mapping)

    private class AggregatedCounterRow
    {
        public string? Key { get; set; }
        public decimal Value { get; set; }
    }

    private class StateTransitionRow
    {
        public DateTime Bucket { get; set; }
        public string? StateName { get; set; }
        public long Cnt { get; set; }
    }

    private class DurationBasicRow
    {
        public string? JobType { get; set; }
        public double AverageMs { get; set; }
        public long MinMs { get; set; }
        public long MaxMs { get; set; }
        public long Count { get; set; }
    }

    private class PercentileRow
    {
        public string? JobType { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
    }

    private class LatencyBasicRow
    {
        public string QueueName { get; set; } = null!;
        public double AverageMs { get; set; }
        public long Cnt { get; set; }
    }

    private class LatencyPercentileRow
    {
        public string QueueName { get; set; } = null!;
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
    }

    private class SlowestJobRow
    {
        public long JobId { get; set; }
        public string? JobName { get; set; }
        public long DurationMs { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    private class FailureRateRow
    {
        public string? JobType { get; set; }
        public long TotalCount { get; set; }
        public long FailedCount { get; set; }
    }

    private class ServerRow
    {
        public string? ServerName { get; set; }
        public string? Data { get; set; }
        public DateTime LastHeartbeat { get; set; }
    }

    private class BusyWorkerRow
    {
        public string? ServerName { get; set; }
        public int BusyCount { get; set; }
    }

    private class QueueThroughputRow
    {
        public DateTime Bucket { get; set; }
        public string QueueName { get; set; } = null!;
        public long SucceededCount { get; set; }
    }

    private class HashRow
    {
        public string Key { get; set; } = null!;
        public string Field { get; set; } = null!;
        public string? Value { get; set; }
    }

    private class RecurringExecutionRow
    {
        public long JobId { get; set; }
        public DateTime ExecutedAt { get; set; }
        public long DurationMs { get; set; }
        public string? StateName { get; set; }
        public string? Reason { get; set; }
    }

    private class RecurringExecutionBatchRow : RecurringExecutionRow
    {
        public string RecurringJobIdStored { get; set; } = null!;
    }

    private class RecurringExecutionSummaryRow
    {
        public string RecurringJobIdStored { get; set; } = null!;
        public string? StateName { get; set; }
        public double DurationMs { get; set; }
    }

    private class StateTimingRow
    {
        public string StateName { get; set; } = null!;
        public double AvgMs { get; set; }
    }

    private class JobTypeVolumeRow
    {
        public string? JobType { get; set; }
        public long ExecutionCount { get; set; }
    }

    private class ScheduleBucketRow
    {
        public string QueueName { get; set; } = null!;
        public int DayIndex { get; set; }
        public int Hour { get; set; }
        public long FireCount { get; set; }
        public long FailureCount { get; set; }
        public double MinMs { get; set; }
        public double AvgMs { get; set; }
        public double MaxMs { get; set; }
    }

    private class SchedulePercentileRow
    {
        public string QueueName { get; set; } = null!;
        public int DayIndex { get; set; }
        public int Hour { get; set; }
        public double P95Ms { get; set; }
    }

    #endregion
}
