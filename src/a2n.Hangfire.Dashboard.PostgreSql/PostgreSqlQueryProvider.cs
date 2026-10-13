using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Storage;
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

        // A substring match can't use a B-tree index, so an uncapped COUNT(*) reads the whole job
        // table. Counting one past the limit stops once the limit is exceeded and still tells
        // "more" apart.
        var countSql = $@"{ctePart}
SELECT COUNT(*) FROM (
    SELECT j.id
    FROM {_jobTable} j
    {stateJoin}
    {contentJoin}
    {whereClause}
    LIMIT @CountLimitPlusOne
) counted";

        // Ordered by the primary key rather than createdat: ids increase with creation time, and
        // walking the key newest-first lets the page stop after @PageSize matches instead of
        // sorting every match.
        var querySql = $@"{ctePart}
SELECT {JobColumns}
FROM {_jobTable} j
{stateJoin}
{contentJoin}
{whereClause}
ORDER BY j.id DESC
LIMIT @PageSize OFFSET @Offset";

        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset", offset);
        parameters.Add("CountLimitPlusOne", JobFilterCriteria.CountLimit + 1);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, parameters, cancellationToken: ct));

        var rows = await connection.QueryAsync<JobRawRow>(
            new CommandDefinition(querySql, parameters, cancellationToken: ct));

        var items = rows.Select(MapToJobSummary).ToList();
        var countIsLowerBound = totalCount > JobFilterCriteria.CountLimit;

        return new PagedResult<JobSummaryDto>
        {
            Items = items,
            TotalCount = countIsLowerBound ? JobFilterCriteria.CountLimit : totalCount,
            TotalCountIsLowerBound = countIsLowerBound,
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
    // GetFailureGroupsAsync / GetFailureGroupJobsAsync
    // ═══════════════════════════════════════════════════════════════════════════

    // The failed-job columns FailureGrouping reads, named after FailedJobRow's properties. The jsonb
    // columns (text on older schemas) are read as text.
    private const string FailedJobRowColumns = @"
j.id AS ""Id"",
j.invocationdata::text AS ""InvocationData"",
j.arguments::text AS ""Arguments"",
j.createdat AS ""CreatedAt"",
s.reason AS ""Reason"",
s.createdat AS ""StateCreatedAt"",
s.data::text AS ""StateData""";

    // A stored value counts only when FailureFingerprint.IsCurrentVersion would accept it: the prefix
    // followed by exactly HashHexLength lowercase hex characters. PostgreSQL regexes are
    // case-sensitive, and the anchors reject anything longer.
    private const string StoredFingerprintMatch = "p.value ~ @StoredPattern";

    internal static readonly string StoredFingerprintPattern =
        "^" + System.Text.RegularExpressions.Regex.Escape(FailureFingerprint.VersionPrefix)
        + "[0-9a-f]{" + FailureFingerprint.HashHexLength + "}$";

    // Stored fingerprints are counted from the job and jobparameter indexes alone; state.data is only
    // read for the fallback and the labels. Takes @ParameterName and @StoredPattern.
    //
    // The parameter is looked up per failed job, rather than joined: as a join, the planner prefers to
    // scan the whole jobparameter table, which holds every job's parameters (several per succeeded
    // job), once there are a few thousand failed jobs. The LIMIT keeps the lookup from being flattened
    // back into a join, and counts a job once should an old schema hold a duplicate parameter.
    internal string StoredFingerprintGroupsSql => $@"
SELECT p.value AS ""Fingerprint"", COUNT(*) AS ""Count"", MAX(j.id) AS ""NewestJobId""
FROM {_jobTable} j
CROSS JOIN LATERAL (
    SELECT p.value FROM {_jobParameterTable} p
    WHERE p.jobid = j.id AND p.name = @ParameterName
    LIMIT 1) p
WHERE j.statename = 'Failed'
  AND {StoredFingerprintMatch}
GROUP BY p.value";

    /// <inheritdoc />
    public bool SupportsFailureGrouping => true;

    /// <inheritdoc />
    public async Task<FailureGroupsResult> GetFailureGroupsAsync(int labelCount, CancellationToken ct)
    {

        var withoutStoredFingerprint = $@"j.statename = 'Failed'
  AND NOT EXISTS (
      SELECT 1 FROM {_jobParameterTable} p
      WHERE p.jobid = j.id AND p.name = @ParameterName AND {StoredFingerprintMatch})";

        var failedSql = $@"
SELECT COUNT(*)
FROM {_jobTable} j
WHERE j.statename = 'Failed'";

        var fallbackSql = $@"
SELECT {FailedJobRowColumns}
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE {withoutStoredFingerprint}
ORDER BY j.id DESC
LIMIT @Limit";

        var samplesSql = $@"
SELECT {FailedJobRowColumns}
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE j.id = ANY(@Ids) AND j.statename = 'Failed'";

        var parameters = new
        {
            ParameterName = FailureFingerprint.ParameterName,
            StoredPattern = StoredFingerprintPattern,
            Limit = JobFilterCriteria.CountLimit,
        };

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var stored = (await connection.QueryAsync<StoredFailureGroup>(
            new CommandDefinition(StoredFingerprintGroupsSql, parameters, cancellationToken: ct))).ToList();
        // Counted as the failed jobs minus the stored ones rather than with NOT EXISTS, which the
        // planner turns into an anti-join over the whole jobparameter table. Jobs that change state
        // between the two queries can make the difference slightly off, never negative.
        var failed = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(failedSql, cancellationToken: ct));
        var unfingerprinted = Math.Max(0, failed - stored.Sum(s => s.Count));
        var fallback = await connection.QueryAsync<FailedJobRow>(
            new CommandDefinition(fallbackSql, parameters, cancellationToken: ct));

        var result = await FailureGrouping.BuildAsync(
            stored,
            unfingerprinted,
            fallback.Select(FailureGrouping.Compute).ToList(),
            labelCount,
            async (ids, token) =>
            {
                var rows = await connection.QueryAsync<FailedJobRow>(
                    new CommandDefinition(samplesSql, new { Ids = ids.ToArray() }, cancellationToken: token));
                return rows.Select(FailureGrouping.Compute).ToList();
            },
            ct);
        result.UsesStoredFingerprints = true;
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSummaryDto>> GetFailureGroupJobsAsync(
        FailureGroupDto group, int count, CancellationToken ct)
    {
        // Group keys are always current fingerprints; anything else can't name a group.
        if (group == null || !FailureFingerprint.IsCurrentVersion(group.Fingerprint) || count <= 0)
            return Array.Empty<JobSummaryDto>();

        // The group's members are collected first, and only the newest @Count of them are joined to
        // their state. Filtering while walking the job table newest-first instead reads the whole table
        // when the group's jobs are old. The LIMIT also keeps the planner from flattening the subquery.
        var sql = $@"
SELECT {FailedJobRowColumns}
FROM (
    {FailureGroupMembersSql}
    ORDER BY 1 DESC
    LIMIT @Count) m
INNER JOIN {_jobTable} j ON j.id = m.id
LEFT JOIN {_stateTable} s ON s.id = j.stateid
ORDER BY j.id DESC";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var rows = await connection.QueryAsync<FailedJobRow>(new CommandDefinition(sql, new
        {
            Count = count,
            ParameterName = FailureFingerprint.ParameterName,
            Fingerprint = group.Fingerprint,
            StoredPattern = StoredFingerprintPattern,
            ComputedIds = ParseJobIds(group.ComputedJobIds),
        }, cancellationToken: ct));

        return rows.Select(FailureGrouping.ToJobSummary).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetFailureGroupJobIdsAsync(FailureGroupDto group, CancellationToken ct)
    {
        if (group == null || !FailureFingerprint.IsCurrentVersion(group.Fingerprint))
            return Array.Empty<string>();

        // job and jobparameter only: a group action needs the ids, not the state, so state.data is
        // never read however large the group is.
        var sql = $@"
{FailureGroupMembersSql}
ORDER BY 1 DESC";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var ids = await connection.QueryAsync<long>(new CommandDefinition(sql, new
        {
            ParameterName = FailureFingerprint.ParameterName,
            Fingerprint = group.Fingerprint,
            StoredPattern = StoredFingerprintPattern,
            ComputedIds = ParseJobIds(group.ComputedJobIds),
        }, cancellationToken: ct));

        return ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

    // The ids of a group's jobs that are still failed: its stored members, looked up per failed job as
    // in StoredFingerprintGroupsSql, plus the fallback members it carries (@ComputedIds), minus any the
    // filter has fingerprinted since (it failed again). Takes @ParameterName, @Fingerprint,
    // @StoredPattern and @ComputedIds. The key is a current fingerprint, so = is the same exact match
    // as StoredFingerprintMatch.
    internal string FailureGroupMembersSql => $@"
    SELECT f.id
    FROM {_jobTable} f
    CROSS JOIN LATERAL (
        SELECT p.value FROM {_jobParameterTable} p
        WHERE p.jobid = f.id AND p.name = @ParameterName
        LIMIT 1) p
    WHERE f.statename = 'Failed' AND p.value = @Fingerprint
    UNION
    SELECT f.id
    FROM {_jobTable} f
    WHERE f.id = ANY(@ComputedIds)
      AND f.statename = 'Failed'
      AND NOT EXISTS (
          SELECT 1 FROM {_jobParameterTable} p
          WHERE p.jobid = f.id AND p.name = @ParameterName AND {StoredFingerprintMatch})";

    private static long[] ParseJobIds(IEnumerable<string> ids)
    {
        var parsed = new List<long>();
        foreach (var id in ids ?? Enumerable.Empty<string>())
        {
            if (long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
                parsed.Add(value);
        }
        return parsed.ToArray();
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

        // Arguments pattern (ILIKE on the arguments column only, never the type or method name)
        if (!string.IsNullOrWhiteSpace(criteria.ArgumentsPattern))
        {
            conditions.Add($"{PgHelper.JobArgumentsSql()} ILIKE @ArgsPattern");
            parameters.Add("ArgsPattern", "%" + PgHelper.EscapeILikePattern(criteria.ArgumentsPattern) + "%");
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
                WHERE jp.jobid = j.id AND jp.name = 'RecurringJobId' AND jp.value = ANY(@RecurringJobIdValues)
            )");
            parameters.Add("RecurringJobIdValues",
                JobParameterMatching.AllValueForms(new[] { criteria.RecurringJobId }));
        }

        // The filter stores the fingerprint as a raw string, not JSON, so it is compared as is rather
        // than through JobParameterMatching.
        if (criteria.EffectiveFailureFingerprint is { } fingerprint)
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {_jobParameterTable} jp
                WHERE jp.jobid = j.id AND jp.name = @FailureFingerprintName AND jp.value = @FailureFingerprint
            )");
            parameters.Add("FailureFingerprintName", FailureFingerprint.ParameterName);
            parameters.Add("FailureFingerprint", fingerprint);
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
