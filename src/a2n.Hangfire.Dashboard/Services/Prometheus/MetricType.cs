#nullable enable

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// The Prometheus metric type of a metric family, as declared on the exposition
/// <c># TYPE</c> line (Req 5.3).
/// </summary>
public enum MetricType
{
    /// <summary>A monotonically increasing cumulative counter.</summary>
    Counter,

    /// <summary>A value that can go up and down.</summary>
    Gauge,

    /// <summary>A histogram sampling observations into cumulative buckets.</summary>
    Histogram
}
