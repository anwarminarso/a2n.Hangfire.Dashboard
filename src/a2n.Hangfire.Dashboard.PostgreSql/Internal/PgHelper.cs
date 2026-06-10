using System.Text.Json;
using System.Text.RegularExpressions;
using HF = Hangfire;
namespace a2n.Hangfire.Dashboard.PostgreSql.Internal;

internal static class PgHelper
{
    /// <summary>Hangfire job parameter for queue (see <see cref="HF.Storage.JobStorageFeatures.JobQueueProperty"/>).</summary>
    public const string JobQueueParameterName = "Job.Queue";

    /// <summary>
    /// Legacy parameter name from early dashboard query providers (not written by Hangfire core;
    /// Hangfire uses <see cref="JobQueueParameterName"/>). Kept for existing databases only.
    /// </summary>
    public const string LegacyCurrentQueueParameterName = "CurrentQueue";

    private static readonly Regex IdentifierRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>SQL IN-list for queue job parameter names.</summary>
    public static string JobQueueParameterInList => $"'{JobQueueParameterName}', '{LegacyCurrentQueueParameterName}'";

    /// <summary>
    /// Validates schema/table identifiers used in SQL fragments (prevents identifier injection via config).
    /// </summary>
    public static string ValidateIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !IdentifierRegex.IsMatch(identifier))
            throw new ArgumentException($"Invalid SQL identifier for {paramName}.", paramName);

        return identifier;
    }

    /// <summary>
    /// SQL expression that normalizes invocationdata to "Type|Method" for consistent GROUP BY.
    /// </summary>
    public static string InvocationDataTypeMethodSql(string invocationDataColumn = "j.invocationdata")
        => $@"CONCAT(
            COALESCE({invocationDataColumn}::json ->> 'Type', {invocationDataColumn}::json ->> 't', ''),
            '|',
            COALESCE({invocationDataColumn}::json ->> 'Method', {invocationDataColumn}::json ->> 'm', '')
        )";

    /// <summary>
    /// Escapes ILIKE pattern special characters: %, _, \
    /// </summary>
    public static string EscapeILikePattern(string input)
    {
        if (input == null)
            return string.Empty;

        return input
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
    }

    /// <summary>
    /// Prefixes table name with schema. E.g., "hangfire"."job"
    /// PostgreSQL uses lowercase by default.
    /// </summary>
    public static string Table(string schema, string tableName)
        => $"\"{schema}\".\"{tableName}\"";

    /// <summary>
    /// Extracts a human-readable job name (ClassName.MethodName) from a pipe-separated
    /// "FullType, Assembly|MethodName" string produced by SQL CONCAT queries.
    /// Falls back to JSON InvocationData parsing if no pipe separator is found.
    /// Returns "Unknown" if parsing fails.
    /// </summary>
    public static string ExtractJobTypeName(string typeMethodString)
    {
        if (string.IsNullOrEmpty(typeMethodString))
            return "Unknown";

        // Expected format: "Namespace.Class, Assembly|MethodName"
        var pipeIdx = typeMethodString.IndexOf('|');
        if (pipeIdx >= 0)
        {
            var typePart = typeMethodString[..pipeIdx];
            var methodPart = typeMethodString[(pipeIdx + 1)..];

            if (!string.IsNullOrEmpty(typePart))
            {
                // Strip assembly info: "Namespace.Class, Assembly" → "Namespace.Class"
                var commaIdx = typePart.IndexOf(',');
                var typeName = commaIdx > 0 ? typePart[..commaIdx].Trim() : typePart.Trim();

                // Get just the class name (last segment after dot)
                var dotIdx = typeName.LastIndexOf('.');
                var className = dotIdx > 0 ? typeName[(dotIdx + 1)..] : typeName;

                if (!string.IsNullOrEmpty(methodPart))
                    return $"{className}.{methodPart}";

                return className;
            }
        }

        // Fallback: try parsing as raw JSON InvocationData
        return ExtractJobName(typeMethodString);
    }

    /// <summary>
    /// Extracts a human-readable job name (Type.Method) from Hangfire's InvocationData JSON.
    /// InvocationData format: {"Type":"Namespace.Class, Assembly","Method":"MethodName","ParameterTypes":"[...]","Arguments":"[...]"}
    /// Returns "ClassName.MethodName" or "Unknown" if parsing fails.
    /// </summary>
    public static string ExtractJobName(string invocationData)
    {
        if (string.IsNullOrWhiteSpace(invocationData))
            return "Unknown";

        try
        {
            using var doc = JsonDocument.Parse(invocationData);
            var root = doc.RootElement;

            string typeName = null;
            string methodName = null;

            // Try "Type" (capital) then "t" (lowercase, older format)
            if (root.TryGetProperty("Type", out var typeProp))
                typeName = typeProp.GetString();
            else if (root.TryGetProperty("t", out typeProp))
                typeName = typeProp.GetString();

            // Try "Method" (capital) then "m" (lowercase)
            if (root.TryGetProperty("Method", out var methodProp))
                methodName = methodProp.GetString();
            else if (root.TryGetProperty("m", out methodProp))
                methodName = methodProp.GetString();

            if (string.IsNullOrEmpty(typeName))
                return "Unknown";

            // Strip assembly info: "Namespace.Class, Assembly" → "Namespace.Class"
            var commaIdx = typeName.IndexOf(',');
            if (commaIdx > 0)
                typeName = typeName.Substring(0, commaIdx).Trim();

            // Get just the class name (last segment after dot)
            var dotIdx = typeName.LastIndexOf('.');
            var className = dotIdx > 0 ? typeName.Substring(dotIdx + 1) : typeName;

            if (!string.IsNullOrEmpty(methodName))
                return $"{className}.{methodName}";

            return className;
        }
        catch
        {
            return "Unknown";
        }
    }
}
