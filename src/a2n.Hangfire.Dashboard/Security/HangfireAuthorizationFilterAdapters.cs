using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard.Security;

/// <summary>
/// Adapts Hangfire's <see cref="Hangfire.Dashboard.IDashboardAuthorizationFilter"/> to the dashboard UI HTTP context model.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilterAdapter : IDashboardAuthorizationFilter
{
    private readonly global::Hangfire.Dashboard.IDashboardAuthorizationFilter _inner;

    public HangfireDashboardAuthorizationFilterAdapter(global::Hangfire.Dashboard.IDashboardAuthorizationFilter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public bool Authorize(HttpContext context)
        => _inner.Authorize(HangfireDashboardContextFactory.Create(context));
}

/// <summary>
/// Adapts Hangfire's <see cref="Hangfire.Dashboard.IDashboardAsyncAuthorizationFilter"/> to the dashboard UI HTTP context model.
/// </summary>
public sealed class HangfireDashboardAsyncAuthorizationFilterAdapter : IDashboardAsyncAuthorizationFilter
{
    private readonly global::Hangfire.Dashboard.IDashboardAsyncAuthorizationFilter _inner;

    public HangfireDashboardAsyncAuthorizationFilterAdapter(global::Hangfire.Dashboard.IDashboardAsyncAuthorizationFilter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.AuthorizeAsync(HangfireDashboardContextFactory.Create(context));
    }
}

internal static class HangfireDashboardContextFactory
{
    internal static AspNetCoreDashboardContext Create(HttpContext context)
    {
        var storage = context.RequestServices.GetRequiredService<JobStorage>();
        var options = context.RequestServices.GetService<DashboardOptions>() ?? new DashboardOptions();
        return new AspNetCoreDashboardContext(storage, options, context);
    }
}
