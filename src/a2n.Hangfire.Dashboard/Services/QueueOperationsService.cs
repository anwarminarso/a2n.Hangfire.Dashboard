using System.Globalization;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Reads and mutates the queue-pause and maintenance-mode state stored in Hangfire's KV store.
/// </summary>
/// <remarks>
/// Storage layout (no schema changes — uses Hangfire's existing connection primitives):
/// <list type="bullet">
///   <item><description>Set <c>queue:paused</c> — names of explicitly paused queues.</description></item>
///   <item><description>Hash <c>queue:operations:state</c> — fields: <c>maintenance.enabled</c>, <c>maintenance.at</c>, <c>maintenance.by</c>, <c>maintenance.reason</c>.</description></item>
/// </list>
/// </remarks>
public class QueueOperationsService
{
    internal const string PausedSetKey = Storage.QueueOperationsStorageKeys.PausedSetKey;
    internal const string StateHashKey = Storage.QueueOperationsStorageKeys.StateHashKey;

    private const string FieldMaintenanceEnabled = Storage.QueueOperationsStorageKeys.FieldMaintenanceEnabled;
    private const string FieldMaintenanceAt = Storage.QueueOperationsStorageKeys.FieldMaintenanceAt;
    private const string FieldMaintenanceBy = Storage.QueueOperationsStorageKeys.FieldMaintenanceBy;
    private const string FieldMaintenanceReason = Storage.QueueOperationsStorageKeys.FieldMaintenanceReason;

    private readonly JobStorage _storage;
    private readonly DashboardUIOptions _options;
    private readonly AuditLogService _audit;
    private readonly AuditActorAccessor _actor;
    private readonly ILogger<QueueOperationsService> _logger;

    public QueueOperationsService(
        JobStorage storage,
        DashboardUIOptions options,
        AuditLogService audit,
        AuditActorAccessor actor = null,
        ILogger<QueueOperationsService> logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _actor = actor;
        _logger = logger;
    }

    /// <summary>Returns the current pause / maintenance state.</summary>
    public QueueOperationsState GetState()
    {
        var state = new QueueOperationsState();

        try
        {
            using var connection = _storage.GetReadOnlyConnection();
            if (connection is not JobStorageConnection storageConnection) return state;

            var pausedQueues = storageConnection.GetAllItemsFromSet(PausedSetKey);
            if (pausedQueues is not null)
            {
                state.PausedQueues = new HashSet<string>(pausedQueues, StringComparer.OrdinalIgnoreCase);
            }

            var hash = storageConnection.GetAllEntriesFromHash(StateHashKey);
            if (hash is not null)
            {
                state.MaintenanceMode = hash.TryGetValue(FieldMaintenanceEnabled, out var enabled)
                    && string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);
                if (hash.TryGetValue(FieldMaintenanceAt, out var atRaw) &&
                    DateTime.TryParse(atRaw, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var at))
                {
                    state.MaintenanceEnabledAtUtc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime();
                }
                state.MaintenanceEnabledBy = hash.GetValueOrDefault(FieldMaintenanceBy);
                state.MaintenanceReason = hash.GetValueOrDefault(FieldMaintenanceReason);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "QueueOperationsService.GetState failed");
        }

        return state;
    }

    /// <summary>Pauses a specific queue. Idempotent — safe to call multiple times.</summary>
    public void PauseQueue(string queueName, string reason = null)
    {
        if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentNullException(nameof(queueName));
        EnsureEnabled();

        var normalized = queueName.Trim();

        using var connection = _storage.GetConnection();
        using var tx = connection.CreateWriteTransaction();
        tx.AddToSet(PausedSetKey, normalized);
        tx.Commit();

        _audit.Log(AuditAction.QueuePaused, target: normalized, reason: reason);
    }

    /// <summary>Resumes a previously paused queue. Idempotent.</summary>
    public void ResumeQueue(string queueName, string reason = null)
    {
        if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentNullException(nameof(queueName));
        EnsureEnabled();

        var normalized = queueName.Trim();

        using var connection = _storage.GetConnection();
        using var tx = connection.CreateWriteTransaction();
        tx.RemoveFromSet(PausedSetKey, normalized);
        tx.Commit();

        _audit.Log(AuditAction.QueueResumed, target: normalized, reason: reason);
    }

    /// <summary>Returns true if the named queue is paused (either explicitly or via maintenance mode).</summary>
    public bool IsQueuePaused(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName)) return false;
        if (!_options.QueueOperations.Enabled) return false;

        var state = GetState();
        if (state.MaintenanceMode) return true;
        return state.PausedQueues.Contains(queueName);
    }

    /// <summary>Enables maintenance mode (pauses all queues globally). Idempotent.</summary>
    public void EnableMaintenanceMode(string reason = null)
    {
        EnsureEnabled();

        var now = DateTime.UtcNow;
        var by = _actor?.HasActor == true ? _actor.User : null;
        var entries = new[]
        {
            new KeyValuePair<string, string>(FieldMaintenanceEnabled, "true"),
            new KeyValuePair<string, string>(FieldMaintenanceAt, now.ToString("O", CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(FieldMaintenanceBy, by ?? string.Empty),
            new KeyValuePair<string, string>(FieldMaintenanceReason, reason ?? string.Empty),
        };

        using var connection = _storage.GetConnection();
        using var tx = connection.CreateWriteTransaction();
        tx.SetRangeInHash(StateHashKey, entries);
        tx.Commit();

        _audit.Log(AuditAction.MaintenanceEnabled, reason: reason);
    }

    /// <summary>Disables maintenance mode (does NOT clear individual queue pauses).</summary>
    public void DisableMaintenanceMode(string reason = null)
    {
        EnsureEnabled();

        var entries = new[]
        {
            new KeyValuePair<string, string>(FieldMaintenanceEnabled, "false"),
        };

        using var connection = _storage.GetConnection();
        using var tx = connection.CreateWriteTransaction();
        tx.SetRangeInHash(StateHashKey, entries);
        tx.Commit();

        _audit.Log(AuditAction.MaintenanceDisabled, reason: reason);
    }

    /// <summary>Convenience for the pause filter and any UI that wants a fresh paused-set snapshot.</summary>
    internal IReadOnlyCollection<string> GetPausedQueuesRaw()
    {
        try
        {
            using var connection = _storage.GetReadOnlyConnection();
            if (connection is JobStorageConnection storageConnection)
                return storageConnection.GetAllItemsFromSet(PausedSetKey) ?? [];
            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Convenience: maintenance flag only (used by the pause filter on hot path).</summary>
    internal bool GetMaintenanceFlagRaw()
    {
        try
        {
            using var connection = _storage.GetReadOnlyConnection();
            if (connection is not JobStorageConnection storageConnection) return false;

            var hash = storageConnection.GetAllEntriesFromHash(StateHashKey);
            return hash != null
                && hash.TryGetValue(FieldMaintenanceEnabled, out var v)
                && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.QueueOperations.Enabled)
        {
            throw new InvalidOperationException(
                "Queue operations are disabled (DashboardUIOptions.QueueOperations.Enabled = false).");
        }
    }
}
