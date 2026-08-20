using Hangfire;
using Hangfire.Storage;
using a2n.Hangfire.Dashboard.Storage;

namespace a2n.Hangfire.Dashboard.Services;

/// <summary>
/// Reads tag data from Hangfire storage.
/// Compatible with data written by both original Hangfire.Tags and a2n.Hangfire.Tags.
/// Key patterns: "tags" (all tags), "tags:{jobId}" (job's tags), "tags:{tag}" (jobs with tag)
/// </summary>
public class TagsDataReader
{
    private readonly JobStorage _storage;

    public TagsDataReader(JobStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Gets all tags for a specific job.
    /// </summary>
    public string[] GetJobTags(string jobId)
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return [];

        return storageConnection.GetAllItemsFromSet($"tags:{jobId}").ToArray();
    }

    /// <summary>
    /// Gets total count of unique tags.
    /// </summary>
    public long GetTagsCount()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return 0;

        // Called from NavMenu, so a storage without the extended API must not throw here.
        return SetCounting.Count(storageConnection, "tags");
    }

    /// <summary>
    /// Gets all unique tags.
    /// </summary>
    public string[] GetAllTags()
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return [];

        return storageConnection.GetAllItemsFromSet("tags").ToArray();
    }

    /// <summary>
    /// Gets job IDs that have a specific tag.
    /// </summary>
    public IReadOnlyList<string> GetJobsByTag(string tag, int from, int count)
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return [];

        return storageConnection.GetRangeFromSet($"tags:{tag}", from, from + count - 1);
    }

    /// <summary>
    /// Gets count of jobs with a specific tag.
    /// </summary>
    public long GetJobCountByTag(string tag)
    {
        using var connection = _storage.GetReadOnlyConnection();
        if (connection is not JobStorageConnection storageConnection)
            return 0;

        return SetCounting.Count(storageConnection, $"tags:{tag}");
    }
}
