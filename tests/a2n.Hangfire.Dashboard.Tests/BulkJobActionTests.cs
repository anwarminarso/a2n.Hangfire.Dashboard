using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Batched requeue and delete with an expected state (#53), as the Failed page's group actions use
/// them, on Hangfire.InMemory with a real audit log.
/// </summary>
public class BulkJobActionTests
{
    private const string Fingerprint = "v1:0123456789abcdef";

    private static readonly Job OrderJob = Job.FromExpression(() => FailureGroupingTests.OrderJobs.Run());

    private readonly JobStorage _storage = new InMemoryStorage();
    private readonly DashboardUIOptions _options = new();
    private readonly AuditLogService _audit;
    private readonly HangfireMonitorService _monitor;
    // No filters: the global AutomaticRetry would turn the Failed state into a scheduled retry.
    private readonly BackgroundJobClient _client;

    public BulkJobActionTests()
    {
        _audit = new AuditLogService(_storage, _options, httpContext: null);
        _monitor = new HangfireMonitorService(_storage, _audit, _options);
        _client = new BackgroundJobClient(_storage, new JobFilterCollection());
    }

    private string CreateFailed()
        => _client.Create(OrderJob, new FailedState(new InvalidOperationException("Order 7 is locked")));

    private string StateOf(string jobId)
    {
        using var connection = _storage.GetConnection();
        return connection.GetStateData(jobId).Name;
    }

    private IReadOnlyList<AuditLogEntry> AuditEntries()
        => _audit.Query(new AuditLogFilter(), 0, 1000);

    private static Dictionary<string, string> GroupMetadata() => new() { ["fingerprint"] = Fingerprint };

    [Fact]
    public void RequeueJobs_FromFailed_LeavesAJobThatHasLeftFailedAlone()
    {
        var stillFailed = CreateFailed();
        var succeededSince = CreateFailed();
        // Between the snapshot of the ids and the action, one of the jobs succeeded on a retry.
        _client.ChangeState(succeededSince, new SucceededState(null, 0, 0));

        var requeued = _monitor.RequeueJobs(new[] { stillFailed, succeededSince }, FailedState.StateName);

        Assert.Equal(1, requeued);
        Assert.Equal(EnqueuedState.StateName, StateOf(stillFailed));
        Assert.Equal(SucceededState.StateName, StateOf(succeededSince));
        var entry = Assert.Single(AuditEntries());
        Assert.Equal(AuditAction.JobsRequeuedBatch, entry.Action);
        Assert.Equal("2", entry.Metadata["requested"]);
        Assert.Equal("1", entry.Metadata["succeeded"]);
    }

    [Fact]
    public void DeleteJobs_FromFailed_LeavesAJobThatHasLeftFailedAlone()
    {
        var stillFailed = CreateFailed();
        var requeuedSince = CreateFailed();
        _client.Requeue(requeuedSince);

        var deleted = _monitor.DeleteJobs(new[] { stillFailed, requeuedSince }, FailedState.StateName);

        Assert.Equal(1, deleted);
        Assert.Equal(DeletedState.StateName, StateOf(stillFailed));
        Assert.Equal(EnqueuedState.StateName, StateOf(requeuedSince));
        var entry = Assert.Single(AuditEntries());
        Assert.Equal(AuditAction.JobsDeletedBatch, entry.Action);
        Assert.Equal("2", entry.Metadata["requested"]);
        Assert.Equal("1", entry.Metadata["succeeded"]);
    }

    [Fact]
    public void RequeueAndDeleteJobs_WithoutFromState_ChangeJobsInAnyState_AsBefore()
    {
        var succeeded = _client.Create(OrderJob, new SucceededState(null, 0, 0));
        var scheduled = _client.Create(OrderJob, new ScheduledState(TimeSpan.FromHours(1)));

        Assert.Equal(1, _monitor.RequeueJobs(new[] { succeeded }));
        Assert.Equal(1, _monitor.DeleteJobs(new[] { scheduled }));

        Assert.Equal(EnqueuedState.StateName, StateOf(succeeded));
        Assert.Equal(DeletedState.StateName, StateOf(scheduled));
        Assert.All(AuditEntries(), e => Assert.Equal(new[] { "requested", "succeeded" }, e.Metadata.Keys.OrderBy(k => k)));
    }

    [Fact]
    public void AuditMetadata_IsAddedToTheEntry_AndCannotReplaceTheCounts()
    {
        var jobId = CreateFailed();
        var metadata = new Dictionary<string, string>
        {
            ["fingerprint"] = Fingerprint,
            ["requested"] = "999",
            ["succeeded"] = "999",
        };

        _monitor.DeleteJobs(new[] { jobId }, FailedState.StateName, metadata);

        var entry = Assert.Single(AuditEntries());
        Assert.Equal(Fingerprint, entry.Metadata["fingerprint"]);
        Assert.Equal("1", entry.Metadata["requested"]);
        Assert.Equal("1", entry.Metadata["succeeded"]);
    }

    [Fact]
    public async Task RequeueInBatches_1234Jobs_RunsAsThreeBatches_WithAnAuditEntryEach()
    {
        var ids = Enumerable.Range(0, 1234).Select(_ => CreateFailed()).ToList();
        var reports = new List<BulkJobActionResult>();

        var result = await _monitor.RequeueJobsInBatchesAsync(
            ids, FailedState.StateName, GroupMetadata(), new SyncProgress(reports.Add));

        Assert.Equal(new BulkJobActionResult(1234, 1234, 1234, Cancelled: false), result);
        Assert.Equal(new[] { 500, 1000, 1234 }, reports.Select(r => r.Processed));
        Assert.All(ids, id => Assert.Equal(EnqueuedState.StateName, StateOf(id)));

        var entries = AuditEntries();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(AuditAction.JobsRequeuedBatch, e.Action);
            Assert.Equal(Fingerprint, e.Metadata["fingerprint"]);
        });
        Assert.Equal(new[] { 234, 500, 500 }, entries.Select(e => int.Parse(e.Metadata["requested"])).OrderBy(c => c));
        Assert.Equal(1234, entries.Sum(e => int.Parse(e.Metadata["succeeded"])));
    }

    [Fact]
    public async Task DeleteInBatches_CountsJobsThatLeftFailedAsSkipped()
    {
        var ids = Enumerable.Range(0, 600).Select(_ => CreateFailed()).ToList();
        _client.ChangeState(ids[3], new SucceededState(null, 0, 0));
        _client.Requeue(ids[550]);

        var result = await _monitor.DeleteJobsInBatchesAsync(ids, FailedState.StateName, GroupMetadata());

        Assert.Equal(600, result.Processed);
        Assert.Equal(598, result.Succeeded);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(SucceededState.StateName, StateOf(ids[3]));
        Assert.Equal(EnqueuedState.StateName, StateOf(ids[550]));
        Assert.Equal(DeletedState.StateName, StateOf(ids[599]));
        // One skipped job in each batch.
        Assert.Equal(new[] { 99, 499 }, AuditEntries().Select(e => int.Parse(e.Metadata["succeeded"])).OrderBy(c => c));
    }

    [Fact]
    public async Task Batches_StopAfterTheBatchInProgress_WhenCancelled()
    {
        var ids = Enumerable.Range(0, 1234).Select(_ => CreateFailed()).ToList();
        using var cts = new CancellationTokenSource();

        // Cancelled while the first batch runs; it still completes, the next ones don't start.
        var result = await _monitor.RequeueJobsInBatchesAsync(
            ids, FailedState.StateName, GroupMetadata(), new SyncProgress(_ => cts.Cancel()), cts.Token);

        Assert.Equal(new BulkJobActionResult(1234, 500, 500, Cancelled: true), result);
        Assert.Equal(500, ids.Count(id => StateOf(id) == EnqueuedState.StateName));
        Assert.Equal(734, ids.Count(id => StateOf(id) == FailedState.StateName));
        Assert.Single(AuditEntries());
    }

    [Fact]
    public async Task Batches_OfNoJobs_DoNothing()
    {
        var result = await _monitor.DeleteJobsInBatchesAsync(Array.Empty<string>(), FailedState.StateName);

        Assert.Equal(new BulkJobActionResult(0, 0, 0, Cancelled: false), result);
        Assert.Empty(AuditEntries());
    }

    // Progress<T> posts to the captured synchronization context; this reports in line, so the test
    // sees every report before the call returns.
    private sealed class SyncProgress : IProgress<BulkJobActionResult>
    {
        private readonly Action<BulkJobActionResult> _report;

        public SyncProgress(Action<BulkJobActionResult> report) => _report = report;

        public void Report(BulkJobActionResult value) => _report(value);
    }
}
