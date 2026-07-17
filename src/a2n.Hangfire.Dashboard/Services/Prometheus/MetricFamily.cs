#nullable enable
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// A Prometheus metric family: a named group of samples sharing a metric name, type, and
/// help text. Rendered with one <c># HELP</c> and one <c># TYPE</c> line per family (Req 5.3).
/// </summary>
/// <param name="Name">The metric family name (e.g. <c>hangfire_jobs_total</c>).</param>
/// <param name="Type">The metric type declared on the <c># TYPE</c> line.</param>
/// <param name="Help">The human-readable help text for the <c># HELP</c> line.</param>
/// <param name="Samples">The samples belonging to this family.</param>
public sealed record MetricFamily(
    string Name,
    MetricType Type,
    string Help,
    IReadOnlyList<MetricSample> Samples);
