using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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

    private static readonly IDashboardAuthorizationFilter[] LocalOnlyFallback = [new LocalRequestsOnlyAuthorizationFilter()];

    private static async Task<bool> IsAuthorizedCoreAsync(
        HttpContext context,
        DashboardUIOptions options,
        CancellationToken cancellationToken)
    {
        // Every sync filter and every async filter must pass. With no filter at all the dashboard
        // fails closed to local requests: opening it to everyone takes an explicit
        // AllowAllRequestsAuthorizationFilter.
        var syncFilters = options.Authorization ?? [];
        var asyncFilters = options.AsyncAuthorization ?? [];
        if (HasNoFilters(options))
            syncFilters = LocalOnlyFallback;

        foreach (var filter in syncFilters)
        {
            if (!filter.Authorize(context))
                return false;
        }

        foreach (var filter in asyncFilters)
        {
            if (!await filter.AuthorizeAsync(context, cancellationToken).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    /// <summary>Whether both filter lists are null or empty, so the local-only fallback applies.</summary>
    internal static bool HasNoFilters(DashboardUIOptions options)
        => !(options.Authorization?.Any() ?? false) && !(options.AsyncAuthorization?.Any() ?? false);

    /// <summary>Whether at least one filter is configured and every one of them lets everyone in.</summary>
    internal static bool AllowsEveryone(DashboardUIOptions options)
    {
        var filters = (options.Authorization ?? []).Cast<object>()
            .Concat((options.AsyncAuthorization ?? []).Cast<object>())
            .ToList();
        return filters.Count > 0 && filters.All(f => f is AllowAllRequestsAuthorizationFilter);
    }

    /// <summary>
    /// Logs, once when the dashboard is mapped, the authorization setups that deserve a warning:
    /// no filter at all (local-only fallback) and a dashboard open to everyone.
    /// </summary>
    internal static void LogStartupWarnings(DashboardUIOptions options, ILogger logger)
    {
        if (logger is null) return;

        if (HasNoFilters(options))
        {
            logger.LogWarning(
                "DashboardUIOptions.Authorization and AsyncAuthorization are both empty, so the Hangfire dashboard only accepts local requests. " +
                "To allow every request (for example when authentication is enforced upstream), add {Filter} explicitly.",
                nameof(AllowAllRequestsAuthorizationFilter));
        }
        else if (AllowsEveryone(options))
        {
            logger.LogWarning(
                "The Hangfire dashboard is accessible without authentication ({Filter}). Make sure authentication is enforced upstream. " +
                "The health endpoint in RequireDashboardAuth mode is open too.",
                nameof(AllowAllRequestsAuthorizationFilter));
        }
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
