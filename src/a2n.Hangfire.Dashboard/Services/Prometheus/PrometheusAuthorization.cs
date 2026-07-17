#nullable enable

namespace a2n.Hangfire.Dashboard.Services.Prometheus;

/// <summary>
/// The authorization mode enforced by the Prometheus metrics endpoint (Req 8.1, 8.2, 8.4).
/// </summary>
public enum PrometheusAuthorization
{
    /// <summary>Restrict access to local requests only (default).</summary>
    LocalOnly,

    /// <summary>Require the request to pass the dashboard's authorization pipeline.</summary>
    RequireDashboardAuth,

    /// <summary>Use a custom scraper authorization filter set configured by the host.</summary>
    Custom
}
