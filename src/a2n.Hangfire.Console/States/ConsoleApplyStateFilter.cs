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
        if (!_options.FollowJobRetentionPolicy)
        {
            // Console sessions use their own expiration timeout.
            // Do not expire here, will be expired by ConsoleServerFilter.
            return;
        }

        var jobDetails = context.Storage.GetMonitoringApi().JobDetails(context.BackgroundJob.Id);
        if (jobDetails?.History is null)
            return;

        var expiration = new ConsoleExpirationTransaction((JobStorageTransaction)transaction);

        foreach (var state in jobDetails.History)
        {
            if (state.StateName != ProcessingState.StateName)
                continue;

            if (!state.Data.TryGetValue("StartedAt", out var startedAtStr))
                continue;

            var startedAt = JobHelper.DeserializeDateTime(startedAtStr);
            var consoleId = new ConsoleId(context.BackgroundJob.Id, startedAt);

            if (context.NewState.IsFinal)
            {
                // Job in final state is a subject for expiration.
                // To keep storage clean, its console sessions should also be expired.
                expiration.Expire(consoleId, context.JobExpirationTimeout);
            }
            else
            {
                // Job will be persisted, so should its console sessions.
                expiration.Persist(consoleId);
            }
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
