using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using a2n.Hangfire.Dashboard.Models;

namespace a2n.Hangfire.Dashboard.Tests.Integrations;

/// <summary>
/// Property tests for the shared paging projection
/// (<see cref="JobRecordProjection.ToResponse{TSource, TResult}"/> and the
/// <see cref="JobSummaryDto"/> overload).
///
/// Feature: integrations-v2-6, Property 11: Paging metadata projection equivalence
///
/// **Property 11: Paging metadata projection equivalence** — for any <see cref="PagedResult{T}"/>,
/// the projected <see cref="PagedResponse{T}"/> preserves the items in order and exposes
/// <c>TotalCount</c>, <c>Page</c>, <c>PageSize</c>, <c>TotalPages</c>, <c>HasNextPage</c>, and
/// <c>HasPreviousPage</c> values equal to those computed by <see cref="PagedResult{T}"/>.
///
/// **Validates: Requirements 9.3**
/// </summary>
public class PagingProjectionProperties
{
    /// <summary>
    /// Generates a <see cref="PagedResult{T}"/> of <see cref="int"/> whose paging metadata spans the
    /// interesting shapes: empty and non-empty item lists, first / middle / last pages, and the
    /// degenerate <c>PageSize == 0</c> case (for which <see cref="PagedResult{T}"/> defines
    /// <c>TotalPages == 0</c>). Values are constrained to non-negative counts and sensible ranges so
    /// they stay within the valid paging input space.
    /// </summary>
    private static Gen<PagedResult<int>> PagedResultGen =>
        from itemCount in Gen.Choose(0, 25)
        from items in Gen.ArrayOf(itemCount, Arb.Default.Int32().Generator)
        from totalCount in Gen.Choose(0, 100_000)
        from page in Gen.Choose(1, 500)
        from pageSize in Gen.Choose(0, 200)
        select new PagedResult<int>
        {
            Items = items.ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };

    private static Arbitrary<PagedResult<int>> PagedResultArb =>
        Arb.From(PagedResultGen);

    [Property(MaxTest = 100)]
    public Property Projection_PreservesItemsAndMetadata()
    {
        return Prop.ForAll(PagedResultArb, paged =>
        {
            // Project via the generic helper using the same item type (identity items).
            var response = paged.ToResponse<int, int>(paged.Items);

            var itemsPreservedInOrder =
                response.Items.SequenceEqual(paged.Items);

            var metadataEqual =
                response.TotalCount == paged.TotalCount &&
                response.Page == paged.Page &&
                response.PageSize == paged.PageSize &&
                response.TotalPages == paged.TotalPages &&
                response.HasNextPage == paged.HasNextPage &&
                response.HasPreviousPage == paged.HasPreviousPage;

            return (itemsPreservedInOrder && metadataEqual)
                .Label(
                    $"Page={paged.Page} PageSize={paged.PageSize} TotalCount={paged.TotalCount} " +
                    $"ItemCount={paged.Items.Count} | " +
                    $"itemsPreserved={itemsPreservedInOrder} metadataEqual={metadataEqual} | " +
                    $"expected(TotalPages={paged.TotalPages}, HasNext={paged.HasNextPage}, " +
                    $"HasPrev={paged.HasPreviousPage}) " +
                    $"actual(TotalPages={response.TotalPages}, HasNext={response.HasNextPage}, " +
                    $"HasPrev={response.HasPreviousPage})");
        });
    }

    [Property(MaxTest = 100)]
    public Property JobSummaryOverload_PreservesOrderAndMetadata()
    {
        // Reuse the same metadata generator but carry JobSummaryDto items so the
        // JobSummaryDto -> JobRecordDto overload is exercised end to end.
        var summaryPagedGen =
            from paged in PagedResultGen
            from jobIds in Gen.ArrayOf(paged.Items.Count, Arb.Default.Int32().Generator)
            select new PagedResult<JobSummaryDto>
            {
                Items = jobIds.Select((id, i) => new JobSummaryDto
                {
                    JobId = $"job-{i}-{id}",
                    JobName = $"Name{i}",
                    State = "Succeeded"
                }).ToList(),
                TotalCount = paged.TotalCount,
                Page = paged.Page,
                PageSize = paged.PageSize
            };

        return Prop.ForAll(Arb.From(summaryPagedGen), paged =>
        {
            var response = paged.ToResponse();

            var orderPreserved =
                response.Items.Select(r => r.JobId)
                    .SequenceEqual(paged.Items.Select(s => s.JobId));

            var metadataEqual =
                response.TotalCount == paged.TotalCount &&
                response.Page == paged.Page &&
                response.PageSize == paged.PageSize &&
                response.TotalPages == paged.TotalPages &&
                response.HasNextPage == paged.HasNextPage &&
                response.HasPreviousPage == paged.HasPreviousPage;

            return (orderPreserved && metadataEqual)
                .Label(
                    $"Page={paged.Page} PageSize={paged.PageSize} TotalCount={paged.TotalCount} " +
                    $"ItemCount={paged.Items.Count} | " +
                    $"orderPreserved={orderPreserved} metadataEqual={metadataEqual}");
        });
    }
}
