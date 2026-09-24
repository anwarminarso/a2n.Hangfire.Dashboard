using Dapper;
using Hangfire.PostgreSql;
using Npgsql;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.QueryProvider;

/// <summary>
/// Search cost on large tables (Issue #45): the result count stops at
/// <see cref="JobFilterCriteria.CountLimit"/>, pages come newest-first by primary key, and the
/// argument predicate can use an optional pg_trgm index.
/// <para>
/// Each test builds its own throwaway schema, since the shared fixture's 100 seeded jobs are
/// counted exactly by other tests.
/// </para>
/// </summary>
[Collection("PostgreSql")]
public class SearchScanCostTests
{
    private const string Marker = "bulk-marker";
    private readonly string _connectionString;

    public SearchScanCostTests(PostgreSqlFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    [SkippableFact]
    public async Task MoreMatchesThanTheLimit_CountStopsAtTheLimit_AndPageIsNewestFirst()
    {
        PostgreSqlFixture.RequireAvailable();
        await using var schema = await ThrowawaySchema.CreateAsync(_connectionString, jobCount: JobFilterCriteria.CountLimit + 5);
        var provider = new PostgreSqlQueryProvider(_connectionString, schema.Name);

        var result = await provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { ArgumentsPattern = Marker }, page: 1, pageSize: 20, CancellationToken.None);

        Assert.Equal(JobFilterCriteria.CountLimit, result.TotalCount);
        Assert.True(result.TotalCountIsLowerBound);
        Assert.Equal(20, result.Items.Count);

        var ids = result.Items.Select(i => long.Parse(i.JobId)).ToList();
        Assert.Equal(JobFilterCriteria.CountLimit + 5, ids[0]);
        Assert.Equal(ids.OrderByDescending(i => i), ids);
    }

    [SkippableFact]
    public async Task FewerMatchesThanTheLimit_CountIsExact()
    {
        PostgreSqlFixture.RequireAvailable();
        await using var schema = await ThrowawaySchema.CreateAsync(_connectionString, jobCount: 30);
        var provider = new PostgreSqlQueryProvider(_connectionString, schema.Name);

        var result = await provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { ArgumentsPattern = Marker }, page: 1, pageSize: 20, CancellationToken.None);

        Assert.Equal(30, result.TotalCount);
        Assert.False(result.TotalCountIsLowerBound);
    }

    [SkippableFact]
    public async Task ArgumentPredicate_CanUseATrigramIndex()
    {
        PostgreSqlFixture.RequireAvailable();

        // The target frameworks' test runs share one database and run at the same time. pg_trgm may
        // be installed into this test's schema and is dropped with it, which would take any other
        // run's index along, so the whole test holds a session-level advisory lock. Declared first,
        // so the schema is dropped before the lock connection closes and releases it.
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await lockConnection.ExecuteAsync("SELECT pg_advisory_lock(45045)");

        await using var schema = await ThrowawaySchema.CreateAsync(_connectionString, jobCount: 50);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var opClass = await schema.TryInstallTrigramAsync(connection);
        Skip.If(opClass is null, "pg_trgm is not available on this server.");

        await connection.ExecuteAsync(
            $@"CREATE INDEX ix_job_arguments_trgm ON ""{schema.Name}"".job USING gin ((arguments::text) {opClass})");

        // The expression must match what the provider generates for ArgumentsPattern
        // (PgHelper.JobArgumentsSql) for the planner to consider the index.
        await using var tx = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("SET LOCAL enable_seqscan = off", transaction: tx);
        var plan = string.Join("\n", await connection.QueryAsync<string>(
            $@"EXPLAIN SELECT j.id FROM ""{schema.Name}"".job j WHERE j.arguments::text ILIKE '%{Marker}%'",
            transaction: tx));

        Assert.Contains("ix_job_arguments_trgm", plan);
    }

    private sealed class ThrowawaySchema : IAsyncDisposable
    {
        private readonly string _connectionString;

        private ThrowawaySchema(string connectionString, string name)
        {
            _connectionString = connectionString;
            Name = name;
        }

        public string Name { get; }

        public static async Task<ThrowawaySchema> CreateAsync(string connectionString, int jobCount)
        {
            var schema = new ThrowawaySchema(connectionString, "test_scan_" + Guid.NewGuid().ToString("N")[..10]);
            try
            {
                await schema.PopulateAsync(jobCount);
                return schema;
            }
            catch
            {
                // The caller never gets the schema to dispose, so drop it here.
                await schema.DisposeAsync();
                throw;
            }
        }

        private async Task PopulateAsync(int jobCount)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            PostgreSqlObjectsInstaller.Install(connection, Name);

            // Ids 1..jobCount, created one second apart, all carrying the marker in their arguments.
            await connection.ExecuteAsync($@"
INSERT INTO ""{Name}"".job (id, invocationdata, arguments, createdat, statename)
SELECT g,
       '{{""Type"":""Bulk.Job, Bulk"",""Method"":""Run"",""ParameterTypes"":""[]"",""Arguments"":null}}',
       ('[""\""{Marker}-' || g || '\""""]')::jsonb,
       now() - make_interval(secs => @JobCount - g),
       'Succeeded'
FROM generate_series(1, @JobCount) g", new { JobCount = jobCount });
        }

        /// <summary>
        /// Returns the qualified <c>gin_trgm_ops</c> operator class, installing pg_trgm into this
        /// schema if the database doesn't have it (it is dropped with the schema), or null when the
        /// extension can't be installed.
        /// </summary>
        public async Task<string> TryInstallTrigramAsync(NpgsqlConnection connection)
        {
            var existingSchema = await connection.QuerySingleOrDefaultAsync<string>(@"
SELECT n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
WHERE e.extname = 'pg_trgm'");
            if (existingSchema is not null)
                return $@"""{existingSchema}"".gin_trgm_ops";

            try
            {
                await connection.ExecuteAsync($@"CREATE EXTENSION pg_trgm WITH SCHEMA ""{Name}""");
                return $@"""{Name}"".gin_trgm_ops";
            }
            catch (PostgresException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync($@"DROP SCHEMA IF EXISTS ""{Name}"" CASCADE");
            }
            catch
            {
                // Best effort, as in PostgreSqlFixture.
            }
        }
    }
}
