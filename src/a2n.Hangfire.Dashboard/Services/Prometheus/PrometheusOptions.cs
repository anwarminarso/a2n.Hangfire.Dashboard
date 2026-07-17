#nullable enable

using Hangfire.Dashboard;

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// Opt-in configuration for the Prometheus <c>/metrics</c> exposition endpoint served inside the
/// dashboard's branched pipeline (Req 8, 15, 16). Disabled by default; enable either by setting
/// <see cref="Enabled"/> on <see cref="DashboardUIOptions.Prometheus"/> directly, or through the
/// <c>DashboardStorageOptionsBuilder.EnablePrometheusMetrics</c> opt-in convenience.
/// </summary>
public sealed class PrometheusOptions
{
    /// <summary>
    /// Whether the Prometheus metrics endpoint is enabled. Opt-in; default <c>false</c> (Req 15.3).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// The endpoint path, relative to the dashboard's configured <c>Path_Prefix</c>.
    /// Default <c>/metrics</c> (Req 5.5, 16.2).
    /// </summary>
    public string Path { get; set; } = "/metrics";

    /// <summary>
    /// The authorization mode enforced before any metric value is emitted. Defaults to
    /// <see cref="PrometheusAuthorization.LocalOnly"/> so metrics are not exposed to remote callers
    /// unless the host explicitly opts into a weaker mode (Req 8.2).
    /// </summary>
    public PrometheusAuthorization AuthorizationMode { get; set; } = PrometheusAuthorization.LocalOnly;

    /// <summary>
    /// Optional dedicated scraper authorization filter set, used when
    /// <see cref="AuthorizationMode"/> is <see cref="PrometheusAuthorization.Custom"/>. Configuring a
    /// dedicated scraper filter keeps metrics access independent of — and does not weaken — the
    /// dashboard-page authorization filters (Req 8.4).
    /// </summary>
    public IEnumerable<IDashboardAuthorizationFilter>? ScraperAuthorization { get; set; }

    /// <summary>
    /// Optional histogram bucket upper bounds (in seconds) for the
    /// <c>hangfire_job_duration_seconds</c> histogram. When <c>null</c> or empty, the exporter uses
    /// its default bucket bounds. Exposed as <c>double[]</c> so the values flow directly into the
    /// exporter's <see cref="System.Collections.Generic.IReadOnlyList{T}"/> bucket-bounds parameter.
    /// </summary>
    /// <remarks>
    /// Deviation from the design document: the design listed this as <c>string[]?</c>, but the
    /// exporter consumes bucket bounds as <c>IReadOnlyList&lt;double&gt;</c>. Using <c>double[]?</c>
    /// here avoids a parse/round-trip step and lets the configured values be passed straight through.
    /// </remarks>
    public double[]? DurationBucketsSeconds { get; set; }
}
