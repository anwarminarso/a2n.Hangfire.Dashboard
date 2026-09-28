using System.Collections.Generic;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.PostgreSql.Internal;
using Dapper;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Npgsql;

namespace a2n.Hangfire.Dashboard.PostgreSql;

/// <summary>
/// Failed-job page for PostgreSQL that leaves missing state fields empty.
/// Same order as <c>PostgreSqlMonitoringApi.FailedJobs</c>: newest id first.
/// </summary>
public sealed class PostgreSqlFailedJobReader : IFailedJobPageSource
{
    private readonly string _connectionString;
    private readonly string _jobTable;
    private readonly string _stateTable;

    public PostgreSqlFailedJobReader(string connectionString, string schema = "hangfire")
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        schema = PgHelper.ValidateIdentifier(schema ?? "hangfire", nameof(schema));
        _jobTable = PgHelper.Table(schema, "job");
        _stateTable = PgHelper.Table(schema, "state");
    }

    public JobList<FailedJobDto> GetFailedJobs(int from, int count)
    {
        if (from < 0)
            from = 0;
        if (count <= 0)
            return Empty();

        var sql = $@"
SELECT j.id::text AS ""Id"",
       j.invocationdata::text AS ""InvocationData"",
       j.arguments::text AS ""Arguments"",
       s.reason AS ""Reason"",
       s.createdat AS ""StateCreatedAt"",
       s.data::text AS ""StateData""
FROM {_jobTable} j
LEFT JOIN {_stateTable} s ON s.id = j.stateid
WHERE j.statename = 'Failed'
ORDER BY j.id DESC
LIMIT @Count OFFSET @From";

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        var rows = connection.Query<FailedJobRow>(sql, new { From = from, Count = count });

        var result = new List<KeyValuePair<string, FailedJobDto>>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Id))
                continue;
            result.Add(new KeyValuePair<string, FailedJobDto>(row.Id, Map(row)));
        }

        return new JobList<FailedJobDto>(result);
    }

    private static JobList<FailedJobDto> Empty()
        => new(Array.Empty<KeyValuePair<string, FailedJobDto>>());

    private static FailedJobDto Map(FailedJobRow row)
    {
        InvocationData invocation = null;
        Job job = null;
        JobLoadException loadException = null;

        if (!string.IsNullOrEmpty(row.InvocationData))
        {
            try
            {
                invocation = SerializationHelper.Deserialize<InvocationData>(row.InvocationData);
                if (invocation != null && row.Arguments != null)
                    invocation.Arguments = row.Arguments;

                if (invocation != null)
                {
                    try
                    {
                        job = invocation.DeserializeJob();
                    }
                    catch (JobLoadException ex)
                    {
                        loadException = ex;
                    }
                }
            }
            catch (JobLoadException ex)
            {
                loadException = ex;
            }
            catch (Exception)
            {
                // Unreadable payload: still list the job id.
            }
        }

        var state = ReadState(row.StateData);
        return new FailedJobDto
        {
            Job = job,
            InvocationData = invocation,
            LoadException = loadException,
            Reason = row.Reason,
            ExceptionType = Get(state, "ExceptionType"),
            ExceptionMessage = Get(state, "ExceptionMessage"),
            ExceptionDetails = Get(state, "ExceptionDetails"),
            FailedAt = ReadFailedAt(state) ?? AsUtc(row.StateCreatedAt),
            InFailedState = true,
            StateData = state,
        };
    }

    private static DateTime? AsUtc(DateTime? value)
    {
        if (value == null)
            return null;
        return value.Value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
    }

    private static DateTime? ReadFailedAt(Dictionary<string, string> state)
    {
        var text = Get(state, "FailedAt");
        if (string.IsNullOrEmpty(text))
            return null;

        try
        {
            return JobHelper.DeserializeNullableDateTime(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadState(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var parsed = SerializationHelper.Deserialize<Dictionary<string, string>>(json);
            if (parsed == null)
                return null;
            return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Get(Dictionary<string, string> state, string key)
    {
        if (state == null)
            return null;
        return state.TryGetValue(key, out var value) ? value : null;
    }

    private sealed class FailedJobRow
    {
        public string Id { get; set; }
        public string InvocationData { get; set; }
        public string Arguments { get; set; }
        public string Reason { get; set; }
        public DateTime? StateCreatedAt { get; set; }
        public string StateData { get; set; }
    }
}
