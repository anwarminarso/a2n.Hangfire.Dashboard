using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Security;

/// <summary>
/// Evaluates <see cref="DashboardPermissions"/> for the HTTP request that renders the dashboard.
/// </summary>
internal static class DashboardPermissionEvaluator
{
    internal const string LoggerCategory = "a2n.Hangfire.Dashboard.Permissions";

    /// <summary>
    /// Computes the permissions for <paramref name="httpContext"/>. Never throws: when the
    /// permissions can't be determined the result is <see cref="DashboardPermissions.FailedClosed"/>
    /// and a warning is logged.
    /// </summary>
    /// <remarks>
    /// <see cref="DashboardUIOptions.IsReadOnly"/> wins over everything else. Otherwise, when the
    /// options were created from Hangfire's <c>DashboardOptions</c>, its <c>IsReadOnlyFunc</c> is
    /// called once with a <c>DashboardContext</c> built from the request and the same
    /// <c>DashboardOptions</c> instance the host configured. Hangfire's default function returns
    /// false, so an unconfigured function leaves the dashboard writable.
    /// </remarks>
    public static DashboardPermissions Evaluate(HttpContext httpContext, DashboardUIOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.IsReadOnly)
            return DashboardPermissions.ReadOnlyBecause(ReadOnlyReason.Option);

        var hangfireOptions = options.HangfireOptions;
        var isReadOnlyFunc = hangfireOptions?.IsReadOnlyFunc;
        if (isReadOnlyFunc is null)
            return DashboardPermissions.Writable;

        if (httpContext is null)
        {
            logger?.LogWarning(
                "The dashboard is read-only for this page load: DashboardOptions.IsReadOnlyFunc is set but no HTTP request was available to evaluate it.");
            return DashboardPermissions.FailedClosed;
        }

        try
        {
            var context = HangfireDashboardContextFactory.Create(httpContext, hangfireOptions);
            return isReadOnlyFunc(context)
                ? DashboardPermissions.ReadOnlyBecause(ReadOnlyReason.IsReadOnlyFunc)
                : DashboardPermissions.Writable;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "The dashboard is read-only for this page load: DashboardOptions.IsReadOnlyFunc threw an exception.");
            return DashboardPermissions.FailedClosed;
        }
    }
}
