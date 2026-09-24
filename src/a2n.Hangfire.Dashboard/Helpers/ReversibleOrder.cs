using Hangfire.Storage.Monitoring;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Lets a storage-paged job list be shown in either direction of the storage's own order.
/// <para>
/// Hangfire's monitoring API returns one page at a time with no sort parameter, so re-ordering the
/// visible rows would only sort that page. Instead, a reversed page is read from the opposite end
/// of the list (default positions <c>total - from - count</c> onwards) and flipped, which gives a
/// correct order across every page on any storage.
/// </para>
/// <para>
/// Storages differ in which way a list runs, so the direction is not assumed: after each fetch
/// <see cref="NewestFirst"/> is read from the rows' timestamps.
/// </para>
/// </summary>
internal sealed class ReversibleOrder
{
    /// <summary>Whether pages are shown in the opposite direction to the storage order.</summary>
    public bool Reversed { get; private set; }

    /// <summary>
    /// The direction of the rows last fetched: <c>true</c> when the newest row comes first,
    /// <c>false</c> when the oldest does, <c>null</c> until a page shows two distinct timestamps.
    /// </summary>
    public bool? NewestFirst { get; private set; }

    public void Toggle()
    {
        Reversed = !Reversed;
        if (NewestFirst.HasValue)
            NewestFirst = !NewestFirst;
    }

    /// <summary>
    /// The storage range to read for the requested page. For a reversed page this is the matching
    /// range from the other end of the list; the count shrinks on the last, partial page.
    /// </summary>
    public (int From, int Count) StorageRange(long total, int from, int count)
    {
        if (!Reversed)
            return (from, count);

        var end = total - from;
        if (end <= 0 || count <= 0)
            return (0, 0);

        var start = Math.Max(0, end - count);
        return ((int)start, (int)(end - start));
    }

    /// <summary>Fetches a page of jobs in the current direction and updates <see cref="NewestFirst"/>.</summary>
    public JobList<T> Fetch<T>(
        Func<int, int, JobList<T>> fetch, long total, int from, int count, Func<T, DateTime?> timestamp)
    {
        var (storageFrom, storageCount) = StorageRange(total, from, count);
        if (storageCount <= 0)
            return new JobList<T>(Array.Empty<KeyValuePair<string, T>>());

        var page = fetch(storageFrom, storageCount);
        if (Reversed && page is not null)
            page = new JobList<T>(Enumerable.Reverse(page));

        if (page is not null)
            Observe(page.Select(p => p.Value is null ? null : timestamp(p.Value)));

        return page;
    }

    /// <summary>
    /// Fetches a page of job ids in the current direction. The caller reports the rows' timestamps
    /// with <see cref="Observe"/> once it has loaded them.
    /// </summary>
    public IReadOnlyList<string> FetchIds(Func<int, int, IReadOnlyList<string>> fetch, long total, int from, int count)
    {
        var (storageFrom, storageCount) = StorageRange(total, from, count);
        if (storageCount <= 0)
            return Array.Empty<string>();

        var ids = fetch(storageFrom, storageCount) ?? Array.Empty<string>();
        return Reversed ? ids.Reverse().ToList() : ids;
    }

    /// <summary>
    /// Updates <see cref="NewestFirst"/> from the timestamps of the displayed rows, in display order.
    /// Rows without a timestamp are ignored; a page with no two distinct timestamps leaves it as is.
    /// </summary>
    public void Observe(IEnumerable<DateTime?> timestamps)
    {
        var known = timestamps.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        if (known.Count < 2 || known[0] == known[^1])
            return;

        NewestFirst = known[0] > known[^1];
    }
}
