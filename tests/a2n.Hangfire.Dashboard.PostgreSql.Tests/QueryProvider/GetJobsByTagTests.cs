using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

[Collection("PostgreSql")]
public class GetJobsByTagTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public GetJobsByTagTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [SkippableFact]
    public async Task GetByTag_Email_ReturnsResults()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsByTagAsync("email", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task GetByTag_Sample_ReturnsResults()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsByTagAsync("sample", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task GetByTag_Payment_ReturnsResults()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsByTagAsync("payment", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task GetByTag_Bulk_ReturnsResults()
    {
        PostgreSqlFixture.RequireAvailable();
        // Every 5th job gets "bulk" tag
        var result = await _provider.GetJobsByTagAsync("bulk", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount >= 15); // ~20 jobs (100/5)
    }

    [SkippableFact]
    public async Task GetByTag_Urgent_ReturnsResults()
    {
        PostgreSqlFixture.RequireAvailable();
        // Every 7th job gets "urgent" tag
        var result = await _provider.GetJobsByTagAsync("urgent", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount >= 10); // ~14 jobs (100/7)
    }

    [SkippableFact]
    public async Task GetByTag_NonExistent_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsByTagAsync("nonexistent-tag", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task GetByTag_EmptyString_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsByTagAsync("", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task GetByTag_Pagination()
    {
        PostgreSqlFixture.RequireAvailable();
        var page1 = await _provider.GetJobsByTagAsync("sample", 1, 5, CancellationToken.None);
        Assert.True(page1.TotalCount > 5);
        Assert.Equal(5, page1.Items.Count);
        Assert.True(page1.HasNextPage);

        var page2 = await _provider.GetJobsByTagAsync("sample", 2, 5, CancellationToken.None);
        Assert.Equal(5, page2.Items.Count);
        Assert.True(page2.HasPreviousPage);
    }

    [SkippableFact]
    public async Task GetByTag_OrderedByCreatedAtDescending()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsByTagAsync("sample", 1, 50, CancellationToken.None);
        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].CreatedAt;
            var next = result.Items[i + 1].CreatedAt;
            if (current.HasValue && next.HasValue)
                Assert.True(current.Value >= next.Value);
        }
    }

    [SkippableFact]
    public async Task GetByTag_ContainsMixedStates()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsByTagAsync("sample", 1, 50, CancellationToken.None);
        var states = result.Items.Select(i => i.State).Distinct().ToList();
        Assert.True(states.Count >= 2);
    }
}
