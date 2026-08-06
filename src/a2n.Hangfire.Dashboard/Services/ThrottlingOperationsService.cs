using Hangfire;
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

    public ThrottlingOperationsService(JobStorage storage, AuditLogService audit)
    {
        _storage = storage;
        _audit = audit;
    }

    /// <summary>
    /// Detaches a background job from the given semaphore, freeing its slot.
    /// Idempotent: detaching a job that no longer holds the semaphore is a no-op.
    /// </summary>
    public void DetachFromSemaphore(string semaphoreId, string jobId)
    {
        if (string.IsNullOrWhiteSpace(semaphoreId) || string.IsNullOrWhiteSpace(jobId))
            return;

        using (var connection = _storage.GetConnection())
        using (var transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveFromSet($"sync:j:sm:{semaphoreId}", jobId);
            transaction.Commit();
        }

        _audit.Log(AuditAction.ThrottlingSemaphoreDetached, target: semaphoreId, reason: $"Detached job #{jobId}");
    }

    /// <summary>
    /// Detaches a background job from the given mutex, releasing it. Removes both the
    /// "{mutexId}/{jobId}" registry pair and the holder entry, mirroring the writer.
    /// Idempotent: detaching a job that no longer holds the mutex is a no-op.
    /// </summary>
    public void DetachFromMutex(string mutexId, string jobId)
    {
        if (string.IsNullOrWhiteSpace(mutexId) || string.IsNullOrWhiteSpace(jobId))
            return;

        using (var connection = _storage.GetConnection())
        using (var transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveFromSet("sync:set:mx", $"{mutexId}/{jobId}");
            transaction.RemoveFromSet($"sync:mx:{mutexId}", jobId);
            transaction.Commit();
        }

        _audit.Log(AuditAction.ThrottlingMutexDetached, target: mutexId, reason: $"Detached job #{jobId}");
    }
}
