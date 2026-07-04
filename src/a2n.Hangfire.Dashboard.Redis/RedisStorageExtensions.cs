using a2n.Hangfire.Dashboard;
using Microsoft.AspNetCore.Builder;

namespace a2n.Hangfire.Dashboard.Redis;

/// <summary>
/// Entry point for Redis and other non-SQL Hangfire storages.
/// </summary>
public static class RedisStorageExtensions
{
    /// <summary>
    /// Registers rollup-based analytics for Redis and other non-SQL Hangfire storages.
    /// Does not open a Redis connection — Hangfire job storage must already be configured.
    /// </summary>
    public static DashboardStorageOptionsBuilder UseRedisStorage(this DashboardStorageOptionsBuilder builder)
        => builder.UseRollupMetrics();
}
