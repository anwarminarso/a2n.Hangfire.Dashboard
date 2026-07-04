using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Redis;
using a2n.Hangfire.Dashboard.Rollup;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace a2n.Hangfire.Dashboard.Rollup.Tests;

public class RollupMetricsIntegrationTests
{
    [Fact]
    public async Task Rollup_provider_records_recurring_schedule_buckets()
    {
        var storage = new InMemoryStorage();
        JobStorage.Current = storage;
        var store = new MetricsRollupStore();
        var provider = new RollupMetricsProvider(storage);

        using (var connection = storage.GetConnection())
        {
            var accumulator = new RollupAccumulator();
            var executedAt = DateTime.UtcNow.AddHours(-1);
            accumulator.Record(new ProcessedExecution
            {
                JobId = "job-1",
                ExecutedAtUtc = executedAt,
                Succeeded = true,
                JobType = "TestJob.Run",
                Queue = "default",
                RecurringJobId = "recurring-1",
                DurationMs = 1500,
                JobName = "TestJob.Run"
            });

            store.Commit(connection, executedAt.Ticks, executedAt.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(executedAt.Ticks));
        }

        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow.AddHours(1);
        var buckets = await provider.GetRecurringScheduleBucketsAsync(from, to, CancellationToken.None);

        Assert.NotEmpty(buckets);
        Assert.Contains(buckets, b => b.Queue == "default" && b.FireCount > 0);
    }

    [Fact]
    public void UseRollupMetrics_registers_provider_and_collector()
    {
        var services = new ServiceCollection();
        services.AddSingleton<JobStorage>(_ => new InMemoryStorage());
        var builder = new a2n.Hangfire.Dashboard.DashboardStorageOptionsBuilder(services);
        builder.UseRollupMetrics();

        Assert.True(builder.HasMetricsProvider);
        Assert.True(builder.UsesRollupMetrics);
        Assert.Contains(services, d => d.ServiceType == typeof(IStorageMetricsProvider));
        Assert.Contains(services, d => d.ImplementationType == typeof(ExecutionRollupCollector));
    }

    [Fact]
    public void UseRedisStorage_delegates_to_rollup_metrics()
    {
        var services = new ServiceCollection();
        services.AddSingleton<JobStorage>(_ => new InMemoryStorage());
        var builder = new a2n.Hangfire.Dashboard.DashboardStorageOptionsBuilder(services);
        builder.UseRedisStorage();

        Assert.True(builder.HasMetricsProvider);
        Assert.True(builder.UsesRollupMetrics);
    }
}
