using Dapper;
using Hangfire;
using Hangfire.Common;
using Hangfire.SqlServer;
using Hangfire.States;
using Microsoft.Data.SqlClient;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.SqlServer.Tests.Fixtures;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.SqlServer.Tests.Storage;

/// <summary>
/// Runs <see cref="FailureFingerprintFilter"/> through Hangfire's state machine against a live SQL
/// Server storage. Hangfire.SqlServer 1.8 does not support setting job parameters inside a write
/// transaction, so this covers the filter's connection path. It also checks that the value lands in
/// <c>JobParameter.Value</c> as plain text a query can compare against, which grouping relies on.
/// </summary>
[Collection("SqlServer")]
public class FailureFingerprintFilterTests
{
    private readonly SqlServerFixture _fixture;

    public FailureFingerprintFilterTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public static class TestJob
    {
        public static void Run() { }
    }

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

    [SkippableFact]
    public void FailedJob_StoresThePlainFingerprint_KeepsItOnRequeue_AndOverwritesItOnTheNextFailure()
    {
        Skip.IfNot(_fixture.Available, $"SQL Server not available: {_fixture.UnavailableReason}");

        var storage = new SqlServerStorage(_fixture.ConnectionString, new SqlServerStorageOptions
        {
            SchemaName = _fixture.SchemaName,
            PrepareSchemaIfNecessary = false,
        });
        var client = new BackgroundJobClient(storage, new JobFilterCollection { new FailureFingerprintFilter() });
        var jobId = client.Create(() => TestJob.Run(), new EnqueuedState("fingerprint"));

        try
        {
            using var connection = storage.GetConnection();

            Assert.True(client.ChangeState(jobId, Fail("Order 42 is locked"), EnqueuedState.StateName));
            var first = connection.GetJobParameter(jobId, FailureFingerprint.ParameterName);
            Assert.True(FailureFingerprint.IsCurrentVersion(first));
            Assert.Equal(FailureFingerprint.Compute(connection.GetStateData(jobId).Data).Fingerprint, first);
            Assert.Equal(first, ReadRawParameter(jobId));

            Assert.True(client.ChangeState(jobId, new EnqueuedState("fingerprint"), FailedState.StateName));
            Assert.Equal(first, connection.GetJobParameter(jobId, FailureFingerprint.ParameterName));

            Assert.True(client.ChangeState(jobId, Fail("Payment gateway rejected the request"), EnqueuedState.StateName));
            var second = connection.GetJobParameter(jobId, FailureFingerprint.ParameterName);
            Assert.NotEqual(first, second);
            Assert.Equal(FailureFingerprint.Compute(connection.GetStateData(jobId).Data).Fingerprint, second);
        }
        finally
        {
            // Other tests in the collection assert on counts over the seeded jobs.
            DeleteJob(jobId);
        }
    }

    private string ReadRawParameter(string jobId)
    {
        using var sql = new SqlConnection(_fixture.ConnectionString);
        return sql.QuerySingle<string>(
            $"SELECT [Value] FROM [{_fixture.SchemaName}].[JobParameter] WHERE [JobId] = @jobId AND [Name] = @name",
            new { jobId = long.Parse(jobId), name = FailureFingerprint.ParameterName });
    }

    private void DeleteJob(string jobId)
    {
        var schema = _fixture.SchemaName;
        using var sql = new SqlConnection(_fixture.ConnectionString);
        sql.Execute(
            $"DELETE FROM [{schema}].[JobQueue] WHERE [JobId] = @jobId; " +
            $"DELETE FROM [{schema}].[JobParameter] WHERE [JobId] = @jobId; " +
            $"DELETE FROM [{schema}].[State] WHERE [JobId] = @jobId; " +
            $"DELETE FROM [{schema}].[Job] WHERE [Id] = @jobId;",
            new { jobId = long.Parse(jobId) });
    }
}
