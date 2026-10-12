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
/// The Exception filter on Search (#53) against a live SQL Server storage: jobs are matched by the
/// fingerprint the real <see cref="FailureFingerprintFilter"/> stored on them, in any state. Mirrors
/// the PostgreSQL suite.
/// <para>
/// The fixture's 20 seeded failed jobs (no stored fingerprint) stay in place throughout, and every
/// test deletes the jobs it adds, since other tests count over the seed.
/// </para>
/// </summary>
[Collection("SqlServer")]
public class SearchByExceptionTests
{
    private const int FirstSeededFailedJob = 41;
    private const int LastSeededFailedJob = 60;

    private readonly SqlServerFixture _fixture;
    private readonly SqlServerStorage _storage;
    private readonly SqlServerQueryProvider _provider;
    private readonly List<string> _created = new();

    public SearchByExceptionTests(SqlServerFixture fixture)
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

    public static class InvoiceJob
    {
        public static void Run() { }
    }

    private static readonly Job Order = Job.FromExpression(() => OrderJob.Run());
    private static readonly Job Invoice = Job.FromExpression(() => InvoiceJob.Run());

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
    private BackgroundJobClient WithFilter => new(_storage, new JobFilterCollection { new FailureFingerprintFilter() });

    private BackgroundJobClient WithoutFilter => new(_storage, new JobFilterCollection());

    private string Track(string jobId)
    {
        _created.Add(jobId);
        return jobId;
    }

    private string StoredFingerprint(string jobId)
    {
        using var connection = _storage.GetConnection();
        return connection.GetJobParameter(jobId, FailureFingerprint.ParameterName);
    }

    private void SetParameter(string jobId, string value)
    {
        using var connection = _storage.GetConnection();
        connection.SetJobParameter(jobId, FailureFingerprint.ParameterName, value);
    }

    private async Task<List<string>> SearchIds(JobFilterCriteria criteria)
    {
        var result = await _provider.GetJobsWithFilterAsync(criteria, 1, 50, CancellationToken.None);
        return result.Items.Select(j => j.JobId).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static List<string> Sorted(params string[] ids) => ids.OrderBy(id => id, StringComparer.Ordinal).ToList();

    [SkippableFact]
    public async Task StoredFingerprints_MatchInFailedSucceededAndDeleted()
    {
        RequireAvailable();
        try
        {
            var failed = Track(WithFilter.Create(Order, Fail("Order 1 is locked")));
            var key = StoredFingerprint(failed);
            Assert.True(FailureFingerprint.IsCurrentVersion(key));

            // Requeued after failing, then succeeded; the parameter stays on the job.
            var succeeded = Track(WithFilter.Create(Order, Fail("Order 2 is locked")));
            WithoutFilter.Requeue(succeeded);
            WithoutFilter.ChangeState(succeeded, new SucceededState(null, 0, 0));
            var deleted = Track(WithFilter.Create(Order, Fail("Order 3 is locked")));
            WithoutFilter.Delete(deleted);
            var otherException = Track(WithFilter.Create(Order, Fail("Order 4 could not be found")));

            var ids = await SearchIds(new JobFilterCriteria { FailureFingerprint = key });

            Assert.Equal(Sorted(failed, succeeded, deleted), ids);
            Assert.DoesNotContain(otherException, ids);
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task TheFilter_CombinesWithStateAndTheOtherFilters()
    {
        RequireAvailable();
        try
        {
            var failedOrder = Track(WithFilter.Create(Order, Fail("Order 1 is locked")));
            var key = StoredFingerprint(failedOrder);
            var succeededOrder = Track(WithFilter.Create(Order, Fail("Order 2 is locked")));
            WithoutFilter.ChangeState(succeededOrder, new SucceededState(null, 0, 0));
            // Another job type, given this key by hand, to isolate the name filter.
            var failedInvoice = Track(WithoutFilter.Create(Invoice, Fail("Order 3 is locked")));
            SetParameter(failedInvoice, key);

            Assert.Equal(Sorted(failedOrder, failedInvoice),
                await SearchIds(new JobFilterCriteria { FailureFingerprint = key, States = new() { "Failed" } }));
            Assert.Equal(Sorted(succeededOrder),
                await SearchIds(new JobFilterCriteria { FailureFingerprint = key, State = "Succeeded" }));
            Assert.Equal(Sorted(failedOrder, succeededOrder),
                await SearchIds(new JobFilterCriteria { FailureFingerprint = key, JobNamePattern = nameof(OrderJob) }));
            Assert.Empty(await SearchIds(new JobFilterCriteria { FailureFingerprint = key, DateFrom = DateTimeOffset.UtcNow.AddDays(1) }));
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task JobsWithoutTheParameter_IncludingTheSeededFailures_AreIgnored()
    {
        RequireAvailable();
        try
        {
            var fingerprinted = Track(WithFilter.Create(Order, Fail("Order 1 is locked")));
            var key = StoredFingerprint(fingerprinted);
            // The same failure without the filter: only the Failed page's fallback knows it.
            var unfingerprinted = Track(WithoutFilter.Create(Order, Fail("Order 2 is locked")));

            var ids = await SearchIds(new JobFilterCriteria { FailureFingerprint = key, State = "Failed" });

            Assert.Equal(Sorted(fingerprinted), ids);
            Assert.DoesNotContain(unfingerprinted, ids);

            // A seeded failure's own fingerprint, computed as the Failed page does, finds nothing:
            // Search only matches stored fingerprints.
            var seeded = FirstSeededFailedJob.ToString();
            using var connection = _storage.GetConnection();
            var seededState = connection.GetStateData(seeded);
            Assert.Equal("Failed", seededState.Name);
            var seededKey = FailureFingerprint.Compute(seededState.Data, ReadStoredJobTypeName(seeded)).Fingerprint;
            Assert.Empty(await SearchIds(new JobFilterCriteria { FailureFingerprint = seededKey }));
            Assert.DoesNotContain(ids, id => long.Parse(id) is >= FirstSeededFailedJob and <= LastSeededFailedJob);
        }
        finally
        {
            DeleteCreated();
        }
    }

    [SkippableFact]
    public async Task NonCurrentValues_AreIgnored_StoredOrSearched()
    {
        RequireAvailable();
        try
        {
            var current = Track(WithFilter.Create(Order, Fail("Order 1 is locked")));
            var key = StoredFingerprint(current);
            // SQL Server compares case-insensitively and ignores trailing spaces by default; the
            // filter must not.
            var upperCase = Track(WithoutFilter.Create(Order, Fail("Order 2 is locked")));
            SetParameter(upperCase, key.ToUpperInvariant());
            var padded = Track(WithoutFilter.Create(Order, Fail("Order 3 is locked")));
            SetParameter(padded, key + " ");

            Assert.Equal(Sorted(current), await SearchIds(new JobFilterCriteria { FailureFingerprint = key }));

            // A searched value that isn't a current fingerprint isn't applied: alone it is no
            // criterion, and next to another filter that filter decides.
            var bad = new JobFilterCriteria { FailureFingerprint = key.ToUpperInvariant() };
            Assert.False(bad.HasAnyCriteria());
            var failedOnly = await _provider.GetJobsWithFilterAsync(new JobFilterCriteria { State = "Failed" }, 1, 20, CancellationToken.None);
            var withBad = await _provider.GetJobsWithFilterAsync(new JobFilterCriteria { State = "Failed", FailureFingerprint = key + " " }, 1, 20, CancellationToken.None);
            Assert.Equal(failedOnly.TotalCount, withBad.TotalCount);
        }
        finally
        {
            DeleteCreated();
        }
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
}
