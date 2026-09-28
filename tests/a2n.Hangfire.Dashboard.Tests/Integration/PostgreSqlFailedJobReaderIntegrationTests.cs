using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using a2n.Hangfire.Dashboard.PostgreSql;
using Dapper;
using Hangfire.Common;
using Hangfire.Storage;
using Npgsql;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests.Integration;

/// <summary>
/// Regression tests for <see cref="PostgreSqlFailedJobReader"/>, covering the Failed Jobs page crash
/// <c>KeyNotFoundException: The given key 'FailedAt' was not present in the dictionary</c> thrown from
/// <c>PostgreSqlMonitoringApi.FailedJobs</c>.
///
/// <para><b>Why the monitoring API throws.</b> Hangfire.PostgreSql builds the state payload as a
/// <c>SafeDictionary</c> whose <c>new</c> indexer returns <c>default</c> for an absent key, but its
/// <c>GetJobs</c> declares the selector as <c>Func&lt;SqlJob, Job, Dictionary&lt;string, string&gt;, TDto&gt;</c>.
/// Member hiding is resolved against the static type, so the <c>FailedJobs</c> selector binds to
/// <see cref="Dictionary{TKey,TValue}"/>'s indexer and the guard never applies. Any failed job whose
/// state payload omits <c>FailedAt</c> takes down the whole page.</para>
///
/// <para><b>The payload is legal.</b> <c>IState.SerializeData</c> has no required keys — Hangfire's own
/// <c>FailedState</c> documents <c>ExceptionDetails</c> as "not required" — so an application that moves a
/// job to a state named <c>Failed</c> with only the exception fields produces exactly
/// <see cref="ProductionPayloadWithoutFailedAt"/>. The dashboard has to tolerate it rather than treat it
/// as corrupt data.</para>
///
/// <para>The DB-backed tests provision an isolated schema using the production column types of Hangfire
/// schema v23 (<c>jsonb</c> payloads, <c>timestamptz</c> timestamps) and drop it afterwards. They skip
/// (and pass) when <c>A2N_HANGFIRE_TEST_POSTGRES</c> is unset or the server is unreachable — see
/// <see cref="RecurringScheduleBucketsIntegrationSupport"/> for the connection-string variable and run
/// instructions.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Storage", "PostgreSql")]
public class PostgreSqlFailedJobReaderIntegrationTests
{
    /// <summary>
    /// A verbatim <c>state.data</c> payload from a database that hit the crash: the three exception
    /// fields are present and <c>FailedAt</c> is absent. Read in the declaration order of the
    /// <c>FailedJobDto</c> initializer, <c>ExceptionDetails</c>/<c>ExceptionMessage</c>/<c>ExceptionType</c>
    /// all resolve, which is why the exception names <c>FailedAt</c> specifically.
    /// </summary>
    private const string ProductionPayloadWithoutFailedAt =
        """{"ExceptionType": "System.Exception", "ExceptionDetails": "", "ExceptionMessage": "Stopped for exclusive token-stable dump"}""";

    /// <summary>Invocation data for a job type the dashboard host cannot load.</summary>
    private const string UnresolvableInvocationData =
        """{"Type": "Absent.Namespace.NoSuchJobs, Absent.Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "Method": "RunAsync", "Arguments": "[]", "ParameterTypes": "[]"}""";

    /// <summary>The <c>state.createdat</c> every seeded state row carries, and the fallback timestamp.</summary>
    private static readonly DateTime StateCreatedAtUtc = new(2026, 9, 28, 8, 10, 13, DateTimeKind.Utc);

    /// <summary>A stored <c>FailedAt</c> written as a millisecond timestamp, distinct from the fallback.</summary>
    private static readonly DateTime FailedAtMilliseconds = new(2026, 9, 28, 7, 59, 50, DateTimeKind.Utc);

    /// <summary>A stored <c>FailedAt</c> written in round-trip ISO-8601 form.</summary>
    private static readonly DateTime FailedAtIso = new(2026, 9, 27, 22, 15, 30, DateTimeKind.Utc);

    /// <summary>
    /// Pins the trigger: the production payload really does throw under the monitoring API's access
    /// pattern, so the reader is fixing a reachable crash and not a hypothetical one. Needs no database.
    /// </summary>
    [Fact]
    public void ProductionPayload_UnderPlainDictionaryIndexer_ThrowsOnFailedAt()
    {
        var stateData = SerializationHelper.Deserialize<Dictionary<string, string>>(ProductionPayloadWithoutFailedAt);

        // The three keys the selector reads before FailedAt all resolve...
        Assert.Equal("System.Exception", stateData["ExceptionType"]);
        Assert.Equal(string.Empty, stateData["ExceptionDetails"]);
        Assert.Equal("Stopped for exclusive token-stable dump", stateData["ExceptionMessage"]);

        // ...and the fourth is the one that brings the page down.
        var ex = Assert.Throws<KeyNotFoundException>(() => stateData["FailedAt"]);
        Assert.Contains("FailedAt", ex.Message);
    }

    /// <summary>
    /// The reader returns every failed job whose state payload is missing, empty, or lacking
    /// <c>FailedAt</c>, instead of throwing, and substitutes the state row's own <c>createdat</c> so the
    /// page still has a timestamp to show.
    /// </summary>
    [Fact]
    public Task GetFailedJobs_WithIncompleteStatePayloads_ReturnsRowsInsteadOfThrowing()
        => WithSeededSchemaAsync(reader =>
        {
            var page = reader.GetFailedJobs(0, 20);

            // Every failed job is listed, and only failed jobs (the Succeeded seed row is excluded).
            Assert.Equal(new[] { "13", "12", "11", "10", "9", "8", "7", "6", "5" }, page.Select(p => p.Key));

            // Missing FailedAt (the reported crash): exception fields survive, timestamp falls back.
            var missingFailedAt = page.Single(p => p.Key == "5").Value;
            Assert.Equal("System.Exception", missingFailedAt.ExceptionType);
            Assert.Equal(string.Empty, missingFailedAt.ExceptionDetails);
            Assert.Equal("Stopped for exclusive token-stable dump", missingFailedAt.ExceptionMessage);
            Assert.Equal("Stopped by dump agent: token race / exclusive dump resume", missingFailedAt.Reason);
            Assert.Equal(StateCreatedAtUtc, missingFailedAt.FailedAt);
            Assert.Equal(DateTimeKind.Utc, missingFailedAt.FailedAt.Value.Kind);
            Assert.True(missingFailedAt.InFailedState);

            // A null reason is not an error, just an absent one.
            Assert.Null(page.Single(p => p.Key == "6").Value.Reason);

            // state.data NULL, and an empty object: no exception fields, still listed and still timestamped.
            foreach (var key in new[] { "7", "8" })
            {
                var dto = page.Single(p => p.Key == key).Value;
                Assert.Null(dto.ExceptionType);
                Assert.Null(dto.ExceptionMessage);
                Assert.Null(dto.ExceptionDetails);
                Assert.Equal(StateCreatedAtUtc, dto.FailedAt);
            }

            // statename='Failed' with no current state row at all: no timestamp to fall back to.
            var orphaned = page.Single(p => p.Key == "11").Value;
            Assert.Null(orphaned.FailedAt);
            Assert.Null(orphaned.Reason);

            // No row is ever mapped to a null DTO, which the rollup collectors dereference directly.
            Assert.All(page, p => Assert.NotNull(p.Value));

            return Task.CompletedTask;
        });

    /// <summary>
    /// A complete payload is still read normally: the stored <c>FailedAt</c> wins over the fallback, in
    /// both formats Hangfire writes (millisecond timestamp and round-trip ISO-8601), and the lookup is
    /// case-insensitive as the monitoring API's <c>OrdinalIgnoreCase</c> dictionary is.
    /// </summary>
    [Fact]
    public Task GetFailedJobs_WithCompleteStatePayload_UsesStoredFailedAt()
        => WithSeededSchemaAsync(reader =>
        {
            var page = reader.GetFailedJobs(0, 20);

            // Millisecond-timestamp form, the default since compatibility level 1.7.0.
            var timestamped = page.Single(p => p.Key == "9").Value;
            Assert.Equal(FailedAtMilliseconds, timestamped.FailedAt);
            Assert.NotEqual(StateCreatedAtUtc, timestamped.FailedAt);
            Assert.Equal("System.InvalidOperationException", timestamped.ExceptionType);

            // Round-trip ISO-8601 form, under a differently-cased key.
            var isoLowerCaseKey = page.Single(p => p.Key == "10").Value;
            Assert.Equal(FailedAtIso, isoLowerCaseKey.FailedAt);
            Assert.Equal(DateTimeKind.Utc, isoLowerCaseKey.FailedAt.Value.Kind);

            return Task.CompletedTask;
        });

    /// <summary>
    /// A job type the host cannot load is reported rather than dropped: the row keeps its id, carries the
    /// raw <c>InvocationData</c> the page renders a best-effort call from, and surfaces the
    /// <see cref="JobLoadException"/> instead of letting it escape.
    /// </summary>
    [Fact]
    public Task GetFailedJobs_WithUnresolvableJobType_ReportsLoadExceptionAndKeepsRow()
        => WithSeededSchemaAsync(reader =>
        {
            var unresolvable = reader.GetFailedJobs(0, 20).Single(p => p.Key == "12").Value;

            Assert.Null(unresolvable.Job);
            Assert.NotNull(unresolvable.LoadException);
            Assert.NotNull(unresolvable.InvocationData);
            Assert.Equal("RunAsync", unresolvable.InvocationData.Method);
            Assert.Equal(StateCreatedAtUtc, unresolvable.FailedAt);

            // A resolvable type in the same page still deserializes to a Job.
            var resolvable = reader.GetFailedJobs(0, 20).Single(p => p.Key == "13").Value;
            Assert.NotNull(resolvable.Job);
            Assert.Null(resolvable.LoadException);
            Assert.Equal(nameof(FailedReaderTestJob.Run), resolvable.Job.Method.Name);

            return Task.CompletedTask;
        });

    /// <summary>
    /// Paging matches <c>PostgreSqlMonitoringApi.FailedJobs</c> — newest id first, <c>from</c> as the
    /// offset — because <see cref="a2n.Hangfire.Dashboard.Helpers.ReversibleOrder"/> and the rollup
    /// collectors compute offsets against that contract.
    /// </summary>
    [Fact]
    public Task GetFailedJobs_PagesNewestIdFirst()
        => WithSeededSchemaAsync(reader =>
        {
            Assert.Equal(new[] { "13", "12", "11" }, reader.GetFailedJobs(0, 3).Select(p => p.Key));
            Assert.Equal(new[] { "10", "9", "8" }, reader.GetFailedJobs(3, 3).Select(p => p.Key));
            Assert.Equal(new[] { "6", "5" }, reader.GetFailedJobs(7, 3).Select(p => p.Key));
            Assert.Empty(reader.GetFailedJobs(9, 3));

            // Degenerate ranges do not reach the database.
            Assert.Empty(reader.GetFailedJobs(0, 0));
            Assert.Empty(reader.GetFailedJobs(0, -1));

            return Task.CompletedTask;
        });

    /// <summary>
    /// One failed job to seed, expressed as the two rows the reader joins. <see cref="StateData"/> null
    /// means the <c>state.data</c> column is NULL; <see cref="HasState"/> false means <c>job.stateid</c>
    /// dangles and the LEFT JOIN finds nothing.
    /// </summary>
    private sealed record Seed(
        long JobId,
        string StateName,
        string InvocationData,
        string Reason,
        string StateData,
        bool HasState = true);

    private static IReadOnlyList<Seed> BuildSeed()
    {
        var resolvable = InvocationData
            .SerializeJob(Job.FromExpression(() => FailedReaderTestJob.Run()))
            .SerializePayload();

        return new List<Seed>
        {
            // 5, 6 — the reported crash, verbatim from a live database, with and without a reason.
            new(5, "Failed", resolvable, "Stopped by dump agent: token race / exclusive dump resume", ProductionPayloadWithoutFailedAt),
            new(6, "Failed", resolvable, null, ProductionPayloadWithoutFailedAt),

            // 7, 8 — no payload at all, and an empty object.
            new(7, "Failed", resolvable, null, null),
            new(8, "Failed", resolvable, null, "{}"),

            // 9, 10 — complete payloads: millisecond timestamp, and ISO-8601 under a lowercase key.
            new(9, "Failed", resolvable, "Job failed", $$"""
                {"FailedAt": "{{ToMillisecondTimestamp(FailedAtMilliseconds)}}", "ExceptionType": "System.InvalidOperationException", "ExceptionMessage": "boom", "ExceptionDetails": "System.InvalidOperationException: boom"}
                """),
            new(10, "Failed", resolvable, null, $$"""
                {"failedat": "{{FailedAtIso.ToString("O", CultureInfo.InvariantCulture)}}", "exceptiontype": "System.Exception"}
                """),

            // 11 — statename says Failed but job.stateid points at nothing.
            new(11, "Failed", resolvable, null, null, HasState: false),

            // 12, 13 — an unloadable job type, and a loadable one.
            new(12, "Failed", UnresolvableInvocationData, null, ProductionPayloadWithoutFailedAt),
            new(13, "Failed", resolvable, null, ProductionPayloadWithoutFailedAt),

            // 14 — a different state, which must not appear in the failed page.
            new(14, "Succeeded", resolvable, null, """{"SucceededAt": "1790582526299"}"""),
        };
    }

    private static string ToMillisecondTimestamp(DateTime utc)
        => ((long)(utc - DateTime.UnixEpoch).TotalMilliseconds).ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// Provisions an isolated schema with the production column types, seeds it, runs <paramref name="assert"/>
    /// against a reader bound to it, and drops the schema. Returns without running when no PostgreSQL is
    /// configured or reachable, so the suite still passes in CI.
    /// </summary>
    private static async Task WithSeededSchemaAsync(Func<PostgreSqlFailedJobReader, Task> assert)
    {
        var connectionString = RecurringScheduleBucketsIntegrationSupport.GetPostgresConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            return; // No DB configured — skip gracefully.

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
            // Hangfire schema v23 column types: jsonb payloads, timestamptz timestamps. The casts matter —
            // the reader reads these columns through ::text, which only works because they are jsonb here.
            await connection.ExecuteAsync($@"
CREATE SCHEMA ""{schema}"";
CREATE TABLE ""{schema}"".""job""(
    id BIGINT PRIMARY KEY,
    stateid BIGINT NULL,
    statename TEXT NULL,
    invocationdata JSONB NOT NULL,
    arguments JSONB NOT NULL,
    createdat TIMESTAMPTZ NOT NULL,
    expireat TIMESTAMPTZ NULL
);
CREATE TABLE ""{schema}"".""state""(
    id BIGINT PRIMARY KEY,
    jobid BIGINT NOT NULL,
    name TEXT NOT NULL,
    reason TEXT NULL,
    createdat TIMESTAMPTZ NOT NULL,
    data JSONB NULL
);");

            foreach (var seed in BuildSeed())
            {
                var stateId = seed.HasState ? seed.JobId + 1000 : seed.JobId + 9000;

                await connection.ExecuteAsync($@"
INSERT INTO ""{schema}"".""job"" (id, stateid, statename, invocationdata, arguments, createdat)
VALUES (@JobId, @StateId, @StateName, @InvocationData::jsonb, '[]'::jsonb, @CreatedAt);",
                    new
                    {
                        seed.JobId,
                        StateId = stateId,
                        seed.StateName,
                        seed.InvocationData,
                        CreatedAt = StateCreatedAtUtc.AddMinutes(-5),
                    });

                if (!seed.HasState)
                    continue;

                await connection.ExecuteAsync($@"
INSERT INTO ""{schema}"".""state"" (id, jobid, name, reason, createdat, data)
VALUES (@StateId, @JobId, @StateName, @Reason, @CreatedAt, @StateData::jsonb);",
                    new
                    {
                        StateId = stateId,
                        seed.JobId,
                        seed.StateName,
                        seed.Reason,
                        CreatedAt = StateCreatedAtUtc,
                        seed.StateData,
                    });
            }

            await assert(new PostgreSqlFailedJobReader(connectionString, schema));
        }
        finally
        {
            try
            {
                await connection.ExecuteAsync($@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;");
            }
            catch
            {
                // Best-effort cleanup.
            }

            await connection.DisposeAsync();
        }
    }
}

/// <summary>A job type the test assembly can resolve, so a successful deserialization has something to hit.</summary>
public static class FailedReaderTestJob
{
    public static void Run() { }
}
