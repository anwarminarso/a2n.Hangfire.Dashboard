using System.Globalization;

namespace a2n.Hangfire.Dashboard.Rollup.Internal;

/// <summary>
/// Packs and unpacks the bounded per-recurring-job execution ring stored in a single Hangfire hash
/// field. Entries are separated by <c>;</c> and their parts by <c>|</c>:
/// <c>{utcTicks}|{1|0 succeeded}|{durationMs}|{jobId}</c>. Both separators are stripped from the job
/// id so a malformed value can never corrupt the surrounding entries.
/// </summary>
internal static class RecurringExecutionCodec
{
    private const char EntrySeparator = ';';
    private const char PartSeparator = '|';

    public static string Pack(IEnumerable<RollupAccumulator.RecurringExecutionEntry> entries)
    {
        if (entries == null)
            return string.Empty;

        var packed = entries
            .Where(e => e != null)
            .Select(e => string.Join(PartSeparator,
                RollupTime.AsUtcTicks(e.ExecutedAtUtc).ToString(CultureInfo.InvariantCulture),
                e.Succeeded ? "1" : "0",
                e.DurationMs.ToString("R", CultureInfo.InvariantCulture),
                Sanitize(e.JobId)));

        return string.Join(EntrySeparator, packed);
    }

    public static List<RollupAccumulator.RecurringExecutionEntry> Parse(string packed)
    {
        var results = new List<RollupAccumulator.RecurringExecutionEntry>();
        if (string.IsNullOrEmpty(packed))
            return results;

        foreach (var raw in packed.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(PartSeparator);
            if (parts.Length < 4)
                continue;

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                continue;

            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                continue;

            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration);

            results.Add(new RollupAccumulator.RecurringExecutionEntry
            {
                ExecutedAtUtc = new DateTime(ticks, DateTimeKind.Utc),
                Succeeded = parts[1] == "1",
                DurationMs = duration,
                JobId = parts[3]
            });
        }

        return results;
    }

    private static string Sanitize(string jobId)
        => string.IsNullOrEmpty(jobId)
            ? string.Empty
            : jobId.Replace(EntrySeparator, '_').Replace(PartSeparator, '_');
}
