using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Newtonsoft.Json.Linq;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Reads console data from Hangfire storage.
/// Compatible with data written by both original Hangfire.Console and a2n.Hangfire.Console.
/// </summary>
public class ConsoleDataReader
{
    private readonly JobStorage _storage;

    public ConsoleDataReader(JobStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Gets console lines for a job execution.
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="startedAt">The job's StartedAt timestamp (from Processing state)</param>
    /// <returns>List of console lines, or empty if no console data exists</returns>
    public IReadOnlyList<ConsoleLineDto> GetLines(string jobId, DateTime startedAt)
    {
        var consoleId = FormatConsoleId(jobId, startedAt);
        var setKey = $"console:set:{consoleId}";
        var hashKey = $"console:hash:{consoleId}";
        var oldKey = $"console:{consoleId}";

        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return [];

        // Try new key format first
        var count = (int)storageConnection.GetSetCount(setKey);
        var useOldKeys = false;

        if (count == 0)
        {
            // Fallback to old key format
            count = (int)storageConnection.GetSetCount(oldKey);
            useOldKeys = true;
        }

        if (count == 0)
            return [];

        var actualSetKey = useOldKeys ? oldKey : setKey;
        var items = storageConnection.GetRangeFromSet(actualSetKey, 0, count - 1);

        if (items is null || items.Count == 0)
            return [];

        var lines = new List<ConsoleLineDto>(items.Count);

        foreach (var item in items)
        {
            var line = SerializationHelper.Deserialize<RawConsoleLine>(item, SerializationOption.User);
            if (line is null) continue;

            var message = line.Message ?? "";

            // Resolve reference (long messages stored in hash)
            if (line.IsReference)
            {
                var resolvedHashKey = useOldKeys ? oldKey : hashKey;
                try
                {
                    message = storageConnection.GetValueFromHash(resolvedHashKey, message) ?? message;
                }
                catch { /* ignore read errors */ }
            }

            lines.Add(new ConsoleLineDto
            {
                TimeOffset = line.TimeOffset,
                Message = message,
                TextColor = line.TextColor,
                ProgressValue = line.ProgressValue,
                ProgressName = line.ProgressName,
                IsProgressBar = line.ProgressValue.HasValue,
                ProgressId = line.ProgressValue.HasValue ? line.Message : null
            });
        }

        return lines;
    }

    /// <summary>
    /// Formats a ConsoleId string (11 hex chars reversed timestamp + jobId).
    /// Must match the format used by Hangfire.Console.
    /// </summary>
    private static string FormatConsoleId(string jobId, DateTime startedAt)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timestamp = (long)(startedAt.ToUniversalTime() - epoch).TotalMilliseconds;

        var buffer = new char[11 + jobId.Length];
        for (var i = 0; i < 11; i++, timestamp >>= 4)
        {
            var c = timestamp & 0x0F;
            buffer[i] = (c < 10) ? (char)(c + '0') : (char)(c - 10 + 'a');
        }
        jobId.CopyTo(0, buffer, 11, jobId.Length);
        return new string(buffer);
    }
}

/// <summary>
/// A console line as displayed in the dashboard.
/// </summary>
public class ConsoleLineDto
{
    public double TimeOffset { get; set; }
    public string Message { get; set; } = "";
    public string TextColor { get; set; }
    public double? ProgressValue { get; set; }
    public string ProgressName { get; set; }
    public bool IsProgressBar { get; set; }

    /// <summary>
    /// Unique identifier for progress bars (from Message field in storage).
    /// Used to group multiple progress updates into a single rendered bar.
    /// </summary>
    public string ProgressId { get; set; }
}

/// <summary>
/// Raw JSON format from storage (matches Hangfire.Console's ConsoleLine).
/// Dual attributes for Newtonsoft.Json (used by Hangfire's SerializationHelper) and System.Text.Json.
/// </summary>
internal class RawConsoleLine
{
    [Newtonsoft.Json.JsonProperty("t")]
    [System.Text.Json.Serialization.JsonPropertyName("t")]
    public double TimeOffset { get; set; }

    [Newtonsoft.Json.JsonProperty("r")]
    [System.Text.Json.Serialization.JsonPropertyName("r")]
    public bool IsReference { get; set; }

    [Newtonsoft.Json.JsonProperty("s")]
    [System.Text.Json.Serialization.JsonPropertyName("s")]
    public string Message { get; set; }

    [Newtonsoft.Json.JsonProperty("c")]
    [System.Text.Json.Serialization.JsonPropertyName("c")]
    public string TextColor { get; set; }

    [Newtonsoft.Json.JsonProperty("p")]
    [System.Text.Json.Serialization.JsonPropertyName("p")]
    public double? ProgressValue { get; set; }

    [Newtonsoft.Json.JsonProperty("n")]
    [System.Text.Json.Serialization.JsonPropertyName("n")]
    public string ProgressName { get; set; }
}
