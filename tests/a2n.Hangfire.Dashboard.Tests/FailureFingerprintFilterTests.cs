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

    private ApplyStateContext BuildContext(IState newState, IWriteOnlyTransaction transaction)
    {
        var job = Job.FromExpression(() => TestJob.Run());
        var backgroundJob = new BackgroundJob(JobId, job, DateTime.UtcNow, new Dictionary<string, string>());
        return new ApplyStateContext(_storage.Object, _connection.Object, transaction, backgroundJob, newState, ProcessingState.StateName);
    }

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
        var expected = FailureFingerprint.Compute(state.SerializeData()).Fingerprint;

        new FailureFingerprintFilter().OnStateApplied(BuildContext(state, _transaction.Object), _transaction.Object);

        _transaction.Verify(t => t.SetJobParameter(JobId, FailureFingerprint.ParameterName, expected), Times.Once);
        _connection.Verify(c => c.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Failed_NonTransactionalStorage_WritesThroughTheConnection()
    {
        StorageSupportsTransactionalParameters(false);
        var state = FailedStateFromThrownException();
        var expected = FailureFingerprint.Compute(state.SerializeData()).Fingerprint;

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

        _transaction.Verify(t => t.SetJobParameter(JobId, FailureFingerprint.ParameterName, FailureFingerprint.Compute(data).Fingerprint), Times.Once);
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
        var fallback = FailureFingerprint.Compute(state.SerializeData());
        Assert.Equal(fallback.Fingerprint, written);
        Assert.True(FailureFingerprint.IsCurrentVersion(written));
        Assert.Equal(typeof(InvalidOperationException).FullName, fallback.ExceptionType);
        Assert.Equal("Order <n> is locked by <ip> since <time>", fallback.NormalizedMessage);
        Assert.Equal(typeof(FailureFingerprintFilterTests).FullName + "." + nameof(ThrowFromOrderJob), fallback.TopFrame);

        // A different order id, from the same place, is the same failure.
        Assert.Equal(fallback.Fingerprint, FailureFingerprint.Compute(FailedStateFromThrownException(orderId: 7).SerializeData()).Fingerprint);
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
        Assert.Equal(FailureFingerprint.Compute(stateData.Data).Fingerprint, stored);
    }
}
