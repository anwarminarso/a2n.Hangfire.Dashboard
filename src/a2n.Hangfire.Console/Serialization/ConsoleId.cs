namespace Hangfire.Console.Serialization;

/// <summary>
/// Console identifier — encodes jobId + timestamp.
/// Format: 11 hex chars (reversed timestamp in ms) + jobId
/// Storage keys: "console:set:{id}" for lines, "console:hash:{id}" for metadata
/// </summary>
internal class ConsoleId : IEquatable<ConsoleId>
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string? _cachedString;

    public string JobId { get; }
    public long Timestamp { get; }
    public DateTime DateValue => UnixEpoch.AddMilliseconds(Timestamp);

    public ConsoleId(string jobId, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(jobId))
            throw new ArgumentNullException(nameof(jobId));

        JobId = jobId;
        Timestamp = (long)(timestamp - UnixEpoch).TotalMilliseconds;

        if (Timestamp <= 0 || Timestamp > int.MaxValue * 1000L)
            throw new ArgumentOutOfRangeException(nameof(timestamp));
    }

    private ConsoleId(string jobId, long timestamp)
    {
        JobId = jobId;
        Timestamp = timestamp;
    }

    public static ConsoleId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length < 12)
            throw new ArgumentException("Invalid value", nameof(value));

        long timestamp = 0;
        for (var i = 10; i >= 0; i--)
        {
            var c = value[i] | 0x20;
            var x = (c >= '0' && c <= '9') ? (c - '0') : (c >= 'a' && c <= 'f') ? (c - 'a' + 10) : -1;
            if (x == -1)
                throw new ArgumentException("Invalid value", nameof(value));
            timestamp = (timestamp << 4) + x;
        }

        return new ConsoleId(value[11..], timestamp) { _cachedString = value };
    }

    /// <summary>
    /// Gets the Set key for storing console lines.
    /// </summary>
    public string GetSetKey() => $"console:set:{this}";

    /// <summary>
    /// Gets the Hash key for storing console metadata and long messages.
    /// </summary>
    public string GetHashKey() => $"console:hash:{this}";

    /// <summary>
    /// Gets the old-format console key (for backward compatibility reads).
    /// </summary>
    public string GetOldConsoleKey() => $"console:{this}";

    public override string ToString()
    {
        if (_cachedString is null)
        {
            var buffer = new char[11 + JobId.Length];
            var timestamp = Timestamp;
            for (var i = 0; i < 11; i++, timestamp >>= 4)
            {
                var c = timestamp & 0x0F;
                buffer[i] = (c < 10) ? (char)(c + '0') : (char)(c - 10 + 'a');
            }
            JobId.CopyTo(0, buffer, 11, JobId.Length);
            _cachedString = new string(buffer);
        }
        return _cachedString;
    }

    public bool Equals(ConsoleId? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(other, this)) return true;
        return other.Timestamp == Timestamp && other.JobId == JobId;
    }

    public override bool Equals(object? obj) => Equals(obj as ConsoleId);
    public override int GetHashCode() => HashCode.Combine(JobId, Timestamp);
}
