using a2n.Hangfire.Dashboard.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Middleware;

/// <summary>
/// Custom middleware that handles all dashboard requests within the branched pipeline.
/// Applies authorization, antiforgery validation, and routes requests to the appropriate handler.
/// </summary>
internal class DashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DashboardUIOptions _options;

    public DashboardMiddleware(RequestDelegate next, DashboardUIOptions options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Health endpoint is handled before any auth/antiforgery so K8s probes can reach it.
        // The endpoint applies its own authorization mode based on
        // DashboardUIOptions.HealthCheckAuthorizationMode.
        if (path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            if (await HealthCheckEndpoint.TryHandleAsync(context, _options))
                return;
        }

        // Prometheus metrics endpoint is handled before the generic dashboard authorization block
        // because it applies its OWN authorization mode (Prometheus.AuthorizationMode) — a
        // LocalOnly scraper must not be forced through dashboard-page auth. Running inside the
        // branch means it honors the configured Path_Prefix (Req 5.5, 8.3, 16.1).
        if (_options.Prometheus is { Enabled: true })
        {
            if (await PrometheusMetricsEndpoint.TryHandleAsync(context, _options))
                return;
        }

        // CSV / JSON job export endpoint. Unlike Prometheus (LocalOnly), the export endpoint calls
        // Dashboard_Authorization itself before streaming any record, so it is placed near the
        // Prometheus block (after healthz/Prometheus, before static resources). Running inside the
        // branch means it honors the configured Path_Prefix and remains available in read-only mode
        // (Req 14.1, 14.2, 14.3, 16.1).
        if (_options.Export is { Enabled: true })
        {
            if (await ExportEndpoint.TryHandleAsync(context, _options))
                return;
        }

        // Serve embedded static resources (CSS, JS, fonts, images) — no auth required for assets
        if (path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/_content", StringComparison.OrdinalIgnoreCase))
        {
            await EmbeddedResourceDispatcher.ServeResourceAsync(context, path);
            return;
        }

        // Framework resources (_framework/blazor.web.js) — pass to next (FrameworkScriptMiddleware)
        if (path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Blazor circuit and SignalR require the same authorization as dashboard pages.
        var requiresAuth = !path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase)
            && !path.Equals("/_content", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase);

        if (requiresAuth && !await DashboardAuthorization.IsAuthorizedAsync(context, _options, context.RequestAborted))
        {
            await DashboardAuthorization.WriteUnauthorizedResponseAsync(context, _options);
            return;
        }

        // SignalR/Blazor negotiate POSTs do not carry antiforgery tokens.
        var skipAntiforgery = path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/hubs", StringComparison.OrdinalIgnoreCase);

        // Validate antiforgery tokens on POST requests
        if (!skipAntiforgery && HttpMethods.IsPost(context.Request.Method))
        {
            var antiforgery = context.RequestServices.GetService<IAntiforgery>();
            if (antiforgery != null)
            {
                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
        }

        // Pass to endpoint routing (Blazor page rendering)
        await _next(context);
    }
}
