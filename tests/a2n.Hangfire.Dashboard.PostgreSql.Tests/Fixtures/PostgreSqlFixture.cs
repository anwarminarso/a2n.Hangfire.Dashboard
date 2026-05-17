using Dapper;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

/// <summary>
/// xUnit collection fixture that manages a unique PostgreSQL schema per test run.
/// 
/// Lifecycle:
/// 1. Generate unique schema name (e.g., "test_abc123def4")
/// 2. CREATE SCHEMA
/// 3. Let Hangfire auto-create tables via UsePostgreSqlStorage (PrepareSchemaIfNecessary)
/// 4. Seed controlled test data
/// 5. Tests run against this schema
/// 6. DROP SCHEMA CASCADE on dispose
/// </summary>
public class PostgreSqlFixture : IAsyncLifetime
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

    public PostgreSqlFixture()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        _connectionString = config.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("PostgreSql connection string not found in appsettings.json");

        // Generate unique schema name per test run
        SchemaName = "test_" + Guid.NewGuid().ToString("N")[..12];
    }

    public async Task InitializeAsync()
    {
        // 1. Create the schema
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE SCHEMA \"{SchemaName}\"");

        // 2. Let Hangfire create tables in our schema
        // This ensures table structure is 100% compatible
        GlobalConfiguration.Configuration.UsePostgreSqlStorage(opts =>
        {
            opts.UseNpgsqlConnection(_connectionString);
        }, new PostgreSqlStorageOptions
        {
            SchemaName = SchemaName,
            PrepareSchemaIfNecessary = true
        });

        // 3. Seed test data
        await TestDataSeeder.SeedAsync(_connectionString, SchemaName);
    }

    public async Task DisposeAsync()
    {
        // Drop the entire schema with all tables — clean slate
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{SchemaName}\" CASCADE");
        }
        catch
        {
            // Best effort cleanup — don't fail tests on cleanup errors
        }
    }
}

/// <summary>
/// xUnit collection definition to share the fixture across all test classes.
/// </summary>
[CollectionDefinition("PostgreSql")]
public class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
}
