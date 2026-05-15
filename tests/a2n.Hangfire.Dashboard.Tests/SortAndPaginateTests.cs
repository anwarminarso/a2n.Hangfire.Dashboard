using Xunit;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for the SearchService.SortAndPaginate helper method.
/// Validates Requirements 12.1, 12.3, 12.4, 14.3.
/// </summary>
public class SortAndPaginateTests
{
    private static List<SearchResultItem> CreateCandidates(int count, DateTime baseTime)
    {
        var items = new List<SearchResultItem>();
        for (int i = 0; i < count; i++)
        {
            items.Add(new SearchResultItem
            {
                JobId = i.ToString(),
                JobName = $"Job{i}",
                State = "Succeeded",
                CreatedAt = baseTime.AddMinutes(i) // Each item 1 minute apart
            });
        }
        return items;
    }

    [Fact]
    public void SortAndPaginate_SortsByCreatedAtDescending()
    {
        // Arrange - items in ascending order
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(5, baseTime);
        var result = new SearchResult();

        // Act
        SearchService.SortAndPaginate(candidates, result, 0, 20);

        // Assert - should be sorted descending (most recent first)
        Assert.Equal(5, result.Items.Count);
        Assert.Equal("4", result.Items[0].JobId); // Latest
        Assert.Equal("3", result.Items[1].JobId);
        Assert.Equal("2", result.Items[2].JobId);
        Assert.Equal("1", result.Items[3].JobId);
        Assert.Equal("0", result.Items[4].JobId); // Earliest
    }

    [Fact]
    public void SortAndPaginate_SetsTotalCount()
    {
        // Arrange
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(25, baseTime);
        var result = new SearchResult();

        // Act
        SearchService.SortAndPaginate(candidates, result, 0, 10);

        // Assert - TotalCount reflects all candidates, not just the page
        Assert.Equal(25, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
    }

    [Fact]
    public void SortAndPaginate_AppliesFromOffset()
    {
        // Arrange
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(10, baseTime);
        var result = new SearchResult();

        // Act - skip first 3 items (after sorting descending)
        SearchService.SortAndPaginate(candidates, result, 3, 20);

        // Assert - after sorting desc: 9,8,7,6,5,4,3,2,1,0 → skip 3 → 6,5,4,3,2,1,0
        Assert.Equal(10, result.TotalCount);
        Assert.Equal(7, result.Items.Count);
        Assert.Equal("6", result.Items[0].JobId);
    }

    [Fact]
    public void SortAndPaginate_EnforcesMaxPageSize50()
    {
        // Arrange - 100 candidates, request page size of 100
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(100, baseTime);
        var result = new SearchResult();

        // Act - request 100 items per page
        SearchService.SortAndPaginate(candidates, result, 0, 100);

        // Assert - capped at 50
        Assert.Equal(100, result.TotalCount);
        Assert.Equal(50, result.Items.Count);
    }

    [Fact]
    public void SortAndPaginate_PageSizeBelow50_ReturnsRequestedSize()
    {
        // Arrange
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(100, baseTime);
        var result = new SearchResult();

        // Act - request 20 items per page
        SearchService.SortAndPaginate(candidates, result, 0, 20);

        // Assert - returns exactly 20
        Assert.Equal(100, result.TotalCount);
        Assert.Equal(20, result.Items.Count);
    }

    [Fact]
    public void SortAndPaginate_PageSizeExactly50_Returns50()
    {
        // Arrange
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(100, baseTime);
        var result = new SearchResult();

        // Act
        SearchService.SortAndPaginate(candidates, result, 0, 50);

        // Assert
        Assert.Equal(50, result.Items.Count);
    }

    [Fact]
    public void SortAndPaginate_EmptyCandidates_ReturnsEmptyResult()
    {
        // Arrange
        var candidates = new List<SearchResultItem>();
        var result = new SearchResult();

        // Act
        SearchService.SortAndPaginate(candidates, result, 0, 20);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void SortAndPaginate_FromBeyondCount_ReturnsEmpty()
    {
        // Arrange
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(5, baseTime);
        var result = new SearchResult();

        // Act - offset beyond available items
        SearchService.SortAndPaginate(candidates, result, 10, 20);

        // Assert - TotalCount still reflects all candidates
        Assert.Equal(5, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void SortAndPaginate_NullCreatedAt_TreatedAsMinValue()
    {
        // Arrange - mix of null and non-null CreatedAt
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", CreatedAt = new DateTime(2024, 1, 1) },
            new() { JobId = "2", CreatedAt = null },
            new() { JobId = "3", CreatedAt = new DateTime(2024, 6, 1) }
        };
        var result = new SearchResult();

        // Act
        SearchService.SortAndPaginate(candidates, result, 0, 20);

        // Assert - sorted desc: June 1 (3), Jan 1 (1), null→MinValue (2)
        Assert.Equal("3", result.Items[0].JobId);
        Assert.Equal("1", result.Items[1].JobId);
        Assert.Equal("2", result.Items[2].JobId);
    }

    [Fact]
    public void SortAndPaginate_SecondPage_ReturnsCorrectSlice()
    {
        // Arrange - 30 items, page size 10, get page 2 (from=10)
        var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
        var candidates = CreateCandidates(30, baseTime);
        var result = new SearchResult();

        // Act
        SearchService.SortAndPaginate(candidates, result, 10, 10);

        // Assert - after sorting desc: 29,28,...,0 → skip 10 → items 19,18,...,10
        Assert.Equal(30, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal("19", result.Items[0].JobId);
        Assert.Equal("10", result.Items[9].JobId);
    }
}
