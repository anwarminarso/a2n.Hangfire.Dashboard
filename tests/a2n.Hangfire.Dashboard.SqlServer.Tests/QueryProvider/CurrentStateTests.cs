using Dapper;
using Hangfire;
using Hangfire.Common;
using Hangfire.SqlServer;
using Hangfire.States;
using Microsoft.Data.SqlClient;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.SqlServer.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.SqlServer.Tests.QueryProvider;

/// <summary>
/// The job lists, search and the recurring executions read each job's current state, joined on
/// <c>State</c>'s whole key (<c>JobId</c>, <c>Id</c>) so SQL Server can seek it (#84). The seeded jobs
/// have a single state each, so these tests add one that went from Enqueued to Succeeded, then to
/// Failed: each query has to return it once, with the Failed state's columns.
/// </summary>
[Collection("SqlServer")]
public class CurrentStateTests : IAsyncLifetime
{
    private const string Tag = "current-state-84";
    private const string RecurringJobId = "current-state-84";
    private const string StaleQueue = "stale_queue_84";
    private const string Message = "Current state 84 is the failure";

    private readonly SqlServerFixture _fixture;
    private readonly SqlServerStorage _storage;
    private readonly SqlServerQueryProvider _queries;
    private readonly SqlServerMetricsProvider _metrics;
    private string _jobId;
    private DateTime _failedAt;

    public CurrentStateTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        if (!fixture.Available)
            return;
        _storage = new SqlServerStorage(fixture.ConnectionString, new SqlServerStorageOptions
        {
            SchemaName = fixture.SchemaName,
            PrepareSchemaIfNecessary = false,
        });
        _queries = new SqlServerQueryProvider(fixture.ConnectionString, fixture.SchemaName);
        _metrics = new SqlServerMetricsProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    public static class ReportJob
    {
        public static void Run() { }
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
            return;

        // No AutomaticRetry, which would turn the Failed state into a scheduled retry.
        var client = new BackgroundJobClient(_storage, new JobFilterCollection());
        _jobId = client.Create(Job.FromExpression(() => ReportJob.Run()), new EnqueuedState(StaleQueue));
        // A previous Succeeded state too, since the recurring executions only read Succeeded and Failed.
        client.ChangeState(_jobId, new SucceededState(null, 0, 0));
        try
        {
            throw new InvalidOperationException(Message);
        }
        catch (Exception ex)
        {
            client.ChangeState(_jobId, new FailedState(ex));
        }

        using (var connection = _storage.GetConnection())
            connection.SetJobParameter(_jobId, "RecurringJobId", SerializationHelper.Serialize(RecurringJobId));

        var schema = _fixture.SchemaName;
        await using var sql = new SqlConnection(_fixture.ConnectionString);
        await sql.ExecuteAsync(
            $"INSERT INTO [{schema}].[Set] ([Key], Value, Score) VALUES (@Key, @Value, 0.0)",
            new { Key = $"tags:{Tag}", Value = _jobId });

        // History is newest first: [0] is the current, Failed state.
        var history = _storage.GetMonitoringApi().JobDetails(_jobId).History;
        Assert.Equal(
            new[] { FailedState.StateName, SucceededState.StateName, EnqueuedState.StateName },
            history.Select(h => h.StateName));
        _failedAt = history[0].CreatedAt;
    }

    public async Task DisposeAsync()
    {
        if (_jobId == null)
            return;
        var schema = _fixture.SchemaName;
        await using var sql = new SqlConnection(_fixture.ConnectionString);
        await sql.ExecuteAsync(
            $"DELETE FROM [{schema}].[Set] WHERE [Key] = @Key; " +
            $"DELETE FROM [{schema}].[JobQueue] WHERE JobId = @Id; " +
            $"DELETE FROM [{schema}].[JobParameter] WHERE JobId = @Id; " +
            $"DELETE FROM [{schema}].[State] WHERE JobId = @Id; " +
            $"DELETE FROM [{schema}].[Job] WHERE Id = @Id;",
            new { Key = $"tags:{Tag}", Id = long.Parse(_jobId) });
    }

    private void RequireAvailable()
        => Skip.IfNot(_fixture.Available, $"SQL Server not available: {_fixture.UnavailableReason}");

    private JobSummaryDto Single(PagedResult<JobSummaryDto> result)
    {
        var row = Assert.Single(result.Items, j => j.JobId == _jobId);
        Assert.Equal(FailedState.StateName, row.State);
        Assert.Equal(typeof(InvalidOperationException).FullName, row.ExceptionType);
        Assert.Equal(Message, row.ExceptionMessage);
        Assert.Equal(_failedAt, row.LastStateChange);
        return row;
    }

    [SkippableFact]
    public async Task JobsByState_ReadTheCurrentState()
    {
        RequireAvailable();
        Single(await _queries.GetJobsByStateAsync(FailedState.StateName, 1, 50, CancellationToken.None));

        var succeeded = await _queries.GetJobsByStateAsync(SucceededState.StateName, 1, 50, CancellationToken.None);
        Assert.DoesNotContain(succeeded.Items, j => j.JobId == _jobId);
    }

    [SkippableFact]
    public async Task JobsByTag_ReadTheCurrentState()
    {
        RequireAvailable();
        var result = await _queries.GetJobsByTagAsync(Tag, 1, 50, CancellationToken.None);
        Assert.Equal(1, result.TotalCount);
        Single(result);
    }

    [SkippableFact]
    public async Task Search_FiltersOnTheCurrentState()
    {
        RequireAvailable();
        var byException = await _queries.GetJobsWithFilterAsync(
            new JobFilterCriteria { ExceptionPattern = Message }, 1, 50, CancellationToken.None);
        Assert.Equal(1, byException.TotalCount);
        Single(byException);

        var byTagAndState = await _queries.GetJobsWithFilterAsync(
            new JobFilterCriteria { State = FailedState.StateName, Tags = new List<string> { Tag } }, 1, 50, CancellationToken.None);
        Assert.Equal(1, byTagAndState.TotalCount);
        Single(byTagAndState);
    }

    [SkippableFact]
    public async Task ContentSearch_ReadsOnlyTheCurrentStatesData()
    {
        RequireAvailable();
        var current = await _queries.GetJobsWithFilterAsync(
            new JobFilterCriteria { ContentPattern = Message, SearchStackTrace = true }, 1, 50, CancellationToken.None);
        Single(current);

        // The queue name is only in the Enqueued state's data, which is no longer the current state.
        var previous = await _queries.GetJobsWithFilterAsync(
            new JobFilterCriteria { ContentPattern = StaleQueue, SearchStackTrace = true }, 1, 50, CancellationToken.None);
        Assert.DoesNotContain(previous.Items, j => j.JobId == _jobId);
    }

    [SkippableFact]
    public async Task RecurringExecutions_ReadTheCurrentState()
    {
        RequireAvailable();
        var single = await _metrics.GetRecurringJobExecutionsAsync(RecurringJobId, 10, CancellationToken.None);
        var execution = Assert.Single(single);
        Assert.Equal(_jobId, execution.JobId);
        Assert.False(execution.Succeeded);
        Assert.Equal(new DateTimeOffset(_failedAt, TimeSpan.Zero), execution.ExecutedAt);

        var batch = await _metrics.GetRecurringJobExecutionsBatchAsync(new[] { RecurringJobId }, 10, CancellationToken.None);
        Assert.Equal(_jobId, Assert.Single(batch[RecurringJobId]).JobId);
    }
}
