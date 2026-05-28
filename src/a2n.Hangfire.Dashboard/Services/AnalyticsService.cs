using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Orchestrates analytics metrics calls. Checks IStorageMetricsProvider availability at runtime.
/// Provides wrapper methods for all metrics provider calls with error handling.
/// Returns empty/default values on error rather than throwing exceptions.
/// </summary>
public class AnalyticsService
{
    private readonly IStorageMetricsProvider _metricsProvider;
    private readonly MetricsQueryCache _cache;
    private readonly ILogger<AnalyticsService> _logger;

    /// <summary>
    /// Indicates whether analytics features are available (IStorageMetricsProvider is registered).
    /// </summary>
    public bool IsAvailable => _metricsProvider != null;

    public AnalyticsService(IServiceProvider serviceProvider)
    {
        // Resolve optionally — null when no metrics provider is registered
        _metricsProvider = serviceProvider.GetService<IStorageMetricsProvider>();
        _cache = serviceProvider.GetService<MetricsQueryCache>();
        _logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<AnalyticsService>()
                  ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsService>.Instance;
    }

    // Cache keys are global per process. Prefix with storage/tenant id if multi-tenant hosting is added later.
    private Task<T> QueryCachedAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct,
        bool snapshot = false)
    {
        if (_cache == null)
            return factory(ct);

        return _cache.GetOrCreateAsync(key, factory, ct, snapshot);
    }

    /// <summary>
    /// Selects the appropriate metrics interval based on the time range span.
    /// </summary>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <returns>The appropriate MetricsInterval for the given range</returns>
    public static MetricsInterval SelectInterval(DateTimeOffset from, DateTimeOffset to)
    {
        var span = to - from;

        if (span <= TimeSpan.FromHours(1))
            return MetricsInterval.OneMinute;
        if (span <= TimeSpan.FromHours(6))
            return MetricsInterval.FiveMinutes;
        if (span <= TimeSpan.FromHours(24))
            return MetricsInterval.FifteenMinutes;
        if (span <= TimeSpan.FromDays(7))
            return MetricsInterval.OneHour;

        return MetricsInterval.OneDay;
    }

    /// <summary>
    /// Computes failure rate as a percentage with 1 decimal place.
    /// </summary>
    /// <param name="failed">Number of failed jobs</param>
    /// <param name="total">Total number of jobs</param>
    /// <returns>Failure rate percentage (0.0 to 100.0), or 0.0 if total is zero</returns>
    public static double ComputeFailureRatePercent(long failed, long total)
    {
        if (total <= 0)
            return 0.0;

        var rate = (double)failed / total * 100.0;
        return Math.Round(rate, 1);
    }

    // ─── Throughput & State Transitions ─────────────────────────────────────────

    /// <summary>
    /// Returns succeeded, failed, and deleted job counts per interval.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<ThroughputDataPoint>> GetThroughputTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<ThroughputDataPoint>();

        try
        {
            return await QueryCachedAsync(
                $"throughput:{from.UtcTicks}:{to.UtcTicks}:{interval}",
                token => _metricsProvider.GetThroughputTimelineAsync(from, to, interval, token),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get throughput timeline from metrics provider");
            return Array.Empty<ThroughputDataPoint>();
        }
    }

    /// <summary>
    /// Returns job state counts per interval.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<StateTransitionDataPoint>> GetStateTransitionsAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<StateTransitionDataPoint>();

        try
        {
            return await QueryCachedAsync(
                $"state-transitions:{from.UtcTicks}:{to.UtcTicks}:{interval}",
                token => _metricsProvider.GetStateTransitionsAsync(from, to, interval, token),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get state transitions from metrics provider");
            return Array.Empty<StateTransitionDataPoint>();
        }
    }

    // ─── Duration & Latency ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns duration statistics per job type.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<JobDurationStatsDto>> GetJobDurationStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<JobDurationStatsDto>();

        try
        {
            return await QueryCachedAsync(
                $"duration-stats:{from.UtcTicks}:{to.UtcTicks}",
                token => _metricsProvider.GetJobDurationStatsAsync(from, to, token),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job duration stats from metrics provider");
            return Array.Empty<JobDurationStatsDto>();
        }
    }

    /// <summary>
    /// Returns queue latency percentiles per queue.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<QueueLatencyStatsDto>> GetQueueLatencyStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<QueueLatencyStatsDto>();

        try
        {
            return await QueryCachedAsync(
                $"queue-latency:{from.UtcTicks}:{to.UtcTicks}",
                token => _metricsProvider.GetQueueLatencyStatsAsync(from, to, token),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue latency stats from metrics provider");
            return Array.Empty<QueueLatencyStatsDto>();
        }
    }

    /// <summary>
    /// Returns the top N slowest jobs by duration.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<SlowestJobDto>();

        try
        {
            return await _metricsProvider.GetSlowestJobsAsync(count, from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get slowest jobs from metrics provider");
            return Array.Empty<SlowestJobDto>();
        }
    }

    // ─── Failure Analysis ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns failure rate per job type.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<JobTypeFailureRateDto>> GetFailureRateByJobTypeAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<JobTypeFailureRateDto>();

        try
        {
            return await _metricsProvider.GetFailureRateByJobTypeAsync(from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get failure rate by job type from metrics provider");
            return Array.Empty<JobTypeFailureRateDto>();
        }
    }

    /// <summary>
    /// Returns the top N exception types with occurrence count.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<ExceptionSummaryDto>> GetTopExceptionsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<ExceptionSummaryDto>();

        try
        {
            return await _metricsProvider.GetTopExceptionsAsync(count, from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get top exceptions from metrics provider");
            return Array.Empty<ExceptionSummaryDto>();
        }
    }

    /// <summary>
    /// Returns retry distribution (jobs grouped by retry count).
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<RetryBucketDto>> GetRetryDistributionAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<RetryBucketDto>();

        try
        {
            return await _metricsProvider.GetRetryDistributionAsync(from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get retry distribution from metrics provider");
            return Array.Empty<RetryBucketDto>();
        }
    }

    // ─── Snapshots ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns current server utilization snapshot.
    /// Returns empty snapshot on error.
    /// </summary>
    public async Task<SnapshotResult<IReadOnlyList<ServerUtilizationDto>>> GetServerUtilizationSnapshotAsync(
        CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return new SnapshotResult<IReadOnlyList<ServerUtilizationDto>>
            {
                Data = Array.Empty<ServerUtilizationDto>(),
                CapturedAt = DateTimeOffset.UtcNow
            };

        try
        {
            return await QueryCachedAsync(
                "server-utilization",
                token => _metricsProvider.GetServerUtilizationSnapshotAsync(token),
                ct,
                snapshot: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get server utilization snapshot from metrics provider");
            return new SnapshotResult<IReadOnlyList<ServerUtilizationDto>>
            {
                Data = Array.Empty<ServerUtilizationDto>(),
                CapturedAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>
    /// Returns current queue depth snapshot.
    /// Returns empty snapshot on error.
    /// </summary>
    public async Task<SnapshotResult<IReadOnlyList<QueueDepthDto>>> GetQueueDepthSnapshotAsync(
        CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return new SnapshotResult<IReadOnlyList<QueueDepthDto>>
            {
                Data = Array.Empty<QueueDepthDto>(),
                CapturedAt = DateTimeOffset.UtcNow
            };

        try
        {
            return await QueryCachedAsync(
                "queue-depth",
                token => _metricsProvider.GetQueueDepthSnapshotAsync(token),
                ct,
                snapshot: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue depth snapshot from metrics provider");
            return new SnapshotResult<IReadOnlyList<QueueDepthDto>>
            {
                Data = Array.Empty<QueueDepthDto>(),
                CapturedAt = DateTimeOffset.UtcNow
            };
        }
    }

    // ─── Queue Throughput ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns succeeded job counts per queue per interval.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<QueueThroughputDataPoint>> GetQueueThroughputAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<QueueThroughputDataPoint>();

        try
        {
            return await _metricsProvider.GetQueueThroughputAsync(from, to, interval, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue throughput from metrics provider");
            return Array.Empty<QueueThroughputDataPoint>();
        }
    }

    // ─── Recurring Job Health ───────────────────────────────────────────────────

    /// <summary>
    /// Returns recurring jobs with their health status.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<RecurringJobHealthDto>> GetRecurringJobHealthAsync(
        CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<RecurringJobHealthDto>();

        try
        {
            return await _metricsProvider.GetRecurringJobHealthAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recurring job health from metrics provider");
            return Array.Empty<RecurringJobHealthDto>();
        }
    }

    /// <summary>
    /// Returns the last N executions for a recurring job.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<RecurringJobExecutionDto>> GetRecurringJobExecutionsAsync(
        string recurringJobId, int count, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<RecurringJobExecutionDto>();

        try
        {
            return await _metricsProvider.GetRecurringJobExecutionsAsync(recurringJobId, count, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recurring job executions for '{JobId}' from metrics provider", recurringJobId);
            return Array.Empty<RecurringJobExecutionDto>();
        }
    }

    // ─── Lifecycle & Activity ───────────────────────────────────────────────────

    /// <summary>
    /// Returns average time spent in each state for lifecycle analysis.
    /// Returns zeroed timings on error.
    /// </summary>
    public async Task<AverageStateTimingsDto> GetAverageStateTimingsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return new AverageStateTimingsDto();

        try
        {
            return await _metricsProvider.GetAverageStateTimingsAsync(from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get average state timings from metrics provider");
            return new AverageStateTimingsDto();
        }
    }

    /// <summary>
    /// Returns job counts grouped by hour of day (0-23) for peak hours detection.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<HourlyActivityDto>> GetHourlyActivityPatternAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<HourlyActivityDto>();

        try
        {
            return await _metricsProvider.GetHourlyActivityPatternAsync(from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get hourly activity pattern from metrics provider");
            return Array.Empty<HourlyActivityDto>();
        }
    }

    /// <summary>
    /// Returns the top N most executed job types with their execution count.
    /// Returns empty list on error.
    /// </summary>
    public async Task<IReadOnlyList<JobTypeVolumeDto>> GetJobTypeVolumeAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_metricsProvider == null)
            return Array.Empty<JobTypeVolumeDto>();

        try
        {
            return await _metricsProvider.GetJobTypeVolumeAsync(count, from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job type volume from metrics provider");
            return Array.Empty<JobTypeVolumeDto>();
        }
    }
}
