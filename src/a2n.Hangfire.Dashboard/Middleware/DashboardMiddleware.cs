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
