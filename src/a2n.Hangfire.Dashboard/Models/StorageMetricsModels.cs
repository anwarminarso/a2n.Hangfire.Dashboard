using System;
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Throughput data point (succeeded/failed/deleted per interval).
/// </summary>
public class ThroughputDataPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public long Succeeded { get; set; }
    public long Failed { get; set; }
    public long Deleted { get; set; }
}

/// <summary>
/// State transition counts per interval.
/// </summary>
public class StateTransitionDataPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public long Enqueued { get; set; }
    public long Processing { get; set; }
    public long Succeeded { get; set; }
    public long Failed { get; set; }
    public long Deleted { get; set; }
    public long Scheduled { get; set; }
}

/// <summary>
/// Duration statistics per job type.
/// </summary>
public class JobDurationStatsDto
{
    public string JobType { get; set; }
    public double AverageMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public long Count { get; set; }
}

/// <summary>
/// Queue latency percentiles.
/// </summary>
public class QueueLatencyStatsDto
{
    public string QueueName { get; set; }
    public double AverageMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
}

/// <summary>
/// Failure rate per job type.
/// </summary>
public class JobTypeFailureRateDto
{
    public string JobType { get; set; }
    public long TotalCount { get; set; }
    public long FailedCount { get; set; }
    public double FailureRate { get; set; }  // 0.0 to 1.0
}

/// <summary>
/// Exception type summary with occurrence count.
/// </summary>
public class ExceptionSummaryDto
{
    public string ExceptionType { get; set; }
    public long Count { get; set; }
}

/// <summary>
/// Retry distribution bucket.
/// </summary>
public class RetryBucketDto
{
    public int RetryCount { get; set; }
    public long JobCount { get; set; }
}

/// <summary>
/// Server utilization snapshot.
/// </summary>
public class ServerUtilizationDto
{
    public string ServerName { get; set; }
    public int TotalWorkers { get; set; }
    public int BusyWorkers { get; set; }
    public double UtilizationPercent { get; set; }  // 0.0 to 100.0
}

/// <summary>
/// Queue depth snapshot.
/// </summary>
public class QueueDepthDto
{
    public string QueueName { get; set; }
    public long EnqueuedCount { get; set; }
    public long FetchedCount { get; set; }
}

/// <summary>
/// Queue throughput per interval.
/// </summary>
public class QueueThroughputDataPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public string QueueName { get; set; }
    public long SucceededCount { get; set; }
}

/// <summary>
/// Recurring job health status.
/// </summary>
public class RecurringJobHealthDto
{
    public string JobId { get; set; }
    public RecurringJobHealthStatus Status { get; set; }
    public DateTimeOffset? LastRunTime { get; set; }
    public double AverageDurationMs { get; set; }
    public string ErrorMessage { get; set; }
    public IReadOnlyList<bool> LastExecutionResults { get; set; } = Array.Empty<bool>();
}

/// <summary>
/// Health status for recurring jobs.
/// </summary>
public enum RecurringJobHealthStatus
{
    Healthy,
    Warning,
    Error
}

/// <summary>
/// Single execution record for a recurring job.
/// </summary>
public class RecurringJobExecutionDto
{
    public string JobId { get; set; }
    public DateTimeOffset ExecutedAt { get; set; }
    public double DurationMs { get; set; }
    public bool Succeeded { get; set; }
    public string ErrorMessage { get; set; }
}

/// <summary>
/// Average time spent in each state (lifecycle analysis).
/// </summary>
public class AverageStateTimingsDto
{
    public double AvgScheduledMs { get; set; }
    public double AvgEnqueuedMs { get; set; }
    public double AvgProcessingMs { get; set; }
}

/// <summary>
/// Hourly activity pattern (peak hours detection).
/// </summary>
public class HourlyActivityDto
{
    public int Hour { get; set; }  // 0-23
    public long JobCount { get; set; }
}

/// <summary>
/// Job type volume ranking.
/// </summary>
public class JobTypeVolumeDto
{
    public string JobType { get; set; }
    public long ExecutionCount { get; set; }
}

/// <summary>
/// Wrapper for snapshot metrics that includes capture timestamp.
/// Used for point-in-time data (server utilization, queue depth) that cannot be historicized.
/// </summary>
/// <typeparam name="T">The snapshot data type</typeparam>
public class SnapshotResult<T>
{
    public T Data { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>
/// Historical recurring-schedule bucket aggregated by queue × day-of-week × hour.
/// Counts only recurring-originated executions (those carrying a RecurringJobId), with
/// fire/failure counts and duration statistics. Populated by the SQL Server and PostgreSQL
/// storage adapters (see <see cref="Interfaces.IStorageMetricsProvider.GetRecurringScheduleBucketsAsync"/>).
/// </summary>
public class HistoricalScheduleBucket
{
    /// <summary>Queue name the bucketed executions belong to.</summary>
    public string Queue { get; set; }

    /// <summary>Day-of-week index (0 = Monday … 6 = Sunday) in the aggregation's time frame.</summary>
    public int DayIndex { get; set; }

    /// <summary>Hour of day (0–23).</summary>
    public int Hour { get; set; }

    /// <summary>Number of recurring-originated executions that fired in this bucket.</summary>
    public long FireCount { get; set; }

    /// <summary>Number of those executions that failed.</summary>
    public long FailureCount { get; set; }

    /// <summary>Minimum execution duration in milliseconds.</summary>
    public double MinMs { get; set; }

    /// <summary>Average execution duration in milliseconds.</summary>
    public double AvgMs { get; set; }

    /// <summary>Maximum execution duration in milliseconds.</summary>
    public double MaxMs { get; set; }

    /// <summary>95th-percentile execution duration in milliseconds.</summary>
    public double P95Ms { get; set; }
}
