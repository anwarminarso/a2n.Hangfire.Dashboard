using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using a2n.Hangfire.Dashboard.Storage;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Internal;

namespace a2n.Hangfire.Dashboard.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IStorageMetricsProvider"/>.
/// Uses Dapper with Npgsql for all database operations.
/// All queries are parameterized — zero string concatenation of user input.
/// </summary>
public class PostgreSqlMetricsProvider : IStorageMetricsProvider
{
    private readonly string _connectionString;
    private readonly string _schema;

    public PostgreSqlMetricsProvider(string connectionString, string schema = "hangfire")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = PgHelper.ValidateIdentifier(schema ?? "hangfire", nameof(schema));
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

    private string StateTable => PgHelper.Table(_schema, "state");
    private string JobTable => PgHelper.Table(_schema, "job");
    private string ServerTable => PgHelper.Table(_schema, "server");
    private string CounterTable => PgHelper.Table(_schema, "counter");
    private string SetTable => PgHelper.Table(_schema, "set");
    private string HashTable => PgHelper.Table(_schema, "hash");
    private string JobParameterTable => PgHelper.Table(_schema, "jobparameter");

    private static string GetIntervalTruncation(MetricsInterval interval) => interval switch
    {
        MetricsInterval.OneMinute => "date_trunc('minute', s.createdat)",
        MetricsInterval.FiveMinutes =>
            "date_trunc('hour', s.createdat) + (EXTRACT(MINUTE FROM s.createdat)::int / 5) * interval '5 minutes'",
        MetricsInterval.FifteenMinutes =>
            "date_trunc('hour', s.createdat) + (EXTRACT(MINUTE FROM s.createdat)::int / 15) * interval '15 minutes'",
        MetricsInterval.OneHour => "date_trunc('hour', s.createdat)",
        MetricsInterval.OneDay => "date_trunc('day', s.createdat)",
        _ => "date_trunc('hour', s.createdat)"
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThroughputDataPoint>> GetThroughputTimelineAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
    {
        // Date filtering is done in C# after parse — daily keys (yyyy-MM-dd) and hourly keys
        // (yyyy-MM-dd-HH) are not lexicographically comparable with a single stamp range.
        var sql = $@"
            SELECT key, value
            FROM {CounterTable}
            WHERE (key LIKE 'stats:succeeded:%'
                OR key LIKE 'stats:failed:%'
                OR key LIKE 'stats:deleted:%')
              AND (expireat IS NULL OR expireat > NOW())";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<CounterRow>(
            new CommandDefinition(sql, cancellationToken: ct));

        var dataPoints = new Dictionary<DateTimeOffset, ThroughputDataPoint>();

        foreach (var row in rows)
        {
            var parsed = ParseCounterKey(row.Key);
            if (parsed == null) continue;

            var (category, timestamp) = parsed.Value;
            if (timestamp < from || timestamp >= to) continue;

            var bucket = TruncateToInterval(timestamp, interval);

            if (!dataPoints.TryGetValue(bucket, out var dp))
            {
                dp = new ThroughputDataPoint { Timestamp = bucket };
                dataPoints[bucket] = dp;
            }

            var value = (long)row.Value;
            switch (category)
            {
                case "succeeded": dp.Succeeded += value; break;
                case "failed": dp.Failed += value; break;
                case "deleted": dp.Deleted += value; break;
            }
        }

        return dataPoints.Values.OrderBy(d => d.Timestamp).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StateTransitionDataPoint>> GetStateTransitionsAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
    {
        var truncExpr = GetIntervalTruncation(interval);

        var sql = $@"
            SELECT {truncExpr} AS bucket,
                   s.name AS statename,
                   COUNT(*) AS count
            FROM {StateTable} s
            WHERE s.createdat >= @From AND s.createdat < @To
            GROUP BY {truncExpr}, s.name
            ORDER BY bucket";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<StateTransitionRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        var dataPoints = new Dictionary<DateTimeOffset, StateTransitionDataPoint>();

        foreach (var row in rows)
        {
            var bucket = new DateTimeOffset(DateTime.SpecifyKind(row.Bucket, DateTimeKind.Utc), TimeSpan.Zero);
            if (!dataPoints.TryGetValue(bucket, out var dp))
            {
                dp = new StateTransitionDataPoint { Timestamp = bucket };
                dataPoints[bucket] = dp;
            }

            switch (row.StateName)
            {
                case "Enqueued": dp.Enqueued += row.Count; break;
                case "Processing": dp.Processing += row.Count; break;
                case "Succeeded": dp.Succeeded += row.Count; break;
                case "Failed": dp.Failed += row.Count; break;
                case "Deleted": dp.Deleted += row.Count; break;
                case "Scheduled": dp.Scheduled += row.Count; break;
            }
        }

        return dataPoints.Values.OrderBy(d => d.Timestamp).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobDurationStatsDto>> GetJobDurationStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var typeMethod = PgHelper.InvocationDataTypeMethodSql();

        var sql = $@"
            WITH DurationData AS (
                SELECT {typeMethod} AS jobtype,
                       (s.data::json ->> 'PerformanceDuration')::numeric AS durationms
                FROM {StateTable} s
                INNER JOIN {JobTable} j ON j.id = s.jobid
                WHERE s.name = 'Succeeded'
                  AND s.createdat >= @From AND s.createdat < @To
                  AND s.data::json ->> 'PerformanceDuration' IS NOT NULL
            )
            SELECT jobtype AS ""JobType"",
                   AVG(durationms) AS ""AverageMs"",
                   MIN(durationms) AS ""MinMs"",
                   MAX(durationms) AS ""MaxMs"",
                   PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY durationms) AS ""P50Ms"",
                   PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY durationms) AS ""P95Ms"",
                   PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY durationms) AS ""P99Ms"",
                   COUNT(*) AS ""Count""
            FROM DurationData
            GROUP BY jobtype";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<JobDurationStatsDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        var results = rows.ToList();
        foreach (var r in results)
            r.JobType = PgHelper.ExtractJobTypeName(r.JobType);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueLatencyStatsDto>> GetQueueLatencyStatsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
            WITH LatencyData AS (
                SELECT COALESCE(
                           (SELECT jp.value FROM {JobParameterTable} jp
                            WHERE jp.jobid = s.jobid AND jp.name IN ({PgHelper.JobQueueParameterInList})
                            ORDER BY CASE jp.name WHEN '{PgHelper.JobQueueParameterName}' THEN 0 ELSE 1 END
                            LIMIT 1),
                           'default') AS queuename,
                       (s.data::json ->> 'Latency')::numeric AS latencyms
                FROM {StateTable} s
                WHERE s.name = 'Succeeded'
                  AND s.createdat >= @From AND s.createdat < @To
                  AND s.data::json ->> 'Latency' IS NOT NULL
            )
            SELECT queuename AS ""QueueName"",
                   AVG(latencyms) AS ""AverageMs"",
                   PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY latencyms) AS ""P50Ms"",
                   PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY latencyms) AS ""P95Ms"",
                   PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY latencyms) AS ""P99Ms""
            FROM LatencyData
            GROUP BY queuename";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<QueueLatencyStatsDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
            SELECT j.id::text AS ""JobId"",
                   CONCAT(
                       COALESCE(j.invocationdata::json ->> 'Type', j.invocationdata::json ->> 't', ''),
                       '|',
                       COALESCE(j.invocationdata::json ->> 'Method', j.invocationdata::json ->> 'm', '')
                   ) AS ""JobName"",
                   (s.data::json ->> 'PerformanceDuration')::numeric AS ""DurationMs"",
                   s.createdat AS ""CompletedAt""
            FROM {StateTable} s
            INNER JOIN {JobTable} j ON j.id = s.jobid
            WHERE s.name = 'Succeeded'
              AND s.createdat >= @From AND s.createdat < @To
              AND s.data::json ->> 'PerformanceDuration' IS NOT NULL
            ORDER BY (s.data::json ->> 'PerformanceDuration')::numeric DESC
            LIMIT @Count";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<SlowestJobDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime, Count = count }, cancellationToken: ct));

        // Extract readable class.method name from the pipe-separated type|method string
        var results = rows.ToList();
        foreach (var r in results)
            r.JobName = PgHelper.ExtractJobTypeName(r.JobName);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobTypeFailureRateDto>> GetFailureRateByJobTypeAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var typeMethod = PgHelper.InvocationDataTypeMethodSql();

        var sql = $@"
            WITH JobCounts AS (
                SELECT {typeMethod} AS jobtype,
                       COUNT(*) AS totalcount,
                       SUM(CASE WHEN j.statename = 'Failed' THEN 1 ELSE 0 END) AS failedcount
                FROM {JobTable} j
                WHERE j.createdat >= @From AND j.createdat < @To
                GROUP BY {typeMethod}
            )
            SELECT jobtype AS ""JobType"",
                   totalcount AS ""TotalCount"",
                   failedcount AS ""FailedCount"",
                   CASE WHEN totalcount > 0
                        THEN failedcount::double precision / totalcount
                        ELSE 0.0
                   END AS ""FailureRate""
            FROM JobCounts
            ORDER BY ""FailureRate"" DESC";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<JobTypeFailureRateDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        var results = rows.ToList();
        foreach (var r in results)
            r.JobType = PgHelper.ExtractJobTypeName(r.JobType);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExceptionSummaryDto>> GetTopExceptionsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
            SELECT s.data::json ->> 'ExceptionType' AS ""ExceptionType"",
                   COUNT(*) AS ""Count""
            FROM {StateTable} s
            WHERE s.name = 'Failed'
              AND s.createdat >= @From AND s.createdat < @To
              AND s.data::json ->> 'ExceptionType' IS NOT NULL
            GROUP BY s.data::json ->> 'ExceptionType'
            ORDER BY ""Count"" DESC
            LIMIT @Count";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<ExceptionSummaryDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime, Count = count }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetryBucketDto>> GetRetryDistributionAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
            SELECT COALESCE((s.data::json ->> 'RetryCount')::int, 0) AS ""RetryCount"",
                   COUNT(*) AS ""JobCount""
            FROM {StateTable} s
            WHERE s.name IN ('Succeeded', 'Failed')
              AND s.createdat >= @From AND s.createdat < @To
            GROUP BY COALESCE((s.data::json ->> 'RetryCount')::int, 0)
            ORDER BY ""RetryCount""";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<RetryBucketDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<SnapshotResult<IReadOnlyList<ServerUtilizationDto>>> GetServerUtilizationSnapshotAsync(
        CancellationToken ct)
    {
        var serverSql = $@"
            SELECT id AS servername,
                   data,
                   lastheartbeat
            FROM {ServerTable}
            WHERE lastheartbeat > NOW() - INTERVAL '5 minutes'";

        var busySql = $@"
            SELECT COALESCE(s.data::json ->> 'ServerId', s.data::json ->> 'ServerName', '') AS servername,
                   COUNT(*) AS busycount
            FROM {StateTable} s
            INNER JOIN {JobTable} j ON j.id = s.jobid
            WHERE s.name = 'Processing'
              AND s.id = j.stateid
            GROUP BY COALESCE(s.data::json ->> 'ServerId', s.data::json ->> 'ServerName', '')";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var servers = (await connection.QueryAsync<ServerRow>(
            new CommandDefinition(serverSql, cancellationToken: ct))).ToList();

        var busyWorkers = (await connection.QueryAsync<BusyWorkerRow>(
            new CommandDefinition(busySql, cancellationToken: ct)))
            .ToDictionary(r => r.ServerName ?? "", r => r.BusyCount);

        var results = new List<ServerUtilizationDto>();
        foreach (var server in servers)
        {
            var totalWorkers = ParseWorkerCount(server.Data);
            var busy = busyWorkers.GetValueOrDefault(server.ServerName ?? "", 0);
            var cappedBusy = totalWorkers > 0 ? Math.Min(busy, totalWorkers) : busy;
            var utilization = totalWorkers > 0
                ? Math.Round((double)cappedBusy / totalWorkers * 100.0, 1)
                : 0.0;

            results.Add(new ServerUtilizationDto
            {
                ServerName = server.ServerName,
                TotalWorkers = totalWorkers,
                BusyWorkers = cappedBusy,
                UtilizationPercent = utilization
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
            SELECT COALESCE(
                       (SELECT jp.value FROM {JobParameterTable} jp
                        WHERE jp.jobid = j.id AND jp.name IN ({PgHelper.JobQueueParameterInList})
                        ORDER BY CASE jp.name WHEN '{PgHelper.JobQueueParameterName}' THEN 0 ELSE 1 END
                        LIMIT 1),
                       s.data::json ->> 'Queue',
                       'default') AS ""QueueName"",
                   SUM(CASE WHEN j.statename = 'Enqueued' THEN 1 ELSE 0 END) AS ""EnqueuedCount"",
                   SUM(CASE WHEN j.statename = 'Processing' THEN 1 ELSE 0 END) AS ""FetchedCount""
            FROM {JobTable} j
            LEFT JOIN {StateTable} s ON s.id = j.stateid
            WHERE j.statename IN ('Enqueued', 'Processing')
            GROUP BY 1";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<QueueDepthDto>(
            new CommandDefinition(sql, cancellationToken: ct));

        return new SnapshotResult<IReadOnlyList<QueueDepthDto>>
        {
            Data = rows.ToList(),
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueThroughputDataPoint>> GetQueueThroughputAsync(
        DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
    {
        var truncExpr = GetIntervalTruncation(interval);

        var sql = $@"
            SELECT {truncExpr} AS bucket,
                   COALESCE(
                       (SELECT jp.value FROM {JobParameterTable} jp
                        WHERE jp.jobid = j.id AND jp.name IN ({PgHelper.JobQueueParameterInList})
                        ORDER BY CASE jp.name WHEN '{PgHelper.JobQueueParameterName}' THEN 0 ELSE 1 END
                        LIMIT 1),
                       s.data::json ->> 'Queue',
                       'default') AS queuename,
                   COUNT(*) AS succeededcount
            FROM {StateTable} s
            INNER JOIN {JobTable} j ON j.id = s.jobid
            WHERE s.name = 'Succeeded'
              AND s.createdat >= @From AND s.createdat < @To
            GROUP BY {truncExpr}, 2
            ORDER BY bucket";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<QueueThroughputRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.Select(r => new QueueThroughputDataPoint
        {
            Timestamp = new DateTimeOffset(DateTime.SpecifyKind(r.Bucket, DateTimeKind.Utc), TimeSpan.Zero),
            QueueName = r.QueueName,
            SucceededCount = r.SucceededCount
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecurringJobHealthDto>> GetRecurringJobHealthAsync(
        CancellationToken ct)
    {
        var setsSql = $@"
            SELECT value
            FROM {SetTable}
            WHERE key = 'recurring-jobs'";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var jobIds = (await connection.QueryAsync<string>(
            new CommandDefinition(setsSql, cancellationToken: ct))).ToList();

        if (jobIds.Count == 0)
            return Array.Empty<RecurringJobHealthDto>();

        var hashSql = $@"
            SELECT key, field, value
            FROM {HashTable}
            WHERE key = ANY(@Keys)";

        var hashKeys = jobIds.Select(id => $"recurring-job:{id}").ToArray();
        var hashRows = (await connection.QueryAsync<HashRow>(
            new CommandDefinition(hashSql, new { Keys = hashKeys }, cancellationToken: ct))).ToList();

        var results = new List<RecurringJobHealthDto>();
        foreach (var jobId in jobIds)
        {
            var prefix = $"recurring-job:{jobId}";
            var fields = hashRows
                .Where(h => h.Key == prefix)
                .ToDictionary(h => h.Field, h => h.Value);

            var lastExecution = fields.GetValueOrDefault("LastExecution");
            var error = fields.GetValueOrDefault("Error");

            DateTimeOffset? lastRunTime = null;
            if (!string.IsNullOrEmpty(lastExecution) &&
                DateTime.TryParse(lastExecution, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime))
            {
                lastRunTime = new DateTimeOffset(parsedTime, TimeSpan.Zero);
            }

            var status = RecurringJobHealthStatus.Healthy;
            if (!string.IsNullOrEmpty(error))
                status = RecurringJobHealthStatus.Error;

            results.Add(new RecurringJobHealthDto
            {
                JobId = jobId,
                Status = status,
                LastRunTime = lastRunTime,
                AverageDurationMs = 0,
                ErrorMessage = error,
                LastExecutionResults = Array.Empty<bool>()
            });
        }

        await EnrichRecurringJobHealthAsync(connection, results, jobIds, ct);

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecurringJobExecutionDto>> GetRecurringJobExecutionsAsync(
        string recurringJobId, int count, CancellationToken ct)
    {
        var sql = $@"
            SELECT j.id::text AS ""JobId"",
                   s.createdat AS ""ExecutedAt"",
                   COALESCE((s.data::json ->> 'PerformanceDuration')::numeric, 0) AS ""DurationMs"",
                   CASE WHEN s.name = 'Succeeded' THEN true ELSE false END AS ""Succeeded"",
                   CASE WHEN s.name = 'Failed' THEN s.reason ELSE NULL END AS ""ErrorMessage""
            FROM {JobTable} j
            INNER JOIN {JobParameterTable} jp
                ON jp.jobid = j.id AND jp.name = 'RecurringJobId'
            INNER JOIN {StateTable} s ON s.id = j.stateid
            WHERE jp.value = ANY(@RecurringJobIdValues)
              AND s.name IN ('Succeeded', 'Failed')
            ORDER BY s.createdat DESC
            LIMIT @Count";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<RecurringJobExecutionDto>(
            new CommandDefinition(sql,
                new
                {
                    RecurringJobIdValues = JobParameterMatching.AllValueForms(new[] { recurringJobId }),
                    Count = count
                },
                cancellationToken: ct));

        return rows.ToList();
    }

    private async Task EnrichRecurringJobHealthAsync(
        NpgsqlConnection connection,
        List<RecurringJobHealthDto> results,
        IReadOnlyList<string> jobIds,
        CancellationToken ct)
    {
        if (results.Count == 0 || jobIds.Count == 0)
            return;

        var sql = $@"
            WITH Ranked AS (
                SELECT jp.value AS RecurringJobIdStored,
                       s.name AS StateName,
                       COALESCE((s.data::json ->> 'PerformanceDuration')::numeric, 0) AS DurationMs,
                       ROW_NUMBER() OVER (PARTITION BY jp.value ORDER BY s.createdat DESC) AS rn
                FROM {JobTable} j
                INNER JOIN {JobParameterTable} jp ON jp.jobid = j.id AND jp.name = 'RecurringJobId'
                INNER JOIN {StateTable} s ON s.id = j.stateid
                WHERE jp.value = ANY(@RecurringJobIdValues)
                  AND s.name IN ('Succeeded', 'Failed')
            )
            SELECT RecurringJobIdStored, StateName, DurationMs
            FROM Ranked
            WHERE rn <= 20
            ORDER BY RecurringJobIdStored, rn";

        var rows = await connection.QueryAsync<RecurringExecutionSummaryRow>(
            new CommandDefinition(sql,
                new { RecurringJobIdValues = JobParameterMatching.AllValueForms(jobIds) },
                cancellationToken: ct));

        var plainIdLookup = JobParameterMatching.BuildStoredValueToPlainIdLookup(jobIds);
        var grouped = new Dictionary<string, List<RecurringExecutionSummaryRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var plainId = JobParameterMatching.ResolvePlainRecurringJobId(row.RecurringJobIdStored, plainIdLookup);
            if (!grouped.TryGetValue(plainId, out var list))
                grouped[plainId] = list = new List<RecurringExecutionSummaryRow>();
            list.Add(row);
        }

        foreach (var dto in results)
        {
            if (!grouped.TryGetValue(dto.JobId, out var execs) || execs.Count == 0)
                continue;

            dto.LastExecutionResults = execs.Take(10).Select(e => e.StateName == "Succeeded").ToList();

            var durations = execs.Where(e => e.DurationMs > 0).Select(e => (double)e.DurationMs).ToList();
            if (durations.Count > 0)
                dto.AverageDurationMs = durations.Average();
        }
    }

    /// <inheritdoc />
    public async Task<AverageStateTimingsDto> GetAverageStateTimingsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
            WITH StateTimings AS (
                SELECT s.jobid,
                       s.name AS statename,
                       s.createdat,
                       LEAD(s.createdat) OVER (PARTITION BY s.jobid ORDER BY s.createdat) AS nextat
                FROM {StateTable} s
                WHERE s.createdat >= @From AND s.createdat < @To
                  AND s.name IN ('Scheduled', 'Enqueued', 'Processing')
            )
            SELECT
                COALESCE(AVG(CASE WHEN statename = 'Scheduled' AND nextat IS NOT NULL
                    THEN EXTRACT(EPOCH FROM (nextat - createdat)) * 1000.0
                    ELSE NULL END), 0) AS ""AvgScheduledMs"",
                COALESCE(AVG(CASE WHEN statename = 'Enqueued' AND nextat IS NOT NULL
                    THEN EXTRACT(EPOCH FROM (nextat - createdat)) * 1000.0
                    ELSE NULL END), 0) AS ""AvgEnqueuedMs"",
                COALESCE(AVG(CASE WHEN statename = 'Processing' AND nextat IS NOT NULL
                    THEN EXTRACT(EPOCH FROM (nextat - createdat)) * 1000.0
                    ELSE NULL END), 0) AS ""AvgProcessingMs""
            FROM StateTimings";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var result = await connection.QuerySingleOrDefaultAsync<AverageStateTimingsDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return result ?? new AverageStateTimingsDto();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HourlyActivityDto>> GetHourlyActivityPatternAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = $@"
            SELECT EXTRACT(HOUR FROM s.createdat)::int AS ""Hour"",
                   COUNT(*) AS ""JobCount""
            FROM {StateTable} s
            WHERE s.name = 'Succeeded'
              AND s.createdat >= @From AND s.createdat < @To
            GROUP BY EXTRACT(HOUR FROM s.createdat)::int
            ORDER BY ""Hour""";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<HourlyActivityDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        var byHour = rows.ToDictionary(r => r.Hour, r => r.JobCount);
        var results = new List<HourlyActivityDto>();
        for (var hour = 0; hour < 24; hour++)
        {
            results.Add(new HourlyActivityDto
            {
                Hour = hour,
                JobCount = byHour.GetValueOrDefault(hour, 0)
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobTypeVolumeDto>> GetJobTypeVolumeAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var typeMethod = PgHelper.InvocationDataTypeMethodSql();

        var sql = $@"
            SELECT {typeMethod} AS ""JobType"",
                   COUNT(*) AS ""ExecutionCount""
            FROM {JobTable} j
            WHERE j.createdat >= @From AND j.createdat < @To
            GROUP BY {typeMethod}
            ORDER BY ""ExecutionCount"" DESC
            LIMIT @Count";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<JobTypeVolumeDto>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime, Count = count }, cancellationToken: ct));

        var results = rows.ToList();
        foreach (var r in results)
            r.JobType = PgHelper.ExtractJobTypeName(r.JobType);
        return results;
    }

    #region Private Helpers

    private static (string Category, DateTimeOffset Timestamp)? ParseCounterKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        // Format: stats:{category}:{date} or stats:{category}:{date}-{hour}
        var parts = key.Split(':');
        if (parts.Length < 3) return null;

        var category = parts[1]; // succeeded, failed, deleted
        var dateStr = string.Join(":", parts.Skip(2));

        // Try hourly format: yyyy-MM-dd-HH
        if (dateStr.Length == 13 &&
            DateTime.TryParseExact(dateStr, "yyyy-MM-dd-HH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hourlyDate))
        {
            return (category, new DateTimeOffset(hourlyDate, TimeSpan.Zero));
        }

        // Try daily format: yyyy-MM-dd
        if (dateStr.Length == 10 &&
            DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dailyDate))
        {
            return (category, new DateTimeOffset(dailyDate, TimeSpan.Zero));
        }

        return null;
    }

    private static DateTimeOffset TruncateToInterval(DateTimeOffset timestamp, MetricsInterval interval)
    {
        return interval switch
        {
            MetricsInterval.OneMinute => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, timestamp.Offset),
            MetricsInterval.FiveMinutes => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, (timestamp.Minute / 5) * 5, 0, timestamp.Offset),
            MetricsInterval.FifteenMinutes => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, (timestamp.Minute / 15) * 15, 0, timestamp.Offset),
            MetricsInterval.OneHour => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, timestamp.Offset),
            MetricsInterval.OneDay => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                0, 0, 0, timestamp.Offset),
            _ => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, timestamp.Offset)
        };
    }

    private static int ParseWorkerCount(string data)
    {
        if (string.IsNullOrEmpty(data)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("WorkerCount", out var workerCount))
            {
                return workerCount.GetInt32();
            }
        }
        catch
        {
            // Ignore JSON parse errors
        }

        return 0;
    }

    #endregion

    #region Internal Row Types

    private class CounterRow
    {
        public string Key { get; set; }
        public decimal Value { get; set; }
    }

    private class StateTransitionRow
    {
        public DateTime Bucket { get; set; }
        public string StateName { get; set; }
        public long Count { get; set; }
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

    private class RecurringExecutionSummaryRow
    {
        public string RecurringJobIdStored { get; set; }
        public string StateName { get; set; }
        public decimal DurationMs { get; set; }
    }

    #endregion
}
