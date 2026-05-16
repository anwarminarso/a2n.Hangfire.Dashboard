using System;
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Generic paginated result container.
/// Provides pagination metadata including total pages, navigation flags, and a static empty factory.
/// </summary>
/// <typeparam name="T">The type of items in the result set</typeparam>
public class PagedResult<T>
{
    /// <summary>
    /// The items for the current page.
    /// </summary>
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();

    /// <summary>
    /// Total number of items across all pages.
    /// </summary>
    public long TotalCount { get; set; }

    /// <summary>
    /// Current page number (1-based).
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Number of items per page.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// Total number of pages based on TotalCount and PageSize.
    /// </summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

    /// <summary>
    /// Indicates whether there is a next page available.
    /// </summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>
    /// Indicates whether there is a previous page available.
    /// </summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>
    /// Creates an empty paged result with zero items.
    /// </summary>
    /// <param name="page">The requested page number</param>
    /// <param name="pageSize">The requested page size</param>
    /// <returns>An empty <see cref="PagedResult{T}"/> instance</returns>
    public static PagedResult<T> Empty(int page, int pageSize) => new()
    {
        Items = Array.Empty<T>(),
        TotalCount = 0,
        Page = page,
        PageSize = pageSize
    };
}

/// <summary>
/// Summary of a job for list and search results.
/// Contains core job metadata, timing information, tags, and exception details.
/// </summary>
public class JobSummaryDto
{
    /// <summary>
    /// The unique job identifier.
    /// </summary>
    public string JobId { get; set; }

    /// <summary>
    /// The display name of the job (typically the type and method name).
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// The current state of the job (e.g., Succeeded, Failed, Processing).
    /// </summary>
    public string State { get; set; }

    /// <summary>
    /// The queue the job was enqueued to.
    /// </summary>
    public string Queue { get; set; }

    /// <summary>
    /// When the job was created.
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// When the job last changed state.
    /// </summary>
    public DateTime? LastStateChange { get; set; }

    /// <summary>
    /// Processing duration in milliseconds (from State.Data PerformanceDuration).
    /// </summary>
    public double? DurationMs { get; set; }

    /// <summary>
    /// Queue wait latency in milliseconds (from State.Data Latency).
    /// </summary>
    public double? LatencyMs { get; set; }

    /// <summary>
    /// Tags associated with the job.
    /// </summary>
    public string[] Tags { get; set; }

    /// <summary>
    /// The exception type if the job failed.
    /// </summary>
    public string ExceptionType { get; set; }

    /// <summary>
    /// The exception message if the job failed.
    /// </summary>
    public string ExceptionMessage { get; set; }
}

/// <summary>
/// Filter criteria for advanced job queries.
/// All fields are optional; non-null fields are combined with AND logic.
/// </summary>
public class JobFilterCriteria
{
    /// <summary>
    /// Filter by job state (e.g., "Succeeded", "Failed").
    /// </summary>
    public string State { get; set; }

    /// <summary>
    /// Filter jobs created on or after this date.
    /// </summary>
    public DateTimeOffset? DateFrom { get; set; }

    /// <summary>
    /// Filter jobs created on or before this date.
    /// </summary>
    public DateTimeOffset? DateTo { get; set; }

    /// <summary>
    /// Filter by queue name.
    /// </summary>
    public string Queue { get; set; }

    /// <summary>
    /// Filter by server name.
    /// </summary>
    public string Server { get; set; }

    /// <summary>
    /// Filter jobs with duration greater than or equal to this value.
    /// </summary>
    public TimeSpan? MinDuration { get; set; }

    /// <summary>
    /// Filter jobs with duration less than or equal to this value.
    /// </summary>
    public TimeSpan? MaxDuration { get; set; }

    /// <summary>
    /// Filter jobs that have all specified tags.
    /// </summary>
    public List<string> Tags { get; set; }

    /// <summary>
    /// Filter by recurring job identifier.
    /// </summary>
    public string RecurringJobId { get; set; }
}

/// <summary>
/// Tag with associated job count for tag cloud display.
/// </summary>
public class TagCountDto
{
    /// <summary>
    /// The tag value.
    /// </summary>
    public string Tag { get; set; }

    /// <summary>
    /// Number of jobs associated with this tag.
    /// </summary>
    public long Count { get; set; }
}

/// <summary>
/// Slowest job entry with duration details.
/// Used for identifying performance bottlenecks.
/// </summary>
public class SlowestJobDto
{
    /// <summary>
    /// The unique job identifier.
    /// </summary>
    public string JobId { get; set; }

    /// <summary>
    /// The display name of the job (type and method).
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// Processing duration in milliseconds.
    /// </summary>
    public double DurationMs { get; set; }

    /// <summary>
    /// When the job completed processing.
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}
