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

        // Blazor circuit and SignalR — pass to endpoint routing without auth
        // (auth is handled at the Blazor component level)
        if (path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // All other requests: apply authorization
        if (!Authorize(context))
        {
            return;
        }

        // Validate antiforgery tokens on POST requests
        if (HttpMethods.IsPost(context.Request.Method))
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

    /// <summary>
    /// Applies all authorization filters from DashboardUIOptions.
    /// Returns true if authorized, false if request was rejected.
    /// </summary>
    private bool Authorize(HttpContext context)
    {
        var filters = _options.Authorization;
        if (filters == null)
        {
            return true;
        }

        foreach (var filter in filters)
        {
            if (!filter.Authorize(context))
            {
                if (context.User?.Identity?.IsAuthenticated == true)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }

                return false;
            }
        }

        return true;
    }
}
