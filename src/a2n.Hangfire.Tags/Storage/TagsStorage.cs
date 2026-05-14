using Hangfire.Storage;

namespace Hangfire.Tags.Storage;

/// <summary>
/// Storage operations for tags. Uses the same key patterns as the original Hangfire.Tags.
/// Key patterns:
///   "tags" — set of all unique tag names
///   "tags:{jobId}" — set of tags for a specific job
///   "tags:{tagName}" — set of job IDs with that tag
/// </summary>
internal class TagsStorage : IDisposable
{
    private const string SetKey = "tags";

    private readonly JobStorageConnection _connection;
    private readonly TagsOptions _options;

    public TagsStorage(JobStorage jobStorage, TagsOptions? options = null)
    {
        _options = options ?? new TagsOptions();
        var connection = jobStorage.GetConnection();
        if (connection is not JobStorageConnection jobStorageConnection)
            throw new NotSupportedException("Storage connections must implement JobStorageConnection");
        _connection = jobStorageConnection;
    }

    public void Dispose() => _connection.Dispose();

    public void AddTags(string jobId, IEnumerable<string> tags)
    {
        using var tran = _connection.CreateWriteTransaction();

        foreach (var tag in tags)
        {
            var cleanTag = CleanTag(tag);
            if (string.IsNullOrEmpty(cleanTag)) continue;

            var score = DateTime.Now.Ticks;

            tran.AddToSet(SetKey, cleanTag, score);
            tran.AddToSet(GetJobSetKey(jobId), cleanTag, score);
            tran.AddToSet(GetTagSetKey(cleanTag), jobId, score);
        }

        tran.Commit();
    }

    public void RemoveTags(string jobId, IEnumerable<string> tags)
    {
        using var tran = _connection.CreateWriteTransaction();

        foreach (var tag in tags)
        {
            var cleanTag = CleanTag(tag);
            if (string.IsNullOrEmpty(cleanTag)) continue;

            tran.RemoveFromSet(GetJobSetKey(jobId), cleanTag);
            tran.RemoveFromSet(GetTagSetKey(cleanTag), jobId);

            if (_connection.GetSetCount(GetTagSetKey(cleanTag)) == 0)
            {
                tran.RemoveFromSet(SetKey, cleanTag);
            }
        }

        tran.Commit();
    }

    public string[] GetTags(string jobId)
    {
        return _connection.GetAllItemsFromSet(GetJobSetKey(jobId)).ToArray();
    }

    public long GetTagsCount()
    {
        return _connection.GetSetCount(SetKey);
    }

    public void Expire(string jobId, TimeSpan expireIn)
    {
        var tags = GetTags(jobId);
        if (tags.Length == 0) return;

        using var tran = (JobStorageTransaction)_connection.CreateWriteTransaction();

        tran.ExpireSet(GetJobSetKey(jobId), expireIn);
        foreach (var tag in tags)
        {
            tran.ExpireSet(GetTagSetKey(tag), expireIn);
        }

        tran.Commit();
    }

    public void Persist(string jobId)
    {
        var tags = GetTags(jobId);
        if (tags.Length == 0) return;

        using var tran = (JobStorageTransaction)_connection.CreateWriteTransaction();

        tran.PersistSet(GetJobSetKey(jobId));
        foreach (var tag in tags)
        {
            tran.PersistSet(GetTagSetKey(tag));
        }

        tran.Commit();
    }

    private string CleanTag(string tag)
    {
        var result = tag.Replace(",", "");

        if ((_options.Clean & Clean.Lowercase) == Clean.Lowercase)
            result = result.ToLowerInvariant();

        if ((_options.Clean & Clean.Punctuation) == Clean.Punctuation)
            result = new string(result.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray())
                .Replace(' ', '-').Replace("--", "-");

        if (_options.MaxTagLength.HasValue && result.Length > _options.MaxTagLength.Value)
            result = result[..(_options.MaxTagLength.Value - 5)];

        return result;
    }

    private static string GetJobSetKey(string jobId) => $"{SetKey}:{jobId}";
    private static string GetTagSetKey(string tag) => $"{SetKey}:{tag}";
}
