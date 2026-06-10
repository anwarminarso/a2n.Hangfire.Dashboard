using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.SqlServer.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.SqlServer.Tests.MetricsProvider;

/// <summary>
/// Executes every <see cref="SqlServerMetricsProvider"/> query against a real SQL Server.
///
/// The primary purpose is regression coverage for SQL Server error 144 (a subquery/aggregate
/// in the GROUP BY list), which silently broke the analytics SignalR broadcast on SQL Server.
/// Every method here runs the actual T-SQL, so any query that SQL Server rejects fails the test
/// instead of failing silently at runtime.
///
/// Tests are skipped (not failed) when no SQL Server is reachable, so CI without a SQL Server
/// instance stays green.
/// </summary>
[Collection("SqlServer")]
public class MetricsProviderSmokeTests
{
    private readonly SqlServerFixture _fixture;
    private readonly SqlServerMetricsProvider _provider;

    private static readonly DateTimeOffset From = DateTimeOffset.UtcNow.AddDays(-8);
    private static readonly DateTimeOffset To = DateTimeOffset.UtcNow.AddMinutes(5);

    public MetricsProviderSmokeTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        if (fixture.Available)
            _provider = new SqlServerMetricsProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    private void RequireSqlServer() =>
        Skip.IfNot(_fixture.Available, $"SQL Server not available: {_fixture.UnavailableReason}");

    [SkippableFact]
    public async Task GetThroughputTimeline_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetThroughputTimelineAsync(From, To, MetricsInterval.OneHour, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetStateTransitions_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetStateTransitionsAsync(From, To, MetricsInterval.OneHour, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetJobDurationStats_ReturnsSucceededJobTypes()
    {
        RequireSqlServer();
        var result = await _provider.GetJobDurationStatsAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
        // 40 succeeded jobs carry PerformanceDuration → at least one job-type bucket
        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.True(r.Count > 0));
    }

    [SkippableFact]
    public async Task GetQueueLatencyStats_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetQueueLatencyStatsAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [SkippableFact]
    public async Task GetSlowestJobs_OrderedByDurationDescending()
    {
        RequireSqlServer();
        var result = await _provider.GetSlowestJobsAsync(10, From, To, CancellationToken.None);
        Assert.NotEmpty(result);
        for (int i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].DurationMs >= result[i + 1].DurationMs);
    }

    [SkippableFact]
    public async Task GetFailureRateByJobType_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetFailureRateByJobTypeAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.InRange(r.FailureRate, 0.0, 1.0));
    }

    [SkippableFact]
    public async Task GetTopExceptions_ReturnsFailedExceptionTypes()
    {
        RequireSqlServer();
        var result = await _provider.GetTopExceptionsAsync(10, From, To, CancellationToken.None);
        Assert.NotNull(result);
        // 20 failed jobs across distinct exception types
        Assert.NotEmpty(result);
    }

    [SkippableFact]
    public async Task GetRetryDistribution_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetRetryDistributionAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetServerUtilizationSnapshot_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetServerUtilizationSnapshotAsync(CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
    }

    /// <summary>
    /// Regression: this query previously placed a subquery in the GROUP BY list (error 144),
    /// which threw on every broadcast tick and silently killed analytics on SQL Server.
    /// </summary>
    [SkippableFact]
    public async Task GetQueueDepthSnapshot_DoesNotThrow_And_ReturnsQueues()
    {
        RequireSqlServer();
        var result = await _provider.GetQueueDepthSnapshotAsync(CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        // Enqueued (15) + Processing (15) jobs are spread across multiple named queues
        Assert.NotEmpty(result.Data);
        Assert.All(result.Data, q => Assert.False(string.IsNullOrEmpty(q.QueueName)));
    }

    /// <summary>
    /// Regression: same error-144 pattern as the queue depth snapshot, in the time-bucketed variant.
    /// </summary>
    [SkippableFact]
    public async Task GetQueueThroughput_DoesNotThrow_And_ReturnsBuckets()
    {
        RequireSqlServer();
        var result = await _provider.GetQueueThroughputAsync(From, To, MetricsInterval.OneHour, CancellationToken.None);
        Assert.NotNull(result);
        // 40 succeeded jobs → at least one bucket
        Assert.NotEmpty(result);
        Assert.All(result, p => Assert.False(string.IsNullOrEmpty(p.QueueName)));
    }

    [SkippableFact]
    public async Task GetRecurringJobHealth_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetRecurringJobHealthAsync(CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetRecurringJobExecutions_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetRecurringJobExecutionsAsync("simple-job", 10, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetAverageStateTimings_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetAverageStateTimingsAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetHourlyActivityPattern_Returns24Buckets()
    {
        RequireSqlServer();
        var result = await _provider.GetHourlyActivityPatternAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
        // Provider fills missing hours, so always 24 buckets (0-23)
        Assert.Equal(24, result.Count);
    }

    [SkippableFact]
    public async Task GetJobTypeVolume_DoesNotThrow()
    {
        RequireSqlServer();
        var result = await _provider.GetJobTypeVolumeAsync(10, From, To, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.True(r.ExecutionCount > 0));
    }
}
