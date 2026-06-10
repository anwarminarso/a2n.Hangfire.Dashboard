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
/// 2. CREATE SCHEMA
/// 3. Let Hangfire auto-create tables via UseSqlServerStorage (PrepareSchemaIfNecessary)
/// 4. Seed controlled test data
/// 5. Tests run against this schema
/// 6. DROP every object in the schema, then DROP SCHEMA on dispose
///
/// Requires a reachable SQL Server (connection string in appsettings.json). Tests that
/// depend on this fixture are skipped automatically when the server is unavailable.
/// </summary>
public class SqlServerFixture : IAsyncLifetime
{
    private readonly string _connectionString;

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
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Captured reason the fixture could not initialize (for skip messages / diagnostics).
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
        try
        {
            // 1. Create the schema (CREATE SCHEMA must be the only statement in its batch)
            await using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync($"IF SCHEMA_ID('{SchemaName}') IS NULL EXEC('CREATE SCHEMA [{SchemaName}]')");
            }

            // 2. Let Hangfire create its tables inside our schema.
            // This guarantees the structure matches what the provider queries against.
            GlobalConfiguration.Configuration.UseSqlServerStorage(
                _connectionString,
                new SqlServerStorageOptions
                {
                    SchemaName = SchemaName,
                    PrepareSchemaIfNecessary = true
                });

            // 3. Seed deterministic test data
            await TestDataSeeder.SeedAsync(_connectionString, SchemaName);

            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason = ex.Message;
        }
    }

    public async Task DisposeAsync()
    {
        if (!Available)
            return;

        // Best-effort cleanup: drop all objects in the schema, then the schema itself.
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(BuildDropSchemaScript(SchemaName));
        }
        catch
        {
            // Don't fail the test run on cleanup errors.
        }
    }

    /// <summary>
    /// SQL Server has no DROP SCHEMA ... CASCADE, so we drop foreign keys, tables, and other
    /// objects belonging to the schema before dropping the (now empty) schema.
    /// </summary>
    private static string BuildDropSchemaScript(string schema) => $@"
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
