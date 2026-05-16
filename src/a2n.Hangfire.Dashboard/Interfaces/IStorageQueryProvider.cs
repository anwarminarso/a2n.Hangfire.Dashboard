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
    /// Searches jobs by type name using database-level pattern matching (LIKE/ILIKE).
    /// Returns jobs whose type name contains the search term (case-insensitive).
    /// </summary>
    /// <param name="searchTerm">Search term (1–200 characters)</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Paginated results ordered by CreatedAt descending</returns>
    Task<PagedResult<JobSummaryDto>> SearchJobsByNameAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Searches failed jobs by exception message or exception type.
    /// </summary>
    /// <param name="searchTerm">Search term (1–500 characters)</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Items per page</param>
    /// <param name="ct">Cancellation token</param>
    Task<PagedResult<JobSummaryDto>> SearchFailedByExceptionAsync(
        string searchTerm, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Returns jobs matching all specified non-null filter criteria (AND logic).
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
}
