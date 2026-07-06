using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Rollup;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for registering rollup-based metrics on non-SQL Hangfire storages.
/// </summary>
public static class RollupStorageExtensions
{
    /// <summary>
    /// Registers rollup-based <see cref="IStorageMetricsProvider"/> and the unified
    /// <see cref="ExecutionRollupCollector"/> background service.
    /// </summary>
    public static DashboardStorageOptionsBuilder UseRollupMetrics(this DashboardStorageOptionsBuilder builder)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        builder.Services.AddSingleton<IStorageMetricsProvider>(sp =>
        {
            var storage = sp.GetRequiredService<JobStorage>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new RollupMetricsProvider(storage, loggerFactory?.CreateLogger(nameof(RollupMetricsProvider)));
        });

        builder.Services.AddHostedService<ExecutionRollupCollector>();
        builder.HasMetricsProvider = true;
        builder.UsesRollupMetrics = true;
        return builder;
    }
}
