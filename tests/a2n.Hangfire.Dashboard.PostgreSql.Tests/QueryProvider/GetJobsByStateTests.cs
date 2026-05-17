using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

[Collection("PostgreSql")]
public class GetJobsByStateTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public GetJobsByStateTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [Theory]
    [InlineData("Succeeded", 40)]
    [InlineData("Failed", 20)]
    [InlineData("Processing", 15)]
    [InlineData("Scheduled", 10)]
    [InlineData("Enqueued", 15)]
    public async Task GetByState_ReturnsCorrectCount(string state, int expectedCount)
    {
        var result = await _provider.GetJobsByStateAsync(state, 1, 50, CancellationToken.None);
        Assert.Equal(expectedCount, result.TotalCount);
        Assert.All(result.Items, item => Assert.Equal(state, item.State));
    }

    [Fact]
    public async Task GetByState_NonExistent_ReturnsEmpty()
    {
        var result = await _provider.GetJobsByStateAsync("Deleted", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetByState_EmptyString_ReturnsEmpty()
    {
        var result = await _provider.GetJobsByStateAsync("", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetByState_CaseSensitive()
    {
        var result = await _provider.GetJobsByStateAsync("succeeded", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetByState_Pagination()
    {
        var page1 = await _provider.GetJobsByStateAsync("Succeeded", 1, 10, CancellationToken.None);
        Assert.Equal(40, page1.TotalCount);
        Assert.Equal(10, page1.Items.Count);
        Assert.True(page1.HasNextPage);

        var page2 = await _provider.GetJobsByStateAsync("Succeeded", 2, 10, CancellationToken.None);
        Assert.Equal(10, page2.Items.Count);
        Assert.True(page2.HasPreviousPage);

        // No overlap
        var ids1 = page1.Items.Select(i => i.JobId).ToHashSet();
        var ids2 = page2.Items.Select(i => i.JobId).ToHashSet();
        Assert.Empty(ids1.Intersect(ids2));
    }

    [Fact]
    public async Task GetByState_OrderedByCreatedAtDescending()
    {
        var result = await _provider.GetJobsByStateAsync("Succeeded", 1, 50, CancellationToken.None);
        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].CreatedAt;
            var next = result.Items[i + 1].CreatedAt;
            if (current.HasValue && next.HasValue)
                Assert.True(current.Value >= next.Value);
        }
    }

    [Fact]
    public async Task GetByState_TotalPages()
    {
        var result = await _provider.GetJobsByStateAsync("Succeeded", 1, 10, CancellationToken.None);
        Assert.Equal(4, result.TotalPages);
    }
}
