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
    private readonly DashboardOptions _options;

    public HangfireDashboardAuthorizationFilterAdapter(global::Hangfire.Dashboard.IDashboardAuthorizationFilter inner)
        : this(inner, null)
    {
    }

    /// <param name="inner">The Hangfire filter to run.</param>
    /// <param name="options">
    /// The <see cref="DashboardOptions"/> exposed to the filter as <c>DashboardContext.Options</c>.
    /// When null, the instance registered in DI is used, or a new default instance.
    /// </param>
    public HangfireDashboardAuthorizationFilterAdapter(
        global::Hangfire.Dashboard.IDashboardAuthorizationFilter inner,
        DashboardOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options;
    }

    public bool Authorize(HttpContext context)
        => _inner.Authorize(HangfireDashboardContextFactory.Create(context, _options));
}

/// <summary>
/// Adapts Hangfire's <see cref="Hangfire.Dashboard.IDashboardAsyncAuthorizationFilter"/> to the dashboard UI HTTP context model.
/// </summary>
public sealed class HangfireDashboardAsyncAuthorizationFilterAdapter : IDashboardAsyncAuthorizationFilter
{
    private readonly global::Hangfire.Dashboard.IDashboardAsyncAuthorizationFilter _inner;
    private readonly DashboardOptions _options;

    public HangfireDashboardAsyncAuthorizationFilterAdapter(global::Hangfire.Dashboard.IDashboardAsyncAuthorizationFilter inner)
        : this(inner, null)
    {
    }

    /// <param name="inner">The Hangfire filter to run.</param>
    /// <param name="options">
    /// The <see cref="DashboardOptions"/> exposed to the filter as <c>DashboardContext.Options</c>.
    /// When null, the instance registered in DI is used, or a new default instance.
    /// </param>
    public HangfireDashboardAsyncAuthorizationFilterAdapter(
        global::Hangfire.Dashboard.IDashboardAsyncAuthorizationFilter inner,
        DashboardOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options;
    }

    public Task<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.AuthorizeAsync(HangfireDashboardContextFactory.Create(context, _options));
    }
}

internal static class HangfireDashboardContextFactory
{
    /// <summary>
    /// Builds the Hangfire <c>DashboardContext</c> for <paramref name="context"/>. Pass the
    /// <see cref="DashboardOptions"/> the host configured so filters and <c>IsReadOnlyFunc</c> see
    /// that instance; without one, the DI-registered instance or a new default is used.
    /// </summary>
    internal static AspNetCoreDashboardContext Create(HttpContext context, DashboardOptions options = null)
    {
        var storage = context.RequestServices.GetRequiredService<JobStorage>();
        options ??= context.RequestServices.GetService<DashboardOptions>() ?? new DashboardOptions();
        return new AspNetCoreDashboardContext(storage, options, context);
    }
}
