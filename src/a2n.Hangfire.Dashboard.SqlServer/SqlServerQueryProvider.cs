using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Storage;
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
        _schema = SqlHelper.ValidateIdentifier(schema ?? throw new ArgumentNullException(nameof(schema)), nameof(schema));
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

        // A substring match can't use an index, so an uncapped COUNT(*) reads the whole Job table.
        // Counting one past the limit stops once the limit is exceeded and still tells "more" apart.
        var countSql = $@"
SELECT COUNT(*) FROM (
    SELECT TOP (@CountLimitPlusOne) j.Id
    FROM {jobTable} j
    {stateJoin}
    {contentJoin}
    {whereClause}
) counted;";

        // Ordered by the clustered primary key rather than CreatedAt: ids increase with creation
        // time, and walking the key newest-first lets the page stop after @PageSize matches instead
        // of sorting every match.
        var querySql = $@"
SELECT {JobColumns}
FROM {jobTable} j
{stateJoin}
{contentJoin}
{whereClause}
ORDER BY j.Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";

        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);
        parameters.Add("CountLimitPlusOne", JobFilterCriteria.CountLimit + 1);

        var result = await ExecutePagedAsync(countSql, querySql, parameters, page, pageSize, ct);
        if (result.TotalCount > JobFilterCriteria.CountLimit)
        {
            result.TotalCount = JobFilterCriteria.CountLimit;
            result.TotalCountIsLowerBound = true;
        }

        return result;
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
    // GetFailureGroupsAsync / GetFailureGroupJobsAsync
    // ═══════════════════════════════════════════════════════════════════════════

    // The job's current state, for the failed-job columns below. State's clustered key is
    // (JobId, Id), so both are given: on Id alone SQL Server can't seek it and reads the whole State
    // table, which holds every state of every job, however few rows the query returns.
    private static string CurrentStateJoin(string stateTable)
        => $"LEFT JOIN {stateTable} s ON s.JobId = j.Id AND s.Id = j.StateId";

    // The failed-job columns FailureGrouping reads, named after FailedJobRow's properties.
    private const string FailedJobRowColumns = @"
j.Id AS Id,
j.StateId AS StateId,
j.InvocationData AS InvocationData,
j.Arguments AS Arguments,
j.CreatedAt AS CreatedAt,
s.Reason AS Reason,
s.CreatedAt AS StateCreatedAt,
s.[Data] AS StateData";

    // A stored value counts only when FailureFingerprint.IsCurrentVersion would accept it: the prefix
    // followed by exactly HashHexLength lowercase hex characters. A binary collation keeps the
    // comparison case-sensitive whatever the database's collation is, and LIKE without a trailing
    // wildcard also rejects anything longer.
    private const string StoredFingerprintMatch = "p.[Value] COLLATE Latin1_General_BIN2 LIKE @StoredPattern";

    private static readonly string StoredFingerprintPattern =
        SqlHelper.EscapeLikePattern(FailureFingerprint.VersionPrefix)
        + string.Concat(Enumerable.Repeat("[0-9a-f]", FailureFingerprint.HashHexLength));

    /// <inheritdoc />
    public bool SupportsFailureGrouping => true;

    /// <inheritdoc />
    public async Task<FailureGroupsResult> GetFailureGroupsAsync(int labelCount, CancellationToken ct)
    {
        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");
        var paramTable = SqlHelper.Table(_schema, "JobParameter");

        // Stored fingerprints are counted from the Job and JobParameter tables alone; State.Data is
        // only read for the fallback and the labels below. Value is nvarchar(max), which can't be
        // grouped on efficiently, hence the cast (a current fingerprint is far shorter).
        //
        // The parameter is sought per failed job rather than joined: as a join the optimizer tends to
        // scan JobParameter, which holds every job's parameters (several per succeeded job), and
        // comparing its nvarchar(max) values costs CPU in proportion to that whole table.
        //
        // A group's newest job is the one that failed last, the highest StateId. SQL Server has no
        // arg-max aggregate, so each job's StateId and Id are put side by side as big-endian bytes:
        // their binary MAX belongs to the job with the highest StateId, whose Id is the last 8 bytes.
        var storedSql = $@"
SELECT CAST(p.[Value] AS NVARCHAR(64)) AS Fingerprint, COUNT_BIG(*) AS [Count],
       CAST(SUBSTRING(MAX(CAST(ISNULL(j.StateId, 0) AS BINARY(8)) + CAST(j.Id AS BINARY(8))), 9, 8) AS BIGINT) AS NewestJobId,
       MAX(ISNULL(j.StateId, 0)) AS NewestStateId
FROM {jobTable} j
CROSS APPLY (
    SELECT TOP (1) p.[Value] FROM {paramTable} p
    WHERE p.JobId = j.Id AND p.[Name] = @ParameterName) p
WHERE j.StateName = 'Failed'
  AND {StoredFingerprintMatch}
GROUP BY CAST(p.[Value] AS NVARCHAR(64));";

        var withoutStoredFingerprint = $@"j.StateName = 'Failed'
  AND NOT EXISTS (
      SELECT 1 FROM {paramTable} p
      WHERE p.JobId = j.Id AND p.[Name] = @ParameterName AND {StoredFingerprintMatch})";

        var failedSql = $@"
SELECT COUNT_BIG(*)
FROM {jobTable} j
WHERE j.StateName = 'Failed';";

        // The ones that failed last. No index orders jobs by StateId, so every failed job without a
        // stored fingerprint is read rather than stopping at @Limit, as walking the primary key did.
        // Only their Id and StateId are sorted, though: the @Limit picked are then joined to the wide
        // columns and their state, as a group's page is, rather than carrying them through the sort.
        var fallbackSql = $@"
SELECT {FailedJobRowColumns}
FROM (
    SELECT TOP (@Limit) j.Id, j.StateId
    FROM {jobTable} j
    WHERE {withoutStoredFingerprint}
    ORDER BY j.StateId DESC, j.Id DESC
) m
INNER JOIN {jobTable} j ON j.Id = m.Id
{CurrentStateJoin(stateTable)}
ORDER BY m.StateId DESC, m.Id DESC;";

        var samplesSql = $@"
SELECT {FailedJobRowColumns}
FROM {jobTable} j
{CurrentStateJoin(stateTable)}
WHERE j.Id IN @Ids AND j.StateName = 'Failed';";

        var parameters = new
        {
            ParameterName = FailureFingerprint.ParameterName,
            StoredPattern = StoredFingerprintPattern,
            Limit = JobFilterCriteria.CountLimit,
        };

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var stored = (await connection.QueryAsync<(string Fingerprint, long Count, long NewestJobId, long NewestStateId)>(
                new CommandDefinition(storedSql, parameters, cancellationToken: ct)))
            .Select(g => new StoredFailureGroup(g.Fingerprint, g.Count, g.NewestJobId) { NewestSequence = g.NewestStateId })
            .ToList();
        // Counted as the failed jobs minus the stored ones, both from indexes, rather than with an
        // anti-join against JobParameter. Jobs that change state between the two queries can make the
        // difference slightly off, never negative.
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
                    new CommandDefinition(samplesSql, new { Ids = ids }, cancellationToken: token));
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

        var jobTable = SqlHelper.Table(_schema, "Job");
        var stateTable = SqlHelper.Table(_schema, "State");
        var paramTable = SqlHelper.Table(_schema, "JobParameter");

        // The group's members are collected first, and only the @Count that failed last are joined to
        // their state. They are ordered by StateId, which no index provides, so every member is read
        // before the first row is returned.
        var sql = $@"
SELECT {FailedJobRowColumns}
FROM (
    SELECT TOP (@Count) m.Id, m.StateId
    FROM ({FailureGroupMembersSql(jobTable, paramTable)}) m
    ORDER BY m.StateId DESC, m.Id DESC
) m
INNER JOIN {jobTable} j ON j.Id = m.Id
{CurrentStateJoin(stateTable)}
ORDER BY m.StateId DESC, m.Id DESC;";

        using var connection = new SqlConnection(_connectionString);
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

        var jobTable = SqlHelper.Table(_schema, "Job");
        var paramTable = SqlHelper.Table(_schema, "JobParameter");

        // Job and JobParameter only: a group action needs the ids, not the state, so State is never
        // read however large the group is. The order comes from Job.StateId.
        var sql = $@"
SELECT m.Id
FROM ({FailureGroupMembersSql(jobTable, paramTable)}) m
ORDER BY m.StateId DESC, m.Id DESC;";

        using var connection = new SqlConnection(_connectionString);
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

    // The ids of a group's jobs that are still failed, with their StateId to order them by: its stored
    // members, sought per failed job as in the grouping query, plus the fallback members it carries
    // (@ComputedIds), minus any the filter has fingerprinted since (it failed again). Takes
    // @ParameterName, @Fingerprint, @StoredPattern and @ComputedIds.
    //
    // A binary = ignores trailing spaces, so the length is compared too; the key itself is a current
    // fingerprint, so this is the same exact match as StoredFingerprintMatch, only cheaper.
    private static string FailureGroupMembersSql(string jobTable, string paramTable) => $@"
        SELECT j.Id, j.StateId
        FROM {jobTable} j
        CROSS APPLY (
            SELECT TOP (1) p.[Value] FROM {paramTable} p
            WHERE p.JobId = j.Id AND p.[Name] = @ParameterName) p
        WHERE j.StateName = 'Failed'
          AND p.[Value] COLLATE Latin1_General_BIN2 = @Fingerprint
          AND DATALENGTH(p.[Value]) = DATALENGTH(@Fingerprint)
        UNION
        SELECT j.Id, j.StateId
        FROM {jobTable} j
        WHERE j.Id IN @ComputedIds
          AND j.StateName = 'Failed'
          AND NOT EXISTS (
              SELECT 1 FROM {paramTable} p
              WHERE p.JobId = j.Id AND p.[Name] = @ParameterName AND {StoredFingerprintMatch})";

    private static List<long> ParseJobIds(IEnumerable<string> ids)
    {
        var parsed = new List<long>();
        foreach (var id in ids ?? Enumerable.Empty<string>())
        {
            if (long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
                parsed.Add(value);
        }
        return parsed;
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

        if (!string.IsNullOrWhiteSpace(criteria.ArgumentsPattern))
        {
            conditions.Add($"{SqlHelper.JobArgumentsSql()} LIKE @ArgsPattern");
            parameters.Add("ArgsPattern", "%" + SqlHelper.EscapeLikePattern(criteria.ArgumentsPattern) + "%");
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
            var stateTable = SqlHelper.Table(_schema, "State");
            conditions.Add($@"(
                EXISTS (
                    SELECT 1 FROM {jobParamTable} jp
                    WHERE jp.JobId = j.Id
                      AND jp.Name IN ({SqlHelper.JobQueueParameterInList})
                      AND jp.Value = @Queue
                )
                OR EXISTS (
                    SELECT 1 FROM {stateTable} s_q
                    WHERE s_q.JobId = j.Id
                      AND s_q.Name IN ('Enqueued', 'Processing')
                      AND COALESCE(JSON_VALUE(s_q.Data, '$.Queue'), '') = @Queue
                )
            )");
            parameters.Add("Queue", criteria.Queue);
        }

        if (!string.IsNullOrWhiteSpace(criteria.RecurringJobId))
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {jobParamTable} jp
                WHERE jp.JobId = j.Id AND jp.Name = 'RecurringJobId' AND jp.Value IN @RecurringJobIdValues
            )");
            parameters.Add("RecurringJobIdValues",
                JobParameterMatching.AllValueForms(new[] { criteria.RecurringJobId }));
        }

        // The filter stores the fingerprint as a raw string, not JSON, so it is compared as is rather
        // than through JobParameterMatching. A binary = ignores trailing spaces, so the length is
        // compared too: the same exact match the Failed page's grouping uses.
        if (criteria.EffectiveFailureFingerprint is { } fingerprint)
        {
            conditions.Add($@"EXISTS (
                SELECT 1 FROM {jobParamTable} jp
                WHERE jp.JobId = j.Id AND jp.Name = @FailureFingerprintName
                  AND jp.Value COLLATE Latin1_General_BIN2 = @FailureFingerprint
                  AND DATALENGTH(jp.Value) = DATALENGTH(@FailureFingerprint)
            )");
            parameters.Add("FailureFingerprintName", FailureFingerprint.ParameterName);
            parameters.Add("FailureFingerprint", fingerprint);
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
