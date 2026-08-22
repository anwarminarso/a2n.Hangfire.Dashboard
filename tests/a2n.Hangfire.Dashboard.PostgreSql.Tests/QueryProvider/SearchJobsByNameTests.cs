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

    [SkippableFact]
    public async Task SearchByClassName_EmailService()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("EmailService");
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("EmailService", item.JobName));
    }

    [SkippableFact]
    public async Task SearchByClassName_PaymentProcessor()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("PaymentProcessor");
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("PaymentProcessor", item.JobName));
    }

    [SkippableFact]
    public async Task SearchByMethodName_SendEmail()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("SendEmail");
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Contains("SendEmail", item.JobName));
    }

    [SkippableFact]
    public async Task SearchByMethodName_GenerateReport()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("GenerateReport");
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task SearchCaseInsensitive()
    {
        PostgreSqlFixture.RequireAvailable();
        var upper = await SearchByName("EMAILSERVICE");
        var lower = await SearchByName("emailservice");
        var mixed = await SearchByName("eMaIlSeRvIcE");

        Assert.Equal(upper.TotalCount, lower.TotalCount);
        Assert.Equal(upper.TotalCount, mixed.TotalCount);
        Assert.True(upper.TotalCount > 0);
    }

    [SkippableFact]
    public async Task SearchPartialName_Sample()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("Sample");
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task SearchByNamespace_SampleApp()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("SampleApp.Jobs");
        // All 100 jobs have "SampleApp.Jobs" in InvocationData
        Assert.Equal(100, result.TotalCount);
    }

    [SkippableFact]
    public async Task SearchNonExistent_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("NonExistentClassName12345", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [SkippableFact]
    public async Task SearchEmptyString_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task SearchWhitespace_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("   ", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task SearchWithSpecialChars_SafeFromInjection()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("100%", pageSize: 50);
        Assert.Equal(0, result.TotalCount);

        result = await SearchByName("test_value", pageSize: 50);
        Assert.Equal(0, result.TotalCount);

        result = await SearchByName("'; DROP TABLE job; --", pageSize: 50);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task SearchPagination_FirstPage()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("SampleJobs", page: 1, pageSize: 10);
        Assert.True(result.TotalCount > 10);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasNextPage);
    }

    [SkippableFact]
    public async Task SearchPagination_SecondPage()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("SampleJobs", page: 2, pageSize: 10);
        Assert.True(result.TotalCount > 10);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasPreviousPage);
    }

    [SkippableFact]
    public async Task SearchResults_OrderedByCreatedAtDescending()
    {
        PostgreSqlFixture.RequireAvailable();
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

    [SkippableFact]
    public async Task SearchResults_ContainExpectedFields()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByName("SimpleJob", pageSize: 10);
        Assert.True(result.Items.Count > 0);
        var item = result.Items[0];

        Assert.NotNull(item.JobId);
        Assert.NotNull(item.JobName);
        Assert.NotNull(item.State);
        Assert.NotNull(item.CreatedAt);
    }
}
