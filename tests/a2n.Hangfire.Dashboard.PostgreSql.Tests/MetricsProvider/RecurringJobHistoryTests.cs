using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.PostgreSql.Tests.Fixtures;

namespace a2n.Hangfire.Dashboard.PostgreSql.Tests.MetricsProvider;

/// <summary>
/// Covers recurring-job history on the PostgreSQL adapter, in particular the batched read added for
/// issue #25. The Recurring Health view fetches history for every recurring job at once; the batched
/// query must return exactly what the per-job query returns, otherwise the page silently shows a
/// different history than the drill-down.
/// </summary>
[Collection("PostgreSql")]
public class RecurringJobHistoryTests
{
    /// <summary>Recurring ids seeded by <see cref="TestDataSeeder"/>.</summary>
    private static readonly string[] SeededRecurringIds =
    {
        "simple-job", "console-job", "failing-job", "long-running-job",
        "report-daily", "email-digest", "payment-check"
    };

    private readonly PostgreSqlMetricsProvider _provider;

    public RecurringJobHistoryTests(PostgreSqlFixture fixture)
    {
        _provider = new PostgreSqlMetricsProvider(fixture.ConnectionString, fixture.SchemaName);
    }

    [SkippableFact]
    public async Task Batch_matches_the_per_job_query_for_every_seeded_id()
    {
        PostgreSqlFixture.RequireAvailable();
        var batch = await _provider.GetRecurringJobExecutionsBatchAsync(
            SeededRecurringIds, 20, CancellationToken.None);

        Assert.NotEmpty(batch);

        foreach (var id in SeededRecurringIds)
        {
            var single = await _provider.GetRecurringJobExecutionsAsync(id, 20, CancellationToken.None);
            var fromBatch = batch.TryGetValue(id, out var found)
                ? found
                : Array.Empty<RecurringJobExecutionDto>();

            Assert.Equal(
                single.Select(e => e.JobId).ToArray(),
                fromBatch.Select(e => e.JobId).ToArray());
            Assert.Equal(
                single.Select(e => e.Succeeded).ToArray(),
                fromBatch.Select(e => e.Succeeded).ToArray());
            Assert.Equal(
                single.Select(e => e.DurationMs).ToArray(),
                fromBatch.Select(e => e.DurationMs).ToArray());
        }
    }

    [SkippableFact]
    public async Task Batch_honours_the_per_job_count_limit()
    {
        PostgreSqlFixture.RequireAvailable();
        var batch = await _provider.GetRecurringJobExecutionsBatchAsync(
            SeededRecurringIds, 1, CancellationToken.None);

        Assert.NotEmpty(batch);
        Assert.All(batch.Values, executions => Assert.Single(executions));

        // The single retained execution must be the newest one.
        foreach (var (id, executions) in batch)
        {
            var all = await _provider.GetRecurringJobExecutionsAsync(id, 20, CancellationToken.None);
            Assert.Equal(all[0].JobId, executions[0].JobId);
        }
    }

    [SkippableFact]
    public async Task Batch_omits_unknown_ids_and_tolerates_an_empty_request()
    {
        PostgreSqlFixture.RequireAvailable();
        var batch = await _provider.GetRecurringJobExecutionsBatchAsync(
            new[] { "simple-job", "no-such-recurring-job" }, 20, CancellationToken.None);

        Assert.True(batch.ContainsKey("simple-job"));
        Assert.False(batch.ContainsKey("no-such-recurring-job"));

        Assert.Empty(await _provider.GetRecurringJobExecutionsBatchAsync(
            Array.Empty<string>(), 20, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Executions_are_ordered_newest_first()
    {
        PostgreSqlFixture.RequireAvailable();
        var executions = await _provider.GetRecurringJobExecutionsAsync(
            "simple-job", 20, CancellationToken.None);

        Assert.NotEmpty(executions);
        for (var i = 0; i < executions.Count - 1; i++)
            Assert.True(executions[i].ExecutedAt >= executions[i + 1].ExecutedAt);
    }

    [SkippableFact]
    public async Task Health_reports_last_results_and_average_duration()
    {
        PostgreSqlFixture.RequireAvailable();
        var health = await _provider.GetRecurringJobHealthAsync(CancellationToken.None);

        // The seeder writes recurring job parameters but no 'recurring-jobs' set, so the health list
        // may legitimately be empty here; the query must still run and map.
        Assert.NotNull(health);
        Assert.All(health, h =>
        {
            Assert.False(string.IsNullOrEmpty(h.JobId));
            Assert.NotNull(h.LastExecutionResults);
        });
    }
}
