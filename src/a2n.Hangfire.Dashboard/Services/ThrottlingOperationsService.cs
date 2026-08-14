using Hangfire;
using Hangfire.Storage;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Mutating operations against Hangfire.Throttling primitives, audited like other dashboard
/// operations. Detaching removes a holder entry so the primitive's slot is freed — the recovery
/// action for jobs that aborted (e.g. on a dead server) without releasing their slot. Storage
/// writes mirror the Hangfire.Throttling package's own detach behavior.
/// </summary>
public class ThrottlingOperationsService
{
    private readonly JobStorage _storage;
    private readonly AuditLogService _audit;
    private readonly DashboardUIOptions _options;

    public ThrottlingOperationsService(JobStorage storage, AuditLogService audit, DashboardUIOptions options = null)
    {
        _storage = storage;
        _audit = audit;
        _options = options ?? new DashboardUIOptions();
    }

    /// <summary>
    /// Detaches a background job from the given semaphore, freeing its slot.
    /// Idempotent: detaching a job that no longer holds the semaphore is a no-op.
    /// Returns whether the holder was actually present and removed.
    /// </summary>
    public bool DetachFromSemaphore(string semaphoreId, string jobId)
    {
        if (_options.IsReadOnly || string.IsNullOrWhiteSpace(semaphoreId) || string.IsNullOrWhiteSpace(jobId))
            return false;

        // Ids are lowercased by the writer, so normalize before touching keys.
        semaphoreId = semaphoreId.ToLowerInvariant();

        using var connection = _storage.GetConnection();

        // RemoveFromSet succeeds silently whether or not the entry was there, so check first.
        // Reporting "detached" for a holder that had already released would tell the operator the
        // slot was freed by them when it was not, and write an audit entry for a no-op.
        if (!HoldsSemaphore(connection, semaphoreId, jobId))
            return false;

        using (var transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveFromSet($"sync:j:sm:{semaphoreId}", jobId);
            transaction.Commit();
        }

        _audit.Log(AuditAction.ThrottlingSemaphoreDetached, target: semaphoreId, reason: $"Detached job #{jobId}");
        return true;
    }

    /// <summary>
    /// Detaches a background job from the given mutex, releasing it. Removes both the
    /// "{mutexId}/{jobId}" registry pair and the holder entry, mirroring the writer.
    /// Idempotent: detaching a job that no longer holds the mutex is a no-op.
    /// Returns whether the holder was actually present and removed.
    /// </summary>
    public bool DetachFromMutex(string mutexId, string jobId)
    {
        if (_options.IsReadOnly || string.IsNullOrWhiteSpace(mutexId) || string.IsNullOrWhiteSpace(jobId))
            return false;

        mutexId = mutexId.ToLowerInvariant();

        using var connection = _storage.GetConnection();

        if (!HoldsMutex(connection, mutexId, jobId))
            return false;

        using (var transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveFromSet("sync:set:mx", $"{mutexId}/{jobId}");
            transaction.RemoveFromSet($"sync:mx:{mutexId}", jobId);
            transaction.Commit();
        }

        _audit.Log(AuditAction.ThrottlingMutexDetached, target: mutexId, reason: $"Detached job #{jobId}");
        return true;
    }

    private static bool HoldsSemaphore(IStorageConnection connection, string semaphoreId, string jobId)
    {
        if (connection is not JobStorageConnection storageConnection)
            return true; // Cannot verify — attempt the write rather than refuse the recovery action.

        return storageConnection.GetAllItemsFromSet($"sync:j:sm:{semaphoreId}")?.Contains(jobId) == true;
    }

    private static bool HoldsMutex(IStorageConnection connection, string mutexId, string jobId)
    {
        if (connection is not JobStorageConnection storageConnection)
            return true;

        return storageConnection.GetAllItemsFromSet("sync:set:mx")?.Contains($"{mutexId}/{jobId}") == true
            || storageConnection.GetAllItemsFromSet($"sync:mx:{mutexId}")?.Contains(jobId) == true;
    }
}
