using Microsoft.AspNetCore.Http;

namespace a2n.Hangfire.Dashboard.Security;

/// <summary>
/// Shared authorization logic for HTTP requests and SignalR hub connections.
/// </summary>
internal static class DashboardAuthorization
{
    public static Task<bool> IsAuthorizedAsync(
        HttpContext context,
        DashboardUIOptions options,
        CancellationToken cancellationToken = default)
    {
        if (context == null)
            return Task.FromResult(false);

        return IsAuthorizedCoreAsync(context, options, cancellationToken);
    }

    private static async Task<bool> IsAuthorizedCoreAsync(
        HttpContext context,
        DashboardUIOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var filter in options.Authorization ?? [])
        {
            if (!filter.Authorize(context))
                return false;
        }

        foreach (var filter in options.AsyncAuthorization ?? [])
        {
            if (!await filter.AuthorizeAsync(context, cancellationToken).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    public static async Task WriteUnauthorizedResponseAsync(HttpContext context, DashboardUIOptions options)
    {
        if (context.User?.Identity?.IsAuthenticated != true
            && !string.IsNullOrEmpty(options?.LoginPath))
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            var loginUrl = options.LoginPath + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
            context.Response.Redirect(loginUrl);
            return;
        }

        if (context.User?.Identity?.IsAuthenticated == true)
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        else
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}
