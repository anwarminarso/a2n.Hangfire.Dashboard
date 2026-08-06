using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Rollup;

/// <summary>
/// Rollup-based implementation of <see cref="IStorageMetricsProvider"/> for non-SQL storages.
/// </summary>
public sealed class RollupMetricsProvider : IStorageMetricsProvider
{
    private readonly JobStorage _storage;
    private readonly MetricsRollupStore _store;
    private readonly ILogger _logger;

    public RollupMetricsProvider(JobStorage storage, ILogger logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _store = new MetricsRollupStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MetricsRollupStore>.Instance);
    }

    public Task<IReadOnlyList<ThroughputDataPoint>> GetThroughputTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
        => WithConnection(connection => _store.ReadThroughput(connection, from, to, interval), ct);

    public Task<IReadOnlyList<StateTransitionDataPoint>> GetStateTransitionsAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
        => WithConnection(connection => _store.ReadStateTransitions(connection, from, to, interval), ct);

    public Task<IReadOnlyList<JobDurationStatsDto>> GetJobDurationStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => WithConnection(connection => _store.ReadJobDurationStats(connection), ct);

    public Task<IReadOnlyList<QueueLatencyStatsDto>> GetQueueLatencyStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => WithConnection(connection => _store.ReadQueueLatencyStats(connection), ct);

    public Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (count <= 0) count = 10;
        if (count > 100) count = 100;
        return WithConnection(connection => _store.ReadSlowestJobs(connection, count), ct);
    }

    public Task<IReadOnlyList<JobTypeFailureRateDto>> GetFailureRateByJobTypeAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => WithConnection(connection => _store.ReadFailureRates(connection), ct);

    public Task<IReadOnlyList<ExceptionSummaryDto>> GetTopExceptionsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (count <= 0) count = 10;
        if (count > 100) count = 100;
        return WithConnection(connection => _store.ReadTopExceptions(connection, count), ct);
    }

    public Task<IReadOnlyList<RetryBucketDto>> GetRetryDistributionAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => WithConnection(connection => _store.ReadRetryDistribution(connection), ct);

    public Task<SnapshotResult<IReadOnlyList<ServerUtilizationDto>>> GetServerUtilizationSnapshotAsync(
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var api = _storage.GetMonitoringApi();
            var servers = api.Servers();
            var processing = api.ProcessingJobs(0, int.MaxValue);

            var busyByServer = processing
                .GroupBy(p => p.Value?.ServerId ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var results = servers.Select(s =>
            {
                busyByServer.TryGetValue(s.Name ?? string.Empty, out var busy);
                var workers = s.WorkersCount;
                return new ServerUtilizationDto
                {
                    ServerName = s.Name,
                    TotalWorkers = workers,
                    BusyWorkers = Math.Min(busy, workers),
                    UtilizationPercent = workers > 0
                        ? Math.Round((double)Math.Min(busy, workers) / workers * 100.0, 1)
                        : 0.0
                };
            }).ToList();

            return new SnapshotResult<IReadOnlyList<ServerUtilizationDto>>
            {
                Data = results,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }, ct);
    }

    public Task<SnapshotResult<IReadOnlyList<QueueDepthDto>>> GetQueueDepthSnapshotAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var api = _storage.GetMonitoringApi();
            var queues = api.Queues();
            var results = queues.Select(q => new QueueDepthDto
            {
                QueueName = q.Name,
                EnqueuedCount = q.Length,
                FetchedCount = q.Fetched ?? 0
            }).ToList();

            return new SnapshotResult<IReadOnlyList<QueueDepthDto>>
            {
                Data = results,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }, ct);
    }

    public Task<IReadOnlyList<QueueThroughputDataPoint>> GetQueueThroughputAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
        => WithConnection(connection => _store.ReadQueueThroughput(connection, from, to, interval), ct);

    public Task<IReadOnlyList<RecurringJobHealthDto>> GetRecurringJobHealthAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = _storage.GetConnection();
            var recurring = connection.GetRecurringJobs();
            if (recurring == null || recurring.Count == 0)
                return (IReadOnlyList<RecurringJobHealthDto>)Array.Empty<RecurringJobHealthDto>();

            // One hash read covers every job's recent executions, so the last-results strip and the
            // average duration cost nothing extra per recurring job.
            var histories = _store.ReadAllRecurringExecutions(connection);

            return (IReadOnlyList<RecurringJobHealthDto>)recurring
                .Where(r => r != null)
                .Select(r =>
                {
                    var status = RecurringJobHealthStatus.Healthy;
                    if (!string.IsNullOrEmpty(r.Error))
                        status = RecurringJobHealthStatus.Error;
                    else if (r.NextExecution.HasValue && r.NextExecution.Value < DateTime.UtcNow)
                        status = RecurringJobHealthStatus.Warning;

                    var executions = r.Id != null && histories.TryGetValue(r.Id, out var found)
                        ? found
                        : Array.Empty<RollupAccumulator.RecurringExecutionEntry>();

                    var durations = executions.Where(e => e.DurationMs > 0).Select(e => e.DurationMs).ToList();

                    return new RecurringJobHealthDto
                    {
                        JobId = r.Id,
                        Status = status,
                        LastRunTime = r.LastExecution.HasValue
                            ? new DateTimeOffset(DateTime.SpecifyKind(r.LastExecution.Value, DateTimeKind.Utc))
                            : null,
                        AverageDurationMs = durations.Count > 0 ? durations.Average() : 0d,
                        ErrorMessage = r.Error,
                        LastExecutionResults = executions.Select(e => e.Succeeded).ToList()
                    };
                })
                .ToList();
        }, ct);
    }

    /// <summary>
    /// Serves a recurring job's execution history from the rollup ring maintained by
    /// <see cref="ExecutionRollupCollector"/>: a single hash read.
    /// </summary>
    /// <remarks>
    /// The previous implementation paged the succeeded and failed lists (up to 2000 jobs each) and
    /// probed the <c>RecurringJobId</c> parameter of every job it saw. With one call per recurring job
    /// that is O(jobs × 4000) storage round-trips, which never completes on a large Redis deployment.
    /// </remarks>
    public Task<IReadOnlyList<RecurringJobExecutionDto>> GetRecurringJobExecutionsAsync(
        string recurringJobId, int count, CancellationToken ct)
    {
        if (count <= 0) count = 10;
        if (count > 100) count = 100;

        return WithConnection(connection => _store.ReadRecurringExecutions(connection, recurringJobId, count), ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IReadOnlyList<RecurringJobExecutionDto>>> GetRecurringJobExecutionsBatchAsync(
        IReadOnlyCollection<string> recurringJobIds, int count, CancellationToken ct)
    {
        if (count <= 0) count = 10;
        if (count > 100) count = 100;

        if (recurringJobIds == null || recurringJobIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<RecurringJobExecutionDto>>>(
                new Dictionary<string, IReadOnlyList<RecurringJobExecutionDto>>(StringComparer.Ordinal));
        }

        var wanted = new HashSet<string>(recurringJobIds.Where(i => !string.IsNullOrEmpty(i)), StringComparer.Ordinal);

        return WithConnection(connection =>
        {
            var histories = _store.ReadAllRecurringExecutions(connection);
            var result = new Dictionary<string, IReadOnlyList<RecurringJobExecutionDto>>(StringComparer.Ordinal);

            foreach (var entry in histories)
            {
                if (!wanted.Contains(entry.Key))
                    continue;

                var executions = entry.Value
                    .Take(count)
                    .Select(e => new RecurringJobExecutionDto
                    {
                        JobId = e.JobId,
                        ExecutedAt = new DateTimeOffset(DateTime.SpecifyKind(e.ExecutedAtUtc, DateTimeKind.Utc)),
                        DurationMs = e.DurationMs,
                        Succeeded = e.Succeeded
                    })
                    .ToList();

                if (executions.Count > 0)
                    result[entry.Key] = executions;
            }

            return (IReadOnlyDictionary<string, IReadOnlyList<RecurringJobExecutionDto>>)result;
        }, ct);
    }

    public Task<AverageStateTimingsDto> GetAverageStateTimingsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => WithConnection(connection => _store.ReadAverageStateTimings(connection), ct);

    public Task<IReadOnlyList<HourlyActivityDto>> GetHourlyActivityPatternAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => WithConnection(connection => _store.ReadHourlyActivity(connection), ct);

    public Task<IReadOnlyList<JobTypeVolumeDto>> GetJobTypeVolumeAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (count <= 0) count = 10;
        if (count > 100) count = 100;
        return WithConnection(connection => _store.ReadJobTypeVolume(connection, count), ct);
    }

    public Task<IReadOnlyList<HistoricalScheduleBucket>> GetRecurringScheduleBucketsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => WithConnection(connection => _store.ReadRecurringScheduleBuckets(connection, from, to), ct);

    private Task<T> WithConnection<T>(Func<IStorageConnection, T> factory, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = _storage.GetConnection();
            return factory(connection);
        }, ct);
    }
}
