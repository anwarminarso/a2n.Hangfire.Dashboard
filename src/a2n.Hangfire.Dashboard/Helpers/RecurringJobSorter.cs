using Hangfire.Storage;

namespace a2n.Hangfire.Dashboard.Helpers;

/// <summary>
/// Columns of the Recurring Jobs grid that can be sorted.
/// </summary>
internal enum RecurringJobSortColumn
{
    /// <summary>Storage order; no sorting applied.</summary>
    None,
    Id,
    Job,
    NextExecution,
    LastExecution,
    Created
}

/// <summary>
/// Sorts the complete recurring job list for the Recurring Jobs grid. Jobs without a value for the
/// sort column (no next execution, never run, unresolved job) are always placed last, whichever
/// the direction, and ties are broken by job id so the order is stable across refreshes.
/// </summary>
internal static class RecurringJobSorter
{
    public static IReadOnlyList<RecurringJobDto> Sort(
        IReadOnlyList<RecurringJobDto> jobs, RecurringJobSortColumn column, bool descending)
    {
        if (column == RecurringJobSortColumn.None || jobs.Count < 2)
            return jobs;

        var ordered = column switch
        {
            RecurringJobSortColumn.Id =>
                OrderBy(jobs, j => j.Id, j => !string.IsNullOrEmpty(j.Id), StringComparer.OrdinalIgnoreCase, descending),
            RecurringJobSortColumn.Job =>
                OrderBy(jobs, GetJobName, j => j.Job is not null, StringComparer.OrdinalIgnoreCase, descending),
            RecurringJobSortColumn.NextExecution =>
                OrderBy(jobs, j => j.NextExecution, j => j.NextExecution.HasValue, Comparer<DateTime?>.Default, descending),
            RecurringJobSortColumn.LastExecution =>
                OrderBy(jobs, j => j.LastExecution, j => j.LastExecution.HasValue, Comparer<DateTime?>.Default, descending),
            RecurringJobSortColumn.Created =>
                OrderBy(jobs, j => j.CreatedAt, j => j.CreatedAt.HasValue, Comparer<DateTime?>.Default, descending),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null)
        };

        return ordered.ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The name shown in the grid's Job column, which is also what the Job sort orders by.
    /// </summary>
    public static string GetJobName(RecurringJobDto job) =>
        job?.Job is null ? "(unknown)" : JobNameHelper.GetDisplayName(job.Job, null);

    private static IOrderedEnumerable<RecurringJobDto> OrderBy<TKey>(
        IEnumerable<RecurringJobDto> jobs,
        Func<RecurringJobDto, TKey> key,
        Func<RecurringJobDto, bool> hasValue,
        IComparer<TKey> comparer,
        bool descending)
    {
        var missingLast = jobs.OrderBy(j => hasValue(j) ? 0 : 1);
        return descending
            ? missingLast.ThenByDescending(key, comparer)
            : missingLast.ThenBy(key, comparer);
    }
}
