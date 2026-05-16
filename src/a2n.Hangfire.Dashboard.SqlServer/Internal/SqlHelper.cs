namespace a2n.Hangfire.Dashboard.SqlServer.Internal;

/// <summary>
/// Internal helper methods for SQL Server query construction.
/// Provides LIKE pattern sanitization and schema-prefixed table name generation.
/// </summary>
internal static class SqlHelper
{
    /// <summary>
    /// Escapes LIKE pattern special characters: %, _, [
    /// Must be called before embedding user input into a LIKE pattern parameter.
    /// Order matters: [ must be escaped first to avoid double-escaping.
    /// </summary>
    /// <param name="input">Raw user input string</param>
    /// <returns>Escaped string safe for use in LIKE patterns</returns>
    public static string EscapeLikePattern(string input)
    {
        return input
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("_", "[_]");
    }

    /// <summary>
    /// Prefixes table name with schema using bracket notation.
    /// E.g., Table("HangFire", "Job") returns "[HangFire].[Job]"
    /// </summary>
    /// <param name="schema">Schema name (e.g., "HangFire")</param>
    /// <param name="tableName">Table name (e.g., "Job")</param>
    /// <returns>Fully qualified table reference</returns>
    public static string Table(string schema, string tableName)
        => $"[{schema}].[{tableName}]";
}
