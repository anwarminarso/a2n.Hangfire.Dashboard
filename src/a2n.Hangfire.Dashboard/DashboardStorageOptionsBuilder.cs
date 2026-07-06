using Microsoft.Extensions.DependencyInjection;

namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Builder for configuring storage adapters during DI registration.
/// Passed to the AddHangfireDashboardUI overload that accepts configuration.
/// </summary>
public class DashboardStorageOptionsBuilder
{
    /// <summary>
    /// The service collection for registering storage adapter services.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Indicates whether a custom IStorageQueryProvider has been registered.
    /// When false, GenericQueryProvider will be used as fallback.
    /// </summary>
    public bool HasQueryProvider { get; set; }

    /// <summary>
    /// Indicates whether an IStorageMetricsProvider has been registered.
    /// When true, analytics features and AnalyticsBroadcastService will be enabled.
    /// </summary>
    public bool HasMetricsProvider { get; set; }

    /// <summary>
    /// Indicates whether rollup-based metrics (non-SQL storages) are enabled.
    /// When true, <c>ExecutionRollupCollector</c> is registered instead of
    /// <see cref="Services.DemandRollupService"/> for demand rollup maintenance.
    /// </summary>
    public bool UsesRollupMetrics { get; set; }

    public DashboardStorageOptionsBuilder(IServiceCollection services)
    {
        Services = services;
    }
}
