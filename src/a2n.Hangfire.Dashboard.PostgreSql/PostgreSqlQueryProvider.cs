using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Internal;
using Dapper;
using Npgsql;

namespace a2n.Hangfire.Dashboard.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IStorageQueryProvider"/> using Dapper and Npgsql.
/// All queries are parameterized — zero string concatenation of user input.
/// Uses ILIKE for case-insensitive matching and ->> for JSON field extraction.
/// </summary>
public class PostgreSqlQueryProvider : IStorageQueryProvider
{
    private readonly string _connectionString;
    private readonly string _schema;

    // Pre-built table references (schema is trusted config, not user input)
    private readonly string _jobTable;
    private readonly string _stateTable;
    private readonly string _setTable;
    private readonly string _jobParameterTable;

    /// <summary>
    /// Creates a new PostgreSQL query provider.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="schema">Schema name (default: "hangfire")</param>
    public PostgreSqlQueryProvider(string connectionString, string schema = "hangfire")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = schema ?? "hangfire";

        _jobTable = PgHelper.Table(_schema, "job");
        _stateTable = PgHelper.Table(_schema, "state");
        _setTable = PgHelper.Table(_schema, "set");
        _jobParameterTable = PgHelper.Table(_schema, "jobparameter");
    }

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> SearchJobsByNameAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var pattern = "%" + PgHelper.EscapeILikePattern(searchTerm) + "%";
        var offset = (page - 1) * pageSize;

        var countSql = $@"
SELECT COUNT(*)
FROM {_jobTable} j
WHERE j.invocationdata::text ILIKE @Pattern";

        var querySql = $@"
SELECT j.id::text AS ""JobId"",
       j.invocationdata::text AS ""InvocationData"",
       j.statename AS ""State"",
       j.createdat AS ""CreatedAt"",
       s.createdat AS ""LastStateChange"",
       s.data::json ->> 'PerformanceDuration' AS ""DurationMsRaw""
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE j.invocationdata::text ILIKE @Pattern
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { Pattern = pattern }, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRowWithDuration>(
            new CommandDefinition(querySql, new { Pattern = pattern, PageSize = pageSize, Offset = offset }, cancellationToken: ct));

        var items = rows.Select(r => new JobSummaryDto
        {
            JobId = r.JobId,
            JobName = ExtractJobName(r.InvocationData),
            State = r.State,
            CreatedAt = r.CreatedAt,
            LastStateChange = r.LastStateChange,
            DurationMs = ParseNullableDouble(r.DurationMsRaw)
        }).ToList();

        return new PagedResult<JobSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> SearchFailedByExceptionAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var pattern = "%" + PgHelper.EscapeILikePattern(searchTerm) + "%";
        var offset = (page - 1) * pageSize;

        // Search in state data for exception type or message using ->> operator
        var countSql = $@"
SELECT COUNT(*)
FROM {_jobTable} j
INNER JOIN {_stateTable} s ON s.id = j.stateid
WHERE j.statename = 'Failed'
  AND (s.data::json ->> 'ExceptionType' ILIKE @Pattern
    OR s.data::json ->> 'ExceptionMessage' ILIKE @Pattern)";

        var querySql = $@"
SELECT j.id::text AS ""JobId"",
       j.invocationdata AS ""InvocationData"",
       j.statename AS ""State"",
       j.createdat AS ""CreatedAt"",
       s.createdat AS ""LastStateChange"",
       s.data::json ->> 'ExceptionType' AS ""ExceptionType"",
       s.data::json ->> 'ExceptionMessage' AS ""ExceptionMessage""
FROM {_jobTable} j
INNER JOIN {_stateTable} s ON s.id = j.stateid
WHERE j.statename = 'Failed'
  AND (s.data::json ->> 'ExceptionType' ILIKE @Pattern
    OR s.data::json ->> 'ExceptionMessage' ILIKE @Pattern)
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { Pattern = pattern }, cancellationToken: ct));

        var rows = await connection.QueryAsync<FailedJobRawRow>(
            new CommandDefinition(querySql, new { Pattern = pattern, PageSize = pageSize, Offset = offset }, cancellationToken: ct));

        var items = rows.Select(r => new JobSummaryDto
        {
            JobId = r.JobId,
            JobName = ExtractJobName(r.InvocationData),
            State = r.State,
            CreatedAt = r.CreatedAt,
            LastStateChange = r.LastStateChange,
            ExceptionType = r.ExceptionType,
            ExceptionMessage = r.ExceptionMessage
        }).ToList();

        return new PagedResult<JobSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
        JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
    {
        if (criteria == null)
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var offset = (page - 1) * pageSize;
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        // Build dynamic WHERE clause with parameterized conditions
        if (!string.IsNullOrWhiteSpace(criteria.State))
        {
            conditions.Add("j.statename = @State");
            parameters.Add("State", criteria.State);
        }

        if (criteria.DateFrom.HasValue)
        {
            conditions.Add("j.createdat >= @DateFrom");
            parameters.Add("DateFrom", criteria.DateFrom.Value.UtcDateTime);
        }

        if (criteria.DateTo.HasValue)
        {
            conditions.Add("j.createdat <= @DateTo");
            parameters.Add("DateTo", criteria.DateTo.Value.UtcDateTime);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Queue))
        {
            conditions.Add(@"EXISTS (
                SELECT 1 FROM {0} jp
                WHERE jp.jobid = j.id AND jp.name = 'CurrentQueue' AND jp.value = @Queue
            )".Replace("{0}", _jobParameterTable));
            parameters.Add("Queue", criteria.Queue);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Server))
        {
            conditions.Add(@"EXISTS (
                SELECT 1 FROM {0} s2
                WHERE s2.jobid = j.id AND s2.name = 'Processing'
                  AND s2.data::json ->> 'ServerId' = @Server
            )".Replace("{0}", _stateTable));
            parameters.Add("Server", criteria.Server);
        }

        if (criteria.MinDuration.HasValue)
        {
            conditions.Add("(s.data::json ->> 'PerformanceDuration')::numeric >= @MinDurationMs");
            parameters.Add("MinDurationMs", criteria.MinDuration.Value.TotalMilliseconds);
        }

        if (criteria.MaxDuration.HasValue)
        {
            conditions.Add("(s.data::json ->> 'PerformanceDuration')::numeric <= @MaxDurationMs");
            parameters.Add("MaxDurationMs", criteria.MaxDuration.Value.TotalMilliseconds);
        }

        if (criteria.Tags != null && criteria.Tags.Count > 0)
        {
            for (int i = 0; i < criteria.Tags.Count; i++)
            {
                var paramName = $"@Tag{i}";
                conditions.Add($@"EXISTS (
                    SELECT 1 FROM {_setTable} t
                    WHERE t.key = 'tags:' || {paramName} AND t.value = j.id::text
                )");
                parameters.Add($"Tag{i}", criteria.Tags[i]);
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.RecurringJobId))
        {
            conditions.Add(@"EXISTS (
                SELECT 1 FROM {0} jp
                WHERE jp.jobid = j.id AND jp.name = 'RecurringJobId' AND jp.value = @RecurringJobId
            )".Replace("{0}", _jobParameterTable));
            parameters.Add("RecurringJobId", criteria.RecurringJobId);
        }

        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        var countSql = $@"
SELECT COUNT(*)
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
{whereClause}";

        var querySql = $@"
SELECT j.id::text AS ""JobId"",
       j.invocationdata AS ""InvocationData"",
       j.statename AS ""State"",
       j.createdat AS ""CreatedAt"",
       s.createdat AS ""LastStateChange"",
       s.data::json ->> 'PerformanceDuration' AS ""DurationMsRaw"",
       s.data::json ->> 'Latency' AS ""LatencyMsRaw""
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
{whereClause}
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset", offset);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, parameters, cancellationToken: ct));

        var rows = await connection.QueryAsync<FilteredJobRawRow>(
            new CommandDefinition(querySql, parameters, cancellationToken: ct));

        var items = rows.Select(r => new JobSummaryDto
        {
            JobId = r.JobId,
            JobName = ExtractJobName(r.InvocationData),
            State = r.State,
            CreatedAt = r.CreatedAt,
            LastStateChange = r.LastStateChange,
            DurationMs = ParseNullableDouble(r.DurationMsRaw),
            LatencyMs = ParseNullableDouble(r.LatencyMsRaw)
        }).ToList();

        return new PagedResult<JobSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(
        string tag, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var offset = (page - 1) * pageSize;
        var tagKey = "tags:" + tag;

        var countSql = $@"
SELECT COUNT(*)
FROM {_setTable} t
INNER JOIN {_jobTable} j ON j.id::text = t.value
WHERE t.key = @TagKey";

        var querySql = $@"
SELECT j.id::text AS ""JobId"",
       j.invocationdata AS ""InvocationData"",
       j.statename AS ""State"",
       j.createdat AS ""CreatedAt"",
       s.createdat AS ""LastStateChange""
FROM {_setTable} t
INNER JOIN {_jobTable} j ON j.id::text = t.value
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE t.key = @TagKey
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { TagKey = tagKey }, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, new { TagKey = tagKey, PageSize = pageSize, Offset = offset }, cancellationToken: ct));

        var items = rows.Select(MapToJobSummary).ToList();

        return new PagedResult<JobSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct)
    {
        var sql = $@"
SELECT SUBSTRING(key FROM 6) AS ""Tag"",
       COUNT(*) AS ""Count""
FROM {_setTable}
WHERE key LIKE 'tags:%'
GROUP BY key
ORDER BY ""Count"" DESC";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var results = await connection.QueryAsync<TagCountDto>(
            new CommandDefinition(sql, cancellationToken: ct));

        return results.ToList();
    }

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(
        string stateName, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var offset = (page - 1) * pageSize;

        var countSql = $@"
SELECT COUNT(*)
FROM {_jobTable} j
WHERE j.statename = @StateName";

        var querySql = $@"
SELECT j.id::text AS ""JobId"",
       j.invocationdata AS ""InvocationData"",
       j.statename AS ""State"",
       j.createdat AS ""CreatedAt"",
       s.createdat AS ""LastStateChange""
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE j.statename = @StateName
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { StateName = stateName }, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, new { StateName = stateName, PageSize = pageSize, Offset = offset }, cancellationToken: ct));

        var items = rows.Select(MapToJobSummary).ToList();

        return new PagedResult<JobSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (count < 1) count = 1;
        if (count > 100) count = 100;

        var sql = $@"
SELECT j.id::text AS ""JobId"",
       j.invocationdata AS ""InvocationData"",
       (s.data::json ->> 'PerformanceDuration')::numeric AS ""DurationMs"",
       s.createdat AS ""CompletedAt""
FROM {_stateTable} s
INNER JOIN {_jobTable} j ON j.id = s.jobid
WHERE s.name = 'Succeeded'
  AND s.createdat >= @From
  AND s.createdat < @To
  AND s.data::json ->> 'PerformanceDuration' IS NOT NULL
ORDER BY (s.data::json ->> 'PerformanceDuration')::numeric DESC
LIMIT @Count";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<SlowestJobRawRow>(
            new CommandDefinition(sql, new { From = from.UtcDateTime, To = to.UtcDateTime, Count = count }, cancellationToken: ct));

        return rows.Select(r => new SlowestJobDto
        {
            JobId = r.JobId,
            JobName = ExtractJobName(r.InvocationData),
            DurationMs = r.DurationMs,
            CompletedAt = r.CompletedAt
        }).ToList();
    }

    #region Private Helpers

    /// <summary>
    /// Extracts a human-readable job name from the InvocationData JSON.
    /// InvocationData typically contains a "Type" field with the fully qualified type name.
    /// </summary>
    private static string ExtractJobName(string invocationData)
    {
        if (string.IsNullOrWhiteSpace(invocationData))
            return "(unknown)";

        try
        {
            // InvocationData is JSON like: {"Type":"Namespace.Class, Assembly","Method":"MethodName",...}
            // Extract Type and Method for a readable name
            var typeStart = invocationData.IndexOf("\"Type\"", StringComparison.OrdinalIgnoreCase);
            if (typeStart < 0)
            {
                // Try lowercase "type" (PostgreSQL may store differently)
                typeStart = invocationData.IndexOf("\"t\"", StringComparison.OrdinalIgnoreCase);
                if (typeStart < 0)
                    return TruncateInvocationData(invocationData);
            }

            var valueStart = invocationData.IndexOf('"', typeStart + 6);
            if (valueStart < 0) return TruncateInvocationData(invocationData);
            valueStart++; // skip opening quote

            var valueEnd = invocationData.IndexOf('"', valueStart);
            if (valueEnd < 0) return TruncateInvocationData(invocationData);

            var typeName = invocationData.Substring(valueStart, valueEnd - valueStart);

            // Get just the class name (last part before comma for assembly)
            var commaIdx = typeName.IndexOf(',');
            if (commaIdx > 0)
                typeName = typeName.Substring(0, commaIdx);

            var dotIdx = typeName.LastIndexOf('.');
            if (dotIdx > 0)
                typeName = typeName.Substring(dotIdx + 1);

            // Try to extract method name
            var methodStart = invocationData.IndexOf("\"Method\"", StringComparison.OrdinalIgnoreCase);
            if (methodStart < 0)
                methodStart = invocationData.IndexOf("\"m\"", StringComparison.OrdinalIgnoreCase);

            if (methodStart >= 0)
            {
                var mValueStart = invocationData.IndexOf('"', methodStart + 8);
                if (mValueStart < 0)
                    mValueStart = invocationData.IndexOf('"', methodStart + 3);
                if (mValueStart >= 0)
                {
                    mValueStart++;
                    var mValueEnd = invocationData.IndexOf('"', mValueStart);
                    if (mValueEnd > mValueStart)
                    {
                        var methodName = invocationData.Substring(mValueStart, mValueEnd - mValueStart);
                        return $"{typeName}.{methodName}";
                    }
                }
            }

            return typeName;
        }
        catch
        {
            return TruncateInvocationData(invocationData);
        }
    }

    private static string TruncateInvocationData(string data)
    {
        if (data.Length <= 100)
            return data;
        return data.Substring(0, 100) + "...";
    }

    private static JobSummaryDto MapToJobSummary(JobRawRow row)
    {
        return new JobSummaryDto
        {
            JobId = row.JobId,
            JobName = ExtractJobName(row.InvocationData),
            State = row.State,
            CreatedAt = row.CreatedAt,
            LastStateChange = row.LastStateChange
        };
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }

    #endregion

    #region Internal Row Types

    private class JobRawRow
    {
        public string JobId { get; set; }
        public string InvocationData { get; set; }
        public string State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
    }

    private class JobRawRowWithDuration
    {
        public string JobId { get; set; }
        public string InvocationData { get; set; }
        public string State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
        public string DurationMsRaw { get; set; }
    }

    private class FailedJobRawRow
    {
        public string JobId { get; set; }
        public string InvocationData { get; set; }
        public string State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
    }

    private class FilteredJobRawRow
    {
        public string JobId { get; set; }
        public string InvocationData { get; set; }
        public string State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
        public string DurationMsRaw { get; set; }
        public string LatencyMsRaw { get; set; }
    }

    private class SlowestJobRawRow
    {
        public string JobId { get; set; }
        public string InvocationData { get; set; }
        public double DurationMs { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    #endregion
}
