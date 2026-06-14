using System;
using System.Linq;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage.Monitoring;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Task 4.6 — service tests for create-vs-update upsert behavior.
//
// HangfireMonitorService.CreateOrUpdateRecurringJob is an upsert by id: it CREATES a recurring job
// when no job with the request's id exists, and UPDATES the existing one (rather than creating a
// duplicate) when the id is already present. These example-based xUnit tests exercise that behavior
// against a real Hangfire.InMemory storage so the assertions observe what the dashboard would read
// back from a JobStorageConnection.
//
// _Requirements: 11.3_

/// <summary>
/// Uniquely-named public fixture whose methods are valid Job_Parameter targets resolvable by the
/// <see cref="JobMethodResolver"/> against the loaded test assembly. The methods are never invoked —
/// the resolver only reflects over them and the converter shapes their <c>Args</c>.
/// </summary>
public sealed class RecurringUpsertFixtureJob
{
    public void Process(string label, int count) { }
}

/// <summary>
/// Service tests for <see cref="HangfireMonitorService.CreateOrUpdateRecurringJob"/> create-vs-update
/// upsert semantics (Req 11.3), backed by real <see cref="InMemoryStorage"/>.
/// </summary>
public class RecurringUpsertServiceTests
{
    private static readonly string FixtureTypeName = typeof(RecurringUpsertFixtureJob).FullName;
    private const string FixtureMethodName = nameof(RecurringUpsertFixtureJob.Process);

    private static HangfireMonitorService CreateService(JobStorage storage)
    {
        var options = new DashboardUIOptions
        {
            IsReadOnly = false,
            EnableRecurringJobAdmin = true,
        };
        return new HangfireMonitorService(storage, null, options, new JobMethodResolver());
    }

    private static RecurringJobRequest Request(
        string jobId,
        string parameterJson,
        string cron,
        string queue = "default") =>
        new RecurringJobRequest(
            JobId: jobId,
            TypeName: FixtureTypeName,
            MethodName: FixtureMethodName,
            ParameterJson: parameterJson,
            Cron: cron,
            Queue: queue,
            TimeZoneId: null,
            IsCustomMethod: false);

    [Fact]
    public void CreateOrUpdateRecurringJob_WhenIdDoesNotExist_CreatesJob()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        // Pre-condition: no recurring jobs exist yet.
        Assert.Empty(service.GetRecurringJobs());

        var result = service.CreateOrUpdateRecurringJob(
            Request("upsert-create-job", "[\"hello\", 5]", "* * * * *"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("upsert-create-job", result.JobId);

        var jobs = service.GetRecurringJobs();
        var created = Assert.Single(jobs);
        Assert.Equal("upsert-create-job", created.Id);
        Assert.Equal("* * * * *", created.Cron);

        // The stored definition carries the typed, positional arguments.
        Assert.NotNull(created.Job);
        Assert.Equal(new object[] { "hello", 5 }, created.Job.Args.ToArray());
    }

    [Fact]
    public void CreateOrUpdateRecurringJob_WhenIdExists_UpdatesExistingInsteadOfDuplicating()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        // First call CREATES the job.
        var createResult = service.CreateOrUpdateRecurringJob(
            Request("upsert-update-job", "[\"first\", 1]", "* * * * *"));
        Assert.True(createResult.Success, createResult.Error);
        Assert.Single(service.GetRecurringJobs());

        // Second call with the SAME id UPDATES it (new cron + new args) rather than adding a duplicate.
        var updateResult = service.CreateOrUpdateRecurringJob(
            Request("upsert-update-job", "[\"second\", 42]", "0 0 * * *"));
        Assert.True(updateResult.Success, updateResult.Error);

        // Count stays exactly 1 — no duplicate created.
        var jobs = service.GetRecurringJobs();
        var updated = Assert.Single(jobs);
        Assert.Equal("upsert-update-job", updated.Id);

        // The stored definition reflects the update.
        Assert.Equal("0 0 * * *", updated.Cron);
        Assert.NotNull(updated.Job);
        Assert.Equal(new object[] { "second", 42 }, updated.Job.Args.ToArray());
    }

    [Fact]
    public void CreateOrUpdateRecurringJob_DistinctIds_CreateSeparateJobs()
    {
        // Guards the create path: distinct ids must NOT collapse into a single upsert target.
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        Assert.True(service.CreateOrUpdateRecurringJob(
            Request("upsert-distinct-a", "[\"a\", 1]", "* * * * *")).Success);
        Assert.True(service.CreateOrUpdateRecurringJob(
            Request("upsert-distinct-b", "[\"b\", 2]", "* * * * *")).Success);

        var jobs = service.GetRecurringJobs();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, j => j.Id == "upsert-distinct-a");
        Assert.Contains(jobs, j => j.Id == "upsert-distinct-b");
    }
}
