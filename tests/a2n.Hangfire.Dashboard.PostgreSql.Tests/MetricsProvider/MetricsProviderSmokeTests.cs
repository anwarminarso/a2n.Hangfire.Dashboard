using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.MetricsProvider;

/// <summary>
/// Exercises <see cref="PostgreSqlMetricsProvider"/> against the seeded schema. Mirrors the SQL Server
/// smoke suite: the point is that every query is syntactically valid against a real database and maps
/// onto its DTO, since a broken projection surfaces as an empty Analytics panel rather than an error.
/// </summary>
[Collection("PostgreSql")]
public class MetricsProviderSmokeTests
{
    private static readonly DateTimeOffset From = DateTimeOffset.UtcNow.AddDays(-8);
    private static readonly DateTimeOffset To = DateTimeOffset.UtcNow.AddHours(1);

    private readonly PostgreSqlMetricsProvider _provider;

    public MetricsProviderSmokeTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlMetricsProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [SkippableFact]
    public async Task GetJobDurationStats_ReturnsBucketsWithPercentiles()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobDurationStatsAsync(From, To, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, s =>
        {
            Assert.False(string.IsNullOrEmpty(s.JobType));
            Assert.True(s.Count > 0);
            Assert.True(s.P95Ms > 0);
        });
    }

    [SkippableFact]
    public async Task GetQueueLatencyStats_ReturnsQueues()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetQueueLatencyStatsAsync(From, To, CancellationToken.None);

        Assert.NotNull(result);
        Assert.All(result, q => Assert.False(string.IsNullOrEmpty(q.QueueName)));
    }

    [SkippableFact]
    public async Task GetSlowestJobs_ReturnsDescendingDurations()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetSlowestJobsAsync(5, From, To, CancellationToken.None);

        Assert.NotEmpty(result);
        for (var i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].DurationMs >= result[i + 1].DurationMs);
    }

    [SkippableFact]
    public async Task GetAverageStateTimings_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetAverageStateTimingsAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetHourlyActivityPattern_Returns24Buckets()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetHourlyActivityPatternAsync(From, To, CancellationToken.None);

        Assert.Equal(24, result.Count);
        Assert.Equal(Enumerable.Range(0, 24), result.Select(h => h.Hour));
    }

    [SkippableFact]
    public async Task GetThroughputTimeline_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetThroughputTimelineAsync(
            From, To, MetricsInterval.OneHour, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetStateTransitions_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetStateTransitionsAsync(
            From, To, MetricsInterval.OneHour, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetFailureRateByJobType_RatesAreFractions()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetFailureRateByJobTypeAsync(From, To, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.InRange(r.FailureRate, 0d, 1d));
    }

    [SkippableFact]
    public async Task GetTopExceptions_ReturnsDescendingCounts()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetTopExceptionsAsync(5, From, To, CancellationToken.None);

        Assert.NotEmpty(result);
        for (var i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].Count >= result[i + 1].Count);
    }

    [SkippableFact]
    public async Task GetJobTypeVolume_ReturnsDescendingCounts()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobTypeVolumeAsync(5, From, To, CancellationToken.None);

        Assert.NotEmpty(result);
        for (var i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].ExecutionCount >= result[i + 1].ExecutionCount);
    }

    [SkippableFact]
    public async Task GetRetryDistribution_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetRetryDistributionAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetQueueThroughput_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetQueueThroughputAsync(
            From, To, MetricsInterval.OneHour, CancellationToken.None);
        Assert.NotNull(result);
    }

    [SkippableFact]
    public async Task GetQueueDepthSnapshot_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetQueueDepthSnapshotAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
    }

    [SkippableFact]
    public async Task GetServerUtilizationSnapshot_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetServerUtilizationSnapshotAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
    }

    [SkippableFact]
    public async Task GetRecurringScheduleBuckets_DoesNotThrow()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetRecurringScheduleBucketsAsync(From, To, CancellationToken.None);
        Assert.NotNull(result);
    }
}
