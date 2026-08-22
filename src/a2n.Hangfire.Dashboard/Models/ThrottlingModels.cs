namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// The three rate-limit window kinds Hangfire.Throttling supports. Each serializes its state with a
/// different shape, so the window type selects how <c>ThrottlingDataReader</c> parses it.
/// </summary>
public static class ThrottleWindowTypes
{
    public const string Fixed = "Fixed";
    public const string Sliding = "Sliding";
    public const string Dynamic = "Dynamic";
}

/// <summary>
/// A semaphore registered by Hangfire.Throttling, with its current holders.
/// </summary>
public class SemaphoreDto
{
    public string Id { get; set; }

    /// <summary>Maximum number of concurrent holders.</summary>
    public int MaxCount { get; set; }

    /// <summary>Optional description, when one was provided at registration.</summary>
    public string Description { get; set; }

    /// <summary>Ids of background jobs currently holding a slot.</summary>
    public IReadOnlyList<string> HolderJobIds { get; set; } = [];
}

/// <summary>
/// A mutex currently tracked by Hangfire.Throttling, with its current holder.
/// Mutex entries are created on demand per resource key and disappear when released.
/// </summary>
public class MutexDto
{
    public string Id { get; set; }

    /// <summary>Ids of background jobs currently holding the mutex (normally one).</summary>
    public IReadOnlyList<string> HolderJobIds { get; set; } = [];
}

/// <summary>
/// Resolved state of a job currently holding a throttling primitive.
/// </summary>
public class ThrottleHolderDto
{
    public string JobId { get; set; }

    /// <summary>Current state name of the job, or null when the job no longer exists.</summary>
    public string StateName { get; set; }

    /// <summary>The server processing the job, when it is in the Processing state.</summary>
    public string ServerId { get; set; }

    /// <summary>
    /// True when the job can no longer release its own slot: its record has expired out of
    /// storage, it settled into a final state without releasing, or it is recorded as Processing
    /// on a server that has stopped sending heartbeats. Such holders are the safe candidates for
    /// detaching; anything else may still be running.
    /// </summary>
    public bool IsOrphaned { get; set; }

    /// <summary>
    /// Why the holder was flagged as orphaned, shown to the operator before they detach it.
    /// Null when <see cref="IsOrphaned"/> is false.
    /// </summary>
    public string OrphanReason { get; set; }
}

/// <summary>
/// A rate-limiting window (fixed, sliding, or dynamic) registered by Hangfire.Throttling.
/// </summary>
public class ThrottleWindowDto
{
    /// <summary>"Fixed", "Sliding", or "Dynamic".</summary>
    public string Type { get; set; }

    public string Id { get; set; }

    /// <summary>Optional description, when one was provided at registration.</summary>
    public string Description { get; set; }

    /// <summary>Maximum executions per window, when the stored object exposes it.</summary>
    public int? Limit { get; set; }

    /// <summary>Window interval in seconds, when the stored object exposes it.</summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>Executions counted in the active window, when the stored object exposes it.</summary>
    public int? Counter { get; set; }
}
