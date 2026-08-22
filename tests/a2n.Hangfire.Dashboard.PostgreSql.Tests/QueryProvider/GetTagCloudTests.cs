using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

[Collection("PostgreSql")]
public class GetTagCloudTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public GetTagCloudTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [SkippableFact]
    public async Task GetTagCloud_ReturnsAllTags()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        // 10 possible tags: email, report, critical, import, sample, payment, notification, bulk, urgent, daily
        Assert.True(result.Count >= 8);
    }

    [SkippableFact]
    public async Task GetTagCloud_OrderedByCountDescending()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        for (int i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].Count >= result[i + 1].Count);
    }

    [SkippableFact]
    public async Task GetTagCloud_TagNamesDoNotContainPrefix()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        Assert.All(result, tag => Assert.DoesNotContain("tags:", tag.Tag));
    }

    [SkippableFact]
    public async Task GetTagCloud_AllCountsPositive()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        Assert.All(result, tag => Assert.True(tag.Count > 0));
    }

    [SkippableFact]
    public async Task GetTagCloud_ContainsExpectedTags()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        var tagNames = result.Select(t => t.Tag).ToHashSet();

        Assert.Contains("sample", tagNames);
        Assert.Contains("email", tagNames);
        Assert.Contains("bulk", tagNames);
        Assert.Contains("urgent", tagNames);
    }

    [SkippableFact]
    public async Task GetTagCloud_BulkTagHasExpectedCount()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        var bulk = result.FirstOrDefault(t => t.Tag == "bulk");
        Assert.NotNull(bulk);
        // Every 5th job gets "bulk" tag → 20 jobs
        Assert.Equal(20, bulk.Count);
    }
}
