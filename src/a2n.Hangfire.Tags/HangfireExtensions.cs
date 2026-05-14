using Hangfire.Server;
using Hangfire.Tags.Storage;

namespace Hangfire.Tags;

/// <summary>
/// Extension methods for adding/removing tags from jobs.
/// API-compatible with the original Hangfire.Tags package.
/// </summary>
public static class HangfireExtensions
{
    /// <summary>
    /// Adds tags to the job with the specified id.
    /// </summary>
    public static string AddTags(this string jobId, IEnumerable<string> tags)
        => jobId.AddTags(tags.ToArray());

    /// <summary>
    /// Adds tags to the job with the specified id.
    /// </summary>
    public static string AddTags(this string jobId, params string[] tags)
    {
        using var storage = new TagsStorage(JobStorage.Current);
        storage.AddTags(jobId, tags);
        return jobId;
    }

    /// <summary>
    /// Removes tags from the job with the specified id.
    /// </summary>
    public static string RemoveTags(this string jobId, IEnumerable<string> tags)
        => jobId.RemoveTags(tags.ToArray());

    /// <summary>
    /// Removes tags from the job with the specified id.
    /// </summary>
    public static string RemoveTags(this string jobId, params string[] tags)
    {
        using var storage = new TagsStorage(JobStorage.Current);
        storage.RemoveTags(jobId, tags);
        return jobId;
    }

    /// <summary>
    /// Gets tags for the job with the specified id.
    /// </summary>
    public static string[] GetTags(this string jobId)
    {
        using var storage = new TagsStorage(JobStorage.Current);
        return storage.GetTags(jobId);
    }

    /// <summary>
    /// Adds tags to the job from a PerformContext.
    /// </summary>
    public static PerformContext AddTags(this PerformContext context, params string[] tags)
    {
        context.BackgroundJob.Id.AddTags(tags);
        return context;
    }

    /// <summary>
    /// Adds tags to the job from a PerformContext.
    /// </summary>
    public static PerformContext AddTags(this PerformContext context, IEnumerable<string> tags)
        => context.AddTags(tags.ToArray());

    /// <summary>
    /// Removes tags from the job via PerformContext.
    /// </summary>
    public static PerformContext RemoveTags(this PerformContext context, params string[] tags)
    {
        context.BackgroundJob.Id.RemoveTags(tags);
        return context;
    }

    /// <summary>
    /// Removes tags from the job via PerformContext.
    /// </summary>
    public static PerformContext RemoveTags(this PerformContext context, IEnumerable<string> tags)
        => context.RemoveTags(tags.ToArray());
}
