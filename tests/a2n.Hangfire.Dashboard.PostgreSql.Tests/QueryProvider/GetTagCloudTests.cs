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

    [Fact]
    public async Task GetTagCloud_ReturnsAllTags()
    {
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        // 10 possible tags: email, report, critical, import, sample, payment, notification, bulk, urgent, daily
        Assert.True(result.Count >= 8);
    }

    [Fact]
    public async Task GetTagCloud_OrderedByCountDescending()
    {
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        for (int i = 0; i < result.Count - 1; i++)
            Assert.True(result[i].Count >= result[i + 1].Count);
    }

    [Fact]
    public async Task GetTagCloud_TagNamesDoNotContainPrefix()
    {
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        Assert.All(result, tag => Assert.DoesNotContain("tags:", tag.Tag));
    }

    [Fact]
    public async Task GetTagCloud_AllCountsPositive()
    {
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        Assert.All(result, tag => Assert.True(tag.Count > 0));
    }

    [Fact]
    public async Task GetTagCloud_ContainsExpectedTags()
    {
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        var tagNames = result.Select(t => t.Tag).ToHashSet();

        Assert.Contains("sample", tagNames);
        Assert.Contains("email", tagNames);
        Assert.Contains("bulk", tagNames);
        Assert.Contains("urgent", tagNames);
    }

    [Fact]
    public async Task GetTagCloud_BulkTagHasExpectedCount()
    {
        var result = await _provider.GetTagCloudAsync(CancellationToken.None);
        var bulk = result.FirstOrDefault(t => t.Tag == "bulk");
        Assert.NotNull(bulk);
        // Every 5th job gets "bulk" tag → 20 jobs
        Assert.Equal(20, bulk.Count);
    }
}
