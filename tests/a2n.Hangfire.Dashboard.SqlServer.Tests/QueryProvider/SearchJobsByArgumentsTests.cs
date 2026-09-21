using Dapper;
using Microsoft.Data.SqlClient;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.SqlServer.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.SqlServer.Tests.QueryProvider;

/// <summary>
/// Tests for SqlServerQueryProvider.GetJobsWithFilterAsync with ArgumentsPattern.
/// Verifies LIKE matching on the Job.Arguments column — where Hangfire.SqlServer keeps the
/// serialized argument array — and that the match never reaches the type or method name.
/// Uses the 100 seeded jobs; see TestDataSeeder for the argument layout.
///
/// Case sensitivity is not asserted unconditionally: on SQL Server LIKE follows the column
/// collation, so the one test that depends on it probes the collation first and skips on a
/// case-sensitive server. PostgreSQL has no such ambiguity because it uses ILIKE.
/// </summary>
[Collection("SqlServer")]
public class SearchJobsByArgumentsTests
{
    private readonly SqlServerFixture _fixture;
    private readonly SqlServerQueryProvider _provider;

    public SearchJobsByArgumentsTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        if (fixture.Available)
            _provider = new SqlServerQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    private void RequireSqlServer() =>
        Skip.IfNot(_fixture.Available, $"SQL Server not available: {_fixture.UnavailableReason}");

    private Task<PagedResult<JobSummaryDto>> SearchByArguments(string pattern, int page = 1, int pageSize = 100)
        => _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { ArgumentsPattern = pattern }, page, pageSize, CancellationToken.None);

    [SkippableFact]
    public async Task SearchByUniqueValue_ReturnsOnlyThatJob()
    {
        RequireSqlServer();
        var result = await SearchByArguments("needle-a1b2c3");

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("42", result.Items[0].JobId);
    }

    [SkippableFact]
    public async Task SearchBySharedValue_MatchesEveryJobCarryingIt()
    {
        RequireSqlServer();
        var result = await SearchByArguments("@example.com");

        // Job 7 is the only job with a null in place of the e-mail argument
        Assert.Equal(TestDataSeeder.Counts.Total - 1, result.TotalCount);
    }

    [SkippableFact]
    public async Task SearchByNumericValue_StaysAnArgumentSearch()
    {
        RequireSqlServer();
        var result = await SearchByArguments("37");

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("37", result.Items[0].JobId);
    }

    [SkippableFact]
    public async Task SearchDoesNotMatchTypeOrMethodName()
    {
        RequireSqlServer();

        // The regression this suite exists for: Hangfire.SqlServer keeps the arguments in a column of
        // their own and writes InvocationData with its own Arguments property left null, so a provider
        // that searches inside InvocationData finds nothing at all, and one that searches the whole
        // payload turns an argument search into a name search.
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
        RequireSqlServer();
        Skip.IfNot(await CollationIsCaseInsensitiveAsync(),
            "Database collation is case-sensitive, so LIKE is too.");

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
        RequireSqlServer();
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
        RequireSqlServer();
        var result = await SearchByArguments("no-such-argument-value-12345", pageSize: 50);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [SkippableFact]
    public async Task SearchEmptyString_ReturnsEmpty()
    {
        RequireSqlServer();
        Assert.Equal(0, (await SearchByArguments("", pageSize: 50)).TotalCount);
    }

    [SkippableFact]
    public async Task SearchWhitespace_ReturnsEmpty()
    {
        RequireSqlServer();
        Assert.Equal(0, (await SearchByArguments("   ", pageSize: 50)).TotalCount);
    }

    [SkippableFact]
    public async Task SearchWithSpecialChars_SafeFromInjection()
    {
        RequireSqlServer();

        // LIKE wildcards are escaped, so these are matched literally and find nothing
        Assert.Equal(0, (await SearchByArguments("100%", pageSize: 50)).TotalCount);
        Assert.Equal(0, (await SearchByArguments("user_1@example.com", pageSize: 50)).TotalCount);
        Assert.Equal(0, (await SearchByArguments("'; DROP TABLE Job; --", pageSize: 50)).TotalCount);
    }

    [SkippableFact]
    public async Task SearchPagination_FirstPage()
    {
        RequireSqlServer();
        var result = await SearchByArguments("@example.com", page: 1, pageSize: 10);

        Assert.Equal(TestDataSeeder.Counts.Total - 1, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasNextPage);
    }

    [SkippableFact]
    public async Task SearchPagination_SecondPage()
    {
        RequireSqlServer();
        var result = await SearchByArguments("@example.com", page: 2, pageSize: 10);

        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasPreviousPage);
    }

    [SkippableFact]
    public async Task SearchResults_OrderedByCreatedAtDescending()
    {
        RequireSqlServer();
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

    /// <summary>
    /// Asks the server whether LIKE ignores case, rather than assuming the default collation.
    /// </summary>
    private async Task<bool> CollationIsCaseInsensitiveAsync()
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN 'a' LIKE 'A' THEN 1 ELSE 0 END") == 1;
    }
}
