using System.Data;
using Dapper;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace a2n.Hangfire.Dashboard.SqlServer.Tests.Fixtures;

/// <summary>
/// xUnit collection fixture that manages a unique SQL Server schema per test run.
///
/// Lifecycle:
/// 1. Generate unique schema name (e.g., "test_abc123def4")
/// 2. CREATE SCHEMA and install Hangfire's tables in it, under the suite's schema lock
/// 3. Point Hangfire at it via UseSqlServerStorage
/// 4. Seed controlled test data
/// 5. Tests run against this schema
/// 6. DROP every object in the schema, then DROP SCHEMA on dispose
///
/// Requires a reachable SQL Server (connection string in appsettings.json). Tests that
/// depend on this fixture are skipped automatically when the server can't be reached. Once it
/// can, a failure to prepare the schema fails every test in the collection instead.
/// </summary>
public class SqlServerFixture : IAsyncLifetime
{
    // Taken by every run of this suite around creating, installing and dropping a schema; see
    // CreateSchemaAsync. Session-owned, so it spans the install script's own transaction.
    private const string SchemaLockResource = "a2n.Hangfire.Dashboard.SqlServer.Tests:Schema";
    private const int SchemaLockTimeoutMs = 120_000;

    private readonly string _connectionString;
    private bool _reachable;

    /// <summary>
    /// The unique schema name for this test run.
    /// </summary>
    public string SchemaName { get; }

    /// <summary>
    /// Connection string for use in tests.
    /// </summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    /// True when a SQL Server instance was reachable and the schema/data was prepared.
    /// Tests should <c>Skip.IfNot(fixture.Available, ...)</c> to stay green on machines without SQL Server.
    /// When the server is reachable but preparing it fails, <see cref="InitializeAsync"/> throws
    /// instead, so the tests fail rather than skip.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Why the server couldn't be reached (for skip messages / diagnostics).
    /// </summary>
    public string UnavailableReason { get; private set; }

    public SqlServerFixture()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = config.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("SqlServer connection string not found in appsettings.json");

        // Generate unique schema name per test run
        SchemaName = "test_" + Guid.NewGuid().ToString("N")[..12];
    }

    public async Task InitializeAsync()
    {
        // Only a server that can't be reached skips the suite. Anything that fails after connecting
        // (the schema, Hangfire's tables, the seed) is thrown, so xUnit fails every test in the
        // collection instead of reporting them all as skipped, which reads as green.
        await using var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason = ex.Message;
            return;
        }
        _reachable = true;

        // 1. Create the schema and Hangfire's tables inside it.
        // This guarantees the structure matches what the provider queries against.
        await CreateSchemaAsync(connection, SchemaName);

        // 2. Point Hangfire at it. The tables are already there.
        GlobalConfiguration.Configuration.UseSqlServerStorage(
            _connectionString,
            new SqlServerStorageOptions
            {
                SchemaName = SchemaName,
                PrepareSchemaIfNecessary = false
            });

        // 3. Seed deterministic test data
        await TestDataSeeder.SeedAsync(_connectionString, SchemaName);

        Available = true;
    }

    public async Task DisposeAsync()
    {
        // Also when preparing the schema failed partway, so a failed run doesn't leave it behind.
        if (!_reachable)
            return;

        // Best-effort cleanup: drop all objects in the schema, then the schema itself.
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await DropSchemaAsync(connection, SchemaName);
        }
        catch
        {
            // Don't fail the test run on cleanup errors.
        }
    }

    /// <summary>
    /// Creates <paramref name="schema"/> and installs Hangfire's tables in it, one test run at a time.
    /// </summary>
    /// <remarks>
    /// <c>dotnet test</c> runs this suite for net8.0, net9.0 and net10.0 at the same time against one
    /// server. Hangfire's install script creates dozens of tables, constraints and indexes in one
    /// transaction, and installs into different schemas of the same database deadlock on SQL Server
    /// (error 1205). <c>SqlServerStorage</c> with <c>PrepareSchemaIfNecessary</c> retries three times,
    /// then only logs a warning, so a run went on with no tables and every test skipped. The install
    /// is therefore run directly, so its errors are thrown, and under an application lock that every
    /// run of this suite takes.
    /// </remarks>
    internal static Task CreateSchemaAsync(SqlConnection connection, string schema)
        => WithSchemaLockAsync(connection, async () =>
        {
            // CREATE SCHEMA must be the only statement in its batch.
            await connection.ExecuteAsync($"IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA [{schema}]')");
            SqlServerObjectsInstaller.Install(connection, schema, enableHeavyMigrations: true);
        });

    /// <summary>
    /// Drops <paramref name="schema"/> and everything in it, under the same lock as
    /// <see cref="CreateSchemaAsync"/>, since dropping is DDL too.
    /// </summary>
    internal static Task DropSchemaAsync(SqlConnection connection, string schema)
        => WithSchemaLockAsync(connection, () => connection.ExecuteAsync(BuildDropSchemaScript(schema)));

    private static async Task WithSchemaLockAsync(SqlConnection connection, Func<Task> action)
    {
        var result = await connection.QuerySingleAsync<int>(@"
DECLARE @Result INT;
EXEC @Result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @TimeoutMs;
SELECT @Result;",
            new { Resource = SchemaLockResource, TimeoutMs = SchemaLockTimeoutMs });

        // 0 or 1 when granted, at once or after waiting; negative when it timed out or was chosen as a
        // deadlock victim.
        if (result < 0)
            throw new InvalidOperationException($"Could not take the lock '{SchemaLockResource}' (sp_getapplock returned {result}).");

        try
        {
            await action();
        }
        finally
        {
            // A connection that broke ends its session, which releases the lock too.
            if (connection.State == ConnectionState.Open)
            {
                await connection.ExecuteAsync(
                    "EXEC sp_releaseapplock @Resource = @Resource, @LockOwner = 'Session';",
                    new { Resource = SchemaLockResource });
            }
        }
    }

    /// <summary>
    /// SQL Server has no DROP SCHEMA ... CASCADE, so we drop foreign keys, tables, and other
    /// objects belonging to the schema before dropping the (now empty) schema.
    /// </summary>
    internal static string BuildDropSchemaScript(string schema) => $@"
DECLARE @sql NVARCHAR(MAX) = N'';

-- Drop foreign keys first so table drops don't fail on dependencies
SELECT @sql += 'ALTER TABLE [{schema}].[' + OBJECT_NAME(fk.parent_object_id) + '] DROP CONSTRAINT [' + fk.name + '];' + CHAR(10)
FROM sys.foreign_keys fk
WHERE SCHEMA_NAME(fk.schema_id) = '{schema}';

-- Drop tables
SELECT @sql += 'DROP TABLE [{schema}].[' + t.name + '];' + CHAR(10)
FROM sys.tables t
WHERE SCHEMA_NAME(t.schema_id) = '{schema}';

EXEC sp_executesql @sql;

IF SCHEMA_ID('{schema}') IS NOT NULL EXEC('DROP SCHEMA [{schema}]');";
}

/// <summary>
/// xUnit collection definition to share the fixture across all test classes.
/// </summary>
[CollectionDefinition("SqlServer")]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
