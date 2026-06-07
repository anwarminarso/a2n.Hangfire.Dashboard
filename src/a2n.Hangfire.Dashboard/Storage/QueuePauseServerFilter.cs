using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Storage;

/// <summary>
/// Hangfire <see cref="IElectStateFilter"/> that prevents jobs on paused queues (or any queue while
/// maintenance mode is active) from entering the Processing state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why state election, not <c>IServerFilter</c>.</b> An earlier design cancelled the job in
/// <c>IServerFilter.OnPerforming</c> by setting <c>context.Canceled = true</c>. That is unsafe:
/// Hangfire's <c>Worker.PerformJob</c> turns a filter-cancelled performance into a
/// <c>DeletedState</c> ("Canceled by filter '…'"), so the paused job would be <b>permanently
/// deleted</b> instead of held. Intercepting at state election — before the worker ever runs the
/// job body — lets us redirect the job back to Scheduled/Enqueued with zero risk of deletion.
/// </para>
/// <para>
/// When a worker tries to move a job to <see cref="ProcessingState"/> while its queue is paused,
/// this filter rewrites the candidate state:
/// <list type="bullet">
///   <item><description><see cref="PausedJobBehavior.Reschedule"/> (default) → <see cref="ScheduledState"/> at <c>now + RescheduleDelay</c>, so workers stop thrashing during long pauses.</description></item>
///   <item><description><see cref="PausedJobBehavior.Requeue"/> → <see cref="EnqueuedState"/> on the same queue (picked up again immediately).</description></item>
/// </list>
/// </para>
/// <para>
/// The filter has no DI dependencies — it reads pause state directly from the storage keys defined
/// in <see cref="QueueOperationsStorageKeys"/>. This keeps it usable on hosts that register filters
/// before DI is built (e.g., the classic Startup pattern). Register via
/// <see cref="HangfireDashboardServerFilterExtensions.UseDashboardQueuePauseFilter"/>.
/// </para>
/// </remarks>
public class QueuePauseServerFilter : IElectStateFilter
{
    /// <summary>Hangfire set key holding paused queue names. See <see cref="QueueOperationsStorageKeys.PausedSetKey"/>.</summary>
    public const string PausedSetKey = QueueOperationsStorageKeys.PausedSetKey;

    /// <summary>Hangfire hash key holding maintenance-mode flag. See <see cref="QueueOperationsStorageKeys.StateHashKey"/>.</summary>
    public const string StateHashKey = QueueOperationsStorageKeys.StateHashKey;

    /// <summary>Hash field name for the maintenance-enabled boolean.</summary>
    public const string MaintenanceEnabledField = QueueOperationsStorageKeys.FieldMaintenanceEnabled;

    private readonly object _cacheLock = new();
    private DateTime _cacheExpiresAt = DateTime.MinValue;
    private HashSet<string> _cachedPausedQueues = new(StringComparer.OrdinalIgnoreCase);
    private bool _cachedMaintenanceMode;

    private readonly QueueOperationsOptions _options;
    private readonly ILogger _logger;

    /// <summary>Constructs the filter with the given options.</summary>
    /// <param name="options">
    /// Behavior options. When null, defaults are used. Note: the filter takes a snapshot — runtime
    /// option changes via <see cref="DashboardUIOptions.QueueOperations"/> will not affect this
    /// filter unless the same instance is shared.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public QueuePauseServerFilter(QueueOperationsOptions options = null, ILogger logger = null)
    {
        _options = options ?? new QueueOperationsOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        if (!_options.Enabled) return;

        // We only intercept the transition INTO Processing — that is the moment a worker has
        // fetched the job and is about to run it. Any other transition (Succeeded, Failed,
        // Scheduled, Deleted, …) must pass through untouched. Match by state name rather than CLR
        // type so a custom IState reporting the Processing name is also handled.
        if (!string.Equals(context.CandidateState?.Name, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
            return;

        var queue = ResolveQueue(context);
        if (string.IsNullOrEmpty(queue)) return;

        if (!IsQueueOrMaintenancePaused(context.Connection, queue)) return;

        var jobId = context.BackgroundJob.Id;

        if (_options.Behavior == PausedJobBehavior.Reschedule)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(1, _options.RescheduleDelay.TotalSeconds));
            context.CandidateState = new ScheduledState(delay)
            {
                Reason = $"Queue '{queue}' is paused — rescheduled by dashboard pause filter.",
            };
            _logger?.LogDebug(
                "Job {JobId} on paused queue {Queue} held: rescheduled +{DelaySec}s.",
                jobId, queue, (int)delay.TotalSeconds);
        }
        else
        {
            context.CandidateState = new EnqueuedState(queue)
            {
                Reason = $"Queue '{queue}' is paused — re-enqueued by dashboard pause filter.",
            };
            _logger?.LogDebug("Job {JobId} on paused queue {Queue} held: re-enqueued.", jobId, queue);
        }
    }

    private bool IsQueueOrMaintenancePaused(IStorageConnection connection, string queue)
    {
        var (paused, maintenance) = GetCachedState(connection);
        if (maintenance) return true;
        return paused.Contains(queue);
    }

    private (HashSet<string> paused, bool maintenance) GetCachedState(IStorageConnection connection)
    {
        var ttl = _options.PauseStateCacheTtl;
        if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromSeconds(2);

        lock (_cacheLock)
        {
            if (DateTime.UtcNow < _cacheExpiresAt)
                return (_cachedPausedQueues, _cachedMaintenanceMode);

            try
            {
                if (connection is JobStorageConnection storageConnection)
                {
                    var pausedRaw = storageConnection.GetAllItemsFromSet(QueueOperationsStorageKeys.PausedSetKey) ?? [];
                    _cachedPausedQueues = new HashSet<string>(pausedRaw, StringComparer.OrdinalIgnoreCase);

                    var hash = storageConnection.GetAllEntriesFromHash(QueueOperationsStorageKeys.StateHashKey);
                    _cachedMaintenanceMode = hash != null
                        && hash.TryGetValue(QueueOperationsStorageKeys.FieldMaintenanceEnabled, out var v)
                        && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

                    _cacheExpiresAt = DateTime.UtcNow + ttl;
                }
            }
            catch
            {
                // On a transient storage error, keep the previous snapshot but don't extend TTL.
            }

            return (_cachedPausedQueues, _cachedMaintenanceMode);
        }
    }

    /// <summary>
    /// Determines the queue a job runs on. Mirrors the logic used elsewhere in the dashboard:
    /// prefer the "Job.Queue" job parameter, then a <see cref="QueueAttribute"/> on the method,
    /// then Hangfire's default queue.
    /// </summary>
    private static string ResolveQueue(ElectStateContext context)
    {
        try
        {
            var queue = context.GetJobParameter<string>("Job.Queue", allowStale: true);
            if (!string.IsNullOrEmpty(queue)) return queue;
        }
        catch { /* parameter missing */ }

        try
        {
            var attr = context.BackgroundJob?.Job?.Method?
                .GetCustomAttributes(typeof(QueueAttribute), inherit: true)
                .FirstOrDefault() as QueueAttribute;
            if (attr is not null && !string.IsNullOrEmpty(attr.Queue)) return attr.Queue;
        }
        catch { /* reflection failure */ }

        return EnqueuedState.DefaultQueue;
    }
}
