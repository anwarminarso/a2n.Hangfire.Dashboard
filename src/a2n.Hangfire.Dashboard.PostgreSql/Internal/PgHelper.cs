using System.Text.Json;

namespace a2n.Hangfire.Dashboard.PostgreSql.Internal;

internal static class PgHelper
{
    /// <summary>
    /// Escapes ILIKE pattern special characters: %, _, \
    /// </summary>
    public static string EscapeILikePattern(string input)
    {
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
