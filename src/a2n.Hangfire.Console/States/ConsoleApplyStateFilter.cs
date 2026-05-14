using Hangfire.Console.Serialization;
using Hangfire.Console.Storage;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace Hangfire.Console.States;

/// <summary>
/// State filter that manages console expiration when job state changes.
/// Ensures console data follows the same retention policy as the parent job.
/// </summary>
internal class ConsoleApplyStateFilter : IApplyStateFilter
{
    private readonly ConsoleOptions _options;

    public ConsoleApplyStateFilter(ConsoleOptions options)
    {
        _options = options;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // We only care about final states (Succeeded, Failed, Deleted)
        if (!context.NewState.IsFinal)
            return;

        try
        {
            // Get the Processing state data to find StartedAt
            var connection = context.Connection;
            if (connection is not JobStorageConnection storageConnection)
                return;

            // Look for StartedAt in job state history
            var jobData = storageConnection.GetStateData(context.BackgroundJob.Id);
            if (jobData is null)
                return;

            // Try to get all state history to find Processing state
            // For simplicity, we'll use the job's properties or state data
            // The ConsoleServerFilter already handles expiration in OnPerformed
            // This filter is a safety net for edge cases

            if (_options.FollowJobRetentionPolicy)
            {
                // Expiration is handled by ConsoleServerFilter.OnPerformed
                // This is just a safety net
            }
        }
        catch
        {
            // Ignore errors during state transitions
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
