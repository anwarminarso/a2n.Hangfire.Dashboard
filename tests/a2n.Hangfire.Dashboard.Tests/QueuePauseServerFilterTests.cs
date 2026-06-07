using System;
using System.Collections.Generic;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="QueuePauseServerFilter"/>. The critical guarantee: a job on a paused queue
/// is redirected (Scheduled / Enqueued) at state election and is NEVER deleted. The previous
/// IServerFilter design cancelled the performance, which Hangfire's worker turns into a DeletedState
/// — permanent data loss. These tests lock in the safe behavior.
/// </summary>
public class QueuePauseServerFilterTests
{
    public static class TestJob
    {
        public static void Run() { }
    }

    // Minimal IState stub reporting an arbitrary state name (ProcessingState's ctor is internal).
    private sealed class FakeState : IState
    {
        public FakeState(string name) => Name = name;
        public string Name { get; }
        public string Reason { get; set; }
        public bool IsFinal => false;
        public bool IgnoreJobLoadException => false;
        public Dictionary<string, string> SerializeData() => new();
    }

    private static IState Processing() => new FakeState(ProcessingState.StateName);

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();

    private void SetupPaused(IEnumerable<string> pausedQueues, bool maintenance = false)
    {
        _connection.Setup(c => c.GetAllItemsFromSet(QueueOperationsStorageKeys.PausedSetKey))
            .Returns(new HashSet<string>(pausedQueues ?? Array.Empty<string>()));
        _connection.Setup(c => c.GetAllEntriesFromHash(QueueOperationsStorageKeys.StateHashKey))
            .Returns(new Dictionary<string, string>
            {
                [QueueOperationsStorageKeys.FieldMaintenanceEnabled] = maintenance ? "true" : "false"
            });
    }

    private ElectStateContext BuildContext(IState candidateState, string queue, string oldStateName = "Enqueued")
    {
        var job = Job.FromExpression(() => TestJob.Run());
        var parameters = new Dictionary<string, string>();
        if (queue is not null)
        {
            // Job.Queue is read via GetJobParameter with allowStale -> ParametersSnapshot.
            parameters["Job.Queue"] = SerializationHelper.Serialize(queue, SerializationOption.User);
        }
        var backgroundJob = new BackgroundJob("job-1", job, DateTime.UtcNow, parameters);

        var applyContext = new ApplyStateContext(
            _storage.Object,
            _connection.Object,
            _transaction.Object,
            backgroundJob,
            candidateState,
            oldStateName);

        return new ElectStateContext(applyContext);
    }

    [Fact]
    public void PausedQueue_RescheduleBehavior_RedirectsToScheduled_NotDeleted()
    {
        SetupPaused(new[] { "default" });
        var filter = new QueuePauseServerFilter(new QueueOperationsOptions
        {
            Behavior = PausedJobBehavior.Reschedule,
            RescheduleDelay = TimeSpan.FromSeconds(30),
            PauseStateCacheTtl = TimeSpan.Zero,
        });

        var ctx = BuildContext(Processing(), "default");
        filter.OnStateElection(ctx);

        Assert.IsType<ScheduledState>(ctx.CandidateState);
        // Must never become a DeletedState.
        Assert.IsNotType<DeletedState>(ctx.CandidateState);
    }

    [Fact]
    public void PausedQueue_RequeueBehavior_RedirectsToEnqueued()
    {
        SetupPaused(new[] { "default" });
        var filter = new QueuePauseServerFilter(new QueueOperationsOptions
        {
            Behavior = PausedJobBehavior.Requeue,
            PauseStateCacheTtl = TimeSpan.Zero,
        });

        var ctx = BuildContext(Processing(), "default");
        filter.OnStateElection(ctx);

        var enqueued = Assert.IsType<EnqueuedState>(ctx.CandidateState);
        Assert.Equal("default", enqueued.Queue);
    }

    [Fact]
    public void MaintenanceMode_PausesAnyQueue()
    {
        SetupPaused(Array.Empty<string>(), maintenance: true);
        var filter = new QueuePauseServerFilter(new QueueOperationsOptions { PauseStateCacheTtl = TimeSpan.Zero });

        var ctx = BuildContext(Processing(), "some-other-queue");
        filter.OnStateElection(ctx);

        Assert.IsType<ScheduledState>(ctx.CandidateState);
    }

    [Fact]
    public void UnpausedQueue_LeavesProcessingStateUntouched()
    {
        SetupPaused(new[] { "other-queue" });
        var filter = new QueuePauseServerFilter(new QueueOperationsOptions { PauseStateCacheTtl = TimeSpan.Zero });

        var processing = Processing();
        var ctx = BuildContext(processing, "default");
        filter.OnStateElection(ctx);

        Assert.Same(processing, ctx.CandidateState);
    }

    [Fact]
    public void NonProcessingTransition_IsNeverIntercepted()
    {
        SetupPaused(new[] { "default" });
        var filter = new QueuePauseServerFilter(new QueueOperationsOptions { PauseStateCacheTtl = TimeSpan.Zero });

        // A job on a paused queue moving to Succeeded must be left alone — we only gate entry into Processing.
        var succeeded = new SucceededState(null, 0, 0);
        var ctx = BuildContext(succeeded, "default", oldStateName: "Processing");
        filter.OnStateElection(ctx);

        Assert.Same(succeeded, ctx.CandidateState);
    }

    [Fact]
    public void DisabledOptions_NoOp()
    {
        SetupPaused(new[] { "default" });
        var filter = new QueuePauseServerFilter(new QueueOperationsOptions { Enabled = false });

        var processing = Processing();
        var ctx = BuildContext(processing, "default");
        filter.OnStateElection(ctx);

        Assert.Same(processing, ctx.CandidateState);
    }
}
