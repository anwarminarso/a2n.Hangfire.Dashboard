using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

/// <summary>
/// Tests for PostgreSqlQueryProvider.GetJobsWithFilterAsync with ExceptionPattern.
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

    private Task<PagedResult<JobSummaryDto>> SearchByException(string pattern, int page = 1, int pageSize = 50)
        => _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { ExceptionPattern = pattern }, page, pageSize, CancellationToken.None);

    [Fact]
    public async Task SearchByExceptionType_InvalidOperation()
    {
        var result = await SearchByException("InvalidOperationException");
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Equal("Failed", item.State));
    }

    [Fact]
    public async Task SearchByExceptionType_Timeout()
    {
        var result = await SearchByException("TimeoutException");
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByExceptionType_Partial()
    {
        // "Exception" should match all failed jobs (all have "Exception" in type name)
        var result = await SearchByException("Exception");
        Assert.Equal(20, result.TotalCount);
    }

    [Fact]
    public async Task SearchByExceptionMessage_Timeout()
    {
        var result = await SearchByException("timed out");
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByExceptionMessage_SMTP()
    {
        var result = await SearchByException("SMTP");
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByExceptionMessage_Deadlock()
    {
        var result = await SearchByException("Deadlock");
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchCaseInsensitive()
    {
        var upper = await SearchByException("TIMEOUTEXCEPTION");
        var lower = await SearchByException("timeoutexception");
        Assert.Equal(upper.TotalCount, lower.TotalCount);
        Assert.True(upper.TotalCount > 0);
    }

    [Fact]
    public async Task SearchNonExistent_ReturnsEmpty()
    {
        var result = await SearchByException("StackOverflowException");
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchEmptyString_ReturnsEmpty()
    {
        var result = await SearchByException("");
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchOnlyReturnsFailedJobs()
    {
        var result = await SearchByException("Exception");
        Assert.All(result.Items, item => Assert.Equal("Failed", item.State));
    }

    [Fact]
    public async Task SearchResults_ContainExceptionDetails()
    {
        var result = await SearchByException("InvalidOperation");
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
        var result = await SearchByException("'; DROP TABLE state; --");
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchPagination_Works()
    {
        var page1 = await SearchByException("Exception", page: 1, pageSize: 10);
        Assert.Equal(20, page1.TotalCount);
        Assert.Equal(10, page1.Items.Count);
        Assert.True(page1.HasNextPage);

        var page2 = await SearchByException("Exception", page: 2, pageSize: 10);
        Assert.Equal(10, page2.Items.Count);
        Assert.False(page2.HasNextPage);
    }

    [Fact]
    public async Task SearchResults_OrderedByCreatedAtDescending()
    {
        var result = await SearchByException("Exception");
        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].CreatedAt;
            var next = result.Items[i + 1].CreatedAt;
            if (current.HasValue && next.HasValue)
                Assert.True(current.Value >= next.Value);
        }
    }
}
