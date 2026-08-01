using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Covers <see cref="IStorageMetricsProvider.GetRecurringJobExecutionsBatchAsync"/>. The Recurring
/// Health view needs history for every recurring job at once; a provider that does not override the
/// batch method must still work through the default per-job fallback (issue #25).
/// </summary>
public class RecurringExecutionsBatchTests
{
    [Fact]
    public async Task Default_implementation_falls_back_to_one_call_per_job()
    {
        var provider = new SingleOnlyProvider();
        // Default interface members are only reachable through the interface.
        IStorageMetricsProvider asProvider = provider;

        var batch = await asProvider.GetRecurringJobExecutionsBatchAsync(
            new[] { "alpha", "empty", "beta", "alpha" }, 20, CancellationToken.None);

        // "alpha" is requested twice but queried once; "empty" has no history so it is omitted.
        Assert.Equal(new[] { "alpha", "beta" }, batch.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "alpha", "empty", "beta" }, provider.QueriedIds);
        Assert.Equal("alpha-job", Assert.Single(batch["alpha"]).JobId);
    }

    [Fact]
    public async Task Default_implementation_returns_empty_for_no_ids()
    {
        var provider = new SingleOnlyProvider();
        IStorageMetricsProvider asProvider = provider;

        Assert.Empty(await asProvider.GetRecurringJobExecutionsBatchAsync(
            Array.Empty<string>(), 20, CancellationToken.None));
        Assert.Empty(await asProvider.GetRecurringJobExecutionsBatchAsync(
            null, 20, CancellationToken.None));
        Assert.Empty(provider.QueriedIds);
    }

    /// <summary>
    /// A provider that implements only the single-job method, standing in for a third-party adapter
    /// written before the batch method existed.
    /// </summary>
    private sealed class SingleOnlyProvider : IStorageMetricsProvider
    {
        public List<string> QueriedIds { get; } = new();

        public Task<IReadOnlyList<RecurringJobExecutionDto>> GetRecurringJobExecutionsAsync(
            string recurringJobId, int count, CancellationToken ct)
        {
            QueriedIds.Add(recurringJobId);

            var executions = recurringJobId == "empty"
                ? Array.Empty<RecurringJobExecutionDto>()
                : new[]
                {
                    new RecurringJobExecutionDto
                    {
                        JobId = $"{recurringJobId}-job",
                        ExecutedAt = DateTimeOffset.UtcNow,
                        DurationMs = 1_000,
                        Succeeded = true
                    }
                };

            return Task.FromResult<IReadOnlyList<RecurringJobExecutionDto>>(executions);
        }

        public Task<IReadOnlyList<ThroughputDataPoint>> GetThroughputTimelineAsync(
            DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<StateTransitionDataPoint>> GetStateTransitionsAsync(
            DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<JobDurationStatsDto>> GetJobDurationStatsAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<QueueLatencyStatsDto>> GetQueueLatencyStatsAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
            int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<JobTypeFailureRateDto>> GetFailureRateByJobTypeAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ExceptionSummaryDto>> GetTopExceptionsAsync(
            int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<RetryBucketDto>> GetRetryDistributionAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<SnapshotResult<IReadOnlyList<ServerUtilizationDto>>> GetServerUtilizationSnapshotAsync(
            CancellationToken ct)
            => throw new NotImplementedException();

        public Task<SnapshotResult<IReadOnlyList<QueueDepthDto>>> GetQueueDepthSnapshotAsync(CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<QueueThroughputDataPoint>> GetQueueThroughputAsync(
            DateTimeOffset from, DateTimeOffset to, MetricsInterval interval, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<RecurringJobHealthDto>> GetRecurringJobHealthAsync(CancellationToken ct)
            => throw new NotImplementedException();

        public Task<AverageStateTimingsDto> GetAverageStateTimingsAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<HourlyActivityDto>> GetHourlyActivityPatternAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<JobTypeVolumeDto>> GetJobTypeVolumeAsync(
            int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
