using System.Data;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Storage;
using a2n.Hangfire.Dashboard.SqlServer.Internal;
using Dapper;
using Microsoft.Data.SqlClient;

namespace a2n.Hangfire.Dashboard.SqlServer;

/// <summary>
/// SQL Server implementation of IStorageMetricsProvider.
/// Uses Dapper with parameterized T-SQL queries for analytics and metrics.
/// All queries use JSON_VALUE() for State.Data field extraction and PERCENTILE_CONT for percentiles.
/// </summary>
public partial class SqlServerMetricsProvider : IStorageMetricsProvider
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

        return StorageMetricsHelpers.AggregateThroughput(rows.Select(r => (r.Key, (long)r.Value)), from, to, interval);
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

        return StorageMetricsHelpers.BuildStateTransitions(rows.Select(r => (r.Bucket, r.StateName, r.Cnt)));
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
        // Hangfire writes Latency on Succeeded state (time from job creation to processing start).
        // Queue name comes from Job.Queue job parameter, not Succeeded state data.
        var sql = $@"
WITH LatencyData AS (
    SELECT COALESCE(
               (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
                WHERE jp.JobId = s.JobId AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
                ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
               'default') AS QueueName,
           CAST(JSON_VALUE(s.Data, '$.Latency') AS BIGINT) AS LatencyMs
    FROM {Table("State")} s
    WHERE s.Name = 'Succeeded'
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
    SELECT COALESCE(
               (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
                WHERE jp.JobId = s.JobId AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
                ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
               'default') AS QueueName,
           CAST(JSON_VALUE(s.Data, '$.Latency') AS BIGINT) AS LatencyMs
    FROM {Table("State")} s
    WHERE s.Name = 'Succeeded'
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
            var workerCount = StorageMetricsHelpers.ParseWorkerCount(server.Data);
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
        // SQL Server forbids a subquery/aggregate in the GROUP BY list (error 144),
        // so we compute QueueName once in a derived table, then aggregate over it in
        // the outer query. This mirrors the PostgreSQL provider's SUM(CASE...) shape.
        var sql = $@"
SELECT q.QueueName,
       SUM(CASE WHEN q.StateName = 'Enqueued'   THEN 1 ELSE 0 END) AS EnqueuedCount,
       SUM(CASE WHEN q.StateName = 'Processing' THEN 1 ELSE 0 END) AS FetchedCount
FROM (
    SELECT j.StateName,
           COALESCE(
               (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
                WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
                ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
               JSON_VALUE(s.Data, '$.Queue'),
               'default') AS QueueName
    FROM {Table("Job")} j
    LEFT JOIN {Table("State")} s ON s.JobId = j.Id AND s.Id = j.StateId
    WHERE j.StateName IN ('Enqueued', 'Processing')
) q
GROUP BY q.QueueName";

        using var connection = CreateConnection();
        var results = (await connection.QueryAsync<QueueDepthDto>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();

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

        // Compute Bucket + QueueName in a derived table so the outer query can GROUP BY
        // them without repeating the subquery (SQL Server error 144 forbids a subquery
        // in the GROUP BY list). Mirrors the PostgreSQL provider's shape.
        var sql = $@"
SELECT t.Bucket, t.QueueName, COUNT(*) AS SucceededCount
FROM (
    SELECT {groupExpr} AS Bucket,
           COALESCE(
               (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
                WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
                ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
               JSON_VALUE(s.Data, '$.Queue'),
               'default') AS QueueName
    FROM {Table("State")} s
    INNER JOIN {Table("Job")} j ON j.Id = s.JobId
    WHERE s.Name = 'Succeeded'
      AND s.CreatedAt >= @From AND s.CreatedAt < @To
) t
GROUP BY t.Bucket, t.QueueName
ORDER BY t.Bucket";

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
}
