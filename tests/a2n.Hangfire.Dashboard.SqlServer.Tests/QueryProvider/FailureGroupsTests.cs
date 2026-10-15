using Dapper;
using Hangfire;
using Hangfire.Common;
using Hangfire.SqlServer;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Data.SqlClient;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.SqlServer.Tests.Fixtures;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.SqlServer.Tests.QueryProvider;

/// <summary>
/// Grouping failed jobs by exception (#53) against a live SQL Server storage. Jobs fail through
/// Hangfire's state machine, with and without <see cref="FailureFingerprintFilter"/>, so the stored
/// and the computed fingerprints come from the real code paths. Mirrors the PostgreSQL suite.
/// <para>
/// The fixture's 20 seeded failed jobs (no stored fingerprint) stay in place throughout, and every
/// test deletes the jobs it adds, since other tests count over the seed.
/// </para>
/// </summary>
[Collection("SqlServer")]
public class FailureGroupsTests
{
    private const int SeededFailedJobs = 20;

    private readonly SqlServerFixture _fixture;
    private readonly SqlServerStorage _storage;
    private readonly SqlServerQueryProvider _provider;
    private readonly List<string> _created = new();

    public FailureGroupsTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        if (!fixture.Available)
            return;
        _storage = new SqlServerStorage(fixture.ConnectionString, new SqlServerStorageOptions
        {
            SchemaName = fixture.SchemaName,
            PrepareSchemaIfNecessary = false,
        });
        _provider = new SqlServerQueryProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    public static class OrderJob
    {
        public static void Run() { }
    }

    private static readonly Job Job = Job.FromExpression(() => OrderJob.Run());

    // Every failure is thrown from here, so jobs failing with the same message have the same trace.
    private static FailedState Fail(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return new FailedState(ex);
        }
    }

    private void RequireAvailable()
        => Skip.IfNot(_fixture.Available, $"SQL Server not available: {_fixture.UnavailableReason}");

    // No AutomaticRetry, which would turn the Failed state into a scheduled retry.
    private string CreateWithFilter(IState state)
        => Track(new BackgroundJobClient(_storage, new JobFilterCollection { new FailureFingerprintFilter() }).Create(Job, state));

    private string CreateWithoutFilter(IState state)
        => Track(new BackgroundJobClient(_storage, new JobFilterCollection()).Create(Job, state));

    private string Track(string jobId)
    {
        _created.Add(jobId);
        return jobId;
    }

    private Task<FailureGroupsResult> GroupsAsync(int labelCount = FailureGroupsResult.DefaultLabelCount)
        => _provider.GetFailureGroupsAsync(labelCount, CancellationToken.None);

    [SkippableFact]
    public async Task FilteredAndUnfilteredFailures_LandInOneGroup_LabeledFromTheNewestJob()
    {
        RequireAvailable();
        try
        {
            var computed = CreateWithoutFilter(Fail("Order 17 is locked"));
            var stored = CreateWithFilter(Fail("Order 18 is locked"));
            using var connection = _storage.GetConnection();
            var key = connection.GetJobParameter(stored, FailureFingerprint.ParameterName);
            Assert.True(FailureFingerprint.IsCurrentVersion(key));

            var result = await GroupsAsync();

            Assert.True(result.UsesStoredFingerprints);
            var group = Assert.Single(result.Groups, g => g.Fingerprint == key);
            Assert.Equal(2, group.Count);
            Assert.Equal(1, group.StoredCount);
            Assert.Equal(1, group.ComputedCount);
            Assert.Equal(new[] { computed }, group.ComputedJobIds);
            Assert.Equal(stored, group.NewestJobId);

            // The newest job has a stored fingerprint, so its label was read back from storage; it
            // must describe the same failure the stored key stands for.
            var label = FailureFingerprint.Compute(connection.GetStateData(stored).Data, ReadStoredJobTypeName(stored));
            Assert.Equal(key, label.Fingerprint);
            Assert.True(group.IsLabeled);
            Assert.Equal(label.ExceptionType, group.ExceptionType);
            Assert.Equal(label.InnerExceptionType, group.InnerExceptionType);
            Assert.Equal("Order <n> is locked", group.NormalizedMessage);
            Assert.Equal(label.TopFrame, group.TopFrame);
            Assert.NotNull(group.LastFailedAt);

            // The seeded failures are fingerprinted on the fly alongside.
            Assert.Equal(SeededFailedJobs + 1, result.UnfingerprintedCount);
            Assert.Equal(result.UnfingerprintedCount, result.FallbackCount);
            Assert.False(result.IsPartial);
            Assert.Equal(SeededFailedJobs + 2, result.Groups.Sum(g => g.Count));
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task JobsInOtherStates_AreNotCounted()
    {
        RequireAvailable();
        try
        {
            var requeued = CreateWithFilter(Fail("Order 21 is locked"));
            using var connection = _storage.GetConnection();
            var key = connection.GetJobParameter(requeued, FailureFingerprint.ParameterName);
            // The parameter stays on a requeued job.
            new BackgroundJobClient(_storage, new JobFilterCollection()).Requeue(requeued);
            var succeeded = CreateWithoutFilter(new SucceededState(null, 0, 0));
            connection.SetJobParameter(succeeded, FailureFingerprint.ParameterName, key);

            var result = await GroupsAsync();

            Assert.DoesNotContain(result.Groups, g => g.Fingerprint == key);
            Assert.Equal(SeededFailedJobs, result.Groups.Sum(g => g.Count));
            Assert.Equal(SeededFailedJobs, result.UnfingerprintedCount);
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task StoredValuesOfAnotherVersionOrForm_AreRecomputed()
    {
        RequireAvailable();
        try
        {
            // SQL Server compares case-insensitively and ignores trailing spaces by default; the
            // grouping query must not.
            var notCurrent = new[]
            {
                "v0:0123456789abcdef",
                "v1:0123456789ABCDEF",
                "V1:0123456789abcdef",
                "v1:0123456789abcdef0",
                "v1:0123456789abcdef ",
                "v1:0123456789abcde",
            };
            using var connection = _storage.GetConnection();
            var ids = notCurrent.Select(value =>
            {
                var id = CreateWithoutFilter(Fail("Order 31 is locked"));
                connection.SetJobParameter(id, FailureFingerprint.ParameterName, value);
                return id;
            }).ToList();
            var expected = FailureFingerprint.Compute(connection.GetStateData(ids[0]).Data, ReadStoredJobTypeName(ids[0])).Fingerprint;

            var result = await GroupsAsync();

            Assert.DoesNotContain(result.Groups, g => notCurrent.Any(v => string.Equals(v.Trim(), g.Fingerprint, StringComparison.OrdinalIgnoreCase)));
            var group = Assert.Single(result.Groups, g => g.Fingerprint == expected);
            Assert.Equal(0, group.StoredCount);
            Assert.Equal(notCurrent.Length, group.ComputedCount);
            Assert.Equal(SeededFailedJobs + notCurrent.Length, result.UnfingerprintedCount);
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task OnlyTheLargestGroups_AreLabeled()
    {
        RequireAvailable();

        var result = await GroupsAsync(labelCount: 2);

        Assert.True(result.Groups.Count > 2);
        Assert.All(result.Groups.Take(2), g => Assert.True(g.IsLabeled));
        Assert.All(result.Groups.Skip(2), g =>
        {
            Assert.False(g.IsLabeled);
            Assert.Null(g.ExceptionType);
            Assert.Null(g.LastFailedAt);
        });
        Assert.Equal(result.Groups.OrderByDescending(g => g.Count).Select(g => g.Count), result.Groups.Select(g => g.Count));
    }

    [SkippableFact]
    public async Task GroupJobs_AreTheNewestStillFailed_StoredAndComputedAlike()
    {
        RequireAvailable();
        try
        {
            var ids = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                ids.Add(CreateWithoutFilter(Fail("Order 41 is locked")));
                ids.Add(CreateWithFilter(Fail("Order 42 is locked")));
            }
            var requeuedLater = CreateWithFilter(Fail("Order 43 is locked"));
            using var connection = _storage.GetConnection();
            var key = connection.GetJobParameter(requeuedLater, FailureFingerprint.ParameterName);
            var group = Assert.Single((await GroupsAsync()).Groups, g => g.Fingerprint == key);
            Assert.Equal(7, group.Count);

            new BackgroundJobClient(_storage, new JobFilterCollection()).Requeue(requeuedLater);
            var jobs = await _provider.GetFailureGroupJobsAsync(group, 4, CancellationToken.None);

            Assert.Equal(ids.AsEnumerable().Reverse().Take(4), jobs.Select(j => j.JobId));
            var newest = jobs[0];
            Assert.Equal("Failed", newest.State);
            Assert.Equal("System.InvalidOperationException", newest.ExceptionType);
            Assert.Equal("Order 42 is locked", newest.ExceptionMessage);
            Assert.Equal(ReadStoredJobTypeName(newest.JobId), newest.InvocationData.Type);
            Assert.Equal("[]", newest.InvocationData.Arguments);
            Assert.EndsWith("OrderJob.Run", newest.JobName);
            Assert.NotNull(newest.LastStateChange);
            Assert.NotNull(newest.CreatedAt);
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task StatesWithoutFailedAtOrExceptionFields_AreGroupedAsEmpty()
    {
        RequireAvailable();
        var stateTime = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc);
        try
        {
            var empty = InsertFailedJob("N'{}'", stateTime);
            var noData = InsertFailedJob("NULL", stateTime);

            var result = await GroupsAsync();

            var group = Assert.Single(result.Groups, g => g.Fingerprint == FailureFingerprint.Compute(null, null, null).Fingerprint);
            Assert.Equal(new[] { noData, empty }, group.ComputedJobIds);
            Assert.True(group.IsLabeled);
            Assert.Equal("", group.ExceptionType);
            Assert.Equal(stateTime, group.LastFailedAt);

            var jobs = await _provider.GetFailureGroupJobsAsync(group, 10, CancellationToken.None);
            Assert.Equal(new[] { noData, empty }, jobs.Select(j => j.JobId));
            Assert.All(jobs, j => Assert.Equal(stateTime, j.LastStateChange));
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task MoreUnfingerprintedJobsThanTheLimit_GivesAPartialResult_FromTheNewest()
    {
        RequireAvailable();
        const int storedJobs = 3;
        const int total = storedJobs + JobFilterCriteria.CountLimit + 5;
        await using var schema = await ThrowawaySchema.CreateAsync(_fixture.ConnectionString, total, storedJobs);
        var provider = new SqlServerQueryProvider(_fixture.ConnectionString, schema.Name);

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal(JobFilterCriteria.CountLimit + 5, result.UnfingerprintedCount);
        Assert.Equal(JobFilterCriteria.CountLimit, result.FallbackCount);
        var group = Assert.Single(result.Groups);
        Assert.Equal(schema.Fingerprint, group.Fingerprint);
        Assert.Equal(storedJobs, group.StoredCount);
        Assert.Equal(JobFilterCriteria.CountLimit, group.ComputedCount);
        Assert.Equal(total.ToString(), group.NewestJobId);
        Assert.Equal((total - JobFilterCriteria.CountLimit + 1).ToString(), group.ComputedJobIds[^1]);
        Assert.Equal("System.TimeoutException", group.ExceptionType);
    }

    [SkippableFact]
    public async Task GroupJobIds_AreEveryStoredAndComputedMemberStillFailed_NewestFirst()
    {
        RequireAvailable();
        try
        {
            var members = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                members.Add(CreateWithoutFilter(Fail("Order 51 is locked")));
                members.Add(CreateWithFilter(Fail("Order 52 is locked")));
            }
            using var connection = _storage.GetConnection();
            var key = connection.GetJobParameter(members[1], FailureFingerprint.ParameterName);
            // Not a current value, so the job counts as unfingerprinted and is a fallback member.
            var otherForm = CreateWithoutFilter(Fail("Order 53 is locked"));
            connection.SetJobParameter(otherForm, FailureFingerprint.ParameterName, key.ToUpperInvariant());
            members.Add(otherForm);
            var requeuedLater = CreateWithFilter(Fail("Order 54 is locked"));
            var succeeded = CreateWithoutFilter(new SucceededState(null, 0, 0));
            connection.SetJobParameter(succeeded, FailureFingerprint.ParameterName, key);
            var otherGroup = CreateWithFilter(Fail("Order 55 could not be found"));
            var group = Assert.Single((await GroupsAsync()).Groups, g => g.Fingerprint == key);
            Assert.Equal(4, group.StoredCount);
            Assert.Equal(4, group.ComputedCount);

            new BackgroundJobClient(_storage, new JobFilterCollection()).Requeue(requeuedLater);
            var ids = await _provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None);

            // Neither the seeded failures, the job in another group, the succeeded job carrying the key
            // nor the requeued one.
            Assert.Equal(members.AsEnumerable().Reverse(), ids);
            Assert.DoesNotContain(otherGroup, ids);
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task AJobCreatedFirstThatFailedLast_IsTheGroupsNewest()
    {
        RequireAvailable();
        try
        {
            // Scheduled first and failed after the others, as a delayed, retried or requeued job does.
            // Newest means the job that failed last, on this adapter as on the generic provider.
            var withFilter = new BackgroundJobClient(_storage, new JobFilterCollection { new FailureFingerprintFilter() });
            var withoutFilter = new BackgroundJobClient(_storage, new JobFilterCollection());
            var storedLast = CreateWithFilter(new ScheduledState(TimeSpan.FromHours(1)));
            var computedLast = CreateWithoutFilter(new ScheduledState(TimeSpan.FromHours(1)));
            var storedFirst = CreateWithFilter(Fail("Order 61 is locked"));
            var computedFirst = CreateWithoutFilter(Fail("Order 62 is locked"));
            withoutFilter.ChangeState(computedLast, Fail("Order 63 is locked"));
            withFilter.ChangeState(storedLast, Fail("Order 64 is locked"));

            using var connection = _storage.GetConnection();
            var key = connection.GetJobParameter(storedFirst, FailureFingerprint.ParameterName);
            Assert.Equal(key, connection.GetJobParameter(storedLast, FailureFingerprint.ParameterName));

            var group = Assert.Single((await GroupsAsync()).Groups, g => g.Fingerprint == key);

            Assert.Equal(storedLast, group.NewestJobId);
            Assert.Equal(FailureGrouping.ReadFailedAt(connection.GetStateData(storedLast).Data, null), group.LastFailedAt);
            Assert.Equal(new[] { computedLast, computedFirst }, group.ComputedJobIds);
            var failedLastFirst = new[] { storedLast, computedLast, computedFirst, storedFirst };
            var jobs = await _provider.GetFailureGroupJobsAsync(group, 10, CancellationToken.None);
            Assert.Equal(failedLastFirst, jobs.Select(j => j.JobId));
            Assert.Equal(failedLastFirst.Take(2), (await _provider.GetFailureGroupJobsAsync(group, 2, CancellationToken.None)).Select(j => j.JobId));
            Assert.Equal(failedLastFirst, await _provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None));
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task APartialResult_ReadsTheJobsThatFailedLast_NotTheLastCreated()
    {
        RequireAvailable();
        const int failedLast = 5;
        const int total = JobFilterCriteria.CountLimit + 10;
        // Jobs 1 to 5 were created first and failed after all the others.
        await using var schema = await ThrowawaySchema.CreateAsync(_fixture.ConnectionString, total, storedJobs: 0, failedLast);
        var provider = new SqlServerQueryProvider(_fixture.ConnectionString, schema.Name);

        var result = await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None);

        // The 1,000 read are jobs 1 to 5 and the newest 995 of the others; 6 to 15 weren't read.
        Assert.True(result.IsPartial);
        var group = Assert.Single(result.Groups);
        var read = Enumerable.Range(1, failedLast).Reverse()
            .Concat(Enumerable.Range(total - (JobFilterCriteria.CountLimit - failedLast) + 1, JobFilterCriteria.CountLimit - failedLast).Reverse())
            .Select(id => id.ToString())
            .ToList();
        Assert.Equal(read, group.ComputedJobIds);
        Assert.Equal("5", group.NewestJobId);
        Assert.Equal(read.Take(3), (await provider.GetFailureGroupJobsAsync(group, 3, CancellationToken.None)).Select(j => j.JobId));
        Assert.Equal(read, await provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None));
    }

    [SkippableFact]
    public async Task GroupJobIds_OfAPartialResult_AreTheStoredMembersAndTheFallbackMembersRead()
    {
        RequireAvailable();
        const int storedJobs = 3;
        const int total = storedJobs + JobFilterCriteria.CountLimit + 5;
        await using var schema = await ThrowawaySchema.CreateAsync(_fixture.ConnectionString, total, storedJobs);
        var provider = new SqlServerQueryProvider(_fixture.ConnectionString, schema.Name);
        var group = Assert.Single((await provider.GetFailureGroupsAsync(FailureGroupsResult.DefaultLabelCount, CancellationToken.None)).Groups);

        var ids = await provider.GetFailureGroupJobIdsAsync(group, CancellationToken.None);

        // Every stored member (the oldest jobs, 1 to 3) and the 1,000 newest unfingerprinted ones the
        // grouping read; jobs 4 to 8 weren't read, so they aren't included.
        var fallback = Enumerable.Range(total - JobFilterCriteria.CountLimit + 1, JobFilterCriteria.CountLimit).Reverse();
        Assert.Equal(fallback.Concat(new[] { 3, 2, 1 }).Select(id => id.ToString()), ids);
    }

    // The job type as the dashboard reads it: from the stored invocation data.
    private string ReadStoredJobTypeName(string jobId)
    {
        using var sql = new SqlConnection(_fixture.ConnectionString);
        var payload = sql.QuerySingle<string>(
            $"SELECT InvocationData FROM [{_fixture.SchemaName}].[Job] WHERE Id = @jobId",
            new { jobId = long.Parse(jobId) });
        return InvocationData.DeserializePayload(payload).Type;
    }

    // A Failed job whose state was written by something other than Hangfire's FailedState.
    private string InsertFailedJob(string stateDataSql, DateTime stateCreatedAt)
    {
        var schema = _fixture.SchemaName;
        using var sql = new SqlConnection(_fixture.ConnectionString);
        var jobId = sql.QuerySingle<long>($@"
DECLARE @JobId BIGINT, @StateId BIGINT;
INSERT INTO [{schema}].[Job] (InvocationData, Arguments, CreatedAt, StateName)
VALUES (N'{{""Type"":""Legacy.Job, Legacy"",""Method"":""Run"",""ParameterTypes"":""[]"",""Arguments"":null}}', N'[]', GETUTCDATE(), N'Failed');
SET @JobId = SCOPE_IDENTITY();
INSERT INTO [{schema}].[State] (JobId, Name, Reason, CreatedAt, Data)
VALUES (@JobId, N'Failed', NULL, @CreatedAt, {stateDataSql});
SET @StateId = SCOPE_IDENTITY();
UPDATE [{schema}].[Job] SET StateId = @StateId WHERE Id = @JobId;
SELECT @JobId;",
            new { CreatedAt = stateCreatedAt });
        return Track(jobId.ToString());
    }

    private void DeleteCreated()
    {
        if (_created.Count == 0)
            return;
        var schema = _fixture.SchemaName;
        using var sql = new SqlConnection(_fixture.ConnectionString);
        sql.Execute(
            $"DELETE FROM [{schema}].[JobQueue] WHERE JobId IN @Ids; " +
            $"DELETE FROM [{schema}].[JobParameter] WHERE JobId IN @Ids; " +
            $"DELETE FROM [{schema}].[State] WHERE JobId IN @Ids; " +
            $"DELETE FROM [{schema}].[Job] WHERE Id IN @Ids;",
            new { Ids = _created.Select(long.Parse).ToArray() });
        _created.Clear();
    }

    /// <summary>
    /// A schema of its own with <c>total</c> failed jobs (ids 1..total, all the same failure), the
    /// first <c>storedJobs</c> of them with the fingerprint stored, since the shared fixture's jobs are
    /// counted exactly by other tests. Jobs fail in the order they were created, except the first
    /// <c>failedLast</c>, which failed after all the others: their state ids come last.
    /// </summary>
    private sealed class ThrowawaySchema : IAsyncDisposable
    {
        private const string JobType = "Bulk.Job, Bulk";
        private const string StateData =
            @"{""FailedAt"":""2026-09-01T00:00:00.0000000Z"",""ExceptionType"":""System.TimeoutException""," +
            @"""ExceptionMessage"":""The operation has timed out."",""ExceptionDetails"":""System.TimeoutException: The operation has timed out.""}";

        private readonly string _connectionString;

        private ThrowawaySchema(string connectionString)
        {
            _connectionString = connectionString;
            Name = "test_groups_" + Guid.NewGuid().ToString("N")[..10];
            Fingerprint = FailureFingerprint.Compute(FailureGrouping.ReadStateData(StateData), JobType).Fingerprint;
        }

        public string Name { get; }

        public string Fingerprint { get; }

        public static async Task<ThrowawaySchema> CreateAsync(string connectionString, int total, int storedJobs, int failedLast = 0)
        {
            var schema = new ThrowawaySchema(connectionString);
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync($"EXEC('CREATE SCHEMA [{schema.Name}]')");
                SqlServerObjectsInstaller.Install(connection, schema.Name, true);
                await connection.ExecuteAsync($@"
SET IDENTITY_INSERT [{schema.Name}].[Job] ON;
WITH n AS (SELECT TOP (@Total) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT INTO [{schema.Name}].[Job] (Id, StateId, InvocationData, Arguments, CreatedAt, StateName)
SELECT g, CASE WHEN g <= @FailedLast THEN @Total + g ELSE g END, N'{{""Type"":""{JobType}"",""Method"":""Run"",""ParameterTypes"":""[]"",""Arguments"":null}}', N'[]', GETUTCDATE(), N'Failed' FROM n;
SET IDENTITY_INSERT [{schema.Name}].[Job] OFF;
SET IDENTITY_INSERT [{schema.Name}].[State] ON;
INSERT INTO [{schema.Name}].[State] (Id, JobId, Name, CreatedAt, Data)
SELECT StateId, Id, N'Failed', GETUTCDATE(), @StateData FROM [{schema.Name}].[Job];
SET IDENTITY_INSERT [{schema.Name}].[State] OFF;
INSERT INTO [{schema.Name}].[JobParameter] (JobId, Name, Value)
SELECT Id, @Name, @Fingerprint FROM [{schema.Name}].[Job] WHERE Id <= @Stored;",
                    new { Total = total, Stored = storedJobs, FailedLast = failedLast, StateData, Name = FailureFingerprint.ParameterName, schema.Fingerprint });
                return schema;
            }
            catch
            {
                await schema.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync(SqlServerFixture.BuildDropSchemaScript(Name));
            }
            catch
            {
                // Best effort, as in SqlServerFixture.
            }
        }
    }
}
