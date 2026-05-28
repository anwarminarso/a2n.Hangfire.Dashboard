using a2n.Hangfire.Dashboard.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Hubs;

/// <summary>
/// Applies <see cref="DashboardUIOptions.Authorization"/> to <see cref="DashboardHub"/> connections and invocations.
/// </summary>
internal sealed class DashboardHubAuthorizationFilter : IHubFilter
{
    public async ValueTask OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        if (context.Hub is DashboardHub && !await AuthorizeAsync(context.Context.GetHttpContext()))
        {
            context.Context.Abort();
            return;
        }

        await next(context);
    }

    public async ValueTask<object> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object>> next)
    {
        if (invocationContext.Hub is DashboardHub && !await AuthorizeAsync(invocationContext.Context.GetHttpContext()))
            throw new HubException("Unauthorized");

        return await next(invocationContext);
    }

    private static async Task<bool> AuthorizeAsync(HttpContext httpContext)
    {
        if (httpContext == null)
            return false;

        var options = httpContext.RequestServices.GetService<DashboardUIOptions>()
            ?? new DashboardUIOptions();

        return await DashboardAuthorization.IsAuthorizedAsync(httpContext, options, httpContext.RequestAborted);
    }
}
