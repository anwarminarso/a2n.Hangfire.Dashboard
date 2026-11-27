using Dapper;
using Npgsql;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Internal;

namespace a2n.Hangfire.Dashboard.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IStorageMetricsProvider"/>.
/// Uses Dapper with Npgsql for all database operations.
/// All queries are parameterized — zero string concatenation of user input.
/// </summary>
public partial class PostgreSqlMetricsProvider : IStorageMetricsProvider
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
}
