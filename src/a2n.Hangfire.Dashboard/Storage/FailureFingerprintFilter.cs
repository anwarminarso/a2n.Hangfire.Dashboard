using a2n.Hangfire.Dashboard.Helpers;
using Hangfire;
using Hangfire.Logging;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Storage;

/// <summary>
/// Hangfire <see cref="IApplyStateFilter"/> that records a <see cref="FailureFingerprint"/> on every
/// job that enters the Failed state, so the dashboard can group failed jobs by the kind of failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why state application, not election.</b> <see cref="OnStateApplied"/> runs only when Failed is
/// actually applied. A failed attempt that <see cref="AutomaticRetryAttribute"/> turns into a retry
/// is elected away from Failed and never reaches this filter, so only final failures get a
/// fingerprint, and the filter's order relative to <c>AutomaticRetry</c> doesn't matter.
/// </para>
/// <para>
/// The fingerprint is computed from <see cref="IState.SerializeData"/> — the same
/// <c>ExceptionType</c>, <c>ExceptionMessage</c> and <c>ExceptionDetails</c> strings Hangfire stores
/// in the state — rather than from the <see cref="Exception"/> object, plus the job's type name, so a
/// failure recorded before the filter was registered gets the same value when the dashboard computes it
/// later from the stored state and invocation data. It is written under <see cref="FailureFingerprint.ParameterName"/> as a raw string, through
/// the state-change transaction when the storage supports
/// <see cref="JobStorageFeatures.Transaction.SetJobParameter"/> and through the connection otherwise.
/// </para>
/// <para>
/// The parameter is kept when the job leaves the Failed state, so a requeued or deleted job can still
/// be found by the failure it had; a later failure overwrites it.
/// </para>
/// <para>
/// The filter never breaks a state transition: any error is logged and the job fails as it would
/// without it. It has no DI dependencies, so it works on hosts that register filters before DI is
/// built. Register via
/// <see cref="HangfireDashboardServerFilterExtensions.UseDashboardFailureFingerprintFilter"/>.
/// </para>
/// </remarks>
public class FailureFingerprintFilter : IApplyStateFilter
{
    private readonly ILogger _logger;

    /// <summary>Constructs the filter.</summary>
    /// <param name="logger">
    /// Optional logger. When null, errors are logged through Hangfire's own log provider, which the
    /// host's Hangfire configuration (e.g. <c>AddHangfire</c>) connects to its logging.
    /// </param>
    public FailureFingerprintFilter(ILogger logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        try
        {
            // Match by state name rather than CLR type so a custom IState reporting the Failed name is
            // also handled, as long as its data carries the exception fields.
            if (!string.Equals(context.NewState?.Name, FailedState.StateName, StringComparison.OrdinalIgnoreCase))
                return;

            var jobId = context.BackgroundJob.Id;
            var fingerprint = FailureFingerprint.Compute(context.NewState.SerializeData(), GetJobTypeName(context)).Fingerprint;

            // In the transaction the parameter commits together with the Failed state. Storages that
            // can't do that get it straight away, which only leaves it behind if the transition is
            // rolled back — harmless, since the parameter is only read for failed jobs.
            if (transaction is JobStorageTransaction storageTransaction
                && context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
            {
                storageTransaction.SetJobParameter(jobId, FailureFingerprint.ParameterName, fingerprint);
            }
            else
            {
                context.Connection.SetJobParameter(jobId, FailureFingerprint.ParameterName, fingerprint);
            }
        }
        catch (Exception ex)
        {
            LogFailure(ex, context?.BackgroundJob?.Id);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Does nothing: the fingerprint stays on the job after it leaves the Failed state.
    /// </remarks>
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    /// <summary>
    /// The job's type name, which the fingerprint uses to prefer the application's own frames, or null
    /// when the type couldn't be loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null means exactly one thing: Hangfire could not deserialize the job.
    /// <c>BackgroundJobStateChanger</c> builds the <see cref="ApplyStateContext"/> from
    /// <see cref="JobData.Job"/>, which is only null after <c>EnsureLoaded</c> threw
    /// <c>JobLoadException</c>, and it is that same failure being recorded, since the changer replaces
    /// the state it was asked for with <c>FailedState(ex.InnerException)</c>.
    /// </para>
    /// <para>
    /// The application's method was therefore never invoked, so the stack trace holds only Hangfire,
    /// serialization and reflection frames and has nothing in the job's own namespace. Passing the
    /// stored type name here would not change the chosen frame, which is why the type is not read back
    /// from the storage: <c>GetJobData</c> returns the invocation data on some storages and not on
    /// others (Hangfire.PostgreSql 1.20 leaves <see cref="JobData.InvocationData"/> unset), so using it
    /// would make a versioned hash depend on which adapter is installed, and would change stored
    /// fingerprints if an adapter started filling that field in. It also costs a query on a path that
    /// is already failing.
    /// </para>
    /// </remarks>
    private static string GetJobTypeName(ApplyStateContext context)
        => context.BackgroundJob.Job?.Type?.FullName;

    private void LogFailure(Exception ex, string jobId)
    {
        try
        {
            if (_logger is not null)
            {
                _logger.LogWarning(ex, "Could not record the failure fingerprint for job {JobId}.", jobId);
            }
            else
            {
                LogProvider.GetLogger(typeof(FailureFingerprintFilter))
                    .WarnException($"Could not record the failure fingerprint for job {jobId}.", ex);
            }
        }
        catch
        {
            // A broken logger must not fail the state transition either.
        }
    }
}
