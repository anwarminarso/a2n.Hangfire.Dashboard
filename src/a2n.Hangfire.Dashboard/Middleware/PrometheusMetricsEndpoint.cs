#nullable enable
using a2n.Hangfire.Dashboard.Services.Prometheus;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Middleware;

/// <summary>
/// Serves the Prometheus text-exposition endpoint (default path <c>/metrics</c>, relative to the
/// dashboard's configured <c>Path_Prefix</c>) within the dashboard's branched pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="HealthCheckEndpoint"/>: it is invoked by path match from
/// <see cref="DashboardMiddleware"/> before Blazor routing so it automatically inherits the
/// configured <c>Path_Prefix</c> (Req 5.5, 16.1).
/// </para>
/// <para>
/// Authorization is enforced <em>before</em> any provider is touched, per
/// <see cref="PrometheusOptions.AuthorizationMode"/>. On authorization failure the endpoint
/// responds with HTTP 401 and an empty body — no metric values are emitted (Req 8.3, 17.1, 17.2).
/// </para>
/// <list type="bullet">
///   <item><description><see cref="PrometheusAuthorization.LocalOnly"/> (default) — only local/loopback requests are allowed (Req 8.2).</description></item>
///   <item><description><see cref="PrometheusAuthorization.RequireDashboardAuth"/> — the dashboard authorization pipeline must pass.</description></item>
///   <item><description><see cref="PrometheusAuthorization.Custom"/> — the configured <see cref="PrometheusOptions.ScraperAuthorization"/> filters must all pass; if none are configured, access is denied (Req 8.4).</description></item>
/// </list>
/// </remarks>
internal static class PrometheusMetricsEndpoint
{
    // Prometheus text exposition format 0.0.4 content type (Req 5.2).
    private const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>
    /// Returns true and writes a response if the request matched the metrics endpoint;
    /// otherwise returns false so the caller can continue processing.
    /// </summary>
    public static async Task<bool> TryHandleAsync(HttpContext context, DashboardUIOptions options)
    {
        var prometheus = options.Prometheus;

        // Not enabled → not handled (Req 15.2).
        if (prometheus is null || !prometheus.Enabled)
            return false;

        var configuredPath = string.IsNullOrEmpty(prometheus.Path) ? "/metrics" : prometheus.Path;
        var path = context.Request.Path.Value ?? string.Empty;

        // Exact match (case-insensitive), tolerating a trailing slash.
        if (!IsPathMatch(path, configuredPath))
            return false;

        // Authorization gate — enforced before any provider is touched (Req 8.3, 17.1).
        if (!await IsAuthorizedAsync(context, options))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return true; // 401 with no body — no metric values emitted (Req 8.3, 17.2).
        }

        try
        {
            // Construct the exporter directly from request services — its ctor takes an
            // IServiceProvider and resolves HangfireMonitorService (registered by the dashboard)
            // plus the optional IStorageMetricsProvider.
            var exporter = new PrometheusExporter(context.RequestServices);

            var snapshot = await exporter.CollectAsync(
                prometheus.DurationBucketsSeconds,
                context.RequestAborted).ConfigureAwait(false);

            var payload = new PrometheusTextFormatter().Format(snapshot.Families, snapshot.Histograms);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = ContentType;
            await context.Response.WriteAsync(payload, context.RequestAborted);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
            logger?.CreateLogger("a2n.Hangfire.Dashboard.Prometheus")
                  .LogError(ex, "Prometheus metrics collection failed");

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }

        return true;
    }

    private static bool IsPathMatch(string requestPath, string configuredPath)
    {
        if (string.Equals(requestPath, configuredPath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Tolerate a trailing slash in either direction.
        var trimmedRequest = requestPath.TrimEnd('/');
        var trimmedConfigured = configuredPath.TrimEnd('/');
        return trimmedConfigured.Length > 0
            && string.Equals(trimmedRequest, trimmedConfigured, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsAuthorizedAsync(HttpContext context, DashboardUIOptions options)
    {
        var mode = options.Prometheus.AuthorizationMode;

        switch (mode)
        {
            case PrometheusAuthorization.RequireDashboardAuth:
                return await Security.DashboardAuthorization.IsAuthorizedAsync(
                    context, options, context.RequestAborted).ConfigureAwait(false);

            case PrometheusAuthorization.Custom:
                var filters = options.Prometheus.ScraperAuthorization;
                if (filters is null)
                    return false; // No scraper filter configured → deny (Req 8.4, 17.2).

                var any = false;
                foreach (var filter in filters)
                {
                    if (filter is null)
                        continue;
                    any = true;
                    if (!filter.Authorize(context))
                        return false;
                }

                // If the set was empty (no filters), deny rather than allow anonymous access.
                return any;

            case PrometheusAuthorization.LocalOnly:
            default:
                return IsLocalRequest(context);
        }
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        var conn = context.Connection;
        if (conn.RemoteIpAddress is null) return true;
        if (conn.LocalIpAddress is null) return System.Net.IPAddress.IsLoopback(conn.RemoteIpAddress);
        return conn.RemoteIpAddress.Equals(conn.LocalIpAddress) || System.Net.IPAddress.IsLoopback(conn.RemoteIpAddress);
    }
}
