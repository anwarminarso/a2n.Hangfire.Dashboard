using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for registering the SQL Server storage adapter
/// with the Hangfire Dashboard.
/// </summary>
public static class SqlServerStorageExtensions
{
    /// <summary>
    /// Registers the SQL Server storage adapter for optimized queries and analytics.
    /// Both IStorageQueryProvider and IStorageMetricsProvider are registered as singletons.
    /// </summary>
    /// <param name="builder">The storage options builder</param>
    /// <param name="connectionString">SQL Server connection string</param>
    /// <param name="schema">Schema name (default: "HangFire")</param>
    /// <returns>The builder for chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when connectionString is null or empty</exception>
    public static DashboardStorageOptionsBuilder UseSqlServerStorage(
        this DashboardStorageOptionsBuilder builder,
        string connectionString,
        string schema = "HangFire")
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        var provider = new SqlServerQueryProvider(connectionString, schema);
        var metricsProvider = new SqlServerMetricsProvider(connectionString, schema);

        builder.Services.AddSingleton<IStorageQueryProvider>(provider);
        builder.Services.AddSingleton<IStorageMetricsProvider>(metricsProvider);
        builder.HasQueryProvider = true;
        builder.HasMetricsProvider = true;

        return builder;
    }
}
