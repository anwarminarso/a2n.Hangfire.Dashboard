using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

/// <summary>
/// Tests for PostgreSqlQueryProvider.SearchFailedByExceptionAsync
/// Uses 20 failed jobs (IDs 41-60) with 10 different exception types.
/// </summary>
[Collection("PostgreSql")]
public class SearchFailedByExceptionTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public SearchFailedByExceptionTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [Fact]
    public async Task SearchByExceptionType_InvalidOperation()
    {
        var result = await _provider.SearchFailedByExceptionAsync("InvalidOperationException", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Equal("Failed", item.State));
    }

    [Fact]
    public async Task SearchByExceptionType_Timeout()
    {
        var result = await _provider.SearchFailedByExceptionAsync("TimeoutException", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByExceptionType_Partial()
    {
        // "Exception" should match all failed jobs (all have "Exception" in type name)
        var result = await _provider.SearchFailedByExceptionAsync("Exception", 1, 50, CancellationToken.None);
        Assert.Equal(20, result.TotalCount);
    }

    [Fact]
    public async Task SearchByExceptionMessage_Timeout()
    {
        var result = await _provider.SearchFailedByExceptionAsync("timed out", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByExceptionMessage_SMTP()
    {
        var result = await _provider.SearchFailedByExceptionAsync("SMTP", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByExceptionMessage_Deadlock()
    {
        var result = await _provider.SearchFailedByExceptionAsync("Deadlock", 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchCaseInsensitive()
    {
        var upper = await _provider.SearchFailedByExceptionAsync("TIMEOUTEXCEPTION", 1, 50, CancellationToken.None);
        var lower = await _provider.SearchFailedByExceptionAsync("timeoutexception", 1, 50, CancellationToken.None);
        Assert.Equal(upper.TotalCount, lower.TotalCount);
        Assert.True(upper.TotalCount > 0);
    }

    [Fact]
    public async Task SearchNonExistent_ReturnsEmpty()
    {
        var result = await _provider.SearchFailedByExceptionAsync("StackOverflowException", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchEmptyString_ReturnsEmpty()
    {
        var result = await _provider.SearchFailedByExceptionAsync("", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchOnlyReturnsFailedJobs()
    {
        var result = await _provider.SearchFailedByExceptionAsync("Exception", 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.Equal("Failed", item.State));
    }

    [Fact]
    public async Task SearchResults_ContainExceptionDetails()
    {
        var result = await _provider.SearchFailedByExceptionAsync("InvalidOperation", 1, 50, CancellationToken.None);
        Assert.True(result.Items.Count > 0);
        Assert.All(result.Items, item =>
        {
            Assert.NotNull(item.ExceptionType);
            Assert.Contains("InvalidOperationException", item.ExceptionType);
        });
    }

    [Fact]
    public async Task SearchWithSpecialChars_SafeFromInjection()
    {
        var result = await _provider.SearchFailedByExceptionAsync("'; DROP TABLE state; --", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchPagination_Works()
    {
        var page1 = await _provider.SearchFailedByExceptionAsync("Exception", 1, 10, CancellationToken.None);
        Assert.Equal(20, page1.TotalCount);
        Assert.Equal(10, page1.Items.Count);
        Assert.True(page1.HasNextPage);

        var page2 = await _provider.SearchFailedByExceptionAsync("Exception", 2, 10, CancellationToken.None);
        Assert.Equal(10, page2.Items.Count);
        Assert.False(page2.HasNextPage);
    }

    [Fact]
    public async Task SearchResults_OrderedByCreatedAtDescending()
    {
        var result = await _provider.SearchFailedByExceptionAsync("Exception", 1, 50, CancellationToken.None);
        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].CreatedAt;
            var next = result.Items[i + 1].CreatedAt;
            if (current.HasValue && next.HasValue)
                Assert.True(current.Value >= next.Value);
        }
    }
}
