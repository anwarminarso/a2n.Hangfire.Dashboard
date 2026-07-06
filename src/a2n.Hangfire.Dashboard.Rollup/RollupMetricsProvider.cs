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

            return (IReadOnlyList<RecurringJobHealthDto>)recurring
                .Where(r => r != null)
                .Select(r =>
                {
                    var status = RecurringJobHealthStatus.Healthy;
                    if (!string.IsNullOrEmpty(r.Error))
                        status = RecurringJobHealthStatus.Error;
                    else if (r.NextExecution.HasValue && r.NextExecution.Value < DateTime.UtcNow)
                        status = RecurringJobHealthStatus.Warning;

                    return new RecurringJobHealthDto
                    {
                        JobId = r.Id,
                        Status = status,
                        LastRunTime = r.LastExecution.HasValue
                            ? new DateTimeOffset(DateTime.SpecifyKind(r.LastExecution.Value, DateTimeKind.Utc))
                            : null,
                        ErrorMessage = r.Error,
                        LastExecutionResults = Array.Empty<bool>()
                    };
                })
                .ToList();
        }, ct);
    }

    public Task<IReadOnlyList<RecurringJobExecutionDto>> GetRecurringJobExecutionsAsync(
        string recurringJobId, int count, CancellationToken ct)
    {
        if (count <= 0) count = 10;
        if (count > 100) count = 100;

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var api = _storage.GetMonitoringApi();
            using var connection = _storage.GetConnection();
            var results = new List<RecurringJobExecutionDto>();

            void ScanSucceeded(int offset)
            {
                var page = api.SucceededJobs(offset, PageSize);
                if (page == null || page.Count == 0)
                    return;

                foreach (var entry in page)
                {
                    var param = SafeGetParameter(connection, entry.Key, "RecurringJobId");
                    if (!string.Equals(param, recurringJobId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dto = entry.Value;
                    if (dto?.SucceededAt == null)
                        continue;

                    results.Add(new RecurringJobExecutionDto
                    {
                        JobId = entry.Key,
                        ExecutedAt = new DateTimeOffset(
                            DateTime.SpecifyKind(dto.SucceededAt.Value, DateTimeKind.Utc)),
                        DurationMs = dto.TotalDuration ?? 0,
                        Succeeded = true
                    });
                }
            }

            void ScanFailed(int offset)
            {
                var page = api.FailedJobs(offset, PageSize);
                if (page == null || page.Count == 0)
                    return;

                foreach (var entry in page)
                {
                    var param = SafeGetParameter(connection, entry.Key, "RecurringJobId");
                    if (!string.Equals(param, recurringJobId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dto = entry.Value;
                    if (dto?.FailedAt == null)
                        continue;

                    results.Add(new RecurringJobExecutionDto
                    {
                        JobId = entry.Key,
                        ExecutedAt = new DateTimeOffset(
                            DateTime.SpecifyKind(dto.FailedAt.Value, DateTimeKind.Utc)),
                        DurationMs = 0,
                        Succeeded = false,
                        ErrorMessage = dto.ExceptionMessage
                    });
                }
            }

            for (var offset = 0; offset < 2000 && results.Count < count; offset += PageSize)
                ScanSucceeded(offset);
            for (var offset = 0; offset < 2000 && results.Count < count; offset += PageSize)
                ScanFailed(offset);

            return (IReadOnlyList<RecurringJobExecutionDto>)results
                .OrderByDescending(r => r.ExecutedAt)
                .Take(count)
                .ToList();
        }, ct);
    }

    public Task<AverageStateTimingsDto> GetAverageStateTimingsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => Task.FromResult(new AverageStateTimingsDto());

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

    private const int PageSize = 200;

    private Task<T> WithConnection<T>(Func<IStorageConnection, T> factory, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = _storage.GetConnection();
            return factory(connection);
        }, ct);
    }

    private static string SafeGetParameter(IStorageConnection connection, string jobId, string name)
    {
        try { return connection.GetJobParameter(jobId, name); }
        catch { return null; }
    }
}
