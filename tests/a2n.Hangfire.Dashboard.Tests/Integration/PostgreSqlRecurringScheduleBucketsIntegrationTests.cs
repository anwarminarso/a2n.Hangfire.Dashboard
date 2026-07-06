using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql;
using Dapper;
using Npgsql;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="PostgreSqlMetricsProvider.GetRecurringScheduleBucketsAsync"/>
/// (task 13.7) against a real PostgreSQL server, validating Requirement 7.3 (per-bucket fire/failure
/// counts and duration stats for recurring-originated executions) and Requirement 7.7 (ad-hoc
/// executions are excluded).
///
/// The test provisions an isolated schema containing only the columns the query touches, seeds the
/// shared representative dataset, runs the provider, and drops the schema afterward. It skips (and
/// passes) when <c>A2N_HANGFIRE_TEST_POSTGRES</c> is unset or the server is unreachable — see
/// <see cref="RecurringScheduleBucketsIntegrationSupport"/> for run instructions.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Storage", "PostgreSql")]
public class PostgreSqlRecurringScheduleBucketsIntegrationTests
{
    [Fact]
    public async Task GetRecurringScheduleBucketsAsync_CountsRecurringOnly_AndExcludesAdHoc()
    {
        var connectionString = RecurringScheduleBucketsIntegrationSupport.GetPostgresEffectiveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            return; // No DB configured — skip gracefully so the suite still passes in CI.

        NpgsqlConnection connection;
        try
        {
            connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
        }
        catch (Exception)
        {
            return; // Server unreachable — skip gracefully.
        }

        var schema = RecurringScheduleBucketsIntegrationSupport.NewSchemaName();
        try
        {
            await CreateSchemaAndTablesAsync(connection, schema);
            await SeedAsync(connection, schema);

            var provider = new PostgreSqlMetricsProvider(connectionString, schema);
            var buckets = await provider.GetRecurringScheduleBucketsAsync(
                RecurringScheduleBucketsIntegrationSupport.From,
                RecurringScheduleBucketsIntegrationSupport.To,
                CancellationToken.None);

            AssertBuckets(buckets);
        }
        finally
        {
            await DropSchemaAsync(connection, schema);
            await connection.DisposeAsync();
        }
    }

    private static void AssertBuckets(System.Collections.Generic.IReadOnlyList<HistoricalScheduleBucket> buckets)
    {
        var expected = RecurringScheduleBucketsIntegrationSupport.ExpectedBuckets();

        // Req 7.7: ad-hoc executions are excluded — only the two recurring buckets exist.
        Assert.Equal(expected.Count, buckets.Count);
        Assert.DoesNotContain(buckets, b => b.Queue == "emails");

        foreach (var exp in expected)
        {
            var actual = Assert.Single(buckets, b =>
                b.Queue == exp.Queue && b.DayIndex == exp.DayIndex && b.Hour == exp.Hour);

            // Req 7.3: fire count >= 0, failure count in [0, fireCount], duration min/avg/max.
            Assert.True(actual.FireCount >= 0);
            Assert.Equal(exp.FireCount, actual.FireCount);
            Assert.InRange(actual.FailureCount, 0, actual.FireCount);
            Assert.Equal(exp.FailureCount, actual.FailureCount);
            Assert.Equal(exp.MinMs, actual.MinMs, 3);
            Assert.Equal(exp.AvgMs, actual.AvgMs, 3);
            Assert.Equal(exp.MaxMs, actual.MaxMs, 3);
            Assert.InRange(actual.P95Ms, actual.MinMs, actual.MaxMs);
        }

        // Req 7.7 (explicit): the ad-hoc fire in the critical/Mon/09 bucket did NOT inflate it.
        var critical = buckets.Single(b => b.Queue == "critical" && b.DayIndex == 0 && b.Hour == 9);
        Assert.Equal(2, critical.FireCount); // 2 recurring, not 3 (ad-hoc excluded)
        Assert.Equal(9999d, critical.MaxMs, 3); // the 9999ms ad-hoc duration was not aggregated
    }

    private static async Task CreateSchemaAndTablesAsync(NpgsqlConnection connection, string schema)
    {
        await connection.ExecuteAsync($@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""job""(
    id BIGINT PRIMARY KEY,
    stateid BIGINT NULL,
    statename TEXT NULL,
    invocationdata TEXT NULL,
    arguments TEXT NULL,
    createdat TIMESTAMPTZ NOT NULL,
    expireat TIMESTAMPTZ NULL
);
CREATE TABLE ""{schema}"".""state""(
    id BIGINT PRIMARY KEY,
    jobid BIGINT NOT NULL,
    name TEXT NOT NULL,
    reason TEXT NULL,
    createdat TIMESTAMPTZ NOT NULL,
    data TEXT NULL
);
CREATE TABLE ""{schema}"".""jobparameter""(
    id BIGSERIAL PRIMARY KEY,
    jobid BIGINT NOT NULL,
    name TEXT NOT NULL,
    value TEXT NULL
);");
    }

    private static async Task SeedAsync(NpgsqlConnection connection, string schema)
    {
        foreach (var e in RecurringScheduleBucketsIntegrationSupport.BuildSeed())
        {
            await connection.ExecuteAsync(
                $@"INSERT INTO ""{schema}"".""job"" (id, stateid, statename, createdat) VALUES (@Id, @StateId, @StateName, @CreatedAt);",
                new { Id = e.JobId, StateId = e.StateId, e.StateName, CreatedAt = e.CreatedAtUtc });

            await connection.ExecuteAsync(
                $@"INSERT INTO ""{schema}"".""state"" (id, jobid, name, createdat, data) VALUES (@Id, @JobId, @Name, @CreatedAt, @Data);",
                new
                {
                    Id = e.StateId,
                    e.JobId,
                    Name = e.StateName,
                    CreatedAt = e.CreatedAtUtc,
                    Data = $"{{\"PerformanceDuration\":\"{e.DurationMs}\"}}"
                });

            await connection.ExecuteAsync(
                $@"INSERT INTO ""{schema}"".""jobparameter"" (jobid, name, value) VALUES (@JobId, 'Job.Queue', @Value);",
                new { e.JobId, Value = e.Queue });

            if (e.RecurringJobId != null)
            {
                await connection.ExecuteAsync(
                    $@"INSERT INTO ""{schema}"".""jobparameter"" (jobid, name, value) VALUES (@JobId, 'RecurringJobId', @Value);",
                    new { e.JobId, Value = e.RecurringJobId });
            }
        }
    }

    private static async Task DropSchemaAsync(NpgsqlConnection connection, string schema)
    {
        try
        {
            await connection.ExecuteAsync($@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;");
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
