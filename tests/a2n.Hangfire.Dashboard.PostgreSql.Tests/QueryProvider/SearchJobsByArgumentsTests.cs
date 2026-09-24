using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

/// <summary>
/// Tests for PostgreSqlQueryProvider.GetJobsWithFilterAsync with ArgumentsPattern.
/// Verifies ILIKE matching on the job.arguments column — where Hangfire.PostgreSql keeps the
/// serialized argument array — and that the match never reaches the type or method name.
/// Uses the 100 seeded jobs; see TestDataSeeder for the argument layout.
/// </summary>
[Collection("PostgreSql")]
public class SearchJobsByArgumentsTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public SearchJobsByArgumentsTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    private Task<PagedResult<JobSummaryDto>> SearchByArguments(string pattern, int page = 1, int pageSize = 100)
        => _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { ArgumentsPattern = pattern }, page, pageSize, CancellationToken.None);

    [SkippableFact]
    public async Task SearchByUniqueValue_ReturnsOnlyThatJob()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByArguments("needle-a1b2c3");

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("42", result.Items[0].JobId);
    }

    [SkippableFact]
    public async Task SearchBySharedValue_MatchesEveryJobCarryingIt()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByArguments("@example.com");

        // Job 7 is the only job with a null in place of the e-mail argument
        Assert.Equal(TestDataSeeder.Counts.Total - 1, result.TotalCount);
    }

    [SkippableFact]
    public async Task SearchByNumericValue_StaysAnArgumentSearch()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByArguments("37");

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("37", result.Items[0].JobId);
    }

    [SkippableFact]
    public async Task SearchDoesNotMatchTypeOrMethodName()
    {
        PostgreSqlFixture.RequireAvailable();

        // Guards two regressions at once: matching the whole InvocationData column turns an argument
        // search into a name search, and matching inside it finds nothing at all on a storage that
        // keeps the arguments in a column of their own.
        var byName = await _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { JobNamePattern = "EmailService" }, 1, 100, CancellationToken.None);
        Assert.True(byName.TotalCount > 0);

        Assert.Equal(0, (await SearchByArguments("EmailService")).TotalCount);
        Assert.Equal(0, (await SearchByArguments("SendEmail")).TotalCount);
        Assert.Equal(0, (await SearchByArguments("SampleApp")).TotalCount);
    }

    [SkippableFact]
    public async Task SearchCaseInsensitive()
    {
        PostgreSqlFixture.RequireAvailable();
        var upper = await SearchByArguments("NEEDLE-A1B2C3");
        var lower = await SearchByArguments("needle-a1b2c3");
        var mixed = await SearchByArguments("NeEdLe-A1b2C3");

        Assert.Equal(1, upper.TotalCount);
        Assert.Equal(upper.TotalCount, lower.TotalCount);
        Assert.Equal(upper.TotalCount, mixed.TotalCount);
    }

    [SkippableFact]
    public async Task SearchCombinedWithState_NarrowsTheResults()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { ArgumentsPattern = "@example.com", State = "Failed" },
            1, 100, CancellationToken.None);

        // Jobs 41-60 are Failed and all of them carry the e-mail argument
        Assert.Equal(TestDataSeeder.Counts.Failed, result.TotalCount);
        Assert.All(result.Items, item => Assert.Equal("Failed", item.State));
    }

    [SkippableFact]
    public async Task SearchNonExistent_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByArguments("no-such-argument-value-12345", pageSize: 50);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [SkippableFact]
    public async Task SearchEmptyString_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        Assert.Equal(0, (await SearchByArguments("", pageSize: 50)).TotalCount);
    }

    [SkippableFact]
    public async Task SearchWhitespace_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        Assert.Equal(0, (await SearchByArguments("   ", pageSize: 50)).TotalCount);
    }

    [SkippableFact]
    public async Task SearchWithSpecialChars_SafeFromInjection()
    {
        PostgreSqlFixture.RequireAvailable();

        // ILIKE wildcards are escaped, so these are matched literally and find nothing
        Assert.Equal(0, (await SearchByArguments("100%", pageSize: 50)).TotalCount);
        Assert.Equal(0, (await SearchByArguments("user_1@example.com", pageSize: 50)).TotalCount);
        Assert.Equal(0, (await SearchByArguments("'; DROP TABLE job; --", pageSize: 50)).TotalCount);
    }

    [SkippableFact]
    public async Task SearchPagination_FirstPage()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByArguments("@example.com", page: 1, pageSize: 10);

        Assert.Equal(TestDataSeeder.Counts.Total - 1, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasNextPage);
    }

    [SkippableFact]
    public async Task SearchPagination_SecondPage()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByArguments("@example.com", page: 2, pageSize: 10);

        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasPreviousPage);
    }

    [SkippableFact]
    public async Task SearchResults_OrderedByCreatedAtDescending()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await SearchByArguments("@example.com", pageSize: 50);

        Assert.True(result.Items.Count >= 2);
        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].CreatedAt;
            var next = result.Items[i + 1].CreatedAt;
            if (current.HasValue && next.HasValue)
                Assert.True(current.Value >= next.Value);
        }
    }
}
