using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for registering the PostgreSQL storage adapter
/// with the Hangfire Dashboard.
/// </summary>
public static class PostgreSqlStorageExtensions
{
    /// <summary>
    /// Registers the PostgreSQL storage adapter for optimized queries and analytics.
    /// </summary>
    /// <param name="builder">The storage options builder</param>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="schema">Schema name (default: "hangfire")</param>
    /// <returns>The builder for chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when connectionString is null or empty</exception>
    public static DashboardStorageOptionsBuilder UsePostgreSqlStorage(
        this DashboardStorageOptionsBuilder builder,
        string connectionString,
        string schema = "hangfire")
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentNullException(nameof(connectionString));

        var provider = new PostgreSqlQueryProvider(connectionString, schema);
        var metricsProvider = new PostgreSqlMetricsProvider(connectionString, schema);

        builder.Services.AddSingleton<IStorageQueryProvider>(provider);
        builder.Services.AddSingleton<IStorageMetricsProvider>(metricsProvider);
        builder.HasQueryProvider = true;
        builder.HasMetricsProvider = true;

        return builder;
    }
}
