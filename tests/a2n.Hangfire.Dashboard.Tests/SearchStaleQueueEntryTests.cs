using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Xunit;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// A job leaves its queue only when a worker fetches it, so a job that changes state while enqueued
/// is still listed by <c>IMonitoringApi.EnqueuedJobs</c>, with <c>InEnqueuedState</c> false. The
/// generic provider finds it once, in its actual state (#68).
/// </summary>
public class SearchStaleQueueEntryTests
{
    public static class SampleJobs
    {
        public static void Run() { }
    }

    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobClient _client;
    private readonly GenericQueryProvider _provider;

    public SearchStaleQueueEntryTests()
    {
        _client = new BackgroundJobClient(_storage, new JobFilterCollection());
        _provider = new GenericQueryProvider(_storage, new TagsDataReader(_storage));
    }

    // Enqueued, then moved to another state with no server running to fetch it.
    private (string Enqueued, string Succeeded, string Deleted) EnqueueAndChangeWhileQueued()
    {
        var enqueued = _client.Enqueue(() => SampleJobs.Run());
        var succeeded = _client.Enqueue(() => SampleJobs.Run());
        Assert.True(_client.ChangeState(succeeded, new SucceededState(null, 0, 0)));
        var deleted = _client.Enqueue(() => SampleJobs.Run());
        Assert.True(_client.Delete(deleted));

        // The precondition: both are still in the queue, no longer in Enqueued state.
        var queued = _storage.GetMonitoringApi().EnqueuedJobs(EnqueuedState.DefaultQueue, 0, 100);
        Assert.False(queued.Single(e => e.Key == succeeded).Value.InEnqueuedState);
        Assert.False(queued.Single(e => e.Key == deleted).Value.InEnqueuedState);

        return (enqueued, succeeded, deleted);
    }

    private static Dictionary<string, string> StateById(PagedResult<JobSummaryDto> result)
    {
        // Each job once; a duplicate key fails here.
        return result.Items.ToDictionary(i => i.JobId, i => i.State);
    }

    [Fact]
    public async Task SearchWithoutStateFilter_FindsEachJobOnce_InItsActualState()
    {
        var (enqueued, succeeded, deleted) = EnqueueAndChangeWhileQueued();

        var result = await _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { JobNamePattern = nameof(SampleJobs) }, 1, 50, CancellationToken.None);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(new Dictionary<string, string>
        {
            [enqueued] = "Enqueued",
            [succeeded] = "Succeeded",
            [deleted] = "Deleted",
        }, StateById(result));
    }

    [Fact]
    public async Task SearchForEnqueuedAndSucceeded_FindsTheSucceededJobOnce()
    {
        var (enqueued, succeeded, _) = EnqueueAndChangeWhileQueued();

        var result = await _provider.GetJobsWithFilterAsync(
            new JobFilterCriteria
            {
                JobNamePattern = nameof(SampleJobs),
                States = new List<string> { "Enqueued", "Succeeded" },
            }, 1, 50, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string>
        {
            [enqueued] = "Enqueued",
            [succeeded] = "Succeeded",
        }, StateById(result));
    }

    [Fact]
    public async Task EnqueuedState_ListsOnlyJobsStillEnqueued()
    {
        var (enqueued, _, _) = EnqueueAndChangeWhileQueued();

        var result = await _provider.GetJobsByStateAsync("Enqueued", 1, 50, CancellationToken.None);

        Assert.Equal(new[] { enqueued }, result.Items.Select(i => i.JobId));
        Assert.Equal(1, result.TotalCount);
    }
}
