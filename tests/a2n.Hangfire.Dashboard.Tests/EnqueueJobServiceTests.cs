using System;
using System.Linq;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage.Monitoring;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

// Feature: job-builder, Task 15.5 — service tests for enqueue behavior.
//
// HangfireMonitorService.EnqueueJob creates a one-off background job from an EnqueueJobRequest,
// building typed Args from the Parameter_JSON via the same resolver/converter as the recurring
// path, then enqueuing onto the selected queue with BackgroundJobClient.Create(job,
// new EnqueuedState(queue)). These example-based xUnit tests exercise that behavior against a real
// Hangfire.InMemory storage and verify the outcome through the monitoring API the dashboard reads.
//
// _Requirements: 12.3_

/// <summary>
/// Uniquely-named public fixture whose methods are valid Job_Parameter targets resolvable by the
/// <see cref="JobMethodResolver"/> against the loaded test assembly. The methods are never invoked —
/// the resolver only reflects over them and the converter shapes their <c>Args</c>.
/// </summary>
public sealed class EnqueueServiceFixtureJob
{
    public void RunNoArgs() { }

    public void RunWithArgs(string label, int count) { }
}

/// <summary>
/// Service tests for <see cref="HangfireMonitorService.EnqueueJob"/> (Req 12.3), backed by real
/// <see cref="InMemoryStorage"/> and verified through the monitoring API.
/// </summary>
public class EnqueueJobServiceTests
{
    private static readonly string FixtureTypeName = typeof(EnqueueServiceFixtureJob).FullName;
    private const string NoArgsMethodName = nameof(EnqueueServiceFixtureJob.RunNoArgs);
    private const string WithArgsMethodName = nameof(EnqueueServiceFixtureJob.RunWithArgs);

    private static HangfireMonitorService CreateService(JobStorage storage) =>
        new HangfireMonitorService(
            storage,
            null,
            new DashboardUIOptions { IsReadOnly = false },
            new JobMethodResolver());

    private static EnqueueJobRequest Request(
        string methodName,
        string parameterJson,
        string queue) =>
        new EnqueueJobRequest(
            TypeName: FixtureTypeName,
            MethodName: methodName,
            ParameterJson: parameterJson,
            Queue: queue,
            IsCustomMethod: false);

    /// <summary>
    /// Returns true when the enqueued job list for <paramref name="queue"/> contains
    /// <paramref name="jobId"/>.
    /// </summary>
    private static bool EnqueuedJobsContains(JobStorage storage, string queue, string jobId)
    {
        var api = storage.GetMonitoringApi();
        // Page generously so the single job under test is always within the window.
        var jobs = api.EnqueuedJobs(queue, 0, 100);
        return jobs.Any(j => j.Key == jobId);
    }

    [Fact]
    public void EnqueueJob_ResolvableJob_CreatesJobOnSelectedQueueAndReturnsId()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);
        const string queue = "enqueue-selected-queue";

        var api = storage.GetMonitoringApi();
        var before = api.EnqueuedCount(queue);

        var result = service.EnqueueJob(Request(NoArgsMethodName, "[]", queue));

        Assert.True(result.Success, result.Error);
        Assert.False(string.IsNullOrEmpty(result.JobId));

        // The chosen queue gained exactly the one new job.
        var after = api.EnqueuedCount(queue);
        Assert.Equal(before + 1, after);

        // And the monitoring API reports the returned id on that queue.
        Assert.True(EnqueuedJobsContains(storage, queue, result.JobId));
    }

    [Fact]
    public void EnqueueJob_BlankQueue_LandsOnDefaultQueue()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var api = storage.GetMonitoringApi();
        var beforeDefault = api.EnqueuedCount("default");

        var result = service.EnqueueJob(Request(NoArgsMethodName, "[]", queue: "   "));

        Assert.True(result.Success, result.Error);
        Assert.False(string.IsNullOrEmpty(result.JobId));

        // Blank queue resolves to "default".
        var afterDefault = api.EnqueuedCount("default");
        Assert.Equal(beforeDefault + 1, afterDefault);
        Assert.True(EnqueuedJobsContains(storage, "default", result.JobId));
    }

    [Fact]
    public void EnqueueJob_WithTypedArgs_EnqueuesSuccessfullyWithConvertedArgs()
    {
        // Parity with recurring: typed args are built the same way for a method with parameters.
        var storage = new InMemoryStorage();
        var service = CreateService(storage);
        const string queue = "enqueue-typed-args";

        var result = service.EnqueueJob(
            Request(WithArgsMethodName, "[\"hello\", 5]", queue));

        Assert.True(result.Success, result.Error);
        Assert.False(string.IsNullOrEmpty(result.JobId));
        Assert.True(EnqueuedJobsContains(storage, queue, result.JobId));

        // The stored job carries the typed, positional arguments.
        var api = storage.GetMonitoringApi();
        var details = api.JobDetails(result.JobId);
        Assert.NotNull(details);
        Assert.Equal(WithArgsMethodName, details.Job.Method.Name);
        Assert.Equal(new object[] { "hello", 5 }, details.Job.Args.ToArray());
    }
}
