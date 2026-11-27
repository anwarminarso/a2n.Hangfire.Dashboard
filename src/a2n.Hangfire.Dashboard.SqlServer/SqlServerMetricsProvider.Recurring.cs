using System.Data;
using a2n.Hangfire.Dashboard.Storage;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.SqlServer.Internal;
using Dapper;

namespace a2n.Hangfire.Dashboard.SqlServer;

public partial class SqlServerMetricsProvider
{
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

        var results = new List<RecurringJobHealthDto>();
        foreach (var jobId in jobIds)
        {
            var hashKey = $"recurring-job:{jobId}";
            hashByJob.TryGetValue(hashKey, out var fields);
            fields ??= new Dictionary<string, string?>();

            var lastExecution = ParseDateTimeOffset(fields.GetValueOrDefault("LastExecution"));
            var nextExecution = ParseDateTimeOffset(fields.GetValueOrDefault("NextExecution"));
            var error = fields.GetValueOrDefault("Error");

            var status = RecurringJobHealthStatus.Healthy;
            if (!string.IsNullOrEmpty(error))
                status = RecurringJobHealthStatus.Error;
            else if (nextExecution.HasValue && nextExecution.Value < DateTimeOffset.UtcNow)
                status = RecurringJobHealthStatus.Warning;

            results.Add(new RecurringJobHealthDto
            {
                JobId = jobId,
                Status = status,
                LastRunTime = lastExecution,
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
INNER JOIN {Table("State")} s ON s.JobId = j.Id AND s.Id = j.StateId
WHERE jp.Value IN @RecurringJobIdValues
  AND s.Name IN ('Succeeded', 'Failed')
ORDER BY s.CreatedAt DESC";

        using var connection = CreateConnection();
        var rows = await connection.QueryAsync<RecurringExecutionRow>(
            new CommandDefinition(sql,
                new { RecurringJobIdValues = JobParameterMatching.AllValueForms(new[] { recurringJobId }), Count = count },
                cancellationToken: ct));

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
WITH Ranked AS (
    SELECT jp.Value AS RecurringJobIdStored,
           j.Id AS JobId,
           s.CreatedAt AS ExecutedAt,
           CAST(COALESCE(JSON_VALUE(s.Data, '$.PerformanceDuration'), '0') AS BIGINT) AS DurationMs,
           s.Name AS StateName,
           s.Reason,
           ROW_NUMBER() OVER (PARTITION BY jp.Value ORDER BY s.CreatedAt DESC) AS rn
    FROM {Table("Job")} j
    INNER JOIN {Table("JobParameter")} jp ON jp.JobId = j.Id AND jp.Name = 'RecurringJobId'
    INNER JOIN {Table("State")} s ON s.JobId = j.Id AND s.Id = j.StateId
    WHERE jp.Value IN @RecurringJobIdValues
      AND s.Name IN ('Succeeded', 'Failed')
)
SELECT RecurringJobIdStored, JobId, ExecutedAt, DurationMs, StateName, Reason
FROM Ranked
WHERE rn <= @Count
ORDER BY RecurringJobIdStored, rn";

        using var connection = CreateConnection();
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
                JobId = r.JobId.ToString(),
                ExecutedAt = new DateTimeOffset(r.ExecutedAt, TimeSpan.Zero),
                DurationMs = r.DurationMs,
                Succeeded = r.StateName == "Succeeded",
                ErrorMessage = r.StateName == "Failed" ? r.Reason : null
            }).ToList();
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HistoricalScheduleBucket>> GetRecurringScheduleBucketsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Count ONLY recurring-originated executions: jobs carrying a 'RecurringJobId' parameter
        // (INNER JOIN to JobParameter). Ad-hoc executions are excluded (Req 7.3, 7.7).
        // Each job is counted once via its current state (j.StateId = s.Id) and bucketed by
        // queue × dayIndex (0 = Monday … 6 = Sunday) × hour (0–23). The day index uses
        // DATEDIFF from a known Monday (1900-01-01) so it is independent of @@DATEFIRST / language.
        // Queue resolution mirrors the other metrics queries (Job.Queue / CurrentQueue parameter,
        // then State.Data '$.Queue', then 'default'); the subquery sits in a derived table so the
        // outer query can GROUP BY the computed column (SQL Server error 144 forbids it inline).
        var sql = $@"
WITH Executions AS (
    SELECT COALESCE(
               (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
                WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
                ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
               JSON_VALUE(s.Data, '$.Queue'),
               'default') AS QueueName,
           (DATEDIFF(DAY, '19000101', s.CreatedAt) % 7) AS DayIndex,
           DATEPART(HOUR, s.CreatedAt) AS [Hour],
           s.Name AS StateName,
           CAST(JSON_VALUE(s.Data, '$.PerformanceDuration') AS BIGINT) AS DurationMs
    FROM {Table("State")} s
    INNER JOIN {Table("Job")} j ON j.Id = s.JobId AND j.StateId = s.Id
    INNER JOIN {Table("JobParameter")} rp ON rp.JobId = j.Id AND rp.Name = 'RecurringJobId'
    WHERE s.Name IN ('Succeeded', 'Failed')
      AND s.CreatedAt >= @From AND s.CreatedAt < @To
)
SELECT QueueName,
       DayIndex,
       [Hour],
       COUNT(*) AS FireCount,
       SUM(CASE WHEN StateName = 'Failed' THEN 1 ELSE 0 END) AS FailureCount,
       COALESCE(MIN(CAST(DurationMs AS FLOAT)), 0) AS MinMs,
       COALESCE(AVG(CAST(DurationMs AS FLOAT)), 0) AS AvgMs,
       COALESCE(MAX(CAST(DurationMs AS FLOAT)), 0) AS MaxMs
FROM Executions
GROUP BY QueueName, DayIndex, [Hour]";

        // PERCENTILE_CONT is a window (not aggregate) function, so p95 is computed in a separate
        // pass partitioned by the same bucket key and merged in-memory (matches GetJobDurationStatsAsync).
        var percentileSql = $@"
WITH Executions AS (
    SELECT COALESCE(
               (SELECT TOP 1 jp.Value FROM {Table("JobParameter")} jp
                WHERE jp.JobId = j.Id AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
                ORDER BY CASE jp.Name WHEN '{SqlHelper.JobQueueParameterName}' THEN 0 ELSE 1 END),
               JSON_VALUE(s.Data, '$.Queue'),
               'default') AS QueueName,
           (DATEDIFF(DAY, '19000101', s.CreatedAt) % 7) AS DayIndex,
           DATEPART(HOUR, s.CreatedAt) AS [Hour],
           CAST(JSON_VALUE(s.Data, '$.PerformanceDuration') AS BIGINT) AS DurationMs
    FROM {Table("State")} s
    INNER JOIN {Table("Job")} j ON j.Id = s.JobId AND j.StateId = s.Id
    INNER JOIN {Table("JobParameter")} rp ON rp.JobId = j.Id AND rp.Name = 'RecurringJobId'
    WHERE s.Name IN ('Succeeded', 'Failed')
      AND s.CreatedAt >= @From AND s.CreatedAt < @To
      AND JSON_VALUE(s.Data, '$.PerformanceDuration') IS NOT NULL
)
SELECT DISTINCT QueueName,
       DayIndex,
       [Hour],
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY DurationMs)
           OVER (PARTITION BY QueueName, DayIndex, [Hour]) AS P95Ms
FROM Executions";

        using var connection = CreateConnection();
        var basicRows = (await connection.QueryAsync<ScheduleBucketRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct)))
            .ToList();

        var percentiles = (await connection.QueryAsync<SchedulePercentileRow>(
            new CommandDefinition(percentileSql, new { From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct)))
            .ToDictionary(p => (p.QueueName ?? "default", p.DayIndex, p.Hour));

        var results = new List<HistoricalScheduleBucket>(basicRows.Count);
        foreach (var row in basicRows)
        {
            var queue = row.QueueName ?? "default";
            percentiles.TryGetValue((queue, row.DayIndex, row.Hour), out var pct);

            results.Add(new HistoricalScheduleBucket
            {
                Queue = queue,
                DayIndex = row.DayIndex,
                Hour = row.Hour,
                FireCount = row.FireCount,
                FailureCount = row.FailureCount,
                MinMs = row.MinMs,
                AvgMs = row.AvgMs,
                MaxMs = row.MaxMs,
                P95Ms = pct?.P95Ms ?? 0
            });
        }

        return results;
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
}
