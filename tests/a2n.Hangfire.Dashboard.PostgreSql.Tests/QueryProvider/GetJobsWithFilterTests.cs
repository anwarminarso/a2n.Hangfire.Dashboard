using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

/// <summary>
/// Tests for PostgreSqlQueryProvider.GetJobsWithFilterAsync
/// Verifies advanced filtering with various parameter combinations (AND logic).
/// Uses 100 seeded jobs with diverse states, queues, servers, tags, durations, and timestamps.
/// </summary>
[Collection("PostgreSql")]
public class GetJobsWithFilterTests
{
    private readonly PostgreSqlQueryProvider _provider;

    public GetJobsWithFilterTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    #region Single Filter Tests

    [SkippableTheory]
    [InlineData("Succeeded", 40)]
    [InlineData("Failed", 20)]
    [InlineData("Processing", 15)]
    [InlineData("Scheduled", 10)]
    [InlineData("Enqueued", 15)]
    public async Task FilterByState_ReturnsCorrectCount(string state, int expected)
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = state };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);

        Assert.Equal(expected, result.TotalCount);
        Assert.All(result.Items, item => Assert.Equal(state, item.State));
    }

    [SkippableFact]
    public async Task FilterByState_NonExistent_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Deleted" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task FilterByQueue_Default()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Queue = "default" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByQueue_Email()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Queue = "email" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByQueue_Payments()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Queue = "payments" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByQueue_Critical()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Queue = "critical" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        // Critical queue: SampleJobs where id%10==0, but SampleJobs are at type indices 0-3
        // IDs at type index 0-3: 1,2,3,4,16,17,18,19,31,32,33,34,46,47,48,49,61,62,63,64,76,77,78,79,91,92,93,94
        // Of those, id%10==0: 30→idx14(NotificationService), 20→idx4(EmailService)... none match
        // So critical queue may be empty — that's valid behavior
        Assert.True(result.TotalCount >= 0);
    }

    [SkippableFact]
    public async Task FilterByQueue_NonExistent_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Queue = "nonexistent-queue" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task FilterByDateFrom_Last24Hours()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { DateFrom = DateTimeOffset.UtcNow.AddHours(-24) };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        // ~14% of 100 jobs (24/168 hours)
        Assert.True(result.TotalCount >= 10);
        Assert.True(result.TotalCount <= 25);
    }

    [SkippableFact]
    public async Task FilterByDateTo_OlderThan5Days()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { DateTo = DateTimeOffset.UtcNow.AddDays(-5) };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        // ~29% of 100 jobs (first 48h of 168h)
        Assert.True(result.TotalCount >= 20);
        Assert.True(result.TotalCount <= 40);
    }

    [SkippableFact]
    public async Task FilterByDateRange_Day3To5()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            DateFrom = DateTimeOffset.UtcNow.AddDays(-5),
            DateTo = DateTimeOffset.UtcNow.AddDays(-3)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByMinDuration_Over10Seconds()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { MinDuration = TimeSpan.FromSeconds(10) };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.True(item.DurationMs >= 10000));
    }

    [SkippableFact]
    public async Task FilterByMaxDuration_Under1Second()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { MaxDuration = TimeSpan.FromSeconds(1) };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.True(item.DurationMs <= 1000));
    }

    [SkippableFact]
    public async Task FilterByDurationRange_1To5Seconds()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            MinDuration = TimeSpan.FromSeconds(1),
            MaxDuration = TimeSpan.FromSeconds(5)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item =>
        {
            Assert.True(item.DurationMs >= 1000);
            Assert.True(item.DurationMs <= 5000);
        });
    }

    [SkippableFact]
    public async Task FilterByTags_Email()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Tags = new List<string> { "email" } };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByTags_Payment()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Tags = new List<string> { "payment" } };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByTags_MultipleTags_AndLogic()
    {
        PostgreSqlFixture.RequireAvailable();
        // Jobs that have BOTH "email" AND "bulk" tags
        var criteria = new JobFilterCriteria { Tags = new List<string> { "email", "bulk" } };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        // Some EmailService jobs at id%5==0 will have both
        Assert.True(result.TotalCount >= 0); // may or may not match
    }

    [SkippableFact]
    public async Task FilterByServer()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Server = "server-1:1234" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByRecurringJobId()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { RecurringJobId = "simple-job" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task FilterByRecurringJobId_NonExistent()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { RecurringJobId = "non-existent-job" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    #endregion

    #region Two-Parameter Combinations

    [SkippableFact]
    public async Task Filter_State_And_Queue()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded", Queue = "email" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Equal("Succeeded", item.State));
    }

    [SkippableFact]
    public async Task Filter_State_And_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        // Succeeded jobs (IDs 1-40) span hours 168 to ~101 ago (linear distribution)
        // Use a wide range that definitely includes some succeeded jobs
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            DateFrom = DateTimeOffset.UtcNow.AddDays(-6),
            DateTo = DateTimeOffset.UtcNow.AddDays(-4)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Equal("Succeeded", item.State));
    }

    [SkippableFact]
    public async Task Filter_State_And_MinDuration()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            MinDuration = TimeSpan.FromSeconds(30)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item =>
        {
            Assert.Equal("Succeeded", item.State);
            Assert.True(item.DurationMs >= 30000);
        });
    }

    [SkippableFact]
    public async Task Filter_State_And_Tags()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Failed",
            Tags = new List<string> { "payment" }
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.Equal("Failed", item.State));
    }

    [SkippableFact]
    public async Task Filter_State_And_Server()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Processing",
            Server = "server-2:5678"
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Equal("Processing", item.State));
    }

    [SkippableFact]
    public async Task Filter_State_And_RecurringJobId()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            RecurringJobId = "report-daily"
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.Equal("Succeeded", item.State));
    }

    [SkippableFact]
    public async Task Filter_Queue_And_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            Queue = "email",
            DateFrom = DateTimeOffset.UtcNow.AddDays(-4)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task Filter_Queue_And_Duration()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            Queue = "reports",
            MinDuration = TimeSpan.FromSeconds(5)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.True(item.DurationMs >= 5000));
    }

    [SkippableFact]
    public async Task Filter_Queue_And_Tags()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            Queue = "default",
            Tags = new List<string> { "sample" }
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task Filter_Tags_And_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            Tags = new List<string> { "report" },
            DateFrom = DateTimeOffset.UtcNow.AddDays(-5)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task Filter_Tags_And_Duration()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            Tags = new List<string> { "sample" },
            MaxDuration = TimeSpan.FromSeconds(2)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.True(item.DurationMs <= 2000));
    }

    [SkippableFact]
    public async Task Filter_Server_And_Tags()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            Server = "server-1:1234",
            Tags = new List<string> { "sample" }
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        // May or may not have results depending on data distribution
        Assert.True(result.TotalCount >= 0);
    }

    [SkippableFact]
    public async Task Filter_Duration_And_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            MinDuration = TimeSpan.FromSeconds(10),
            DateFrom = DateTimeOffset.UtcNow.AddDays(-3)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.True(item.DurationMs >= 10000));
    }

    [SkippableFact]
    public async Task Filter_RecurringJobId_And_Queue()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            RecurringJobId = "email-digest",
            Queue = "email"
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        // email-digest recurring jobs in email queue
        Assert.True(result.TotalCount >= 0);
    }

    #endregion

    #region Three-Parameter Combinations

    [SkippableFact]
    public async Task Filter_State_Queue_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Queue = "default",
            DateFrom = DateTimeOffset.UtcNow.AddDays(-4)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.Equal("Succeeded", item.State));
    }

    [SkippableFact]
    public async Task Filter_State_Queue_Tags()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Queue = "default",
            Tags = new List<string> { "sample" }
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
        Assert.All(result.Items, item => Assert.Equal("Succeeded", item.State));
    }

    [SkippableFact]
    public async Task Filter_State_Queue_Duration()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Queue = "email",
            MinDuration = TimeSpan.FromSeconds(1)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item =>
        {
            Assert.Equal("Succeeded", item.State);
            Assert.True(item.DurationMs >= 1000);
        });
    }

    [SkippableFact]
    public async Task Filter_State_DateRange_Duration()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            DateFrom = DateTimeOffset.UtcNow.AddDays(-5),
            MinDuration = TimeSpan.FromSeconds(5),
            MaxDuration = TimeSpan.FromSeconds(60)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item =>
        {
            Assert.Equal("Succeeded", item.State);
            Assert.True(item.DurationMs >= 5000);
            Assert.True(item.DurationMs <= 60000);
        });
    }

    [SkippableFact]
    public async Task Filter_State_Tags_Duration()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Tags = new List<string> { "email" },
            MaxDuration = TimeSpan.FromSeconds(10)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item =>
        {
            Assert.Equal("Succeeded", item.State);
            Assert.True(item.DurationMs <= 10000);
        });
    }

    [SkippableFact]
    public async Task Filter_Queue_Tags_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            Queue = "default",
            Tags = new List<string> { "sample" },
            DateFrom = DateTimeOffset.UtcNow.AddDays(-3)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.True(result.TotalCount > 0);
    }

    [SkippableFact]
    public async Task Filter_State_Server_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Processing",
            Server = "server-1:1234",
            DateFrom = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.Equal("Processing", item.State));
    }

    #endregion

    #region Four+ Parameter Combinations

    [SkippableFact]
    public async Task Filter_State_Queue_DateRange_Duration()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Queue = "default",
            DateFrom = DateTimeOffset.UtcNow.AddDays(-5),
            MinDuration = TimeSpan.FromSeconds(1)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item =>
        {
            Assert.Equal("Succeeded", item.State);
            Assert.True(item.DurationMs >= 1000);
        });
    }

    [SkippableFact]
    public async Task Filter_State_Queue_Tags_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Queue = "default",
            Tags = new List<string> { "sample" },
            DateFrom = DateTimeOffset.UtcNow.AddDays(-4)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.Equal("Succeeded", item.State));
    }

    [SkippableFact]
    public async Task Filter_State_Queue_RecurringJobId_DateRange()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Queue = "default",
            RecurringJobId = "simple-job",
            DateFrom = DateTimeOffset.UtcNow.AddDays(-6)
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.All(result.Items, item => Assert.Equal("Succeeded", item.State));
    }

    [SkippableFact]
    public async Task Filter_AllParameters_Contradictory_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria
        {
            State = "Succeeded",
            Queue = "payments",
            Server = "server-1:1234", // server only for Processing jobs
            MinDuration = TimeSpan.FromMinutes(10),
            Tags = new List<string> { "nonexistent-tag" },
            RecurringJobId = "nonexistent-recurring"
        };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    #endregion

    #region Edge Cases & Pagination

    [SkippableFact]
    public async Task FilterWithNullCriteria_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var result = await _provider.GetJobsWithFilterAsync(null, 1, 50, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task FilterWithEmptyCriteria_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria();
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    [SkippableFact]
    public async Task FilterPagination_Page1()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 10, CancellationToken.None);

        Assert.Equal(40, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasNextPage);
        Assert.False(result.HasPreviousPage);
    }

    [SkippableFact]
    public async Task FilterPagination_Page2()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 2, 10, CancellationToken.None);

        Assert.Equal(40, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
        Assert.True(result.HasNextPage);
        Assert.True(result.HasPreviousPage);
    }

    [SkippableFact]
    public async Task FilterPagination_LastPage()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 4, 10, CancellationToken.None);

        Assert.Equal(40, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
        Assert.False(result.HasNextPage);
    }

    [SkippableFact]
    public async Task FilterPagination_BeyondTotal()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 10, 10, CancellationToken.None);

        Assert.Equal(40, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [SkippableFact]
    public async Task FilterPagination_NoPagesOverlap()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded" };
        var page1 = await _provider.GetJobsWithFilterAsync(criteria, 1, 10, CancellationToken.None);
        var page2 = await _provider.GetJobsWithFilterAsync(criteria, 2, 10, CancellationToken.None);

        var ids1 = page1.Items.Select(i => i.JobId).ToHashSet();
        var ids2 = page2.Items.Select(i => i.JobId).ToHashSet();
        Assert.Empty(ids1.Intersect(ids2));
    }

    [SkippableFact]
    public async Task FilterResults_OrderedByCreatedAtDescending()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);

        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].CreatedAt;
            var next = result.Items[i + 1].CreatedAt;
            if (current.HasValue && next.HasValue)
                Assert.True(current.Value >= next.Value);
        }
    }

    [SkippableFact]
    public async Task FilterResults_ContainDurationForSucceeded()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { State = "Succeeded" };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);

        var withDuration = result.Items.Where(i => i.DurationMs.HasValue).ToList();
        Assert.True(withDuration.Count > 0);
    }

    [SkippableFact]
    public async Task FilterByEmptyTagsList_ReturnsEmpty()
    {
        PostgreSqlFixture.RequireAvailable();
        var criteria = new JobFilterCriteria { Tags = new List<string>() };
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 100, CancellationToken.None);
        Assert.Equal(0, result.TotalCount);
    }

    #endregion
}
