namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Categorizes audit-log entries. Stable string values are persisted to storage; do not rename
/// existing values. Add new entries at the bottom and document them in the changelog.
/// </summary>
public static class AuditAction
{
    // Job actions
    public const string JobRequeued = "job.requeued";
    public const string JobDeleted = "job.deleted";
    public const string JobsRequeuedBatch = "jobs.requeued";
    public const string JobsDeletedBatch = "jobs.deleted";

    // Recurring job actions
    public const string RecurringCreated = "recurring.created";
    public const string RecurringUpdated = "recurring.updated";
    public const string RecurringDeleted = "recurring.deleted";
    public const string RecurringTriggered = "recurring.triggered";
    public const string RecurringStopped = "recurring.stopped";
    public const string RecurringStarted = "recurring.started";

    // Queue / maintenance actions (added in v2.3.x Operations release)
    public const string QueuePaused = "queue.paused";
    public const string QueueResumed = "queue.resumed";
    public const string MaintenanceEnabled = "maintenance.enabled";
    public const string MaintenanceDisabled = "maintenance.disabled";

    // Throttling actions (Hangfire.Throttling primitives)
    public const string ThrottlingSemaphoreDetached = "throttling.semaphore-detached";
    public const string ThrottlingMutexDetached = "throttling.mutex-detached";
}

/// <summary>
/// A single audit-log record describing an admin action against the dashboard.
/// </summary>
public class AuditLogEntry
{
    /// <summary>Sortable identifier (UTC ticks suffix); also the score used by the storage set.</summary>
    public string Id { get; set; }

    /// <summary>UTC timestamp of the action.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>The actor — typically the authenticated user name or "(local)" for unauthenticated local requests.</summary>
    public string User { get; set; }

    /// <summary>The originating client IP (best-effort; may reflect proxy address).</summary>
    public string ClientIp { get; set; }

    /// <summary>The action key (see <see cref="AuditAction"/>).</summary>
    public string Action { get; set; }

    /// <summary>The target of the action — job id, queue name, recurring id, or empty for global actions.</summary>
    public string Target { get; set; }

    /// <summary>Optional human-readable reason supplied by the operator.</summary>
    public string Reason { get; set; }

    /// <summary>Optional small metadata bag (e.g., batch counts, before/after values). Truncated when persisted.</summary>
    public Dictionary<string, string> Metadata { get; set; }
}

/// <summary>Filter used by audit-log queries. All fields are optional and ANDed together.</summary>
public class AuditLogFilter
{
    /// <summary>Earliest timestamp inclusive.</summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>Latest timestamp inclusive.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>Match on action prefix (e.g., "job." matches all job actions). Case-insensitive.</summary>
    public string ActionPrefix { get; set; }

    /// <summary>Match on user (substring, case-insensitive).</summary>
    public string User { get; set; }

    /// <summary>Match on target (substring, case-insensitive).</summary>
    public string Target { get; set; }
}

/// <summary>Configuration for the audit log subsystem.</summary>
public class AuditLogOptions
{
    /// <summary>Whether to record audit entries at all. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Retention window. Entries older than this are eligible for deletion when the
    /// audit page is paged or a write occurs. Default: 30 days.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Maximum entries to retain regardless of <see cref="Retention"/>. Older entries above this
    /// count are trimmed on write (cheap O(log n) set trim). Default: 10,000.
    /// </summary>
    public int MaxEntries { get; set; } = 10_000;
}

/// <summary>
/// A page of audit-log entries plus the total number of entries matching the query's filter, used
/// to render a numbered pager on the Audit Log page.
/// </summary>
/// <param name="Items">The entries for the requested page, newest-first.</param>
/// <param name="TotalCount">Total entries matching the filter across all pages.</param>
public sealed record AuditLogPage(IReadOnlyList<AuditLogEntry> Items, int TotalCount);
