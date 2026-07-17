#nullable enable
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// A single Prometheus metric sample: an ordered set of label key/value pairs and the
/// sample value.
/// </summary>
/// <param name="Labels">The label key/value pairs applied to this sample.</param>
/// <param name="Value">The numeric sample value.</param>
public sealed record MetricSample(
    IReadOnlyList<KeyValuePair<string, string>> Labels,
    double Value);
