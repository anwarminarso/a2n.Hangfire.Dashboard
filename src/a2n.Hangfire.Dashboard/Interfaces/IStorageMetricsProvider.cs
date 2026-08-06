namespace a2n.Hangfire.Dashboard.Interfaces;

using a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Valid intervals for time-series metrics queries.
/// </summary>
public enum MetricsInterval
{
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    OneHour,
    OneDay
}

/// <summary>
/// Provides analytics and metrics queries against the storage backend.
/// Optional — dashboard gracefully degrades (hides analytics) when not registered.
/// </summary>
public interface IStorageMetricsProvider
{
    /// <summary>
    /// Returns succeeded, failed, and deleted job counts per interval from the AggregatedCounter table.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="interval">Aggregation interval</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<ThroughputDataPoint>> GetThroughputTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct);

    /// <summary>
    /// Returns job state counts per interval from the State table.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="interval">Aggregation interval</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<StateTransitionDataPoint>> GetStateTransitionsAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct);

    /// <summary>
    /// Returns average, min, max, p50, p95, and p99 duration statistics per job type.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<JobDurationStatsDto>> GetJobDurationStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns queue wait time percentiles (p50, p95, p99) per queue.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<QueueLatencyStatsDto>> GetQueueLatencyStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns the top N slowest jobs ordered by PerformanceDuration descending.
    /// </summary>
    /// <param name="count">Number of results (1–100)</param>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns the failed-to-total ratio (0.0 to 1.0) per job type.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<JobTypeFailureRateDto>> GetFailureRateByJobTypeAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns the top N exception types with their occurrence count, ordered by count descending.
    /// </summary>
    /// <param name="count">Number of results (1–100)</param>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<ExceptionSummaryDto>> GetTopExceptionsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns jobs grouped by their retry count.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<RetryBucketDto>> GetRetryDistributionAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns current busy worker count versus total worker count as a snapshot with retrieval timestamp.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task<SnapshotResult<IReadOnlyList<ServerUtilizationDto>>> GetServerUtilizationSnapshotAsync(
        CancellationToken ct);

    /// <summary>
    /// Returns current enqueued job count per queue as a snapshot with retrieval timestamp.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task<SnapshotResult<IReadOnlyList<QueueDepthDto>>> GetQueueDepthSnapshotAsync(
        CancellationToken ct);

    /// <summary>
    /// Returns succeeded job counts per queue per interval.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="interval">Aggregation interval</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<QueueThroughputDataPoint>> GetQueueThroughputAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct);

    /// <summary>
    /// Returns recurring jobs with their error status and missed execution indicator.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<RecurringJobHealthDto>> GetRecurringJobHealthAsync(
        CancellationToken ct);

    /// <summary>
    /// Returns the last N executions for a recurring job ordered by execution time descending.
    /// </summary>
    /// <param name="recurringJobId">The recurring job identifier</param>
    /// <param name="count">Number of results (1–100)</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<RecurringJobExecutionDto>> GetRecurringJobExecutionsAsync(
        string recurringJobId, int count, CancellationToken ct);

    /// <summary>
    /// Returns the last N executions for several recurring jobs at once, keyed by recurring job id and
    /// ordered by execution time descending within each job. Recurring jobs without retained history
    /// are omitted from the result.
    /// </summary>
    /// <remarks>
    /// The Recurring Health view needs history for every recurring job at once. Calling
    /// <see cref="GetRecurringJobExecutionsAsync"/> in a loop costs one storage round-trip per job,
    /// which does not scale past a few dozen recurring jobs. This is a default interface method that
    /// falls back to exactly that loop, so third-party providers keep working unchanged; the bundled
    /// adapters override it with a single query or hash read.
    /// </remarks>
    /// <param name="recurringJobIds">The recurring job identifiers to fetch history for</param>
    /// <param name="count">Number of results per job (1–100)</param>
    /// <param name="ct">Cancellation token</param>
    async Task<IReadOnlyDictionary<string, IReadOnlyList<RecurringJobExecutionDto>>> GetRecurringJobExecutionsBatchAsync(
        IReadOnlyCollection<string> recurringJobIds, int count, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<RecurringJobExecutionDto>>(StringComparer.Ordinal);
        if (recurringJobIds == null || recurringJobIds.Count == 0)
            return result;

        foreach (var id in recurringJobIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var executions = await GetRecurringJobExecutionsAsync(id, count, ct).ConfigureAwait(false);
            if (executions is { Count: > 0 })
                result[id] = executions;
        }

        return result;
    }

    /// <summary>
    /// Returns average time spent in each state (Scheduled, Enqueued, Processing) for lifecycle analysis.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<AverageStateTimingsDto> GetAverageStateTimingsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns job counts grouped by hour of day (0-23) for peak hours detection.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<HourlyActivityDto>> GetHourlyActivityPatternAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns the top N most executed job types with their execution count, ordered by count descending.
    /// </summary>
    /// <param name="count">Number of results (1–100)</param>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<JobTypeVolumeDto>> GetJobTypeVolumeAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns historical recurring-schedule activity bucketed by queue × day-of-week × hour over the
    /// supplied time range. Only recurring-originated executions are counted — i.e. executions that
    /// carry a <c>RecurringJobId</c> — with per-bucket fire counts, failure counts, and duration
    /// statistics (min/avg/max/p95 in milliseconds).
    /// </summary>
    /// <remarks>
    /// This is a default interface method that returns an empty list, allowing the dashboard and
    /// any provider that does not yet support historical recurring buckets to compile and degrade
    /// gracefully. The SQL Server adapter (task 13.2) and the PostgreSQL adapter (task 13.3) override
    /// this with a Dapper query that filters to recurring-originated executions and groups by
    /// queue × dayIndex × hour.
    /// </remarks>
    /// <param name="from">Start of time range (inclusive)</param>
    /// <param name="to">End of time range (exclusive)</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<HistoricalScheduleBucket>> GetRecurringScheduleBucketsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HistoricalScheduleBucket>>(System.Array.Empty<HistoricalScheduleBucket>());
}
