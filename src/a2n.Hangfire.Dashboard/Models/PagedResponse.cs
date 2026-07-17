#nullable enable
using System.Collections.Generic;

namespace a2n.Hangfire.Dashboard.Models;

/// <summary>
/// Serialization-friendly paging envelope that mirrors <see cref="PagedResult{T}"/> for JSON
/// responses (Req 9.3). Unlike <see cref="PagedResult{T}"/> (whose paging metadata is computed
/// on demand), all values here are materialized so they round-trip cleanly through serializers.
/// </summary>
/// <typeparam name="T">The type of items in the page.</typeparam>
/// <param name="Items">The items for the current page, in order.</param>
/// <param name="TotalCount">Total number of items across all pages.</param>
/// <param name="Page">Current page number (1-based).</param>
/// <param name="PageSize">Number of items per page.</param>
/// <param name="TotalPages">Total number of pages.</param>
/// <param name="HasNextPage">Whether a next page is available.</param>
/// <param name="HasPreviousPage">Whether a previous page is available.</param>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    long TotalCount,
    int Page,
    int PageSize,
    int TotalPages,
    bool HasNextPage,
    bool HasPreviousPage);
