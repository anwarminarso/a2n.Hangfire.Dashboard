using System.Globalization;

namespace a2n.Hangfire.Dashboard.Rollup.Internal;

/// <summary>Percentile and reservoir-sample helpers for rollup metrics.</summary>
internal static class RollupMath
{
    public const int MaxReservoirSamples = 200;

    public static double ContinuousPercentile(double[] values, double p)
    {
        if (values == null || values.Length == 0)
            return 0d;

        if (values.Length == 1)
            return values[0];

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);

        if (lo == hi)
            return sorted[lo];

        var frac = rank - lo;
        return sorted[lo] + (frac * (sorted[hi] - sorted[lo]));
    }

    public static double[] ParseSamples(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return Array.Empty<double>();

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<double>(parts.Length);
        foreach (var part in parts)
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                list.Add(v);
        }

        return list.ToArray();
    }

    public static string PackSamples(IReadOnlyList<double> samples)
    {
        if (samples == null || samples.Count == 0)
            return string.Empty;

        return string.Join(",", samples.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
    }

    public static List<double> MergeReservoir(IReadOnlyList<double> existing, double newValue, int cap = MaxReservoirSamples)
    {
        var list = existing != null ? new List<double>(existing) : new List<double>();
        list.Add(newValue);
        if (list.Count <= cap)
            return list;

        list.Sort();
        return list.Take(cap).ToList();
    }

    public static string PackCountSum(long count, double sum)
        => $"{count.ToString(CultureInfo.InvariantCulture)}|{sum.ToString("R", CultureInfo.InvariantCulture)}";

    public static (long Count, double Sum) ParseCountSum(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return (0, 0d);

        var sep = raw.IndexOf('|');
        if (sep < 0)
        {
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var only)
                ? (only, 0d)
                : (0, 0d);
        }

        long.TryParse(raw[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);
        double.TryParse(raw[(sep + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var sum);
        return (count, sum);
    }
}
