using Dapper;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using Hangfire.Storage;
using Npgsql;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.Storage;

/// <summary>
/// Runs <see cref="FailureFingerprintFilter"/> through Hangfire's state machine against a live
/// PostgreSQL storage. Hangfire.PostgreSql 1.20 does not support setting job parameters inside a
/// write transaction, so this covers the filter's connection path. It also checks that the value
/// lands in <c>jobparameter.value</c> as plain text a query can compare against, which grouping
/// relies on.
/// </summary>
[Collection("PostgreSql")]
public class FailureFingerprintFilterTests
{
    private readonly PostgreSqlFixture _fixture;

    public FailureFingerprintFilterTests(PostgreSqlFixture fixture)
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
        PostgreSqlFixture.RequireAvailable();

        var options = new PostgreSqlStorageOptions
        {
            SchemaName = _fixture.SchemaName,
            PrepareSchemaIfNecessary = false,
        };
        var storage = new PostgreSqlStorage(
            new global::Hangfire.PostgreSql.Factories.NpgsqlConnectionFactory(_fixture.ConnectionString, options),
            options);
        var client = new BackgroundJobClient(storage, new JobFilterCollection { new FailureFingerprintFilter() });
        var jobId = client.Create(() => TestJob.Run(), new EnqueuedState("fingerprint"));

        try
        {
            using var connection = storage.GetConnection();
            var jobTypeName = ReadStoredJobTypeName(jobId);

            Assert.True(client.ChangeState(jobId, Fail("Order 42 is locked"), EnqueuedState.StateName));
            var first = connection.GetJobParameter(jobId, FailureFingerprint.ParameterName);
            Assert.True(FailureFingerprint.IsCurrentVersion(first));
            Assert.Equal(FailureFingerprint.Compute(connection.GetStateData(jobId).Data, jobTypeName).Fingerprint, first);
            Assert.Equal(first, ReadRawParameter(jobId));

            Assert.True(client.ChangeState(jobId, new EnqueuedState("fingerprint"), FailedState.StateName));
            Assert.Equal(first, connection.GetJobParameter(jobId, FailureFingerprint.ParameterName));

            Assert.True(client.ChangeState(jobId, Fail("Payment gateway rejected the request"), EnqueuedState.StateName));
            var second = connection.GetJobParameter(jobId, FailureFingerprint.ParameterName);
            Assert.NotEqual(first, second);
            Assert.Equal(FailureFingerprint.Compute(connection.GetStateData(jobId).Data, jobTypeName).Fingerprint, second);
        }
        finally
        {
            // Other tests in the collection assert on counts over the seeded jobs.
            DeleteJob(jobId);
        }
    }

    // The job type as the dashboard's fallback reads it: from the stored invocation data. (Hangfire.PostgreSql's
    // GetJobData leaves JobData.InvocationData unset, so the column is the one source every storage has.)
    private string ReadStoredJobTypeName(string jobId)
    {
        using var sql = new NpgsqlConnection(_fixture.ConnectionString);
        var payload = sql.QuerySingle<string>(
            $"SELECT invocationdata::text FROM \"{_fixture.SchemaName}\".job WHERE id = @jobId",
            new { jobId = long.Parse(jobId) });
        return InvocationData.DeserializePayload(payload).Type;
    }

    private string ReadRawParameter(string jobId)
    {
        using var sql = new NpgsqlConnection(_fixture.ConnectionString);
        return sql.QuerySingle<string>(
            $"SELECT value FROM \"{_fixture.SchemaName}\".jobparameter WHERE jobid = @jobId AND name = @name",
            new { jobId = long.Parse(jobId), name = FailureFingerprint.ParameterName });
    }

    private void DeleteJob(string jobId)
    {
        var schema = _fixture.SchemaName;
        using var sql = new NpgsqlConnection(_fixture.ConnectionString);
        sql.Execute(
            $"DELETE FROM \"{schema}\".jobqueue WHERE jobid = @jobId; " +
            $"DELETE FROM \"{schema}\".jobparameter WHERE jobid = @jobId; " +
            $"DELETE FROM \"{schema}\".state WHERE jobid = @jobId; " +
            $"DELETE FROM \"{schema}\".job WHERE id = @jobId;",
            new { jobId = long.Parse(jobId) });
    }
}
