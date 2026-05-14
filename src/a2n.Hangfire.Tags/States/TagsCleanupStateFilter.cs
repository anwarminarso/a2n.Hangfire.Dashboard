using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Tags.Storage;

namespace Hangfire.Tags.States;

/// <summary>
/// State filter that manages tag expiration when job state changes.
/// </summary>
internal class TagsCleanupStateFilter : IApplyStateFilter
{
    private readonly TagsOptions _options;

    public TagsCleanupStateFilter(TagsOptions options)
    {
        _options = options;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        using var storage = new TagsStorage(context.Storage, _options);
        var jobId = context.BackgroundJob.Id;

        if (context.NewState.IsFinal)
        {
            // Final state — set tag expiration to match job expiration
            storage.Expire(jobId, context.JobExpirationTimeout);
        }
        else
        {
            // Non-final state — persist tags (remove expiration)
            storage.Persist(jobId);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
