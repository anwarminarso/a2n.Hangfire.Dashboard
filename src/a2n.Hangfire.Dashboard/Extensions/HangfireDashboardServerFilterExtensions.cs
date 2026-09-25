using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Helpers;
using a2n.Hangfire.Dashboard.Storage;
using Hangfire;

namespace Hangfire;

/// <summary>
/// Extension methods for registering the dashboard's server-side filters with the Hangfire pipeline.
/// </summary>
public static class HangfireDashboardServerFilterExtensions
{
    /// <summary>
    /// Registers the queue-pause server filter (<see cref="QueuePauseServerFilter"/>) globally so
    /// the dashboard's pause and maintenance-mode toggles take effect on running Hangfire servers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this on your <see cref="IGlobalConfiguration"/> chain alongside
    /// <c>UseSqlServerStorage</c> / <c>UsePostgreSqlStorage</c>:
    /// </para>
    /// <code>
    /// builder.Services.AddHangfire(config =&gt; config
    ///     .UseSqlServerStorage(connStr)
    ///     .UseDashboardQueuePauseFilter());
    /// </code>
    /// <para>
    /// Without this call, the dashboard's pause/maintenance UI still updates the underlying
    /// storage keys but no <em>running</em> server will respect the pause — jobs keep executing
    /// until the host restarts with the filter enabled.
    /// </para>
    /// <para>
    /// The filter has no DI dependencies; it reads pause state directly from Hangfire storage.
    /// To customize behavior (delay, reschedule vs requeue), pass a configured
    /// <see cref="QueueOperationsOptions"/>.
    /// </para>
    /// </remarks>
    /// <param name="configuration">The Hangfire global configuration chain.</param>
    /// <param name="options">Optional behaviour options. When null, defaults are used.</param>
    public static IGlobalConfiguration UseDashboardQueuePauseFilter(
        this IGlobalConfiguration configuration,
        QueueOperationsOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        GlobalJobFilters.Filters.Add(new QueuePauseServerFilter(options));
        return configuration;
    }

    /// <summary>
    /// Registers the failure-fingerprint filter (<see cref="FailureFingerprintFilter"/>) globally so
    /// every job that fails records which kind of failure it was, for grouping failed jobs on the
    /// dashboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this on your <see cref="IGlobalConfiguration"/> chain alongside
    /// <c>UseSqlServerStorage</c> / <c>UsePostgreSqlStorage</c>:
    /// </para>
    /// <code>
    /// builder.Services.AddHangfire(config =&gt; config
    ///     .UseSqlServerStorage(connStr)
    ///     .UseDashboardFailureFingerprintFilter());
    /// </code>
    /// <para>
    /// When a job enters the Failed state, the filter stores a short fingerprint of the exception
    /// type, the normalized first line of the message and the top application stack frame in the
    /// job parameter <see cref="FailureFingerprint.ParameterName"/>. Retry attempts are not
    /// fingerprinted, only failures that end in the Failed state.
    /// </para>
    /// <para>
    /// The filter is optional. Failures recorded without it can still be fingerprinted from their
    /// stored exception data, but that has to be done when the list is read, so it only covers a
    /// bounded number of jobs. The filter has no DI dependencies.
    /// </para>
    /// </remarks>
    /// <param name="configuration">The Hangfire global configuration chain.</param>
    public static IGlobalConfiguration UseDashboardFailureFingerprintFilter(this IGlobalConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        GlobalJobFilters.Filters.Add(new FailureFingerprintFilter());
        return configuration;
    }
}
