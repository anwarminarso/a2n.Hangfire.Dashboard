using System.Globalization;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Rollup.Internal;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Rollup;

public sealed partial class MetricsRollupStore
{
    private static SlowestJobDto? ParseSlowest(KeyValuePair<string, string> entry)
    {
        var parts = entry.Value?.Split('|') ?? Array.Empty<string>();
        if (parts.Length < 2)
            return null;

        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration);
        long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks);
        return new SlowestJobDto
        {
            JobId = entry.Key,
            DurationMs = duration,
            CompletedAt = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null,
            JobName = parts.Length > 2 ? parts[2] : "Unknown"
        };
    }

    /// <summary>
    /// Recovers the distinct entity prefixes (job type or queue name) stored in a duration-style hash.
    /// Fields are persisted by <see cref="WriteDurationFields"/> as <c>{prefix}:count</c>,
    /// <c>{prefix}:sum</c>, … so the prefix is the part before the trailing <c>:count</c> marker.
    /// Splitting on the first colon is not safe: prefixes such as <c>MyJob.Run</c> are free to contain
    /// separators of their own.
    /// </summary>
    private static List<string> EnumerateDurationPrefixes(IReadOnlyDictionary<string, string> hash)
        => hash.Keys
            .Where(k => k != null && k.EndsWith(CountSuffix, StringComparison.Ordinal) && k.Length > CountSuffix.Length)
            .Select(k => k[..^CountSuffix.Length])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    private static JobDurationStatsDto? ParseDurationFields(
        IReadOnlyDictionary<string, string> hash, string prefix, bool isQueue = false)
    {
        var stats = ReadDurationFields(hash, prefix);
        if (stats == null || stats.Count == 0)
            return null;

        var samples = RollupMath.ParseSamples(hash.GetValueOrDefault($"{prefix}:samples"));
        var dto = new JobDurationStatsDto
        {
            JobType = prefix,
            AverageMs = stats.Sum / stats.Count,
            MinMs = stats.Min,
            MaxMs = stats.Max,
            Count = stats.Count,
            P50Ms = RollupMath.ContinuousPercentile(samples, 0.50),
            P95Ms = RollupMath.ContinuousPercentile(samples, 0.95),
            P99Ms = RollupMath.ContinuousPercentile(samples, 0.99)
        };
        return dto;
    }

    private sealed class DurationFields
    {
        public long Count { get; set; }
        public double Sum { get; set; }
        public double Min { get; set; } = double.MaxValue;
        public double Max { get; set; }
        public List<double> Samples { get; set; } = new();
    }

    private static DurationFields? ReadDurationFields(IReadOnlyDictionary<string, string> hash, string prefix)
    {
        var count = ParseLong(hash, $"{prefix}:count");
        if (count == 0)
            return null;

        return new DurationFields
        {
            Count = count,
            Sum = ParseDouble(hash, $"{prefix}:sum"),
            Min = ParseDouble(hash, $"{prefix}:min"),
            Max = ParseDouble(hash, $"{prefix}:max"),
            Samples = RollupMath.ParseSamples(hash.GetValueOrDefault($"{prefix}:samples")).ToList()
        };
    }

    private static DurationFields MergeDurationDelta(DurationFields? prior, RollupAccumulator.DurationDelta delta)
    {
        prior ??= new DurationFields();
        var samples = prior.Samples;
        foreach (var sample in delta.Samples)
            samples = RollupMath.MergeReservoir(samples, sample);

        return new DurationFields
        {
            Count = prior.Count + delta.Count,
            Sum = prior.Sum + delta.Sum,
            Min = prior.Count == 0 ? delta.Min : Math.Min(prior.Min, delta.Min),
            Max = Math.Max(prior.Max, delta.Max),
            Samples = samples
        };
    }

    private static IEnumerable<KeyValuePair<string, string>> WriteDurationFields(string prefix, DurationFields stats)
    {
        yield return new KeyValuePair<string, string>($"{prefix}:count", stats.Count.ToString(CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:sum", stats.Sum.ToString("R", CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:min", stats.Min.ToString("R", CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:max", stats.Max.ToString("R", CultureInfo.InvariantCulture));
        yield return new KeyValuePair<string, string>($"{prefix}:samples", RollupMath.PackSamples(stats.Samples));
    }

    private static bool TryParseScheduleField(string field, out int day, out int hour)
    {
        day = 0;
        hour = 0;
        var sep = field.IndexOf(':');
        if (sep <= 0)
            return false;

        return int.TryParse(field[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out day)
               && int.TryParse(field[(sep + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour);
    }

    private static long ParseLong(IReadOnlyDictionary<string, string> hash, string key)
    {
        if (hash != null && hash.TryGetValue(key, out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return v;
        return 0;
    }

    private static double ParseDouble(IReadOnlyDictionary<string, string> hash, string key)
    {
        if (hash != null && hash.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return 0d;
    }

    private static long ParseTicks(IReadOnlyDictionary<string, string> state, string field)
        => state.TryGetValue(field, out var raw)
           && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? ticks
            : 0L;

    private static Dictionary<string, string>? SafeReadHashStatic(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllEntriesFromHash(key);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, string>? SafeReadHash(IStorageConnection connection, string key)
        => SafeReadHashStatic(connection, key);

    private HashSet<string> SafeReadSet(IStorageConnection connection, string key)
    {
        try
        {
            return connection.GetAllItemsFromSet(key) ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read rollup set {Key}.", key);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
