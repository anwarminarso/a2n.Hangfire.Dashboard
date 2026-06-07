namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Behaviour of the dashboard pause filter when a paused queue's worker tries to execute a job.
/// </summary>
public enum PausedJobBehavior
{
    /// <summary>
    /// Reschedule the job to <see cref="QueueOperationsOptions.RescheduleDelay"/> in the future.
    /// Recommended default — minimizes CPU thrashing while pause is active.
    /// </summary>
    Reschedule = 0,

    /// <summary>
    /// Re-enqueue immediately without delay. The job will be picked up again right away,
    /// causing busy-loop behavior; use only if you have very few workers and short pauses.
    /// </summary>
    Requeue = 1,
}

/// <summary>
/// Options for the queue pause / maintenance subsystem.
/// </summary>
public class QueueOperationsOptions
{
    /// <summary>
    /// Whether queue pause / maintenance mode is enabled. When false, the dashboard's pause
    /// toggle UI is hidden and the server filter is a no-op. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// What the server filter does when a worker fetches a job whose queue is paused.
    /// Default: <see cref="PausedJobBehavior.Reschedule"/>.
    /// </summary>
    public PausedJobBehavior Behavior { get; set; } = PausedJobBehavior.Reschedule;

    /// <summary>
    /// Delay applied when <see cref="Behavior"/> is <see cref="PausedJobBehavior.Reschedule"/>.
    /// Default: 30 seconds. Larger delays reduce DB churn during long pauses.
    /// </summary>
    public TimeSpan RescheduleDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cache TTL for the paused-queues set lookup. The pause filter consults Hangfire storage at
    /// most once per this interval per server to avoid hitting the DB on every job execution.
    /// Default: 2 seconds — short enough to feel snappy after toggling in the UI.
    /// </summary>
    public TimeSpan PauseStateCacheTtl { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Snapshot of the queue pause / maintenance state.</summary>
public class QueueOperationsState
{
    /// <summary>True when the entire dashboard is in maintenance mode (all queues paused).</summary>
    public bool MaintenanceMode { get; set; }

    /// <summary>UTC timestamp when maintenance mode was last enabled (null if not active).</summary>
    public DateTime? MaintenanceEnabledAtUtc { get; set; }

    /// <summary>Optional reason for maintenance mode (set by the operator).</summary>
    public string MaintenanceReason { get; set; }

    /// <summary>The user who enabled maintenance mode (best effort).</summary>
    public string MaintenanceEnabledBy { get; set; }

    /// <summary>Set of explicitly paused queue names (independent of maintenance mode).</summary>
    public HashSet<string> PausedQueues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
