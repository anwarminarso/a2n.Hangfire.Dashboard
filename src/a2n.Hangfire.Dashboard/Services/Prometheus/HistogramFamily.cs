#nullable enable
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// A Prometheus histogram metric family. Carries the bucket bounds and cumulative bucket
/// counts; the <c>_bucket</c>, <c>_sum</c>, and <c>_count</c> series are derived at format
/// time (Req 6.7).
/// </summary>
/// <param name="Name">The histogram family name (e.g. <c>hangfire_job_duration_seconds</c>).</param>
/// <param name="Help">The human-readable help text for the <c># HELP</c> line.</param>
/// <param name="BucketBoundsSeconds">The upper bounds (<c>le</c>) of each bucket, in seconds.</param>
/// <param name="BucketCounts">The cumulative observation counts per bucket.</param>
/// <param name="Sum">The sum of all observed values.</param>
/// <param name="Count">The total number of observations.</param>
public sealed record HistogramFamily(
    string Name,
    string Help,
    IReadOnlyList<double> BucketBoundsSeconds,
    IReadOnlyList<long> BucketCounts,
    double Sum,
    long Count);
