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
        // Apply authorization filters before processing any request
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

        var path = context.Request.Path.Value ?? string.Empty;

        // Route requests based on path pattern
        if (path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/_content", StringComparison.OrdinalIgnoreCase))
        {
            // Serve embedded static resources
            await EmbeddedResourceDispatcher.ServeResourceAsync(context, path);
            return;
        }

        if (path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase))
        {
            // Pass to next middleware for Blazor circuit handler (needs endpoint routing)
            await _next(context);
            return;
        }

        if (path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase))
        {
            // Pass to next middleware for framework scripts (blazor.web.js served by ASP.NET Core)
            await _next(context);
            return;
        }

        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            // Pass to next middleware for SignalR hub (needs endpoint routing)
            await _next(context);
            return;
        }

        // Unknown paths that don't match any known route → pass to next (fallback endpoint handles HTML shell)
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
                // Determine if user is authenticated to choose 401 vs 403
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
