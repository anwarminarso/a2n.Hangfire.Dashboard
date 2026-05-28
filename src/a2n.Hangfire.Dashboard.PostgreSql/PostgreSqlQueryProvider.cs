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

    private readonly string _jobTable;
    private readonly string _stateTable;
    private readonly string _setTable;
    private readonly string _jobParameterTable;
    private readonly string _hashTable;

    public PostgreSqlQueryProvider(string connectionString, string schema = "hangfire")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _schema = PgHelper.ValidateIdentifier(schema ?? "hangfire", nameof(schema));

        _jobTable = PgHelper.Table(_schema, "job");
        _stateTable = PgHelper.Table(_schema, "state");
        _setTable = PgHelper.Table(_schema, "set");
        _jobParameterTable = PgHelper.Table(_schema, "jobparameter");
        _hashTable = PgHelper.Table(_schema, "hash");
    }

    // ─── Column definitions (reused across queries) ──────────────────────────

    private string JobColumns => $@"
j.id::text AS ""JobId"",
j.invocationdata::text AS ""InvocationData"",
j.statename AS ""State"",
j.createdat AS ""CreatedAt"",
s.createdat AS ""LastStateChange"",
(s.data::json ->> 'PerformanceDuration')::double precision AS ""DurationMs"",
(s.data::json ->> 'Latency')::double precision AS ""LatencyMs"",
s.data::json ->> 'ExceptionType' AS ""ExceptionType"",
s.data::json ->> 'ExceptionMessage' AS ""ExceptionMessage""";

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
        var needsStateJoin = false;

        // ═══ Stage 1: Basic filters (indexed columns) ═══

        BuildBasicFilters(criteria, conditions, parameters);

        // ═══ Stage 2: State data filters (requires JOIN to state table) ═══

        needsStateJoin = BuildStateDataFilters(criteria, conditions, parameters);

        // ═══ Stage 3: Cross-table filters (EXISTS subqueries) ═══

        BuildCrossTableFilters(criteria, conditions, parameters);

        // ═══ Stage 4: Content search (conditional UNION via CTE) ═══

        var contentCte = BuildContentSearchCte(criteria, parameters);

        // ═══ Final: Assemble and execute ═══

        var stateJoin = $"LEFT JOIN {_stateTable} s ON s.id = j.stateid";
        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        // If content search is active, add CTE and INNER JOIN to matched IDs
        string ctePart = "";
        string contentJoin = "";
        if (!string.IsNullOrEmpty(contentCte))
        {
            ctePart = $"WITH matched_jobs AS (\n{contentCte}\n)\n";
            contentJoin = "INNER JOIN matched_jobs mj ON mj.id = j.id";
        }

        var countSql = $@"{ctePart}
SELECT COUNT(*)
FROM {_jobTable} j
{stateJoin}
{contentJoin}
{whereClause}";

        var querySql = $@"{ctePart}
SELECT {JobColumns}
FROM {_jobTable} j
{stateJoin}
{contentJoin}
{whereClause}
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset", offset);

        await using var connection = new NpgsqlConnection(_connectionString);
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

    // ═══════════════════════════════════════════════════════════════════════════
    // GetJobsByTagAsync — Simple tag-based lookup
    // ═══════════════════════════════════════════════════════════════════════════

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
SELECT {JobColumns}
FROM {_setTable} t
INNER JOIN {_jobTable} j ON j.id::text = t.value
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE t.key = @TagKey
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        return await ExecutePagedAsync(countSql, querySql,
            new { TagKey = tagKey, PageSize = pageSize, Offset = offset }, page, pageSize, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GetTagCloudAsync — Tag aggregation
    // ═══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct)
    {
        var sql = $@"
SELECT SUBSTRING(t.key FROM 6) AS ""Tag"",
       COUNT(*) AS ""Count""
FROM {_setTable} t
INNER JOIN {_jobTable} j ON j.id::text = t.value
WHERE t.key LIKE 'tags:%'
GROUP BY t.key
ORDER BY ""Count"" DESC";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var results = await connection.QueryAsync<TagCountDto>(
            new CommandDefinition(sql, cancellationToken: ct));

        return results.ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GetJobsByStateAsync — Simple state-based pagination
    // ═══════════════════════════════════════════════════════════════════════════

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
SELECT {JobColumns}
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE j.statename = @StateName
ORDER BY j.createdat DESC
LIMIT @PageSize OFFSET @Offset";

        return await ExecutePagedAsync(countSql, querySql,
            new { StateName = stateName, PageSize = pageSize, Offset = offset }, page, pageSize, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GetSlowestJobsAsync — Top N by duration
    // ═══════════════════════════════════════════════════════════════════════════

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
            JobName = PgHelper.ExtractJobName(r.InvocationData),
            DurationMs = r.DurationMs,
            CompletedAt = r.CompletedAt
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private: Multi-stage filter builders
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stage 1: Basic filters on indexed job table columns.
    /// </summary>
    private void BuildBasicFilters(JobFilterCriteria criteria, List<string> conditions, DynamicParameters parameters)
    {
        // State filter
        var states = criteria.GetEffectiveStates();
        if (states.Count == 1)
        {
            conditions.Add("j.statename = @State");
            parameters.Add("State", states[0]);
        }
        else if (states.Count > 1)
        {
            conditions.Add("j.statename = ANY(@States)");
            parameters.Add("States", states.ToArray());
        }

        // Date range
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

        // Job name pattern (ILIKE on invocationdata)
        if (!string.IsNullOrWhiteSpace(criteria.JobNamePattern))
        {
            conditions.Add("j.invocationdata::text ILIKE @NamePattern");
            parameters.Add("NamePattern", "%" + PgHelper.EscapeILikePattern(criteria.JobNamePattern) + "%");
        }
    }

    /// <summary>
    /// Stage 2: Filters that require state data (JSON extraction).
    /// Returns true if state JOIN is needed.
    /// </summary>
    private bool BuildStateDataFilters(JobFilterCriteria criteria, List<string> conditions, DynamicParameters parameters)
    {
        bool needed = false;

        // Server filter
        if (!string.IsNullOrWhiteSpace(criteria.Server))
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {_stateTable} s2
                WHERE s2.jobid = j.id AND s2.name = 'Processing'
                  AND s2.data::json ->> 'ServerId' = @Server
            )");
            parameters.Add("Server", criteria.Server);
            needed = true;
        }

        // Duration filters
        if (criteria.MinDuration.HasValue)
        {
            conditions.Add("(s.data::json ->> 'PerformanceDuration')::double precision >= @MinDurationMs");
            parameters.Add("MinDurationMs", criteria.MinDuration.Value.TotalMilliseconds);
            needed = true;
        }
        if (criteria.MaxDuration.HasValue)
        {
            conditions.Add("(s.data::json ->> 'PerformanceDuration')::double precision <= @MaxDurationMs");
            parameters.Add("MaxDurationMs", criteria.MaxDuration.Value.TotalMilliseconds);
            needed = true;
        }

        // Exception pattern
        if (!string.IsNullOrWhiteSpace(criteria.ExceptionPattern))
        {
            var pattern = "%" + PgHelper.EscapeILikePattern(criteria.ExceptionPattern) + "%";
            conditions.Add(@"(s.data::json ->> 'ExceptionType' ILIKE @ExPattern
                OR s.data::json ->> 'ExceptionMessage' ILIKE @ExPattern)");
            parameters.Add("ExPattern", pattern);
            needed = true;

            // Exception search implies Failed state (unless user explicitly set another state)
            if (criteria.GetEffectiveStates().Count == 0)
            {
                conditions.Add("j.statename = 'Failed'");
            }
        }

        return needed;
    }

    /// <summary>
    /// Stage 3: Cross-table filters using EXISTS subqueries.
    /// </summary>
    private void BuildCrossTableFilters(JobFilterCriteria criteria, List<string> conditions, DynamicParameters parameters)
    {
        // Queue filter
        if (!string.IsNullOrWhiteSpace(criteria.Queue))
        {
            conditions.Add($@"(
                EXISTS (
                    SELECT 1 FROM {_jobParameterTable} jp
                    WHERE jp.jobid = j.id
                      AND jp.name IN ({PgHelper.JobQueueParameterInList})
                      AND jp.value = @Queue
                )
                OR EXISTS (
                    SELECT 1 FROM {_stateTable} s_q
                    WHERE s_q.jobid = j.id
                      AND s_q.name IN ('Enqueued', 'Processing')
                      AND COALESCE(s_q.data::json ->> 'Queue', '') = @Queue
                )
            )");
            parameters.Add("Queue", criteria.Queue);
        }

        // Recurring job ID filter
        if (!string.IsNullOrWhiteSpace(criteria.RecurringJobId))
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {_jobParameterTable} jp
                WHERE jp.jobid = j.id AND jp.name = 'RecurringJobId' AND jp.value = @RecurringJobId
            )");
            parameters.Add("RecurringJobId", criteria.RecurringJobId);
        }

        // Tags filter (AND logic: job must have ALL specified tags)
        if (criteria.Tags != null && criteria.Tags.Count > 0)
        {
            for (int i = 0; i < criteria.Tags.Count; i++)
            {
                var paramName = $"Tag{i}";
                conditions.Add($@"EXISTS (
                    SELECT 1 FROM {_setTable} t
                    WHERE t.key = 'tags:' || @{paramName} AND t.value = j.id::text
                )");
                parameters.Add(paramName, criteria.Tags[i]);
            }
        }
    }

    /// <summary>
    /// Stage 4: Content search CTE (stack trace + console output).
    /// Returns the CTE body SQL or null if not needed.
    /// </summary>
    private string BuildContentSearchCte(JobFilterCriteria criteria, DynamicParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(criteria.ContentPattern))
            return null;

        if (!criteria.SearchStackTrace && !criteria.SearchConsoleOutput)
            return null;

        var pattern = "%" + PgHelper.EscapeILikePattern(criteria.ContentPattern) + "%";
        parameters.Add("ContentPattern", pattern);

        var unionParts = new List<string>();

        if (criteria.SearchStackTrace)
        {
            // Search in state.data (full JSON text includes ExceptionDetails/stack trace)
            unionParts.Add($@"
                SELECT j2.id
                FROM {_jobTable} j2
                INNER JOIN {_stateTable} s2 ON s2.id = j2.stateid
                WHERE s2.data::text ILIKE @ContentPattern");
        }

        if (criteria.SearchConsoleOutput)
        {
            // Console messages stored in set table (short messages as JSON)
            unionParts.Add($@"
                SELECT CAST(h_ref.value AS bigint) AS id
                FROM {_setTable} st
                INNER JOIN {_hashTable} h_ref
                    ON h_ref.key = REPLACE(st.key, 'console:set:', 'console:hash:')
                    AND h_ref.field = 'jobId'
                WHERE st.key LIKE 'console:set:%'
                  AND st.value ILIKE @ContentPattern");

            // Console messages stored in hash table (long messages)
            unionParts.Add($@"
                SELECT CAST(h_ref.value AS bigint) AS id
                FROM {_hashTable} h
                INNER JOIN {_hashTable} h_ref
                    ON h_ref.key = h.key
                    AND h_ref.field = 'jobId'
                WHERE h.key LIKE 'console:hash:%'
                  AND h.field <> 'jobId'
                  AND h.field <> 'progress'
                  AND h.value ILIKE @ContentPattern");
        }

        return string.Join("\n    UNION\n", unionParts);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private: Execution helpers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a count + paginated query pair and returns a PagedResult.
    /// </summary>
    private async Task<PagedResult<JobSummaryDto>> ExecutePagedAsync(
        string countSql, string querySql, object parameters, int page, int pageSize, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
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
            JobId = row.JobId,
            JobName = PgHelper.ExtractJobName(row.InvocationData),
            State = row.State,
            CreatedAt = row.CreatedAt,
            LastStateChange = row.LastStateChange,
            DurationMs = row.DurationMs,
            LatencyMs = row.LatencyMs,
            ExceptionType = row.ExceptionType,
            ExceptionMessage = row.ExceptionMessage
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Internal row types
    // ═══════════════════════════════════════════════════════════════════════════

    private class JobRawRow
    {
        public string JobId { get; set; }
        public string InvocationData { get; set; }
        public string State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }
        public double? DurationMs { get; set; }
        public double? LatencyMs { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
    }

    private class SlowestJobRawRow
    {
        public string JobId { get; set; }
        public string InvocationData { get; set; }
        public double DurationMs { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
