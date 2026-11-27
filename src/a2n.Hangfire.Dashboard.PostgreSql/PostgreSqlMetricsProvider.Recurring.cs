using System.Globalization;
using Dapper;
using Npgsql;
using a2n.Hangfire.Dashboard.Storage;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Internal;

namespace a2n.Hangfire.Dashboard.PostgreSql;

public partial class PostgreSqlMetricsProvider
{
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

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<RecurringJobExecutionDto>>> GetRecurringJobExecutionsBatchAsync(
        IReadOnlyCollection<string> recurringJobIds, int count, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<RecurringJobExecutionDto>>(StringComparer.Ordinal);

        var ids = (recurringJobIds ?? Array.Empty<string>())
            .Where(i => !string.IsNullOrEmpty(i))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return result;

        if (count <= 0) count = 10;
        if (count > 100) count = 100;

        // One pass for every recurring job: ROW_NUMBER partitioned by the stored RecurringJobId keeps
        // the newest @Count executions per job, mirroring EnrichRecurringJobHealthAsync.
        var sql = $@"
            WITH ranked AS (
                SELECT jp.value AS ""RecurringJobIdStored"",
                       j.id::text AS ""JobId"",
                       s.createdat AS ""ExecutedAt"",
                       COALESCE((s.data::json ->> 'PerformanceDuration')::numeric, 0) AS ""DurationMs"",
                       CASE WHEN s.name = 'Succeeded' THEN true ELSE false END AS ""Succeeded"",
                       CASE WHEN s.name = 'Failed' THEN s.reason ELSE NULL END AS ""ErrorMessage"",
                       ROW_NUMBER() OVER (PARTITION BY jp.value ORDER BY s.createdat DESC) AS rn
                FROM {JobTable} j
                INNER JOIN {JobParameterTable} jp
                    ON jp.jobid = j.id AND jp.name = 'RecurringJobId'
                INNER JOIN {StateTable} s ON s.id = j.stateid
                WHERE jp.value = ANY(@RecurringJobIdValues)
                  AND s.name IN ('Succeeded', 'Failed')
            )
            SELECT ""RecurringJobIdStored"", ""JobId"", ""ExecutedAt"", ""DurationMs"", ""Succeeded"", ""ErrorMessage""
            FROM ranked
            WHERE rn <= @Count
            ORDER BY ""RecurringJobIdStored"", rn";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<RecurringExecutionBatchRow>(
            new CommandDefinition(sql,
                new { RecurringJobIdValues = JobParameterMatching.AllValueForms(ids), Count = count },
                cancellationToken: ct));

        var plainIdLookup = JobParameterMatching.BuildStoredValueToPlainIdLookup(ids);
        foreach (var group in rows.GroupBy(r =>
                     JobParameterMatching.ResolvePlainRecurringJobId(r.RecurringJobIdStored, plainIdLookup)))
        {
            result[group.Key] = group.Select(r => new RecurringJobExecutionDto
            {
                JobId = r.JobId,
                ExecutedAt = r.ExecutedAt,
                DurationMs = r.DurationMs,
                Succeeded = r.Succeeded,
                ErrorMessage = r.ErrorMessage
            }).ToList();
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HistoricalScheduleBucket>> GetRecurringScheduleBucketsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Count only recurring-originated executions: jobs that carry a 'RecurringJobId' parameter
        // (same convention as GetRecurringJobExecutionsAsync / GetRecurringJobHealthAsync). Ad-hoc
        // executions have no such parameter and are excluded here (Req 7.7).
        //
        // Buckets are queue × dayIndex × hour over [from, to):
        //   - dayIndex 0=Monday … 6=Sunday. Postgres EXTRACT(DOW) is 0=Sunday … 6=Saturday, so we
        //     shift by +6 mod 7 to get the Monday-based index the design expects.
        //   - hour is the clock hour (0–23) of the execution's state-creation time (s.createdat),
        //     matching how the other timeline queries in this adapter bucket activity.
        // Duration stats use PERCENTILE_CONT(0.95) WITHIN GROUP for p95, consistent with
        // GetJobDurationStatsAsync / GetQueueLatencyStatsAsync.
        var sql = $@"
            WITH Executions AS (
                SELECT COALESCE(
                           (SELECT jp2.value FROM {JobParameterTable} jp2
                            WHERE jp2.jobid = j.id AND jp2.name IN ({PgHelper.JobQueueParameterInList})
                            ORDER BY CASE jp2.name WHEN '{PgHelper.JobQueueParameterName}' THEN 0 ELSE 1 END
                            LIMIT 1),
                           s.data::json ->> 'Queue',
                           'default') AS queuename,
                       ((EXTRACT(DOW FROM s.createdat)::int + 6) % 7) AS dayindex,
                       EXTRACT(HOUR FROM s.createdat)::int AS hourofday,
                       CASE WHEN s.name = 'Failed' THEN 1 ELSE 0 END AS isfailure,
                       COALESCE((s.data::json ->> 'PerformanceDuration')::numeric, 0) AS durationms
                FROM {JobTable} j
                INNER JOIN {JobParameterTable} jp
                    ON jp.jobid = j.id AND jp.name = 'RecurringJobId'
                INNER JOIN {StateTable} s ON s.id = j.stateid
                WHERE s.name IN ('Succeeded', 'Failed')
                  AND s.createdat >= @From AND s.createdat < @To
            )
            SELECT queuename AS ""Queue"",
                   dayindex AS ""DayIndex"",
                   hourofday AS ""Hour"",
                   COUNT(*) AS ""FireCount"",
                   SUM(isfailure) AS ""FailureCount"",
                   MIN(durationms) AS ""MinMs"",
                   AVG(durationms) AS ""AvgMs"",
                   MAX(durationms) AS ""MaxMs"",
                   PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY durationms) AS ""P95Ms""
            FROM Executions
            GROUP BY queuename, dayindex, hourofday
            ORDER BY queuename, dayindex, hourofday";

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<HistoricalScheduleBucket>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

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
            if (!grouped.TryGetValue(dto.JobId!, out var execs) || execs.Count == 0)
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
}
