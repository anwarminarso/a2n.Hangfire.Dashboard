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

/// <summary>
/// Regression coverage for issues #26 and #27: the rollup duration/latency hashes store every value
/// under a <c>{prefix}:field</c> key, so reading them back must strip the field suffix instead of
/// looking for suffix-free keys. The old reader found none, which starved the heatmap's p95 estimate
/// (it always fell back to the 1-minute default) and left the performance panels empty.
/// </summary>
public class RollupDurationReadbackTests
{
    [Fact]
    public async Task Duration_latency_and_state_timings_round_trip_through_the_rollup_hashes()
    {
        var storage = new InMemoryStorage();
        var store = new MetricsRollupStore();
        var provider = new RollupMetricsProvider(storage);
        var executedAt = DateTime.UtcNow.AddMinutes(-30);

        using (var connection = storage.GetConnection())
        {
            var accumulator = new RollupAccumulator();
            for (var i = 0; i < 5; i++)
            {
                accumulator.Record(new ProcessedExecution
                {
                    JobId = $"job-{i}",
                    ExecutedAtUtc = executedAt,
                    Succeeded = true,
                    JobType = "ForwardChinaJob.ForwardChinaAsync",
                    Queue = "default",
                    DurationMs = 300_000,
                    LatencyMs = 2_000,
                    JobName = "ForwardChinaJob.ForwardChinaAsync"
                });
            }

            store.Commit(connection, executedAt.Ticks, executedAt.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(executedAt.Ticks));
        }

        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow.AddHours(1);

        var durations = await provider.GetJobDurationStatsAsync(from, to, CancellationToken.None);
        var stats = Assert.Single(durations);
        Assert.Equal("ForwardChinaJob.ForwardChinaAsync", stats.JobType);
        Assert.Equal(5, stats.Count);
        Assert.Equal(300_000d, stats.AverageMs);
        // The p95 is what the heatmap turns into an estimated duration — it must not be zero.
        Assert.True(stats.P95Ms > 0);

        var latency = await provider.GetQueueLatencyStatsAsync(from, to, CancellationToken.None);
        var queue = Assert.Single(latency);
        Assert.Equal("default", queue.QueueName);
        Assert.Equal(2_000d, queue.AverageMs);

        var timings = await provider.GetAverageStateTimingsAsync(from, to, CancellationToken.None);
        Assert.Equal(2_000d, timings.AvgEnqueuedMs);
        Assert.Equal(300_000d, timings.AvgProcessingMs);
    }

    [Fact]
    public async Task Queue_throughput_survives_queue_names_containing_separators()
    {
        var storage = new InMemoryStorage();
        var store = new MetricsRollupStore();
        var provider = new RollupMetricsProvider(storage);
        var executedAt = DateTime.UtcNow.AddMinutes(-20);

        using (var connection = storage.GetConnection())
        {
            var accumulator = new RollupAccumulator();
            accumulator.Record(new ProcessedExecution
            {
                JobId = "job-1",
                ExecutedAtUtc = executedAt,
                Succeeded = true,
                JobType = "Tenant.Run",
                Queue = "tenant:alpha",
                DurationMs = 500,
                JobName = "Tenant.Run"
            });

            store.Commit(connection, executedAt.Ticks, executedAt.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(executedAt.Ticks));
        }

        var throughput = await provider.GetQueueThroughputAsync(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(1),
            MetricsInterval.OneHour, CancellationToken.None);

        var point = Assert.Single(throughput);
        Assert.Equal("tenant:alpha", point.QueueName);
        Assert.Equal(1, point.SucceededCount);
    }

    [Fact]
    public async Task Job_types_containing_separators_are_read_back_intact()
    {
        var storage = new InMemoryStorage();
        var store = new MetricsRollupStore();
        var provider = new RollupMetricsProvider(storage);
        var executedAt = DateTime.UtcNow.AddMinutes(-10);

        using (var connection = storage.GetConnection())
        {
            var accumulator = new RollupAccumulator();
            accumulator.Record(new ProcessedExecution
            {
                JobId = "job-a",
                ExecutedAtUtc = executedAt,
                Succeeded = true,
                JobType = "Outer.Inner:Job.Run",
                Queue = "alpha:beta",
                DurationMs = 1_000,
                LatencyMs = 500,
                JobName = "Outer.Inner:Job.Run"
            });

            store.Commit(connection, executedAt.Ticks, executedAt.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(executedAt.Ticks));
        }

        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow.AddHours(1);

        var durations = await provider.GetJobDurationStatsAsync(from, to, CancellationToken.None);
        Assert.Equal("Outer.Inner:Job.Run", Assert.Single(durations).JobType);

        var latency = await provider.GetQueueLatencyStatsAsync(from, to, CancellationToken.None);
        Assert.Equal("alpha:beta", Assert.Single(latency).QueueName);
    }
}

/// <summary>
/// Regression coverage for issue #25: recurring execution history is served from the rollup ring, so
/// the health page no longer needs to page the succeeded/failed lists once per recurring job.
/// </summary>
public class RollupRecurringHistoryTests
{
    [Fact]
    public async Task Recurring_history_and_last_results_come_from_the_rollup_ring()
    {
        var storage = new InMemoryStorage();
        var store = new MetricsRollupStore();
        var provider = new RollupMetricsProvider(storage);
        var baseTime = DateTime.UtcNow.AddHours(-6);

        using (var connection = storage.GetConnection())
        {
            var accumulator = new RollupAccumulator();
            for (var i = 0; i < 6; i++)
            {
                accumulator.Record(new ProcessedExecution
                {
                    JobId = $"job-{i}",
                    ExecutedAtUtc = baseTime.AddMinutes(i * 10),
                    Succeeded = i != 4,
                    JobType = "ForwardChinaJob.ForwardChinaAsync",
                    Queue = "default",
                    RecurringJobId = "ForwardChinaAsync",
                    DurationMs = 4_000,
                    JobName = "ForwardChinaJob.ForwardChinaAsync"
                });
            }

            store.Commit(connection, baseTime.Ticks, baseTime.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(baseTime.Ticks));
        }

        var executions = await provider.GetRecurringJobExecutionsAsync("ForwardChinaAsync", 20, CancellationToken.None);

        Assert.Equal(6, executions.Count);
        // Newest first.
        Assert.True(executions[0].ExecutedAt > executions[^1].ExecutedAt);
        Assert.Equal(5, executions.Count(e => e.Succeeded));
        Assert.All(executions.Where(e => e.Succeeded), e => Assert.Equal(4_000d, e.DurationMs));

        var unknown = await provider.GetRecurringJobExecutionsAsync("does-not-exist", 20, CancellationToken.None);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task Health_view_reports_last_results_and_average_duration()
    {
        var storage = new InMemoryStorage();
        var store = new MetricsRollupStore();
        var provider = new RollupMetricsProvider(storage);
        var baseTime = DateTime.UtcNow.AddHours(-2);

        using (var connection = storage.GetConnection())
        {
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.AddToSet("recurring-jobs", "ForwardChinaAsync");
                transaction.SetRangeInHash("recurring-job:ForwardChinaAsync", new[]
                {
                    new KeyValuePair<string, string>("Cron", "*/5 * * * *"),
                });
                transaction.Commit();
            }

            var accumulator = new RollupAccumulator();
            accumulator.Record(new ProcessedExecution
            {
                JobId = "job-ok",
                ExecutedAtUtc = baseTime,
                Succeeded = true,
                JobType = "ForwardChinaJob.ForwardChinaAsync",
                Queue = "default",
                RecurringJobId = "ForwardChinaAsync",
                DurationMs = 2_000,
                JobName = "ForwardChinaJob.ForwardChinaAsync"
            });
            accumulator.Record(new ProcessedExecution
            {
                JobId = "job-bad",
                ExecutedAtUtc = baseTime.AddMinutes(5),
                Succeeded = false,
                JobType = "ForwardChinaJob.ForwardChinaAsync",
                Queue = "default",
                RecurringJobId = "ForwardChinaAsync",
                JobName = "ForwardChinaJob.ForwardChinaAsync"
            });

            store.Commit(connection, baseTime.Ticks, baseTime.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(baseTime.Ticks));
        }

        var health = await provider.GetRecurringJobHealthAsync(CancellationToken.None);
        var job = Assert.Single(health);

        Assert.Equal("ForwardChinaAsync", job.JobId);
        // Newest first: the failure precedes the success.
        Assert.Equal(new[] { false, true }, job.LastExecutionResults);
        // Only successful executions carry a duration, so the failure must not drag the average to 1s.
        Assert.Equal(2_000d, job.AverageDurationMs);
    }

    [Fact]
    public async Task Batch_history_returns_one_entry_per_requested_job()
    {
        var storage = new InMemoryStorage();
        var store = new MetricsRollupStore();
        IStorageMetricsProvider provider = new RollupMetricsProvider(storage);
        var baseTime = DateTime.UtcNow.AddHours(-3);

        using (var connection = storage.GetConnection())
        {
            var accumulator = new RollupAccumulator();
            foreach (var id in new[] { "alpha", "beta", "gamma" })
            {
                accumulator.Record(new ProcessedExecution
                {
                    JobId = $"{id}-1",
                    ExecutedAtUtc = baseTime,
                    Succeeded = true,
                    JobType = $"{id}.Run",
                    Queue = "default",
                    RecurringJobId = id,
                    DurationMs = 1_000,
                    JobName = $"{id}.Run"
                });
            }

            store.Commit(connection, baseTime.Ticks, baseTime.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(baseTime.Ticks));
        }

        // "delta" has no history and must be absent rather than present-but-empty.
        var batch = await provider.GetRecurringJobExecutionsBatchAsync(
            new[] { "alpha", "gamma", "delta" }, 20, CancellationToken.None);

        Assert.Equal(2, batch.Count);
        Assert.True(batch.ContainsKey("alpha"));
        Assert.True(batch.ContainsKey("gamma"));
        Assert.False(batch.ContainsKey("delta"));
        // "beta" has history but was not requested.
        Assert.False(batch.ContainsKey("beta"));
        Assert.Equal(1_000d, Assert.Single(batch["alpha"]).DurationMs);

        Assert.Empty(await provider.GetRecurringJobExecutionsBatchAsync(
            Array.Empty<string>(), 20, CancellationToken.None));
    }

    [Fact]
    public void Recurring_ring_is_bounded_and_merges_across_polls()
    {
        var storage = new InMemoryStorage();
        var store = new MetricsRollupStore();
        var baseTime = DateTime.UtcNow.AddDays(-1);

        // Two separate polls, 15 executions each — the ring keeps the 20 newest.
        for (var poll = 0; poll < 2; poll++)
        {
            using var connection = storage.GetConnection();
            var accumulator = new RollupAccumulator();
            for (var i = 0; i < 15; i++)
            {
                accumulator.Record(new ProcessedExecution
                {
                    JobId = $"poll{poll}-job{i}",
                    ExecutedAtUtc = baseTime.AddHours(poll).AddMinutes(i),
                    Succeeded = true,
                    JobType = "Chatty.Run",
                    Queue = "default",
                    RecurringJobId = "chatty",
                    DurationMs = 100,
                    JobName = "Chatty.Run"
                });
            }

            store.Commit(connection, baseTime.Ticks, baseTime.Ticks, accumulator,
                Internal.RollupTime.WeekIndex(baseTime.Ticks));
        }

        using (var connection = storage.GetConnection())
        {
            var history = store.ReadRecurringExecutions(connection, "chatty", 100);

            Assert.Equal(20, history.Count);
            // The newest entry belongs to the second poll.
            Assert.StartsWith("poll1-", history[0].JobId);
            // Ids stay unique — merging must not duplicate entries already persisted.
            Assert.Equal(20, history.Select(h => h.JobId).Distinct().Count());
        }
    }
}
