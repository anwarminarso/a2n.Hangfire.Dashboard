using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.SqlServer.Internal;
using Dapper;
using Microsoft.Data.SqlClient;

namespace a2n.Hangfire.Dashboard.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IStorageQueryProvider"/>.
/// Uses Dapper with parameterized T-SQL queries for database-level search, filter, and pagination.
/// All queries use JSON_VALUE() for State.Data field extraction and OFFSET/FETCH for pagination.
/// </summary>
public class SqlServerQueryProvider : IStorageQueryProvider
{
    private readonly string _connectionString;
    private readonly string _schema;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlServerQueryProvider"/> class.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string</param>
    /// <param name="schema">Schema name (default: "HangFire")</param>
    public SqlServerQueryProvider(string connectionString, string schema = "HangFire")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> SearchJobsByNameAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var pattern = "%" + SqlHelper.EscapeLikePattern(searchTerm) + "%";
        var offset = (page - 1) * pageSize;

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");

        var countSql = $@"
SELECT COUNT(*)
FROM {jobTable} j
WHERE j.InvocationData LIKE @Pattern;";

        var querySql = $@"
SELECT j.Id AS JobId,
       j.InvocationData AS JobName,
       j.StateName AS [State],
       j.CreatedAt,
       s.CreatedAt AS LastStateChange,
       CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) AS DurationMs,
       CAST(JSON_VALUE(s.[Data], '$.Latency') AS BIGINT) AS LatencyMs
FROM {jobTable} j
LEFT JOIN {stateTable} s ON s.Id = j.StateId
WHERE j.InvocationData LIKE @Pattern
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { Pattern = pattern }, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, new { Pattern = pattern, Offset = offset, PageSize = pageSize }, cancellationToken: ct));

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
    public async Task<PagedResult<JobSummaryDto>> SearchFailedByExceptionAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var pattern = "%" + SqlHelper.EscapeLikePattern(searchTerm) + "%";
        var offset = (page - 1) * pageSize;

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");

        var countSql = $@"
SELECT COUNT(*)
FROM {jobTable} j
INNER JOIN {stateTable} s ON s.Id = j.StateId
WHERE j.StateName = 'Failed'
  AND (JSON_VALUE(s.[Data], '$.ExceptionMessage') LIKE @Pattern
       OR JSON_VALUE(s.[Data], '$.ExceptionType') LIKE @Pattern);";

        var querySql = $@"
SELECT j.Id AS JobId,
       j.InvocationData AS JobName,
       j.StateName AS [State],
       j.CreatedAt,
       s.CreatedAt AS LastStateChange,
       JSON_VALUE(s.[Data], '$.ExceptionType') AS ExceptionType,
       JSON_VALUE(s.[Data], '$.ExceptionMessage') AS ExceptionMessage
FROM {jobTable} j
INNER JOIN {stateTable} s ON s.Id = j.StateId
WHERE j.StateName = 'Failed'
  AND (JSON_VALUE(s.[Data], '$.ExceptionMessage') LIKE @Pattern
       OR JSON_VALUE(s.[Data], '$.ExceptionType') LIKE @Pattern)
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { Pattern = pattern }, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, new { Pattern = pattern, Offset = offset, PageSize = pageSize }, cancellationToken: ct));

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
    public async Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
        JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
    {
        if (criteria == null)
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var offset = (page - 1) * pageSize;

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");
        var setTable = SqlHelper.Table(_schema, "Set");

        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);

        // State filter
        if (!string.IsNullOrWhiteSpace(criteria.State))
        {
            conditions.Add("j.StateName = @State");
            parameters.Add("State", criteria.State);
        }

        // Date range filter
        if (criteria.DateFrom.HasValue)
        {
            conditions.Add("j.CreatedAt >= @DateFrom");
            parameters.Add("DateFrom", criteria.DateFrom.Value.UtcDateTime);
        }
        if (criteria.DateTo.HasValue)
        {
            conditions.Add("j.CreatedAt < @DateTo");
            parameters.Add("DateTo", criteria.DateTo.Value.UtcDateTime);
        }

        // Queue filter (via JobParameter table)
        var needsQueueJoin = !string.IsNullOrWhiteSpace(criteria.Queue);
        if (needsQueueJoin)
        {
            conditions.Add("qp.Value = @Queue");
            parameters.Add("Queue", criteria.Queue);
        }

        // Server filter (via State.Data ServerId)
        if (!string.IsNullOrWhiteSpace(criteria.Server))
        {
            conditions.Add("JSON_VALUE(s.[Data], '$.ServerId') = @Server");
            parameters.Add("Server", criteria.Server);
        }

        // Duration range filter (via State.Data PerformanceDuration)
        if (criteria.MinDuration.HasValue)
        {
            conditions.Add("CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) >= @MinDurationMs");
            parameters.Add("MinDurationMs", (long)criteria.MinDuration.Value.TotalMilliseconds);
        }
        if (criteria.MaxDuration.HasValue)
        {
            conditions.Add("CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) <= @MaxDurationMs");
            parameters.Add("MaxDurationMs", (long)criteria.MaxDuration.Value.TotalMilliseconds);
        }

        // Tags filter (via Set table JOIN)
        var needsTagJoin = criteria.Tags != null && criteria.Tags.Count > 0;
        if (needsTagJoin)
        {
            for (int i = 0; i < criteria.Tags.Count; i++)
            {
                var alias = $"t{i}";
                var paramName = $"TagKey{i}";
                conditions.Add($"{alias}.[Key] = @{paramName}");
                parameters.Add(paramName, $"tags:{criteria.Tags[i]}");
            }
        }

        // Recurring job ID filter (via JobParameter)
        var needsRecurringJoin = !string.IsNullOrWhiteSpace(criteria.RecurringJobId);
        if (needsRecurringJoin)
        {
            conditions.Add("rp.Value = @RecurringJobId");
            parameters.Add("RecurringJobId", criteria.RecurringJobId);
        }

        // Build JOIN clauses
        var jobParamTable = SqlHelper.Table(_schema, "JobParameter");
        var joins = $"LEFT JOIN {stateTable} s ON s.Id = j.StateId\n";

        if (needsQueueJoin)
        {
            joins += $"INNER JOIN {jobParamTable} qp ON qp.JobId = j.Id AND qp.Name = 'Queue'\n";
        }

        if (needsRecurringJoin)
        {
            joins += $"INNER JOIN {jobParamTable} rp ON rp.JobId = j.Id AND rp.Name = 'RecurringJobId'\n";
        }

        if (needsTagJoin)
        {
            for (int i = 0; i < criteria.Tags.Count; i++)
            {
                var alias = $"t{i}";
                joins += $"INNER JOIN {setTable} {alias} ON {alias}.Value = CAST(j.Id AS NVARCHAR(20)) AND {alias}.[Key] = @TagKey{i}\n";
            }
        }

        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        var countSql = $@"
SELECT COUNT(*)
FROM {jobTable} j
{joins}
{whereClause};";

        var querySql = $@"
SELECT j.Id AS JobId,
       j.InvocationData AS JobName,
       j.StateName AS [State],
       j.CreatedAt,
       s.CreatedAt AS LastStateChange,
       CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) AS DurationMs,
       CAST(JSON_VALUE(s.[Data], '$.Latency') AS BIGINT) AS LatencyMs,
       JSON_VALUE(s.[Data], '$.ExceptionType') AS ExceptionType,
       JSON_VALUE(s.[Data], '$.ExceptionMessage') AS ExceptionMessage
FROM {jobTable} j
{joins}
{whereClause}
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, parameters, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, parameters, cancellationToken: ct));

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
    public async Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(
        string tag, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var offset = (page - 1) * pageSize;

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");
        var setTable = SqlHelper.Table(_schema, "Set");

        var tagKey = $"tags:{tag}";

        var countSql = $@"
SELECT COUNT(*)
FROM {setTable} st
INNER JOIN {jobTable} j ON j.Id = CAST(st.Value AS BIGINT)
WHERE st.[Key] = @TagKey;";

        var querySql = $@"
SELECT j.Id AS JobId,
       j.InvocationData AS JobName,
       j.StateName AS [State],
       j.CreatedAt,
       s.CreatedAt AS LastStateChange,
       CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) AS DurationMs,
       CAST(JSON_VALUE(s.[Data], '$.Latency') AS BIGINT) AS LatencyMs
FROM {setTable} st
INNER JOIN {jobTable} j ON j.Id = CAST(st.Value AS BIGINT)
LEFT JOIN {stateTable} s ON s.Id = j.StateId
WHERE st.[Key] = @TagKey
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { TagKey = tagKey }, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, new { TagKey = tagKey, Offset = offset, PageSize = pageSize }, cancellationToken: ct));

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
        var setTable = SqlHelper.Table(_schema, "Set");

        var sql = $@"
SELECT [Key] AS Tag, COUNT(*) AS [Count]
FROM {setTable}
WHERE [Key] LIKE 'tags:%'
GROUP BY [Key]
ORDER BY COUNT(*) DESC;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<TagRawRow>(
            new CommandDefinition(sql, cancellationToken: ct));

        // Strip "tags:" prefix from the key
        return rows.Select(r => new TagCountDto
        {
            Tag = r.Tag.StartsWith("tags:", StringComparison.OrdinalIgnoreCase)
                ? r.Tag.Substring(5)
                : r.Tag,
            Count = r.Count
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(
        string stateName, int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var offset = (page - 1) * pageSize;

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");

        var countSql = $@"
SELECT COUNT(*)
FROM {jobTable} j
WHERE j.StateName = @StateName;";

        var querySql = $@"
SELECT j.Id AS JobId,
       j.InvocationData AS JobName,
       j.StateName AS [State],
       j.CreatedAt,
       s.CreatedAt AS LastStateChange,
       CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) AS DurationMs,
       CAST(JSON_VALUE(s.[Data], '$.Latency') AS BIGINT) AS LatencyMs,
       JSON_VALUE(s.[Data], '$.ExceptionType') AS ExceptionType,
       JSON_VALUE(s.[Data], '$.ExceptionMessage') AS ExceptionMessage
FROM {jobTable} j
LEFT JOIN {stateTable} s ON s.Id = j.StateId
WHERE j.StateName = @StateName
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, new { StateName = stateName }, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, new { StateName = stateName, Offset = offset, PageSize = pageSize }, cancellationToken: ct));

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
        if (count <= 0) count = 10;
        if (count > 100) count = 100;

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");

        var sql = $@"
SELECT TOP (@Count)
       j.Id AS JobId,
       j.InvocationData AS JobName,
       CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) AS DurationMs,
       s.CreatedAt AS CompletedAt
FROM {stateTable} s
INNER JOIN {jobTable} j ON j.Id = s.JobId
WHERE s.Name = 'Succeeded'
  AND s.CreatedAt >= @From
  AND s.CreatedAt < @To
  AND JSON_VALUE(s.[Data], '$.PerformanceDuration') IS NOT NULL
ORDER BY CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) DESC;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<SlowestJobRawRow>(
            new CommandDefinition(sql, new { Count = count, From = from.UtcDateTime, To = to.UtcDateTime }, cancellationToken: ct));

        return rows.Select(r => new SlowestJobDto
        {
            JobId = r.JobId.ToString(),
            JobName = ExtractJobName(r.JobName),
            DurationMs = r.DurationMs,
            CompletedAt = r.CompletedAt
        }).ToList();
    }

    #region Private Helpers

    /// <summary>
    /// Extracts a readable job name from InvocationData JSON.
    /// InvocationData contains a JSON object with a "Type" field containing the fully qualified type name.
    /// </summary>
    private static string ExtractJobName(string invocationData)
    {
        if (string.IsNullOrWhiteSpace(invocationData))
            return "(unknown)";

        try
        {
            // Simple extraction: find "Type":"..." pattern
            var typeIndex = invocationData.IndexOf("\"Type\"", StringComparison.OrdinalIgnoreCase);
            if (typeIndex < 0)
            {
                typeIndex = invocationData.IndexOf("\"t\"", StringComparison.OrdinalIgnoreCase);
            }

            if (typeIndex < 0)
                return invocationData.Length > 100 ? invocationData.Substring(0, 100) + "..." : invocationData;

            var colonIndex = invocationData.IndexOf(':', typeIndex);
            if (colonIndex < 0) return "(unknown)";

            var quoteStart = invocationData.IndexOf('"', colonIndex + 1);
            if (quoteStart < 0) return "(unknown)";

            var quoteEnd = invocationData.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return "(unknown)";

            var typeName = invocationData.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);

            // Extract just the class name (last segment before comma if assembly-qualified)
            var commaIndex = typeName.IndexOf(',');
            if (commaIndex > 0)
                typeName = typeName.Substring(0, commaIndex);

            var dotIndex = typeName.LastIndexOf('.');
            if (dotIndex > 0)
                typeName = typeName.Substring(dotIndex + 1);

            // Also try to extract method name
            var methodIndex = invocationData.IndexOf("\"Method\"", StringComparison.OrdinalIgnoreCase);
            if (methodIndex < 0)
            {
                methodIndex = invocationData.IndexOf("\"m\"", StringComparison.OrdinalIgnoreCase);
            }

            if (methodIndex > 0)
            {
                var mColonIndex = invocationData.IndexOf(':', methodIndex);
                if (mColonIndex > 0)
                {
                    var mQuoteStart = invocationData.IndexOf('"', mColonIndex + 1);
                    if (mQuoteStart > 0)
                    {
                        var mQuoteEnd = invocationData.IndexOf('"', mQuoteStart + 1);
                        if (mQuoteEnd > 0)
                        {
                            var methodName = invocationData.Substring(mQuoteStart + 1, mQuoteEnd - mQuoteStart - 1);
                            return $"{typeName}.{methodName}";
                        }
                    }
                }
            }

            return typeName;
        }
        catch
        {
            return invocationData.Length > 100 ? invocationData.Substring(0, 100) + "..." : invocationData;
        }
    }

    private static JobSummaryDto MapToJobSummary(JobRawRow row)
    {
        return new JobSummaryDto
        {
            JobId = row.JobId.ToString(),
            JobName = ExtractJobName(row.JobName),
            State = row.State,
            CreatedAt = row.CreatedAt,
            LastStateChange = row.LastStateChange,
            DurationMs = row.DurationMs,
            LatencyMs = row.LatencyMs,
            ExceptionType = row.ExceptionType,
            ExceptionMessage = row.ExceptionMessage
        };
    }

    #endregion

    #region Internal Row Types

    /// <summary>
    /// Internal Dapper mapping type for job query results.
    /// </summary>
    private class JobRawRow
    {
        public long JobId { get; set; }
        public string JobName { get; set; }
        public string State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
        public long? DurationMs { get; set; }
        public long? LatencyMs { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
    }

    /// <summary>
    /// Internal Dapper mapping type for tag cloud results.
    /// </summary>
    private class TagRawRow
    {
        public string Tag { get; set; }
        public long Count { get; set; }
    }

    /// <summary>
    /// Internal Dapper mapping type for slowest job results.
    /// </summary>
    private class SlowestJobRawRow
    {
        public long JobId { get; set; }
        public string JobName { get; set; }
        public long DurationMs { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    #endregion
}
