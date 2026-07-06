using System;
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Tests.Integration;

/// <summary>
/// Shared support for the recurring-schedule-buckets DB integration tests (task 13.7,
/// Requirements 7.3 and 7.7). Provides:
/// <list type="bullet">
///   <item>Connection-string discovery from environment variables, with graceful skip when absent.</item>
///   <item>A single representative seed dataset reused by the SQL Server and PostgreSQL tests so
///   both adapters are validated against identical data and identical expectations.</item>
/// </list>
///
/// <para><b>How to run these tests against a live database</b></para>
/// <para>
/// The tests skip (and pass) when their connection-string environment variable is unset or the
/// database is unreachable, so the suite still builds and passes in CI without a live database.
/// To exercise them, point the variables at an empty/disposable database — each test creates and
/// drops its own isolated schema, so no Hangfire installation is required:
/// </para>
/// <code>
/// # SQL Server
/// setx A2N_HANGFIRE_TEST_SQLSERVER "Server=localhost;Database=hangfire_test;User Id=sa;Password=...;TrustServerCertificate=True"
///
/// # PostgreSQL
/// setx A2N_HANGFIRE_TEST_POSTGRES  "Host=localhost;Database=hangfire_test;Username=postgres;Password=..."
/// </code>
/// </summary>
internal static class RecurringScheduleBucketsIntegrationSupport
{
    public const string SqlServerConnectionStringVariable = "A2N_HANGFIRE_TEST_SQLSERVER";
    public const string PostgresConnectionStringVariable = "A2N_HANGFIRE_TEST_POSTGRES";

    public static string GetSqlServerConnectionString()
        => Environment.GetEnvironmentVariable(SqlServerConnectionStringVariable);

    public static string GetPostgresConnectionString()
        => Environment.GetEnvironmentVariable(PostgresConnectionStringVariable);

    /// <summary>
    /// Returns the configured PostgreSQL connection string with the session time zone pinned to UTC
    /// (via the libpq <c>options</c> startup parameter). The adapter's query buckets executions with
    /// <c>EXTRACT(DOW/HOUR FROM s.createdat)</c> on a <c>timestamptz</c> column, whose result depends
    /// on the session time zone — pinning it to UTC makes the test deterministic on any server.
    /// Returns null when no connection string is configured.
    /// </summary>
    public static string GetPostgresEffectiveConnectionString()
    {
        var raw = GetPostgresConnectionString();
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        if (raw.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0)
            return raw; // Respect any caller-provided startup options.

        var trimmed = raw.TrimEnd();
        if (!trimmed.EndsWith(";", StringComparison.Ordinal))
            trimmed += ";";
        return trimmed + "Options=-c timezone=UTC";
    }

    /// <summary>
    /// Generates a fresh, valid SQL identifier for an isolated test schema. The adapter validates
    /// identifiers against <c>^[a-zA-Z_][a-zA-Z0-9_]*$</c>, so the name starts with a letter and
    /// contains only hex digits afterwards.
    /// </summary>
    public static string NewSchemaName()
        => "hftest_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The aggregation window: Monday 2024-01-01 00:00 UTC through the following Monday
    /// 2024-01-08 00:00 UTC (half-open). 2024-01-01 is a Monday, so day index 0 = Monday.
    /// </summary>
    public static readonly DateTimeOffset From = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset To = new(2024, 1, 8, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One Hangfire execution row to seed. <see cref="RecurringJobId"/> being non-null marks the
    /// execution as recurring-originated (counted); null marks it ad-hoc (must be excluded, Req 7.7).
    /// </summary>
    public sealed record SeedExecution(
        long JobId,
        long StateId,
        string Queue,
        string RecurringJobId,
        string StateName,
        DateTime CreatedAtUtc,
        long DurationMs);

    /// <summary>
    /// Representative dataset spanning multiple queues / days / hours, including recurring
    /// executions that form two buckets plus ad-hoc and out-of-window executions that must be
    /// excluded entirely.
    /// </summary>
    public static IReadOnlyList<SeedExecution> BuildSeed() => new List<SeedExecution>
    {
        // ── Bucket A: queue "critical", Monday (day 0) hour 9 — two recurring fires, no failures.
        //    Durations {1000, 3000} → min 1000, avg 2000, max 3000.
        new(JobId: 1, StateId: 101, Queue: "critical", RecurringJobId: "recur-a",
            StateName: "Succeeded", CreatedAtUtc: new DateTime(2024, 1, 1, 9, 15, 0, DateTimeKind.Utc), DurationMs: 1000),
        new(JobId: 2, StateId: 102, Queue: "critical", RecurringJobId: "recur-a",
            StateName: "Succeeded", CreatedAtUtc: new DateTime(2024, 1, 1, 9, 45, 0, DateTimeKind.Utc), DurationMs: 3000),

        // ── Bucket B: queue "default", Wednesday (day 2) hour 14 — one failed + one succeeded fire.
        //    Durations {500, 1500} → min 500, avg 1000, max 1500; failureCount 1 of 2.
        new(JobId: 3, StateId: 103, Queue: "default", RecurringJobId: "recur-b",
            StateName: "Failed", CreatedAtUtc: new DateTime(2024, 1, 3, 14, 30, 0, DateTimeKind.Utc), DurationMs: 500),
        new(JobId: 4, StateId: 104, Queue: "default", RecurringJobId: "recur-b",
            StateName: "Succeeded", CreatedAtUtc: new DateTime(2024, 1, 3, 14, 50, 0, DateTimeKind.Utc), DurationMs: 1500),

        // ── Ad-hoc execution in the SAME bucket as A (critical / Mon / hour 9) but NO RecurringJobId.
        //    Must be excluded (Req 7.7): bucket A must report fireCount 2, not 3, and not inflate duration.
        new(JobId: 5, StateId: 105, Queue: "critical", RecurringJobId: null,
            StateName: "Succeeded", CreatedAtUtc: new DateTime(2024, 1, 1, 9, 20, 0, DateTimeKind.Utc), DurationMs: 9999),

        // ── Ad-hoc execution in its own queue/time (emails / Fri / hour 11). Must not appear at all.
        new(JobId: 6, StateId: 106, Queue: "emails", RecurringJobId: null,
            StateName: "Succeeded", CreatedAtUtc: new DateTime(2024, 1, 5, 11, 0, 0, DateTimeKind.Utc), DurationMs: 2000),

        // ── Recurring execution OUTSIDE the window (the day before "from"). Must be excluded by the range.
        new(JobId: 7, StateId: 107, Queue: "critical", RecurringJobId: "recur-a",
            StateName: "Succeeded", CreatedAtUtc: new DateTime(2023, 12, 31, 9, 0, 0, DateTimeKind.Utc), DurationMs: 7777),
    };

    /// <summary>Expected bucket totals keyed by (queue, dayIndex, hour) after running the query.</summary>
    public sealed record ExpectedBucket(
        string Queue, int DayIndex, int Hour,
        long FireCount, long FailureCount, double MinMs, double AvgMs, double MaxMs);

    public static IReadOnlyList<ExpectedBucket> ExpectedBuckets() => new List<ExpectedBucket>
    {
        new("critical", 0, 9, FireCount: 2, FailureCount: 0, MinMs: 1000, AvgMs: 2000, MaxMs: 3000),
        new("default", 2, 14, FireCount: 2, FailureCount: 1, MinMs: 500, AvgMs: 1000, MaxMs: 1500),
    };
}
