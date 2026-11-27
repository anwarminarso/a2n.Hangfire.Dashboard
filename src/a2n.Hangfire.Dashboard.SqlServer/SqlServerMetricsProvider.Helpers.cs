using System.Data;
using System.Globalization;
using a2n.Hangfire.Dashboard.Storage;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace a2n.Hangfire.Dashboard.SqlServer;

public partial class SqlServerMetricsProvider
{
    #region Private Helpers

    private static string GetTimeGroupExpression(MetricsInterval interval)
    {
        return interval switch
        {
            MetricsInterval.OneMinute => "DATEADD(MINUTE, DATEDIFF(MINUTE, 0, s.CreatedAt), 0)",
            MetricsInterval.FiveMinutes => "DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, s.CreatedAt) / 5) * 5, 0)",
            MetricsInterval.FifteenMinutes => "DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, s.CreatedAt) / 15) * 15, 0)",
            MetricsInterval.OneHour => "DATEADD(HOUR, DATEDIFF(HOUR, 0, s.CreatedAt), 0)",
            MetricsInterval.OneDay => "CAST(s.CreatedAt AS DATE)",
            _ => "DATEADD(HOUR, DATEDIFF(HOUR, 0, s.CreatedAt), 0)"
        };
    }

    private static string ExtractJobTypeName(string? invocationData)
    {
        if (string.IsNullOrEmpty(invocationData)) return "Unknown";

        // New format: "Namespace.Class, Assembly|MethodName" (from CONCAT in SQL)
        var pipeIdx = invocationData.IndexOf('|');
        if (pipeIdx >= 0)
        {
            var typePart = invocationData[..pipeIdx];
            var methodPart = invocationData[(pipeIdx + 1)..];

            if (!string.IsNullOrEmpty(typePart))
            {
                // Strip assembly info
                var commaIdx = typePart.IndexOf(',');
                var typeName = commaIdx > 0 ? typePart[..commaIdx].Trim() : typePart.Trim();

                // Get just the class name
                var dotIdx = typeName.LastIndexOf('.');
                var className = dotIdx > 0 ? typeName[(dotIdx + 1)..] : typeName;

                if (!string.IsNullOrEmpty(methodPart))
                    return $"{className}.{methodPart}";

                return className;
            }
        }

        // Fallback: try parsing as JSON (legacy format)
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(invocationData);
            if (doc.RootElement.TryGetProperty("Type", out var typeProp))
            {
                var fullType = typeProp.GetString();
                if (!string.IsNullOrEmpty(fullType))
                {
                    var commaIdx = fullType.IndexOf(',');
                    var typeName = commaIdx > 0 ? fullType[..commaIdx] : fullType;
                    var dotIdx = typeName.LastIndexOf('.');
                    return dotIdx > 0 ? typeName[(dotIdx + 1)..] : typeName;
                }
            }
        }
        catch
        {
            // Malformed invocation data: fall through to the "Unknown" type name below.
        }

        return "Unknown";
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
            return result;

        // Try Unix timestamp (milliseconds)
        if (long.TryParse(value, out var unixMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

        return null;
    }

    private async Task EnrichRecurringJobHealthAsync(
        SqlConnection connection,
        List<RecurringJobHealthDto> results,
        IReadOnlyList<string> jobIds,
        CancellationToken ct)
    {
        if (results.Count == 0 || jobIds.Count == 0)
            return;

        var sql = $@"
WITH Ranked AS (
    SELECT jp.Value AS RecurringJobIdStored,
           s.Name AS StateName,
           CAST(COALESCE(JSON_VALUE(s.Data, '$.PerformanceDuration'), '0') AS FLOAT) AS DurationMs,
           ROW_NUMBER() OVER (PARTITION BY jp.Value ORDER BY s.CreatedAt DESC) AS rn
    FROM {Table("Job")} j
    INNER JOIN {Table("JobParameter")} jp ON jp.JobId = j.Id AND jp.Name = 'RecurringJobId'
    INNER JOIN {Table("State")} s ON s.JobId = j.Id AND s.Id = j.StateId
    WHERE jp.Value IN @RecurringJobIdValues
      AND s.Name IN ('Succeeded', 'Failed')
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

            var durations = execs.Where(e => e.DurationMs > 0).Select(e => e.DurationMs).ToList();
            if (durations.Count > 0)
                dto.AverageDurationMs = durations.Average();
        }
    }

    #endregion
}
