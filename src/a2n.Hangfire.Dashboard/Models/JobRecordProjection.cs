#nullable enable
using System;
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Projection helpers that map the internal storage query shapes
/// (<see cref="JobSummaryDto"/> and <see cref="PagedResult{T}"/>) to the shared,
/// serialization-friendly shapes (<see cref="JobRecordDto"/> and <see cref="PagedResponse{T}"/>)
/// consumed by the read-only REST API JSON responses and the JSON export.
/// Centralizing the mapping guarantees both surfaces share one shape (Req 9.3 / 13.3).
/// </summary>
public static class JobRecordProjection
{
    /// <summary>
    /// Projects a single <see cref="JobSummaryDto"/> to a <see cref="JobRecordDto"/>.
    /// </summary>
    /// <param name="summary">The job summary to project. Must not be <c>null</c>.</param>
    /// <returns>The projected <see cref="JobRecordDto"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="summary"/> is <c>null</c>.</exception>
    public static JobRecordDto ToRecord(this JobSummaryDto summary)
    {
        if (summary is null)
            throw new ArgumentNullException(nameof(summary));

        return new JobRecordDto(
            JobId: summary.JobId,
            JobName: summary.JobName,
            State: summary.State,
            Queue: summary.Queue,
            CreatedAt: summary.CreatedAt,
            LastStateChange: summary.LastStateChange,
            DurationMs: summary.DurationMs,
            LatencyMs: summary.LatencyMs,
            Tags: summary.Tags,
            ExceptionType: summary.ExceptionType,
            ExceptionMessage: summary.ExceptionMessage);
    }

    /// <summary>
    /// Projects a page of <see cref="JobSummaryDto"/> to a <see cref="PagedResponse{T}"/> of
    /// <see cref="JobRecordDto"/>, preserving item order and copying all paging metadata.
    /// </summary>
    /// <param name="paged">The paged result to project. Must not be <c>null</c>.</param>
    /// <returns>A <see cref="PagedResponse{T}"/> of <see cref="JobRecordDto"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="paged"/> is <c>null</c>.</exception>
    public static PagedResponse<JobRecordDto> ToResponse(this PagedResult<JobSummaryDto> paged)
    {
        if (paged is null)
            throw new ArgumentNullException(nameof(paged));

        var source = paged.Items ?? Array.Empty<JobSummaryDto>();
        var items = new List<JobRecordDto>(source.Count);
        foreach (var summary in source)
            items.Add(summary.ToRecord());

        return paged.ToResponse((IReadOnlyList<JobRecordDto>)items);
    }

    /// <summary>
    /// Projects the paging metadata of an arbitrary <see cref="PagedResult{T}"/> onto a
    /// <see cref="PagedResponse{T}"/> using already-projected items. The metadata
    /// (<c>TotalCount</c>, <c>Page</c>, <c>PageSize</c>, <c>TotalPages</c>, <c>HasNextPage</c>,
    /// <c>HasPreviousPage</c>) is copied exactly from the source, so the projection matches the
    /// values computed by <see cref="PagedResult{T}"/> (Req 9.3).
    /// </summary>
    /// <typeparam name="TSource">The source item type of the paged result.</typeparam>
    /// <typeparam name="TResult">The item type of the projected response.</typeparam>
    /// <param name="paged">The paged result whose metadata is copied. Must not be <c>null</c>.</param>
    /// <param name="items">The already-projected items, in order. Must not be <c>null</c>.</param>
    /// <returns>A <see cref="PagedResponse{T}"/> carrying <paramref name="items"/> and the copied metadata.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="paged"/> or <paramref name="items"/> is <c>null</c>.</exception>
    public static PagedResponse<TResult> ToResponse<TSource, TResult>(
        this PagedResult<TSource> paged, IReadOnlyList<TResult> items)
    {
        if (paged is null)
            throw new ArgumentNullException(nameof(paged));
        if (items is null)
            throw new ArgumentNullException(nameof(items));

        return new PagedResponse<TResult>(
            Items: items,
            TotalCount: paged.TotalCount,
            Page: paged.Page,
            PageSize: paged.PageSize,
            TotalPages: paged.TotalPages,
            HasNextPage: paged.HasNextPage,
            HasPreviousPage: paged.HasPreviousPage);
    }
}
