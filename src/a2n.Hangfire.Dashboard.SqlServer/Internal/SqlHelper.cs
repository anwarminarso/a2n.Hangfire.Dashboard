using System.Text.RegularExpressions;

namespace a2n.Hangfire.Dashboard.SqlServer.Internal;

/// <summary>
/// Internal helper methods for SQL Server query construction.
/// Provides LIKE pattern sanitization and schema-prefixed table name generation.
/// </summary>
internal static class SqlHelper
{
    public const string JobQueueParameterName = "Job.Queue";
    /// <summary>Legacy dashboard parameter only — Hangfire core uses <see cref="JobQueueParameterName"/>.</summary>
    public const string LegacyCurrentQueueParameterName = "CurrentQueue";

    private static readonly Regex IdentifierRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

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
    /// Escapes LIKE pattern special characters: %, _, [
    /// Must be called before embedding user input into a LIKE pattern parameter.
    /// Order matters: [ must be escaped first to avoid double-escaping.
    /// </summary>
    /// <param name="input">Raw user input string</param>
    /// <returns>Escaped string safe for use in LIKE patterns</returns>
    public static string EscapeLikePattern(string input)
    {
        if (input == null)
            return string.Empty;

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
