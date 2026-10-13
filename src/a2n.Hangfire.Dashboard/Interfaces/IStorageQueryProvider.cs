using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Interfaces;

/// <summary>
/// Provides database-level search, filter, and pagination for job queries.
/// Implementations exist per storage backend (SQL Server, PostgreSQL).
/// A GenericQueryProvider fallback uses IMonitoringApi with client-side filtering.
/// </summary>
public interface IStorageQueryProvider
{
    /// <summary>
    /// Unified advanced search and filter method.
    /// Returns jobs matching all specified non-null filter criteria (AND logic).
    /// Supports: state, date range, queue, server, duration, tags, recurring job ID,
    /// job name pattern, exception pattern, and content search (stack trace + console output).
    /// Uses multi-stage query approach for optimal performance.
    /// </summary>
    /// <param name="criteria">Filter criteria (all fields optional, AND between non-null)</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="ct">Cancellation token</param>
    Task<PagedResult<JobSummaryDto>> GetJobsWithFilterAsync(
        JobFilterCriteria criteria, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Returns jobs associated with a specific tag using JOIN-based filtering.
    /// </summary>
    /// <param name="tag">Tag value (exact match, case-insensitive)</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="ct">Cancellation token</param>
    Task<PagedResult<JobSummaryDto>> GetJobsByTagAsync(
        string tag, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Returns all tags with their associated job counts (GROUP BY aggregation).
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Tags ordered by count descending</returns>
    Task<IReadOnlyList<TagCountDto>> GetTagCloudAsync(CancellationToken ct);

    /// <summary>
    /// Returns jobs in a specific state with server-side pagination (OFFSET/FETCH).
    /// </summary>
    /// <param name="stateName">State name (e.g., "Succeeded", "Failed")</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="ct">Cancellation token</param>
    Task<PagedResult<JobSummaryDto>> GetJobsByStateAsync(
        string stateName, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Returns the top N slowest jobs by PerformanceDuration within a time range.
    /// </summary>
    /// <param name="count">Number of results (1–100)</param>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<SlowestJobDto>> GetSlowestJobsAsync(
        int count, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// True when <see cref="GetFailureGroupsAsync"/> is implemented. The Failed page reads it to decide
    /// whether to offer its Exception filter without running a grouping query first. Defaults to
    /// false, so third-party providers keep compiling and the filter stays hidden for them.
    /// </summary>
    bool SupportsFailureGrouping => false;

    /// <summary>
    /// Groups the jobs currently in the Failed state by their <see cref="Helpers.FailureFingerprint"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A job's fingerprint is the value <c>FailureFingerprintFilter</c> stored on it, when that value
    /// is of the current version (<see cref="Helpers.FailureFingerprint.IsCurrentVersion"/>). Jobs
    /// without one are fingerprinted on the fly from their state data and stored job type, newest
    /// first and at most <see cref="JobFilterCriteria.CountLimit"/> of them; beyond that the result is
    /// partial. Nothing is written back, so this is safe in read-only mode.
    /// </para>
    /// <para>
    /// Labels are read from one sample job per group, the newest, for the
    /// <paramref name="labelCount"/> largest groups only.
    /// </para>
    /// <para>
    /// This is a default interface method that reports grouping as unsupported, so third-party
    /// providers keep compiling and the Failed page hides its Exception filter for them.
    /// </para>
    /// </remarks>
    /// <param name="labelCount">How many of the largest groups to label (see <see cref="FailureGroupsResult.DefaultLabelCount"/>).</param>
    /// <param name="ct">Cancellation token</param>
    Task<FailureGroupsResult> GetFailureGroupsAsync(int labelCount, CancellationToken ct)
        => Task.FromResult(FailureGroupsResult.Unsupported());

    /// <summary>
    /// Returns the newest <paramref name="count"/> jobs in a group from
    /// <see cref="GetFailureGroupsAsync"/> that are still in the Failed state, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the group rather than its fingerprint because the group carries the ids of the jobs the
    /// fallback fingerprinted (<see cref="FailureGroupDto.ComputedJobIds"/>), so they are not read and
    /// fingerprinted again. This is a default interface method that returns no jobs.
    /// </para>
    /// <para>
    /// "Newest" is by job id, that is creation order, on the SQL Server and PostgreSQL adapters, and by
    /// failure time on the generic provider. For a delayed or requeued job the two can differ.
    /// </para>
    /// </remarks>
    /// <param name="group">A group returned by <see cref="GetFailureGroupsAsync"/>.</param>
    /// <param name="count">Maximum number of jobs to return.</param>
    /// <param name="ct">Cancellation token</param>
    Task<IReadOnlyList<JobSummaryDto>> GetFailureGroupJobsAsync(FailureGroupDto group, int count, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JobSummaryDto>>(Array.Empty<JobSummaryDto>());

    /// <summary>
    /// Returns the ids of every job in a group from <see cref="GetFailureGroupsAsync"/> that is still
    /// in the Failed state, newest first: all the jobs carrying the group's stored fingerprint, plus
    /// the fallback members the group carries. The Failed page's group actions requeue or delete
    /// exactly these.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the group for the same reason as <see cref="GetFailureGroupJobsAsync"/>: the jobs without
    /// a stored fingerprint are the ones the grouping already read
    /// (<see cref="FailureGroupDto.ComputedJobIds"/>, at most <see cref="JobFilterCriteria.CountLimit"/>),
    /// so they are not read and fingerprinted a second time. When that result was partial, older
    /// failures without a stored fingerprint are not included.
    /// </para>
    /// <para>
    /// "Newest" means the same as in <see cref="GetFailureGroupJobsAsync"/>. The default implementation
    /// takes the ids from <see cref="GetFailureGroupJobsAsync"/> without a limit, so a provider that
    /// implements only that still gets the group actions; it returns no ids for providers without
    /// grouping.
    /// </para>
    /// </remarks>
    /// <param name="group">A group returned by <see cref="GetFailureGroupsAsync"/>.</param>
    /// <param name="ct">Cancellation token</param>
    async Task<IReadOnlyList<string>> GetFailureGroupJobIdsAsync(FailureGroupDto group, CancellationToken ct)
    {
        var jobs = await GetFailureGroupJobsAsync(group, int.MaxValue, ct).ConfigureAwait(false);
        return (jobs ?? Array.Empty<JobSummaryDto>()).Select(j => j.JobId).ToList();
    }
}
