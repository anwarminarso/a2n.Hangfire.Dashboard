using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="FailureFingerprintFilter"/>. The guarantees: a job that enters Failed gets the
/// fingerprint of its stored exception data — through the transaction when the storage supports it —
/// nothing else is touched, and no error in the filter can break the state transition.
/// </summary>
public class FailureFingerprintFilterTests
{
    public static class TestJob
    {
        public static void Run() { }
    }

    // Minimal IState stub with an arbitrary name and data (ProcessingState's ctor is internal).
    private sealed class FakeState : IState
    {
        private readonly Func<Dictionary<string, string>> _data;

        public FakeState(string name, Func<Dictionary<string, string>> data = null)
        {
            Name = name;
            _data = data ?? (() => new Dictionary<string, string>());
        }

        public string Name { get; }
        public string Reason { get; set; }
        public bool IsFinal => false;
        public bool IgnoreJobLoadException => false;
        public Dictionary<string, string> SerializeData() => _data();
    }

    private const string JobId = "job-1";

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<JobStorageTransaction> _transaction = new();

    private void StorageSupportsTransactionalParameters(bool supported)
        => _storage.Setup(s => s.HasFeature(JobStorageFeatures.Transaction.SetJobParameter)).Returns(supported);

    private static readonly Job TestJobCall = Job.FromExpression(() => TestJob.Run());

    // What the dashboard's fallback passes: the type name as Hangfire stores it.
    private static readonly string StoredJobTypeName = InvocationData.SerializeJob(TestJobCall).Type;

    private ApplyStateContext BuildContext(IState newState, IWriteOnlyTransaction transaction, bool jobLoaded = true)
    {
        var backgroundJob = new BackgroundJob(JobId, jobLoaded ? TestJobCall : null, DateTime.UtcNow, new Dictionary<string, string>());
        return new ApplyStateContext(_storage.Object, _connection.Object, transaction, backgroundJob, newState, ProcessingState.StateName);
    }

    private static string FallbackFingerprint(IDictionary<string, string> stateData)
        => FailureFingerprint.Compute(stateData, StoredJobTypeName).Fingerprint;

    // A trace whose first application frame is a shared helper outside the job's namespace.
    private static Dictionary<string, string> SharedHelperFailure(string jobFrame) => new()
    {
        ["ExceptionType"] = "System.TimeoutException",
        ["ExceptionMessage"] = "The operation has timed out.",
        ["ExceptionDetails"] =
            "System.TimeoutException: The operation has timed out.\n" +
            "   at Contoso.Shared.Retry.Run(Action action)\n" +
            "   at " + jobFrame + "()",
    };

    // What Hangfire records when it cannot deserialize a job: BackgroundJobStateChanger catches
    // JobLoadException from EnsureLoaded and replaces the state it was asked for with
    // FailedState(ex.InnerException). The job's method was never invoked, so every frame belongs to
    // the framework or to Hangfire.
    private static Dictionary<string, string> JobLoadFailure() => new()
    {
        ["ExceptionType"] = "System.TypeLoadException",
        ["ExceptionMessage"] = "Could not load type 'Contoso.Jobs.OrderJob' from assembly 'Contoso'.",
        ["ExceptionDetails"] =
            "System.TypeLoadException: Could not load type 'Contoso.Jobs.OrderJob' from assembly 'Contoso'.\n" +
            "   at System.Reflection.RuntimeAssembly.GetTypeCore(QCallAssembly assembly, String typeName)\n" +
            "   at Hangfire.Storage.InvocationData.DeserializeJob()\n" +
            "   at Hangfire.Storage.JobData.EnsureLoaded()\n" +
            "   at Hangfire.States.BackgroundJobStateChanger.ChangeState(StateChangeContext context)",
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromOrderJob(int orderId)
        => throw new InvalidOperationException($"Order {orderId} is locked by 10.0.0.12:5432 since 2026-09-25T13:20:06Z");

    private static FailedState FailedStateFromThrownException(int orderId = 42)
    {
        try
        {
            ThrowFromOrderJob(orderId);
        }
        catch (Exception ex)
        {
            return new FailedState(ex);
        }
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public void Failed_TransactionalStorage_WritesThroughTheTransaction()
    {
        StorageSupportsTransactionalParameters(true);
        var state = FailedStateFromThrownException();
        var expected = FallbackFingerprint(state.SerializeData());

        new FailureFingerprintFilter().OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object);

        _transaction.Verify(t => t.SetJobParameter(JobId, FailureFingerprint.ParameterName, expected), Times.Once);
        _connection.Verify(c => c.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Failed_NonTransactionalStorage_WritesThroughTheConnection()
    {
        StorageSupportsTransactionalParameters(false);
        var state = FailedStateFromThrownException();
        var expected = FallbackFingerprint(state.SerializeData());

        new FailureFingerprintFilter().OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object);

        _connection.Verify(c => c.SetJobParameter(JobId, FailureFingerprint.ParameterName, expected), Times.Once);
        _transaction.Verify(t => t.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Failed_TransactionIsNotAJobStorageTransaction_WritesThroughTheConnection()
    {
        // The feature flag alone isn't enough: the method only exists on JobStorageTransaction.
        StorageSupportsTransactionalParameters(true);
        var transaction = new Mock<IWriteOnlyTransaction>();
        var state = FailedStateFromThrownException();

        new FailureFingerprintFilter().OnStateApplied(BuildContext(state, transaction.Object), transaction.Object);

        _connection.Verify(c => c.SetJobParameter(JobId, FailureFingerprint.ParameterName, It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Processing")]
    [InlineData("Enqueued")]
    [InlineData("Scheduled")]
    [InlineData("Deleted")]
    [InlineData("Awaiting")]
    public void NonFailedStates_WriteNothing(string stateName)
    {
        StorageSupportsTransactionalParameters(true);
        // Exception data on a non-Failed state must not be enough to trigger a write.
        var state = new FakeState(stateName, () => FailedStateFromThrownException().SerializeData());

        new FailureFingerprintFilter().OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object);

        _transaction.Verify(t => t.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _connection.Verify(c => c.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CustomStateNamedFailed_IsFingerprintedFromItsData()
    {
        StorageSupportsTransactionalParameters(true);
        var data = new Dictionary<string, string>
        {
            ["ExceptionType"] = "System.TimeoutException",
            ["ExceptionMessage"] = "Timed out after 00:00:30",
        };
        var state = new FakeState("Failed", () => data);

        new FailureFingerprintFilter().OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object);

        _transaction.Verify(t => t.SetJobParameter(JobId, FailureFingerprint.ParameterName, FallbackFingerprint(data)), Times.Once);
    }

    public enum FailurePoint { SerializeData, HasFeature, TransactionWrite, ConnectionWrite }

    [Theory]
    [InlineData(FailurePoint.SerializeData)]
    [InlineData(FailurePoint.HasFeature)]
    [InlineData(FailurePoint.TransactionWrite)]
    [InlineData(FailurePoint.ConnectionWrite)]
    public void Exceptions_DoNotEscape_AndAreLogged(FailurePoint failurePoint)
    {
        var boom = new InvalidOperationException("storage unavailable");
        IState state = FailedStateFromThrownException();

        switch (failurePoint)
        {
            case FailurePoint.SerializeData:
                StorageSupportsTransactionalParameters(true);
                state = new FakeState(FailedState.StateName, () => throw boom);
                break;
            case FailurePoint.HasFeature:
                _storage.Setup(s => s.HasFeature(It.IsAny<string>())).Throws(boom);
                break;
            case FailurePoint.TransactionWrite:
                StorageSupportsTransactionalParameters(true);
                _transaction.Setup(t => t.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Throws(boom);
                break;
            case FailurePoint.ConnectionWrite:
                StorageSupportsTransactionalParameters(false);
                _connection.Setup(c => c.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Throws(boom);
                break;
        }

        var logger = new Mock<ILogger>();
        var filter = new FailureFingerprintFilter(logger.Object);

        var thrown = Record.Exception(() => filter.OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object));

        Assert.Null(thrown);
        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            boom,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Once);
    }

    [Fact]
    public void ThrowingLogger_DoesNotEscapeEither()
    {
        StorageSupportsTransactionalParameters(false);
        _connection.Setup(c => c.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage unavailable"));
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()))
            .Throws(new InvalidOperationException("logger broken"));
        var state = FailedStateFromThrownException();

        var thrown = Record.Exception(() => new FailureFingerprintFilter(logger.Object)
            .OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object));

        Assert.Null(thrown);
    }

    [Fact]
    public void WithoutALogger_ExceptionsDoNotEscape()
    {
        StorageSupportsTransactionalParameters(false);
        _connection.Setup(c => c.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage unavailable"));
        var state = FailedStateFromThrownException();

        var thrown = Record.Exception(() => new FailureFingerprintFilter()
            .OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object));

        Assert.Null(thrown);
    }

    [Fact]
    public void JobType_IsPassedOn_SoTheJobsOwnFrameIsPreferred()
    {
        StorageSupportsTransactionalParameters(true);
        var jobFrame = typeof(TestJob).FullName + "." + nameof(TestJob.Run);
        var data = SharedHelperFailure(jobFrame);

        new FailureFingerprintFilter().OnStateApplied(
            BuildContext(new FakeState(FailedState.StateName, () => data), _transaction.Object), _transaction.Object);

        Assert.Equal(jobFrame, FailureFingerprint.Compute(data, StoredJobTypeName).TopFrame);
        _transaction.Verify(t => t.SetJobParameter(JobId, FailureFingerprint.ParameterName, FallbackFingerprint(data)), Times.Once);
        Assert.NotEqual(FailureFingerprint.Compute(data).Fingerprint, FallbackFingerprint(data));
    }

    /// <summary>
    /// The job type is taken from the context alone. A null <c>Job</c> means Hangfire could not
    /// deserialize the job, and it is that load failure being recorded: the application's method was
    /// never invoked, so the trace has no frame in the job's namespace and the type name cannot change
    /// the fingerprint. The filter therefore doesn't go back to the storage for it, which would make a
    /// versioned hash depend on whether the installed adapter fills in
    /// <see cref="JobData.InvocationData"/>.
    /// </summary>
    [Fact]
    public void JobTypeNotLoadable_TheStorageIsNotQueried_AndTheFingerprintIsTheSameEitherWay()
    {
        StorageSupportsTransactionalParameters(true);
        var data = JobLoadFailure();

        new FailureFingerprintFilter().OnStateApplied(
            BuildContext(new FakeState(FailedState.StateName, () => data), _transaction.Object, jobLoaded: false),
            _transaction.Object);

        _connection.Verify(c => c.GetJobData(It.IsAny<string>()), Times.Never);
        var withoutJobType = FailureFingerprint.Compute(data).Fingerprint;
        Assert.Equal(withoutJobType, FallbackFingerprint(data));
        _transaction.Verify(t => t.SetJobParameter(JobId, FailureFingerprint.ParameterName, withoutJobType), Times.Once);
    }

    [Fact]
    public void OnStateUnapplied_KeepsTheFingerprint()
    {
        StorageSupportsTransactionalParameters(true);

        new FailureFingerprintFilter().OnStateUnapplied(
            BuildContext(new FakeState(EnqueuedState.StateName), _transaction.Object), _transaction.Object);

        _transaction.VerifyNoOtherCalls();
        _connection.VerifyNoOtherCalls();
    }

    [Fact]
    public void RealFailedState_FilterValueEqualsTheFallbackFromStateData()
    {
        StorageSupportsTransactionalParameters(true);
        var state = FailedStateFromThrownException();
        string written = null;
        _transaction
            .Setup(t => t.SetJobParameter(JobId, FailureFingerprint.ParameterName, It.IsAny<string>()))
            .Callback<string, string, string>((_, _, value) => written = value);

        new FailureFingerprintFilter().OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object);

        // The fallback for failures without a stored fingerprint starts from the same dictionary.
        var fallback = FailureFingerprint.Compute(state.SerializeData(), StoredJobTypeName);
        Assert.Equal(fallback.Fingerprint, written);
        Assert.True(FailureFingerprint.IsCurrentVersion(written));
        Assert.Equal(typeof(InvalidOperationException).FullName, fallback.ExceptionType);
        Assert.Equal("Order <n> is locked by <ip> since <time>", fallback.NormalizedMessage);
        Assert.Equal(typeof(FailureFingerprintFilterTests).FullName + "." + nameof(ThrowFromOrderJob), fallback.TopFrame);

        // A different order id, from the same place, is the same failure.
        Assert.Equal(fallback.Fingerprint, FallbackFingerprint(FailedStateFromThrownException(orderId: 7).SerializeData()));
    }

    [Fact]
    public void InMemoryStorage_RetriesAreSkipped_AndTheStoredValueMatchesTheStoredStateData()
    {
        var storage = new InMemoryStorage();
        var filters = new JobFilterCollection
        {
            new AutomaticRetryAttribute { Attempts = 1 },
            new FailureFingerprintFilter(),
        };
        var client = new BackgroundJobClient(storage, filters);
        var jobId = client.Create(() => TestJob.Run(), new EnqueuedState());

        using var connection = storage.GetConnection();

        // The first failure is elected away from Failed by AutomaticRetry, so the filter never sees it.
        // ChangeState reports false because the applied state isn't the requested one.
        Assert.False(client.ChangeState(jobId, FailedStateFromThrownException(), EnqueuedState.StateName));
        Assert.Equal(ScheduledState.StateName, connection.GetStateData(jobId).Name);
        Assert.Null(connection.GetJobParameter(jobId, FailureFingerprint.ParameterName));

        // The retry fails too and the job is left Failed: now the fingerprint is written.
        Assert.True(client.ChangeState(jobId, FailedStateFromThrownException(), ScheduledState.StateName));
        var stateData = connection.GetStateData(jobId);
        Assert.Equal(FailedState.StateName, stateData.Name);

        // Stored raw, not JSON-encoded, and equal to what the fallback computes from the stored state.
        var stored = connection.GetJobParameter(jobId, FailureFingerprint.ParameterName);
        Assert.True(FailureFingerprint.IsCurrentVersion(stored));
        Assert.Equal(FailureFingerprint.Compute(stateData.Data, connection.GetJobData(jobId).InvocationData.Type).Fingerprint, stored);
    }
}
