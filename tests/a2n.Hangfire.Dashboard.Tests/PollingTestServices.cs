using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Registers what the dashboard's pages need to read storage and refresh themselves (issue #64),
/// for bunit tests that build their own service collection instead of calling
/// <c>AddHangfireDashboardUI</c>. Requires a <see cref="DashboardUIOptions"/> registration.
/// </summary>
internal static class PollingTestServices
{
    public static IServiceCollection AddDashboardPolling(this IServiceCollection services)
    {
        services.TryAddSingleton<DashboardStorageGate>();
        services.TryAddScoped<DashboardCircuitState>();
        services.TryAddScoped(DashboardPolling.Create);

        // Processing and Queues read the server list from the metrics feed, and the sidebar caches
        // the tag count; both are shared across tabs in a real host.
        services.AddMemoryCache();
        services.TryAddSingleton<MetricsQueryCache>();
        services.TryAddSingleton<DashboardSubscriptionTracker>();
        services.TryAddSingleton<DashboardMetricsFeed>();
        return services;
    }
}
