using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

/// <summary>
/// Tests for PostgreSqlQueryProvider.SearchJobsByNameAsync
/// Verifies ILIKE pattern matching on InvocationData (type + method name).
/// Uses 100 seeded jobs with 15 different job types.
/// </summary>
[Collection("PostgreSql")]
public class SearchJobsByNameTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public SearchJobsByNameTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [Fact]
    public async Task SearchByClassName_EmailService()
    {
        var result = await _provider.SearchJobsByNameAsync("EmailService", 1, 100, CancellationToken.None);
        // EmailService jobs appear at positions 5,6,7 in the 15-type cycle → ~20 jobs
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("EmailService", item.JobName));
    }

    [Fact]
    public async Task SearchByClassName_PaymentProcessor()
    {
        var result = await _provider.SearchJobsByNameAsync("PaymentProcessor", 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("PaymentProcessor", item.JobName));
    }

    [Fact]
    public async Task SearchByMethodName_SendEmail()
    {
        var result = await _provider.SearchJobsByNameAsync("SendEmail", 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("SendEmail", item.JobName));
    }

    [Fact]
    public async Task SearchByMethodName_GenerateReport()
    {
        var result = await _provider.SearchJobsByNameAsync("GenerateReport", 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchCaseInsensitive()
    {
        var upper = await _provider.SearchJobsByNameAsync("EMAILSERVICE", 1, 100, CancellationToken.None);
        var lower = await _provider.SearchJobsByNameAsync("emailservice", 1, 100, CancellationToken.None);
        var mixed = await _provider.SearchJobsByNameAsync("eMaIlSeRvIcE", 1, 100, CancellationToken.None);

        Assert.Equal(upper.TotalCount, lower.TotalCount);
        Assert.Equal(upper.TotalCount, mixed.TotalCount);
        Assert.True(upper.TotalCount > 0);
    }

    [Fact]
    public async Task SearchPartialName_Sample()
    {
        var result = await _provider.SearchJobsByNameAsync("Sample", 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByNamespace_SampleApp()
    {
        var result = await _provider.SearchJobsByNameAsync("SampleApp.Jobs", 1, 100, CancellationToken.None);
        // All 100 jobs have "SampleApp.Jobs" in InvocationData
        Assert.Equal(100, result.TotalCount);
    }

    [Fact]
    public async Task SearchNonExistent_ReturnsEmpty()
    {
        var result = await _provider.SearchJobsByNameAsync("NonExistentClassName12345", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchEmptyString_ReturnsEmpty()
    {
        var result = await _provider.SearchJobsByNameAsync("", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchWhitespace_ReturnsEmpty()
    {
        var result = await _provider.SearchJobsByNameAsync("   ", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchWithSpecialChars_SafeFromInjection()
    {
        var result = await _provider.SearchJobsByNameAsync("100%", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);

        result = await _provider.SearchJobsByNameAsync("test_value", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);

        result = await _provider.SearchJobsByNameAsync("'; DROP TABLE job; --", 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchPagination_FirstPage()
    {
        var result = await _provider.SearchJobsByNameAsync("SampleJobs", 1, 10, CancellationToken.None);
        Assert.True(result.TotalCount > 10);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public async Task SearchPagination_SecondPage()
    {
        var result = await _provider.SearchJobsByNameAsync("SampleJobs", 2, 10, CancellationToken.None);
        Assert.True(result.TotalCount > 10);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasPreviousPage);
    }

    [Fact]
    public async Task SearchResults_OrderedByCreatedAtDescending()
    {
        var result = await _provider.SearchJobsByNameAsync("SampleJobs", 1, 50, CancellationToken.None);
        Assert.True(result.Items.Count >= 2);

        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].CreatedAt;
            var next = result.Items[i + 1].CreatedAt;
            if (current.HasValue && next.HasValue)
                Assert.True(current.Value >= next.Value);
        }
    }

    [Fact]
    public async Task SearchResults_ContainExpectedFields()
    {
        var result = await _provider.SearchJobsByNameAsync("SimpleJob", 1, 10, CancellationToken.None);
        Assert.True(result.Items.Count > 0);
        var item = result.Items[0];

        Assert.NotNull(item.JobId);
        Assert.NotNull(item.JobName);
        Assert.NotNull(item.State);
        Assert.NotNull(item.CreatedAt);
    }
}
