using System;
using System.Collections.Generic;
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

    public SqlServerQueryProvider(string connectionString, string schema = "HangFire")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    // ─── Column definitions ──────────────────────────────────────────────────

    private string JobColumns => $@"
j.Id AS JobId,
j.InvocationData,
j.StateName AS [State],
j.CreatedAt,
s.CreatedAt AS LastStateChange,
CAST(JSON_VALUE(s.[Data], '$.PerformanceDuration') AS BIGINT) AS DurationMs,
CAST(JSON_VALUE(s.[Data], '$.Latency') AS BIGINT) AS LatencyMs,
JSON_VALUE(s.[Data], '$.ExceptionType') AS ExceptionType,
JSON_VALUE(s.[Data], '$.ExceptionMessage') AS ExceptionMessage";

    // ═══════════════════════════════════════════════════════════════════════════
    // GetJobsWithFilterAsync — Unified multi-stage advanced search
    // ═══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
        JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct)
    {
        if (criteria == null || !criteria.HasAnyCriteria())
            return PagedResult<JobSummaryDto>.Empty(page, pageSize);

        var offset = (page - 1) * pageSize;
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");

        // ═══ Stage 1: Basic filters ═══
        BuildBasicFilters(criteria, conditions, parameters);

        // ═══ Stage 2: State data filters ═══
        BuildStateDataFilters(criteria, conditions, parameters, stateTable);

        // ═══ Stage 3: Cross-table filters ═══
        BuildCrossTableFilters(criteria, conditions, parameters);

        // ═══ Stage 4: Content search CTE ═══
        var contentCte = BuildContentSearchCte(criteria, parameters);

        // ═══ Final: Assemble ═══
        var stateJoin = $"LEFT JOIN {stateTable} s ON s.Id = j.StateId";
        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        string contentJoin = "";
        if (!string.IsNullOrEmpty(contentCte))
        {
            // Use subquery approach instead of CTE (works with separate count + data queries)
            contentJoin = $"INNER JOIN (\n{contentCte}\n) mj ON mj.Id = j.Id";
        }

        var countSql = $@"
SELECT COUNT(*)
FROM {jobTable} j
{stateJoin}
{contentJoin}
{whereClause};";

        var querySql = $@"
SELECT {JobColumns}
FROM {jobTable} j
{stateJoin}
{contentJoin}
{whereClause}
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);

        return await ExecutePagedAsync(countSql, querySql, parameters, page, pageSize, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GetJobsByTagAsync
    // ═══════════════════════════════════════════════════════════════════════════

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
INNER JOIN {jobTable} j ON j.Id = TRY_CAST(st.Value AS BIGINT)
WHERE st.[Key] = @TagKey
  AND TRY_CAST(st.Value AS BIGINT) IS NOT NULL;";

        var querySql = $@"
SELECT {JobColumns}
FROM {setTable} st
INNER JOIN {jobTable} j ON j.Id = TRY_CAST(st.Value AS BIGINT)
LEFT JOIN {stateTable} s ON s.Id = j.StateId
WHERE st.[Key] = @TagKey
  AND TRY_CAST(st.Value AS BIGINT) IS NOT NULL
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        return await ExecutePagedAsync(countSql, querySql,
            new { TagKey = tagKey, Offset = offset, PageSize = pageSize }, page, pageSize, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GetTagCloudAsync
    // ═══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct)
    {
        var setTable = SqlHelper.Table(_schema, "Set");
        var jobTable = SqlHelper.Table(_schema, "Job");

        var sql = $@"
SELECT SUBSTRING(t.[Key], 6, LEN(t.[Key]) - 5) AS Tag, COUNT(*) AS [Count]
FROM {setTable} t
INNER JOIN {jobTable} j ON j.Id = TRY_CAST(t.Value AS BIGINT)
WHERE t.[Key] LIKE 'tags:%'
  AND TRY_CAST(t.Value AS BIGINT) IS NOT NULL
GROUP BY t.[Key]
ORDER BY COUNT(*) DESC;";

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<TagCountDto>(
            new CommandDefinition(sql, cancellationToken: ct));

        return rows.ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GetJobsByStateAsync
    // ═══════════════════════════════════════════════════════════════════════════

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
SELECT {JobColumns}
FROM {jobTable} j
LEFT JOIN {stateTable} s ON s.Id = j.StateId
WHERE j.StateName = @StateName
ORDER BY j.CreatedAt DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        return await ExecutePagedAsync(countSql, querySql,
            new { StateName = stateName, Offset = offset, PageSize = pageSize }, page, pageSize, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GetSlowestJobsAsync
    // ═══════════════════════════════════════════════════════════════════════════

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
       j.InvocationData,
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
            JobName = ExtractJobName(r.InvocationData),
            DurationMs = r.DurationMs,
            CompletedAt = r.CompletedAt
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private: Multi-stage filter builders
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildBasicFilters(JobFilterCriteria criteria, List<string> conditions, DynamicParameters parameters)
    {
        var states = criteria.GetEffectiveStates();
        if (states.Count == 1)
        {
            conditions.Add("j.StateName = @State");
            parameters.Add("State", states[0]);
        }
        else if (states.Count > 1)
        {
            conditions.Add("j.StateName IN @States");
            parameters.Add("States", states.ToList());
        }

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

        if (!string.IsNullOrWhiteSpace(criteria.JobNamePattern))
        {
            conditions.Add("j.InvocationData LIKE @NamePattern");
            parameters.Add("NamePattern", "%" + SqlHelper.EscapeLikePattern(criteria.JobNamePattern) + "%");
        }
    }

    private void BuildStateDataFilters(JobFilterCriteria criteria, List<string> conditions,
        DynamicParameters parameters, string stateTable)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Server))
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {stateTable} s2
                WHERE s2.JobId = j.Id AND s2.Name = 'Processing'
                  AND JSON_VALUE(s2.[Data], '$.ServerId') = @Server
            )");
            parameters.Add("Server", criteria.Server);
        }

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

        if (!string.IsNullOrWhiteSpace(criteria.ExceptionPattern))
        {
            var pattern = "%" + SqlHelper.EscapeLikePattern(criteria.ExceptionPattern) + "%";
            conditions.Add(@"(JSON_VALUE(s.[Data], '$.ExceptionType') LIKE @ExPattern
                OR JSON_VALUE(s.[Data], '$.ExceptionMessage') LIKE @ExPattern)");
            parameters.Add("ExPattern", pattern);

            if (criteria.GetEffectiveStates().Count == 0)
            {
                conditions.Add("j.StateName = 'Failed'");
            }
        }
    }

    private void BuildCrossTableFilters(JobFilterCriteria criteria, List<string> conditions, DynamicParameters parameters)
    {
        var jobParamTable = SqlHelper.Table(_schema, "JobParameter");
        var setTable = SqlHelper.Table(_schema, "Set");

        if (!string.IsNullOrWhiteSpace(criteria.Queue))
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {jobParamTable} jp
                WHERE jp.JobId = j.Id AND jp.Name = 'CurrentQueue' AND jp.Value = @Queue
            )");
            parameters.Add("Queue", criteria.Queue);
        }

        if (!string.IsNullOrWhiteSpace(criteria.RecurringJobId))
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {jobParamTable} jp
                WHERE jp.JobId = j.Id AND jp.Name = 'RecurringJobId' AND jp.Value = @RecurringJobId
            )");
            parameters.Add("RecurringJobId", criteria.RecurringJobId);
        }

        if (criteria.Tags != null && criteria.Tags.Count > 0)
        {
            for (int i = 0; i < criteria.Tags.Count; i++)
            {
                var paramName = $"TagKey{i}";
                conditions.Add($@"EXISTS (
                    SELECT 1 FROM {setTable} t
                    WHERE t.[Key] = @{paramName} AND t.Value = CAST(j.Id AS NVARCHAR(20))
                )");
                parameters.Add(paramName, $"tags:{criteria.Tags[i]}");
            }
        }
    }

    private string BuildContentSearchCte(JobFilterCriteria criteria, DynamicParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(criteria.ContentPattern))
            return null;

        if (!criteria.SearchStackTrace && !criteria.SearchConsoleOutput)
            return null;

        var pattern = "%" + SqlHelper.EscapeLikePattern(criteria.ContentPattern) + "%";
        parameters.Add("ContentPattern", pattern);

        var unionParts = new List<string>();
        var stateTable = SqlHelper.Table(_schema, "State");
        var jobTable = SqlHelper.Table(_schema, "Job");
        var setTable = SqlHelper.Table(_schema, "Set");
        var hashTable = SqlHelper.Table(_schema, "Hash");

        if (criteria.SearchStackTrace)
        {
            unionParts.Add($@"
                SELECT j2.Id
                FROM {jobTable} j2
                INNER JOIN {stateTable} s2 ON s2.Id = j2.StateId
                WHERE s2.[Data] IS NOT NULL
                  AND s2.[Data] LIKE @ContentPattern");
        }

        if (criteria.SearchConsoleOutput)
        {
            // Console messages in Set table: key = 'console:set:{consoleId}', value = JSON with message
            // Job ID lookup: Hash table key = 'console:hash:{consoleId}', field = 'jobId', value = '{jobId}'
            unionParts.Add($@"
                SELECT TRY_CAST(h_ref.Value AS BIGINT) AS Id
                FROM {setTable} st
                INNER JOIN {hashTable} h_ref
                    ON h_ref.[Key] = REPLACE(st.[Key], 'console:set:', 'console:hash:')
                    AND h_ref.Field = 'jobId'
                WHERE st.[Key] LIKE 'console:set:%'
                  AND CAST(st.Value AS NVARCHAR(MAX)) LIKE @ContentPattern
                  AND TRY_CAST(h_ref.Value AS BIGINT) IS NOT NULL");

            // Long console messages in Hash table: key = 'console:hash:{consoleId}', field = GUID, value = message
            unionParts.Add($@"
                SELECT TRY_CAST(h_ref.Value AS BIGINT) AS Id
                FROM {hashTable} h
                INNER JOIN {hashTable} h_ref
                    ON h_ref.[Key] = h.[Key]
                    AND h_ref.Field = 'jobId'
                WHERE h.[Key] LIKE 'console:hash:%'
                  AND h.Field <> 'jobId'
                  AND h.Field <> 'progress'
                  AND CAST(h.Value AS NVARCHAR(MAX)) LIKE @ContentPattern
                  AND TRY_CAST(h_ref.Value AS BIGINT) IS NOT NULL");
        }

        return string.Join("\n    UNION\n", unionParts);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private: Execution helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<PagedResult<JobSummaryDto>> ExecutePagedAsync(
        string countSql, string querySql, object parameters, int page, int pageSize, CancellationToken ct)
    {
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

    private static JobSummaryDto MapToJobSummary(JobRawRow row)
    {
        return new JobSummaryDto
        {
            JobId = row.JobId.ToString(),
            JobName = ExtractJobName(row.InvocationData),
            State = row.State,
            CreatedAt = row.CreatedAt,
            LastStateChange = row.LastStateChange,
            DurationMs = row.DurationMs,
            LatencyMs = row.LatencyMs,
            ExceptionType = row.ExceptionType,
            ExceptionMessage = row.ExceptionMessage
        };
    }

    private static string ExtractJobName(string invocationData)
    {
        if (string.IsNullOrWhiteSpace(invocationData))
            return "(unknown)";

        try
        {
            var typeIndex = invocationData.IndexOf("\"Type\"", StringComparison.OrdinalIgnoreCase);
            if (typeIndex < 0)
                typeIndex = invocationData.IndexOf("\"t\"", StringComparison.OrdinalIgnoreCase);
            if (typeIndex < 0)
                return invocationData.Length > 100 ? invocationData[..100] + "..." : invocationData;

            var colonIndex = invocationData.IndexOf(':', typeIndex);
            if (colonIndex < 0) return "(unknown)";

            var quoteStart = invocationData.IndexOf('"', colonIndex + 1);
            if (quoteStart < 0) return "(unknown)";

            var quoteEnd = invocationData.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return "(unknown)";

            var typeName = invocationData[(quoteStart + 1)..quoteEnd];

            var commaIndex = typeName.IndexOf(',');
            if (commaIndex > 0) typeName = typeName[..commaIndex];

            var dotIndex = typeName.LastIndexOf('.');
            if (dotIndex > 0) typeName = typeName[(dotIndex + 1)..];

            var methodIndex = invocationData.IndexOf("\"Method\"", StringComparison.OrdinalIgnoreCase);
            if (methodIndex < 0)
                methodIndex = invocationData.IndexOf("\"m\"", StringComparison.OrdinalIgnoreCase);

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
                            var methodName = invocationData[(mQuoteStart + 1)..mQuoteEnd];
                            return $"{typeName}.{methodName}";
                        }
                    }
                }
            }

            return typeName;
        }
        catch
        {
            return invocationData.Length > 100 ? invocationData[..100] + "..." : invocationData;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Internal row types
    // ═══════════════════════════════════════════════════════════════════════════

    private class JobRawRow
    {
        public long JobId { get; set; }
        public string InvocationData { get; set; }
        public string State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
        public long? DurationMs { get; set; }
        public long? LatencyMs { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
    }

    private class SlowestJobRawRow
    {
        public long JobId { get; set; }
        public string InvocationData { get; set; }
        public long DurationMs { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
