using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

/// <summary>
/// Tests for PostgreSqlQueryProvider.GetJobsWithFilterAsync with JobNamePattern.
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

    private Task<PagedResult<JobSummaryDto>> SearchByName(string pattern, int page = 1, int pageSize = 100)
        => _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { JobNamePattern = pattern }, page, pageSize, CancellationToken.None);

    [Fact]
    public async Task SearchByClassName_EmailService()
    {
        var result = await SearchByName("EmailService");
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("EmailService", item.JobName));
    }

    [Fact]
    public async Task SearchByClassName_PaymentProcessor()
    {
        var result = await SearchByName("PaymentProcessor");
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("PaymentProcessor", item.JobName));
    }

    [Fact]
    public async Task SearchByMethodName_SendEmail()
    {
        var result = await SearchByName("SendEmail");
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("SendEmail", item.JobName));
    }

    [Fact]
    public async Task SearchByMethodName_GenerateReport()
    {
        var result = await SearchByName("GenerateReport");
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchCaseInsensitive()
    {
        var upper = await SearchByName("EMAILSERVICE");
        var lower = await SearchByName("emailservice");
        var mixed = await SearchByName("eMaIlSeRvIcE");

        Assert.Equal(upper.TotalCount, lower.TotalCount);
        Assert.Equal(upper.TotalCount, mixed.TotalCount);
        Assert.True(upper.TotalCount > 0);
    }

    [Fact]
    public async Task SearchPartialName_Sample()
    {
        var result = await SearchByName("Sample");
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task SearchByNamespace_SampleApp()
    {
        var result = await SearchByName("SampleApp.Jobs");
        // All 100 jobs have "SampleApp.Jobs" in InvocationData
        Assert.Equal(100, result.TotalCount);
    }

    [Fact]
    public async Task SearchNonExistent_ReturnsEmpty()
    {
        var result = await SearchByName("NonExistentClassName12345", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchEmptyString_ReturnsEmpty()
    {
        var result = await SearchByName("", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchWhitespace_ReturnsEmpty()
    {
        var result = await SearchByName("   ", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchWithSpecialChars_SafeFromInjection()
    {
        var result = await SearchByName("100%", pageSize: 50);
        Assert.Equal(0, result.TotalCount);

        result = await SearchByName("test_value", pageSize: 50);
        Assert.Equal(0, result.TotalCount);

        result = await SearchByName("'; DROP TABLE job; --", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchPagination_FirstPage()
    {
        var result = await SearchByName("SampleJobs", page: 1, pageSize: 10);
        Assert.True(result.TotalCount > 10);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public async Task SearchPagination_SecondPage()
    {
        var result = await SearchByName("SampleJobs", page: 2, pageSize: 10);
        Assert.True(result.TotalCount > 10);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasPreviousPage);
    }

    [Fact]
    public async Task SearchResults_OrderedByCreatedAtDescending()
    {
        var result = await SearchByName("SampleJobs", pageSize: 50);
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
        var result = await SearchByName("SimpleJob", pageSize: 10);
        Assert.True(result.Items.Count > 0);
        var item = result.Items[0];

        Assert.NotNull(item.JobId);
        Assert.NotNull(item.JobName);
        Assert.NotNull(item.State);
        Assert.NotNull(item.CreatedAt);
    }
}
