using System.Text.Json;
using a2n.Hangfire.Dashboard.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Middleware;

/// <summary>
/// Serves the dashboard health endpoint at <c>/healthz</c>, <c>/healthz/ready</c>, and
/// <c>/healthz/full</c> within the dashboard's branched pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Returns a JSON document conforming to the <see cref="HealthReport"/> shape. HTTP status codes
/// follow the K8s health-probe convention:
/// </para>
/// <list type="bullet">
///   <item><description>HTTP 200 — overall status is <see cref="HealthStatus.Healthy"/> or <see cref="HealthStatus.Degraded"/></description></item>
///   <item><description>HTTP 503 — overall status is <see cref="HealthStatus.Unhealthy"/></description></item>
/// </list>
/// <para>
/// Authorization is governed by <see cref="DashboardUIOptions.HealthCheckAuthorizationMode"/>.
/// </para>
/// </remarks>
internal static class HealthCheckEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Returns true and writes a response if the request matched a health endpoint;
    /// otherwise returns false so the caller can continue processing.
    /// </summary>
    public static async Task<bool> TryHandleAsync(HttpContext context, DashboardUIOptions options)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Match exact paths to avoid catching unrelated routes.
        bool isLive = string.Equals(path, "/healthz", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(path, "/healthz/", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(path, "/healthz/live", StringComparison.OrdinalIgnoreCase);
        bool isReady = string.Equals(path, "/healthz/ready", StringComparison.OrdinalIgnoreCase);
        bool isFull = string.Equals(path, "/healthz/full", StringComparison.OrdinalIgnoreCase);

        if (!isLive && !isReady && !isFull)
            return false;

        // Authorization gate. Default for the health endpoint: AllowAnonymous (so K8s probes work
        // out of the box without configuring auth on the prober). Hosts that need auth can set
        // HealthCheckAuthorizationMode = RequireDashboardAuth.
        bool dashboardAuthorized;
        if (options.HealthCheckAuthorizationMode == HealthCheckAuthorization.RequireDashboardAuth)
        {
            dashboardAuthorized = await IsDashboardAuthorizedAsync(context, options);
            if (!dashboardAuthorized)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return true;
            }
        }
        else
        {
            if (options.HealthCheckAuthorizationMode == HealthCheckAuthorization.LocalOnly && !IsLocalRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return true;
            }

            // Reaching the endpoint doesn't make the caller a dashboard user: the report's details
            // (descriptions, queue and server names, job ids) are only for those who pass the
            // dashboard's own authorization.
            dashboardAuthorized = await IsDashboardAuthorizedAsync(context, options);
        }

        var service = context.RequestServices.GetService<HealthReportCache>();
        if (service is null)
        {
            // Should not happen — the cache is registered by AddHangfireDashboardUI. Fail loud.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("HealthReportCache not registered.");
            return true;
        }

        HealthReport report;
        try
        {
            // The cache computes on a background thread (all probes touch storage synchronously)
            // and single-flights concurrent requests, preventing the request thread from blocking
            // the SignalR/Blazor circuit pool and avoiding redundant storage round-trips.
            var mode = isLive ? HealthReportCache.Mode.Live
                     : isReady ? HealthReportCache.Mode.Ready
                     : HealthReportCache.Mode.Full;
            report = await service.GetAsync(mode, ct: context.RequestAborted);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger("a2n.Hangfire.Dashboard.HealthCheck");
            logger?.LogError(ex, "Health check pipeline failed");

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json; charset=utf-8";
            // The exception message never leaves the server (it is in the log above); a dashboard
            // user also gets the exception type.
            var payload = dashboardAuthorized
                ? JsonSerializer.Serialize(new { status = nameof(HealthStatus.Unhealthy), error = ex.GetType().FullName }, JsonOptions)
                : JsonSerializer.Serialize(new { status = nameof(HealthStatus.Unhealthy) }, JsonOptions);
            await context.Response.WriteAsync(payload);
            return true;
        }

        var statusCode = report.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        // Disable caching — health responses must always be fresh.
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";

        if (dashboardAuthorized)
            await JsonSerializer.SerializeAsync(context.Response.Body, report, JsonOptions, context.RequestAborted);
        else
            await JsonSerializer.SerializeAsync(context.Response.Body, Redact(report), JsonOptions, context.RequestAborted);
        return true;
    }

    /// <summary>
    /// What a caller who isn't a dashboard user sees: the overall status and each check's name and
    /// status. No descriptions, data, version or timings.
    /// </summary>
    internal static object Redact(HealthReport report) => new
    {
        status = report.Status,
        checks = report.Checks.ToDictionary(kv => kv.Key, kv => new { status = kv.Value.Status }),
    };

    /// <summary>
    /// Whether the caller passes the dashboard's authorization filters. A filter that throws counts
    /// as a refusal, so a broken filter can't widen what the endpoint reveals.
    /// </summary>
    private static async Task<bool> IsDashboardAuthorizedAsync(HttpContext context, DashboardUIOptions options)
    {
        try
        {
            return await Security.DashboardAuthorization.IsAuthorizedAsync(context, options, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger("a2n.Hangfire.Dashboard.HealthCheck")
                .LogWarning(ex, "Health check: a dashboard authorization filter threw; the response is redacted.");
            return false;
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
