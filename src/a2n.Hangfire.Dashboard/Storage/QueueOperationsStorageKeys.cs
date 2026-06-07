namespace a2n.Hangfire.Dashboard.Storage;

/// <summary>
/// Single source of truth for the Hangfire storage keys used by the queue-pause / maintenance
/// subsystem. Both <see cref="QueuePauseServerFilter"/> (server side) and
/// <see cref="Services.QueueOperationsService"/> (dashboard side) read and write these keys, so
/// they must agree exactly — keeping them here prevents a silent drift that would make the pause
/// filter look at a different key than the UI writes to.
/// </summary>
public static class QueueOperationsStorageKeys
{
    /// <summary>Hangfire set holding the names of explicitly paused queues.</summary>
    public const string PausedSetKey = "queue:paused";

    /// <summary>Hangfire hash holding maintenance-mode state.</summary>
    public const string StateHashKey = "queue:operations:state";

    /// <summary>Hash field: maintenance-enabled boolean ("true"/"false").</summary>
    public const string FieldMaintenanceEnabled = "maintenance.enabled";

    /// <summary>Hash field: ISO-8601 UTC timestamp when maintenance mode was last enabled.</summary>
    public const string FieldMaintenanceAt = "maintenance.at";

    /// <summary>Hash field: actor (user) who enabled maintenance mode.</summary>
    public const string FieldMaintenanceBy = "maintenance.by";

    /// <summary>Hash field: operator-supplied reason for maintenance mode.</summary>
    public const string FieldMaintenanceReason = "maintenance.reason";
}
