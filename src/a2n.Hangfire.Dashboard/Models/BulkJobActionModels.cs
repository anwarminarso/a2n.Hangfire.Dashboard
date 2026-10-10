namespace a2n.Hangfire.Dashboard;

/// <summary>
/// Progress or outcome of a batched requeue or delete
/// (<see cref="Services.HangfireMonitorService.RequeueJobsInBatchesAsync"/>,
/// <see cref="Services.HangfireMonitorService.DeleteJobsInBatchesAsync"/>).
/// </summary>
/// <param name="Total">How many jobs were asked for.</param>
/// <param name="Processed">How many of them have been tried so far.</param>
/// <param name="Succeeded">How many of those were changed.</param>
/// <param name="Cancelled">True when the run stopped before every job was tried.</param>
public sealed record BulkJobActionResult(int Total, int Processed, int Succeeded, bool Cancelled)
{
    /// <summary>
    /// Jobs tried but not changed: with an expected state, the ones that had already left it.
    /// </summary>
    public int Skipped => Processed - Succeeded;
}
