using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.SqlServer.Internal;
using Dapper;
using Microsoft.Data.SqlClient;

namespace a2n.Hangfire.Dashboard.SqlServer;

/// <summary>
/// SQL Server implementation of IStorageMetricsProvider.
/// Uses Dapper with parameterized T-SQL queries for analytics and metrics.
/// All queries use JSON_VALUE() for State.Data field extraction and PERCENTILE_CONT for percentiles.
/// </summary>
public class SqlServerMetricsProvider : IStorageMetricsProvider
{
    private readonly string _connectionString;
    private readonly string _schema;

    /// <summary>
    /// Creates a new SqlServerMetricsProvider instance.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string</param>
    /// <param name="schema">Schema name (default: "HangFire")</param>
    public SqlServerMetricsProvider(string connectionString, string schema = "HangFire")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = SqlHelper.ValidateIdentifier(schema ?? "HangFire", nameof(schema));
    }

    private SqlConnection CreateConnection() => new SqlConnection(_connectionString);

    private string Table(string tableName) => SqlHelper.Table(_schema, tableName);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThroughputDataPoint>> GetThroughputTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
    {
        var sql = $@"
SELECT [Key], [Value]
FROM {Table("AggregatedCounter")}
WHERE ([Key] LIKE 'stats:succeeded:%'
    OR [Key] LIKE 'stats:failed:%'
    OR [Key] LIKE 'stats:deleted:%')";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<AggregatedCounterRow>(
            new CommandDefinition(sql, cancellationToken: ct));

        return AggregateCounterRows(rows, from, to, interval);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateTransitionDataPoint>> GetStateTransitionsAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
    {
        var groupExpr = GetTimeGroupExpression(interval);

        var sql = $@"
SELECT {groupExpr} AS Bucket,
       s.Name AS StateName,
       COUNT(*) AS Cnt
FROM {Table("State")} s
WHERE s.CreatedAt >= @From AND s.CreatedAt < @To
GROUP BY {groupExpr}, s.Name
ORDER BY Bucket";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<StateTransitionRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return BuildStateTransitions(rows, from, to, interval);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobDurationStatsDto>> GetJobDurationStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
WITH DurationData AS (
    SELECT CONCAT(
               COALESCE(JSON_VALUE(j.InvocationData, '$.Type'), JSON_VALUE(j.InvocationData, '$.t'), ''),
               '|',
               COALESCE(JSON_VALUE(j.InvocationData, '$.Method'), JSON_VALUE(j.InvocationData, '$.m'), '')
           ) AS JobType,
           CAST(JSON_VALUE(s.Data, '$.PerformanceDuration') AS BIGINT) AS DurationMs
    FROM {Table("State")} s
    INNER JOIN {Table("Job")} j ON j.Id = s.JobId
    WHERE s.Name = 'Succeeded'
      AND s.CreatedAt >= @From AND s.CreatedAt < @To
      AND JSON_VALUE(s.Data, '$.PerformanceDuration') IS NOT NULL
)
SELECT JobType,
       AVG(CAST(DurationMs AS FLOAT)) AS AverageMs,
       MIN(DurationMs) AS MinMs,
       MAX(DurationMs) AS MaxMs,
       COUNT(*) AS [Count]
FROM DurationData
GROUP BY JobType";

        var percentileSql = $@"
WITH DurationData AS (
    SELECT CONCAT(
               COALESCE(JSON_VALUE(j.InvocationData, '$.Type'), JSON_VALUE(j.InvocationData, '$.t'), ''),
               '|',
               COALESCE(JSON_VALUE(j.InvocationData, '$.Method'), JSON_VALUE(j.InvocationData, '$.m'), '')
           ) AS JobType,
           CAST(JSON_VALUE(s.Data, '$.PerformanceDuration') AS BIGINT) AS DurationMs
    FROM {Table("State")} s
    INNER JOIN {Table("Job")} j ON j.Id = s.JobId
    WHERE s.Name = 'Succeeded'
      AND s.CreatedAt >= @From AND s.CreatedAt < @To
      AND JSON_VALUE(s.Data, '$.PerformanceDuration') IS NOT NULL
)
SELECT DISTINCT JobType,
       PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY DurationMs) OVER (PARTITION BY JobType) AS P50Ms,
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY DurationMs) OVER (PARTITION BY JobType) AS P95Ms,
       PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY DurationMs) OVER (PARTITION BY JobType) AS P99Ms
FROM DurationData";

        using var connection = CreateConnection();
        var basicStats = (await connection.QueryAsync<DurationBasicRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct)))
            .ToList();

        var percentiles = (await connection.QueryAsync<PercentileRow>(
            new CommandDefinition(percentileSql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct)))
            .ToDictionary(p => p.JobType ?? string.Empty);

        var results = new List<JobDurationStatsDto>();
        foreach (var stat in basicStats)
        {
            percentiles.TryGetValue(stat.JobType ?? string.Empty, out var pct);
            results.Add(new JobDurationStatsDto
            {
                JobType = ExtractJobTypeName(stat.JobType),
                AverageMs = stat.AverageMs,
                MinMs = stat.MinMs,
                MaxMs = stat.MaxMs,
                P50Ms = pct?.P50Ms ?? 0,
                P95Ms = pct?.P95Ms ?? 0,
                P99Ms = pct?.P99Ms ?? 0,
                Count = stat.Count
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueLatencyStatsDto>> GetQueueLatencyStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
WITH LatencyData AS (
    SELECT COALESCE(JSON_VALUE(s.Data, '$.Queue'), 'default') AS QueueName,
           CAST(JSON_VALUE(s.Data, '$.Latency') AS BIGINT) AS LatencyMs
    FROM {Table("State")} s
    WHERE s.Name = 'Processing'
      AND s.CreatedAt >= @From AND s.CreatedAt < @To
      AND JSON_VALUE(s.Data, '$.Latency') IS NOT NULL
)
SELECT QueueName,
       AVG(CAST(LatencyMs AS FLOAT)) AS AverageMs,
       COUNT(*) AS Cnt
FROM LatencyData
GROUP BY QueueName";

        var percentileSql = $@"
WITH LatencyData AS (
    SELECT COALESCE(JSON_VALUE(s.Data, '$.Queue'), 'default') AS QueueName,
           CAST(JSON_VALUE(s.Data, '$.Latency') AS BIGINT) AS LatencyMs
    FROM {Table("State")} s
    WHERE s.Name = 'Processing'
      AND s.CreatedAt >= @From AND s.CreatedAt < @To
      AND JSON_VALUE(s.Data, '$.Latency') IS NOT NULL
)
SELECT DISTINCT QueueName,
       PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY LatencyMs) OVER (PARTITION BY QueueName) AS P50Ms,
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY LatencyMs) OVER (PARTITION BY QueueName) AS P95Ms,
       PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY LatencyMs) OVER (PARTITION BY QueueName) AS P99Ms
FROM LatencyData";

        using var connection = CreateConnection();
        var basicStats = (await connection.QueryAsync<LatencyBasicRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct)))
            .ToList();

        var percentiles = (await connection.QueryAsync<LatencyPercentileRow>(
            new CommandDefinition(percentileSql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct)))
            .ToDictionary(p => p.QueueName ?? "default");

        var results = new List<QueueLatencyStatsDto>();
        foreach (var stat in basicStats)
        {
            percentiles.TryGetValue(stat.QueueName ?? "default", out var pct);
            results.Add(new QueueLatencyStatsDto
            {
                QueueName = stat.QueueName ?? "default",
                AverageMs = stat.AverageMs,
                P50Ms = pct?.P50Ms ?? 0,
                P95Ms = pct?.P95Ms ?? 0,
                P99Ms = pct?.P99Ms ?? 0
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
SELECT TOP (@Count)
       j.Id AS JobId,
       CONCAT(
           COALESCE(JSON_VALUE(j.InvocationData, '$.Type'), JSON_VALUE(j.InvocationData, '$.t'), ''),
           '|',
           COALESCE(JSON_VALUE(j.InvocationData, '$.Method'), JSON_VALUE(j.InvocationData, '$.m'), '')
       ) AS JobName,
       CAST(JSON_VALUE(s.Data, '$.PerformanceDuration') AS BIGINT) AS DurationMs,
       s.CreatedAt AS CompletedAt
FROM {Table("State")} s
INNER JOIN {Table("Job")} j ON j.Id = s.JobId
WHERE s.Name = 'Succeeded'
  AND s.CreatedAt >= @From AND s.CreatedAt < @To
  AND JSON_VALUE(s.Data, '$.PerformanceDuration') IS NOT NULL
ORDER BY CAST(JSON_VALUE(s.Data, '$.PerformanceDuration') AS BIGINT) DESC";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<SlowestJobRow>(
            new CommandDefinition(sql, new { Count = count, From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.Select(r => new SlowestJobDto
        {
            JobId = r.JobId.ToString(),
            JobName = ExtractJobTypeName(r.JobName),
            DurationMs = r.DurationMs,
            CompletedAt = r.CompletedAt
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobTypeFailureRateDto>> GetFailureRateByJobTypeAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
SELECT CONCAT(
           COALESCE(JSON_VALUE(j.InvocationData, '$.Type'), JSON_VALUE(j.InvocationData, '$.t'), ''),
           '|',
           COALESCE(JSON_VALUE(j.InvocationData, '$.Method'), JSON_VALUE(j.InvocationData, '$.m'), '')
       ) AS JobType,
       COUNT(*) AS TotalCount,
       SUM(CASE WHEN j.StateName = 'Failed' THEN 1 ELSE 0 END) AS FailedCount
FROM {Table("Job")} j
WHERE j.CreatedAt >= @From AND j.CreatedAt < @To
GROUP BY CONCAT(
           COALESCE(JSON_VALUE(j.InvocationData, '$.Type'), JSON_VALUE(j.InvocationData, '$.t'), ''),
           '|',
           COALESCE(JSON_VALUE(j.InvocationData, '$.Method'), JSON_VALUE(j.InvocationData, '$.m'), '')
       )
ORDER BY CAST(SUM(CASE WHEN j.StateName = 'Failed' THEN 1 ELSE 0 END) AS FLOAT) / NULLIF(COUNT(*), 0) DESC";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<FailureRateRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.Select(r => new JobTypeFailureRateDto
        {
            JobType = ExtractJobTypeName(r.JobType),
            TotalCount = r.TotalCount,
            FailedCount = r.FailedCount,
            FailureRate = r.TotalCount > 0 ? (double)r.FailedCount / r.TotalCount : 0.0
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExceptionSummaryDto>> GetTopExceptionsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
SELECT JSON_VALUE(s.Data, '$.ExceptionType') AS ExceptionType,
       COUNT(*) AS [Count]
FROM {Table("State")} s
WHERE s.Name = 'Failed'
  AND s.CreatedAt >= @From AND s.CreatedAt < @To
  AND JSON_VALUE(s.Data, '$.ExceptionType') IS NOT NULL
GROUP BY JSON_VALUE(s.Data, '$.ExceptionType')
ORDER BY COUNT(*) DESC
OFFSET 0 ROWS FETCH NEXT @Count ROWS ONLY";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<ExceptionSummaryDto>(
            new CommandDefinition(sql, new { Count = count, From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetryBucketDto>> GetRetryDistributionAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
SELECT CAST(COALESCE(JSON_VALUE(s.Data, '$.RetryCount'), '0') AS INT) AS RetryCount,
       COUNT(*) AS JobCount
FROM {Table("State")} s
INNER JOIN {Table("Job")} j ON j.Id = s.JobId
WHERE s.Name IN ('Succeeded', 'Failed')
  AND s.CreatedAt >= @From AND s.CreatedAt < @To
GROUP BY CAST(COALESCE(JSON_VALUE(s.Data, '$.RetryCount'), '0') AS INT)
ORDER BY RetryCount";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<RetryBucketDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<SnapshotResult<IReadOnlyList<ServerUtilizationDto>>> GetServerUtilizationSnapshotAsync(
        CancellationToken ct)
    {
        // Get active servers (heartbeat within last 5 minutes)
        var serverSql = $@"
SELECT Id AS ServerName,
       Data,
       LastHeartbeat
FROM {Table("Server")}
WHERE LastHeartbeat > DATEADD(MINUTE, -5, GETUTCDATE())";

        // Count processing jobs per server
        var busySql = $@"
SELECT COALESCE(JSON_VALUE(s.Data, '$.ServerId'), JSON_VALUE(s.Data, '$.ServerName'), '') AS ServerName,
       COUNT(*) AS BusyCount
FROM {Table("State")} s
INNER JOIN {Table("Job")} j ON j.Id = s.JobId AND j.StateId = s.Id
WHERE s.Name = 'Processing'
GROUP BY COALESCE(JSON_VALUE(s.Data, '$.ServerId'), JSON_VALUE(s.Data, '$.ServerName'), '')";

        using var connection = CreateConnection();
        var servers = (await connection.QueryAsync<ServerRow>(
            new CommandDefinition(serverSql, cancellationToken: ct))).ToList();

        var busyWorkers = (await connection.QueryAsync<BusyWorkerRow>(
            new CommandDefinition(busySql, cancellationToken: ct)))
            .ToDictionary(b => b.ServerName ?? string.Empty, b => b.BusyCount);

        var results = new List<ServerUtilizationDto>();
        foreach (var server in servers)
        {
            var workerCount = ParseWorkerCount(server.Data);
            busyWorkers.TryGetValue(server.ServerName ?? string.Empty, out var busy);

            results.Add(new ServerUtilizationDto
            {
                ServerName = server.ServerName,
                TotalWorkers = workerCount,
                BusyWorkers = Math.Min(busy, workerCount),
                UtilizationPercent = workerCount > 0
                    ? Math.Round((double)Math.Min(busy, workerCount) / workerCount * 100.0, 1)
                    : 0.0
            });
        }

        return new SnapshotResult<IReadOnlyList<ServerUtilizationDto>>
        {
            Data = results,
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<SnapshotResult<IReadOnlyList<QueueDepthDto>>> GetQueueDepthSnapshotAsync(
        CancellationToken ct)
    {
        var sql = $@"
SELECT j.StateName,
       COALESCE(
           (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
            WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
            ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
           JSON_VALUE(s.Data, '$.Queue'),
           'default') AS QueueName,
       COUNT(*) AS Cnt
FROM {Table("Job")} j
LEFT JOIN {Table("State")} s ON s.Id = j.StateId
WHERE j.StateName IN ('Enqueued', 'Processing')
GROUP BY j.StateName,
         COALESCE(
           (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
            WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
            ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
           JSON_VALUE(s.Data, '$.Queue'),
           'default')";

        using var connection = CreateConnection();
        var rows = (await connection.QueryAsync<QueueDepthRow>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();

        var grouped = rows.GroupBy(r => r.QueueName ?? "default");
        var results = grouped.Select(g => new QueueDepthDto
        {
            QueueName = g.Key,
            EnqueuedCount = g.Where(r => r.StateName == "Enqueued").Sum(r => r.Cnt),
            FetchedCount = g.Where(r => r.StateName == "Processing").Sum(r => r.Cnt)
        }).ToList();

        return new SnapshotResult<IReadOnlyList<QueueDepthDto>>
        {
            Data = results,
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueThroughputDataPoint>> GetQueueThroughputAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
    {
        var groupExpr = GetTimeGroupExpression(interval);

        var sql = $@"
SELECT {groupExpr} AS Bucket,
       COALESCE(
           (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
            WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
            ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
           JSON_VALUE(s.Data, '$.Queue'),
           'default') AS QueueName,
       COUNT(*) AS SucceededCount
FROM {Table("State")} s
INNER JOIN {Table("Job")} j ON j.Id = s.JobId
WHERE s.Name = 'Succeeded'
  AND s.CreatedAt >= @From AND s.CreatedAt < @To
GROUP BY {groupExpr},
         COALESCE(
           (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
            WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
            ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
           JSON_VALUE(s.Data, '$.Queue'),
           'default')
ORDER BY Bucket";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<QueueThroughputRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.Select(r => new QueueThroughputDataPoint
        {
            Timestamp = new DateTimeOffset(r.Bucket, TimeSpan.Zero),
            QueueName = r.QueueName ?? "default",
            SucceededCount = r.SucceededCount
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecurringJobHealthDto>> GetRecurringJobHealthAsync(
        CancellationToken ct)
    {
        // Get recurring job IDs from Set table
        var setsSql = $@"
SELECT Value AS JobId
FROM {Table("Set")}
WHERE [Key] = 'recurring-jobs'";

        using var connection = CreateConnection();
        var jobIds = (await connection.QueryAsync<string>(
            new CommandDefinition(setsSql, cancellationToken: ct))).ToList();

        if (jobIds.Count == 0)
            return Array.Empty<RecurringJobHealthDto>();

        // Get hash data for each recurring job
        var hashSql = $@"
SELECT [Key], Field, Value
FROM {Table("Hash")}
WHERE [Key] IN @Keys";

        var hashKeys = jobIds.Select(id => $"recurring-job:{id}").ToList();
        var hashRows = (await connection.QueryAsync<HashRow>(
            new CommandDefinition(hashSql, new { Keys = hashKeys }, cancellationToken: ct))).ToList();

        var hashByJob = hashRows.GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.ToDictionary(h => h.Field, h => h.Value));

        var lastResultsByJob = await GetLastExecutionResultsBatchAsync(connection, jobIds, ct);

        var results = new List<RecurringJobHealthDto>();
        foreach (var jobId in jobIds)
        {
            var hashKey = $"recurring-job:{jobId}";
            hashByJob.TryGetValue(hashKey, out var fields);
            fields ??= new Dictionary<string, string>();

            var lastExecution = ParseDateTimeOffset(fields.GetValueOrDefault("LastExecution"));
            var nextExecution = ParseDateTimeOffset(fields.GetValueOrDefault("NextExecution"));
            var error = fields.GetValueOrDefault("Error");

            var status = RecurringJobHealthStatus.Healthy;
            if (!string.IsNullOrEmpty(error))
                status = RecurringJobHealthStatus.Error;
            else if (nextExecution.HasValue && nextExecution.Value < DateTimeOffset.UtcNow)
                status = RecurringJobHealthStatus.Warning;

            lastResultsByJob.TryGetValue(jobId, out var lastResults);

            results.Add(new RecurringJobHealthDto
            {
                JobId = jobId,
                Status = status,
                LastRunTime = lastExecution,
                AverageDurationMs = 0,
                ErrorMessage = error,
                LastExecutionResults = lastResults ?? Array.Empty<bool>()
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecurringJobExecutionDto>> GetRecurringJobExecutionsAsync(
        string recurringJobId, int count, CancellationToken ct)
    {
        // Find jobs associated with this recurring job via the RecurringJobId parameter
        var sql = $@"
SELECT TOP (@Count)
       j.Id AS JobId,
       s.CreatedAt AS ExecutedAt,
       CAST(COALESCE(JSON_VALUE(s.Data, '$.PerformanceDuration'), '0') AS BIGINT) AS DurationMs,
       s.Name AS StateName,
       s.Reason
FROM {Table("Job")} j
INNER JOIN {Table("JobParameter")} jp ON jp.JobId = j.Id AND jp.Name = 'RecurringJobId'
INNER JOIN {Table("State")} s ON s.Id = j.StateId
WHERE jp.Value = @RecurringJobId
  AND s.Name IN ('Succeeded', 'Failed')
ORDER BY s.CreatedAt DESC";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<RecurringExecutionRow>(
            new CommandDefinition(sql, new { RecurringJobId = recurringJobId, Count = count }, cancellationToken: ct));

        return rows.Select(r => new RecurringJobExecutionDto
        {
            JobId = r.JobId.ToString(),
            ExecutedAt = new DateTimeOffset(r.ExecutedAt, TimeSpan.Zero),
            DurationMs = r.DurationMs,
            Succeeded = r.StateName == "Succeeded",
            ErrorMessage = r.StateName == "Failed" ? r.Reason : null
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<AverageStateTimingsDto> GetAverageStateTimingsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Calculate average time spent in each state by looking at state transitions
        var sql = $@"
WITH StateTimings AS (
    SELECT s.JobId,
           s.Name AS StateName,
           s.CreatedAt,
           LEAD(s.CreatedAt) OVER (PARTITION BY s.JobId ORDER BY s.CreatedAt) AS NextStateAt
    FROM {Table("State")} s
    WHERE s.CreatedAt >= @From AND s.CreatedAt < @To
)
SELECT StateName,
       AVG(CAST(DATEDIFF(MILLISECOND, CreatedAt, NextStateAt) AS FLOAT)) AS AvgMs
FROM StateTimings
WHERE NextStateAt IS NOT NULL
  AND StateName IN ('Scheduled', 'Enqueued', 'Processing')
GROUP BY StateName";

        using var connection = CreateConnection();
        var rows = (await connection.QueryAsync<StateTimingRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct)))
            .ToDictionary(r => r.StateName, r => r.AvgMs);

        return new AverageStateTimingsDto
        {
            AvgScheduledMs = rows.GetValueOrDefault("Scheduled"),
            AvgEnqueuedMs = rows.GetValueOrDefault("Enqueued"),
            AvgProcessingMs = rows.GetValueOrDefault("Processing")
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HourlyActivityDto>> GetHourlyActivityPatternAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
SELECT DATEPART(HOUR, s.CreatedAt) AS [Hour],
       COUNT(*) AS JobCount
FROM {Table("State")} s
WHERE s.Name = 'Succeeded'
  AND s.CreatedAt >= @From AND s.CreatedAt < @To
GROUP BY DATEPART(HOUR, s.CreatedAt)
ORDER BY [Hour]";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<HourlyActivityDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        // Fill in missing hours with zero counts
        var result = Enumerable.Range(0, 24)
            .Select(h => rows.FirstOrDefault(r => r.Hour == h) ?? new HourlyActivityDto { Hour = h, JobCount = 0 })
            .ToList();

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobTypeVolumeDto>> GetJobTypeVolumeAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
SELECT TOP (@Count)
       CONCAT(
           COALESCE(JSON_VALUE(j.InvocationData, '$.Type'), JSON_VALUE(j.InvocationData, '$.t'), ''),
           '|',
           COALESCE(JSON_VALUE(j.InvocationData, '$.Method'), JSON_VALUE(j.InvocationData, '$.m'), '')
       ) AS JobType,
       COUNT(*) AS ExecutionCount
FROM {Table("Job")} j
WHERE j.CreatedAt >= @From AND j.CreatedAt < @To
GROUP BY CONCAT(
           COALESCE(JSON_VALUE(j.InvocationData, '$.Type'), JSON_VALUE(j.InvocationData, '$.t'), ''),
           '|',
           COALESCE(JSON_VALUE(j.InvocationData, '$.Method'), JSON_VALUE(j.InvocationData, '$.m'), '')
       )
ORDER BY COUNT(*) DESC";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<JobTypeVolumeRow>(
            new CommandDefinition(sql, new { Count = count, From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.Select(r => new JobTypeVolumeDto
        {
            JobType = ExtractJobTypeName(r.JobType),
            ExecutionCount = r.ExecutionCount
        }).ToList();
    }

    #region Private Helpers

    private static string GetTimeGroupExpression(MetricsInterval interval)
    {
        return interval switch
        {
            MetricsInterval.OneMinute => "DATEADD(MINUTE, DATEDIFF(MINUTE, 0, s.CreatedAt), 0)",
            MetricsInterval.FiveMinutes => "DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, s.CreatedAt) / 5) * 5, 0)",
            MetricsInterval.FifteenMinutes => "DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, s.CreatedAt) / 15) * 15, 0)",
            MetricsInterval.OneHour => "DATEADD(HOUR, DATEDIFF(HOUR, 0, s.CreatedAt), 0)",
            MetricsInterval.OneDay => "CAST(s.CreatedAt AS DATE)",
            _ => "DATEADD(HOUR, DATEDIFF(HOUR, 0, s.CreatedAt), 0)"
        };
    }

    private IReadOnlyList<ThroughputDataPoint> AggregateCounterRows(
        IEnumerable<AggregatedCounterRow> rows, DateTimeOffset from, DateTimeOffset to, MetricsInterval interval)
    {
        var dataPoints = new Dictionary<DateTimeOffset, ThroughputDataPoint>();

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Key)) continue;

            var parts = row.Key.Split(':');
            if (parts.Length < 3) continue;

            var category = parts[1]; // succeeded, failed, deleted
            var timestampPart = string.Join(":", parts.Skip(2));

            if (!TryParseCounterTimestamp(timestampPart, out var timestamp))
                continue;

            if (timestamp < from || timestamp >= to)
                continue;

            var bucket = GetBucketTimestamp(timestamp, interval);

            if (!dataPoints.TryGetValue(bucket, out var point))
            {
                point = new ThroughputDataPoint { Timestamp = bucket };
                dataPoints[bucket] = point;
            }

            var value = (long)row.Value;
            switch (category)
            {
                case "succeeded":
                    point.Succeeded += value;
                    break;
                case "failed":
                    point.Failed += value;
                    break;
                case "deleted":
                    point.Deleted += value;
                    break;
            }
        }

        return dataPoints.Values.OrderBy(p => p.Timestamp).ToList();
    }

    private static bool TryParseCounterTimestamp(string timestampPart, out DateTimeOffset result)
    {
        result = default;

        // Try hourly format: yyyy-MM-dd-HH
        if (DateTimeOffset.TryParseExact(timestampPart, "yyyy-MM-dd-HH",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
            return true;

        // Try daily format: yyyy-MM-dd
        if (DateTimeOffset.TryParseExact(timestampPart, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
            return true;

        return false;
    }

    private static DateTimeOffset GetBucketTimestamp(DateTimeOffset timestamp, MetricsInterval interval)
    {
        return interval switch
        {
            MetricsInterval.OneMinute => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, TimeSpan.Zero),
            MetricsInterval.FiveMinutes => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, (timestamp.Minute / 5) * 5, 0, TimeSpan.Zero),
            MetricsInterval.FifteenMinutes => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, (timestamp.Minute / 15) * 15, 0, TimeSpan.Zero),
            MetricsInterval.OneHour => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, TimeSpan.Zero),
            MetricsInterval.OneDay => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                0, 0, 0, TimeSpan.Zero),
            _ => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, TimeSpan.Zero)
        };
    }

    private static IReadOnlyList<StateTransitionDataPoint> BuildStateTransitions(
        IEnumerable<StateTransitionRow> rows, DateTimeOffset from, DateTimeOffset to, MetricsInterval interval)
    {
        var dataPoints = new Dictionary<DateTime, StateTransitionDataPoint>();

        foreach (var row in rows)
        {
            if (!dataPoints.TryGetValue(row.Bucket, out var point))
            {
                point = new StateTransitionDataPoint
                {
                    Timestamp = new DateTimeOffset(row.Bucket, TimeSpan.Zero)
                };
                dataPoints[row.Bucket] = point;
            }

            switch (row.StateName)
            {
                case "Enqueued": point.Enqueued += row.Cnt; break;
                case "Processing": point.Processing += row.Cnt; break;
                case "Succeeded": point.Succeeded += row.Cnt; break;
                case "Failed": point.Failed += row.Cnt; break;
                case "Deleted": point.Deleted += row.Cnt; break;
                case "Scheduled": point.Scheduled += row.Cnt; break;
            }
        }

        return dataPoints.Values.OrderBy(p => p.Timestamp).ToList();
    }

    private static int ParseWorkerCount(string serverData)
    {
        if (string.IsNullOrEmpty(serverData)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(serverData);
            if (doc.RootElement.TryGetProperty("WorkerCount", out var workerProp))
                return workerProp.GetInt32();
        }
        catch
        {
            // Ignore parse errors
        }

        return 0;
    }

    private static string ExtractJobTypeName(string invocationData)
    {
        if (string.IsNullOrEmpty(invocationData)) return "Unknown";

        // New format: "Namespace.Class, Assembly|MethodName" (from CONCAT in SQL)
        var pipeIdx = invocationData.IndexOf('|');
        if (pipeIdx >= 0)
        {
            var typePart = invocationData[..pipeIdx];
            var methodPart = invocationData[(pipeIdx + 1)..];

            if (!string.IsNullOrEmpty(typePart))
            {
                // Strip assembly info
                var commaIdx = typePart.IndexOf(',');
                var typeName = commaIdx > 0 ? typePart[..commaIdx].Trim() : typePart.Trim();

                // Get just the class name
                var dotIdx = typeName.LastIndexOf('.');
                var className = dotIdx > 0 ? typeName[(dotIdx + 1)..] : typeName;

                if (!string.IsNullOrEmpty(methodPart))
                    return $"{className}.{methodPart}";

                return className;
            }
        }

        // Fallback: try parsing as JSON (legacy format)
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(invocationData);
            if (doc.RootElement.TryGetProperty("Type", out var typeProp))
            {
                var fullType = typeProp.GetString();
                if (!string.IsNullOrEmpty(fullType))
                {
                    var commaIdx = fullType.IndexOf(',');
                    var typeName = commaIdx > 0 ? fullType[..commaIdx] : fullType;
                    var dotIdx = typeName.LastIndexOf('.');
                    return dotIdx > 0 ? typeName[(dotIdx + 1)..] : typeName;
                }
            }
        }
        catch { }

        return "Unknown";
    }

    private static DateTimeOffset? ParseDateTimeOffset(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
            return result;

        // Try Unix timestamp (milliseconds)
        if (long.TryParse(value, out var unixMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

        return null;
    }

    private async Task<Dictionary<string, IReadOnlyList<bool>>> GetLastExecutionResultsBatchAsync(
        SqlConnection connection, IReadOnlyList<string> recurringJobIds, CancellationToken ct)
    {
        if (recurringJobIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<bool>>();

        var sql = $@"
WITH Ranked AS (
    SELECT jp.Value AS RecurringJobId,
           s.Name AS StateName,
           ROW_NUMBER() OVER (PARTITION BY jp.Value ORDER BY s.CreatedAt DESC) AS rn
    FROM {Table("Job")} j
    INNER JOIN {Table("JobParameter")} jp ON jp.JobId = j.Id AND jp.Name = 'RecurringJobId'
    INNER JOIN {Table("State")} s ON s.Id = j.StateId
    WHERE jp.Value IN @RecurringJobIds
      AND s.Name IN ('Succeeded', 'Failed')
)
SELECT RecurringJobId, StateName
FROM Ranked
WHERE rn <= 10
ORDER BY RecurringJobId, rn";

        var rows = await connection.QueryAsync<RecurringExecutionResultRow>(
            new CommandDefinition(sql, new { RecurringJobIds = recurringJobIds }, cancellationToken: ct));

        return rows
            .GroupBy(r => r.RecurringJobId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<bool>)g.Select(r => r.StateName == "Succeeded").ToList());
    }

    #endregion

    #region Internal Row Types (Dapper mapping)

    private class AggregatedCounterRow
    {
        public string Key { get; set; }
        public decimal Value { get; set; }
    }

    private class StateTransitionRow
    {
        public DateTime Bucket { get; set; }
        public string StateName { get; set; }
        public long Cnt { get; set; }
    }

    private class DurationBasicRow
    {
        public string JobType { get; set; }
        public double AverageMs { get; set; }
        public long MinMs { get; set; }
        public long MaxMs { get; set; }
        public long Count { get; set; }
    }

    private class PercentileRow
    {
        public string JobType { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
    }

    private class LatencyBasicRow
    {
        public string QueueName { get; set; }
        public double AverageMs { get; set; }
        public long Cnt { get; set; }
    }

    private class LatencyPercentileRow
    {
        public string QueueName { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
    }

    private class SlowestJobRow
    {
        public long JobId { get; set; }
        public string JobName { get; set; }
        public long DurationMs { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    private class FailureRateRow
    {
        public string JobType { get; set; }
        public long TotalCount { get; set; }
        public long FailedCount { get; set; }
    }

    private class ServerRow
    {
        public string ServerName { get; set; }
        public string Data { get; set; }
        public DateTime LastHeartbeat { get; set; }
    }

    private class BusyWorkerRow
    {
        public string ServerName { get; set; }
        public int BusyCount { get; set; }
    }

    private class QueueDepthRow
    {
        public string StateName { get; set; }
        public string QueueName { get; set; }
        public long Cnt { get; set; }
    }

    private class QueueThroughputRow
    {
        public DateTime Bucket { get; set; }
        public string QueueName { get; set; }
        public long SucceededCount { get; set; }
    }

    private class HashRow
    {
        public string Key { get; set; }
        public string Field { get; set; }
        public string Value { get; set; }
    }

    private class RecurringExecutionRow
    {
        public long JobId { get; set; }
        public DateTime ExecutedAt { get; set; }
        public long DurationMs { get; set; }
        public string StateName { get; set; }
        public string Reason { get; set; }
    }

    private class RecurringExecutionResultRow
    {
        public string RecurringJobId { get; set; }
        public string StateName { get; set; }
    }

    private class StateTimingRow
    {
        public string StateName { get; set; }
        public double AvgMs { get; set; }
    }

    private class JobTypeVolumeRow
    {
        public string JobType { get; set; }
        public long ExecutionCount { get; set; }
    }

    #endregion
}
