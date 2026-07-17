using System.Linq;
using a2n.Hangfire.Dashboard.Services.Export;
using a2n.Hangfire.Dashboard.Services.Prometheus;
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

    /// <summary>
    /// Opt-in convenience that enables the Prometheus <c>/metrics</c> exposition endpoint on the
    /// dashboard options registered by <c>AddHangfireDashboardUI</c>. Sets
    /// <see cref="Services.Prometheus.PrometheusOptions.Enabled"/> to <c>true</c> and lets the caller
    /// tune the remaining options (path, authorization mode, scraper filters, histogram buckets).
    /// Mirrors the other opt-in builder methods such as <c>UseRollupMetrics</c> (Req 8.1, 15.3, 16.2).
    /// </summary>
    /// <param name="configure">
    /// Optional callback to further configure the <see cref="Services.Prometheus.PrometheusOptions"/>
    /// (e.g. change the path or authorization mode). The endpoint is enabled regardless of whether a
    /// callback is supplied.
    /// </param>
    /// <returns>The builder for chaining.</returns>
    public DashboardStorageOptionsBuilder EnablePrometheusMetrics(Action<PrometheusOptions> configure = null)
    {
        // Resolve the DashboardUIOptions singleton registered earlier in AddHangfireDashboardUI so
        // the opt-in flows into the same options instance the branched pipeline reads.
        var options = Services
            .FirstOrDefault(d => d.ServiceType == typeof(DashboardUIOptions))?
            .ImplementationInstance as DashboardUIOptions;

        if (options != null)
        {
            options.Prometheus ??= new PrometheusOptions();
            options.Prometheus.Enabled = true;
            configure?.Invoke(options.Prometheus);
        }

        return this;
    }

    /// <summary>
    /// Opt-in convenience that enables the CSV / JSON job export endpoint on the dashboard options
    /// registered by <c>AddHangfireDashboardUI</c>. Sets
    /// <see cref="Services.Export.ExportOptions.Enabled"/> to <c>true</c> and lets the caller tune the
    /// remaining options (path, max records). Mirrors the other opt-in builder methods such as
    /// <c>EnablePrometheusMetrics</c> (Req 15.3, 16.2).
    /// </summary>
    /// <param name="configure">
    /// Optional callback to further configure the <see cref="Services.Export.ExportOptions"/>
    /// (e.g. change the path or record cap). The endpoint is enabled regardless of whether a callback
    /// is supplied.
    /// </param>
    /// <returns>The builder for chaining.</returns>
    public DashboardStorageOptionsBuilder EnableJobExport(Action<ExportOptions> configure = null)
    {
        // Resolve the DashboardUIOptions singleton registered earlier in AddHangfireDashboardUI so
        // the opt-in flows into the same options instance the branched pipeline reads.
        var options = Services
            .FirstOrDefault(d => d.ServiceType == typeof(DashboardUIOptions))?
            .ImplementationInstance as DashboardUIOptions;

        if (options != null)
        {
            options.Export ??= new ExportOptions();
            options.Export.Enabled = true;
            configure?.Invoke(options.Export);
        }

        return this;
    }
}
