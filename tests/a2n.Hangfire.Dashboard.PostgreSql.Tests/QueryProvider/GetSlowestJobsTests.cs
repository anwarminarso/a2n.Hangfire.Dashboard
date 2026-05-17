using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

[Collection("PostgreSql")]
public class GetSlowestJobsTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public GetSlowestJobsTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [Fact]
    public async Task GetSlowest_Top5_OrderedByDuration()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-8);
        var to = DateTimeOffset.UtcNow;
        var result = await _provider.GetSlowestJobsAsync(5, from, to, CancellationToken.None);

        Assert.Equal(5, result.Count);
        for (int i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].DurationMs >= result[i + 1].DurationMs);
    }

    [Fact]
    public async Task GetSlowest_Top1_ReturnsSlowest()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-8);
        var to = DateTimeOffset.UtcNow;
        var result = await _provider.GetSlowestJobsAsync(1, from, to, CancellationToken.None);

        Assert.Single(result);
        // The slowest job should have duration = 120000ms (id%10==0 pattern)
        Assert.Equal(120000, result[0].DurationMs);
    }

    [Fact]
    public async Task GetSlowest_AllInRange()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-8);
        var to = DateTimeOffset.UtcNow;
        var result = await _provider.GetSlowestJobsAsync(100, from, to, CancellationToken.None);

        // 40 succeeded jobs have PerformanceDuration
        Assert.Equal(40, result.Count);
    }

    [Fact]
    public async Task GetSlowest_NarrowTimeRange()
    {
        // Succeeded jobs (IDs 1-40) are created between 168h and ~101h ago
        // Use a range that covers part of that window
        var from = DateTimeOffset.UtcNow.AddDays(-6);
        var to = DateTimeOffset.UtcNow.AddDays(-4);
        var result = await _provider.GetSlowestJobsAsync(50, from, to, CancellationToken.None);

        Assert.True(result.Count > 0);
        Assert.True(result.Count < 40); // not all succeeded jobs
    }

    [Fact]
    public async Task GetSlowest_FutureRange_ReturnsEmpty()
    {
        var from = DateTimeOffset.UtcNow.AddDays(1);
        var to = DateTimeOffset.UtcNow.AddDays(2);
        var result = await _provider.GetSlowestJobsAsync(10, from, to, CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSlowest_CountCappedAt100()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-8);
        var to = DateTimeOffset.UtcNow;
        var result = await _provider.GetSlowestJobsAsync(200, from, to, CancellationToken.None);
        Assert.True(result.Count <= 100);
    }

    [Fact]
    public async Task GetSlowest_Results_ContainExpectedFields()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-8);
        var to = DateTimeOffset.UtcNow;
        var result = await _provider.GetSlowestJobsAsync(5, from, to, CancellationToken.None);

        Assert.All(result, item =>
        {
            Assert.NotNull(item.JobId);
            Assert.NotNull(item.JobName);
            Assert.True(item.DurationMs > 0);
            Assert.NotNull(item.CompletedAt);
        });
    }

    [Fact]
    public async Task GetSlowest_JobNamesExtracted()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-8);
        var to = DateTimeOffset.UtcNow;
        var result = await _provider.GetSlowestJobsAsync(5, from, to, CancellationToken.None);

        Assert.All(result, item =>
        {
            Assert.NotEqual("(unknown)", item.JobName);
            Assert.Contains(".", item.JobName);
        });
    }
}
