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
/// the direction (see <see cref="GridSort"/>), and ties are broken by job id so the order is stable
/// across refreshes.
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
                GridSort.By(jobs, j => j.Id, j => !string.IsNullOrEmpty(j.Id), StringComparer.OrdinalIgnoreCase, descending),
            RecurringJobSortColumn.Job =>
                GridSort.By(jobs, GetJobName, j => j.Job is not null, StringComparer.OrdinalIgnoreCase, descending),
            RecurringJobSortColumn.NextExecution =>
                GridSort.By(jobs, j => j.NextExecution, j => j.NextExecution.HasValue, Comparer<DateTime?>.Default, descending),
            RecurringJobSortColumn.LastExecution =>
                GridSort.By(jobs, j => j.LastExecution, j => j.LastExecution.HasValue, Comparer<DateTime?>.Default, descending),
            RecurringJobSortColumn.Created =>
                GridSort.By(jobs, j => j.CreatedAt, j => j.CreatedAt.HasValue, Comparer<DateTime?>.Default, descending),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null)
        };

        return ordered.ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The name shown in the grid's Job column, which is also what the Job sort orders by.
    /// </summary>
    public static string GetJobName(RecurringJobDto job) =>
        job?.Job is null ? "(unknown)" : JobNameHelper.GetDisplayName(job.Job, null);
}
