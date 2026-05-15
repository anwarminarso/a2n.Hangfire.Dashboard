using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Unit tests for SearchService.ApplySecondaryFilters method.
/// Validates: Requirements 7.2, 7.3, 7.4, 8.2, 8.3, 9.2, 10.2, 10.3, 10.4, 15.3, 15.4, 16.2, 16.3, 17.2, 17.3
/// </summary>
public class SecondaryFilterTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;
    private readonly SearchService _service;

    public SecondaryFilterTests()
    {
        _mockStorage = new Mock<JobStorage>();
        _mockMonitoringApi = new Mock<IMonitoringApi>();
        _mockConnection = new Mock<JobStorageConnection>();

        _mockStorage.Setup(s => s.GetMonitoringApi()).Returns(_mockMonitoringApi.Object);
        _mockStorage.Setup(s => s.GetReadOnlyConnection()).Returns(_mockConnection.Object);

        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Returns(new HashSet<string>());

        _mockMonitoringApi.Setup(m => m.Queues())
            .Returns(new List<QueueWithTopEnqueuedJobsDto>());

        _tagsReader = new TagsDataReader(_mockStorage.Object);
        _service = new SearchService(_mockStorage.Object, _tagsReader);
    }

    #region Date Range Filter Tests

    [Fact]
    public void DateRangeFilter_DateFrom_ExcludesJobsBeforeFrom()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", CreatedAt = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "2", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "3", CreatedAt = new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" }
        };
        var request = new SearchRequest { DateFrom = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc) };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, i => i.JobId == "1");
        Assert.Contains(result, i => i.JobId == "2"); // Inclusive
        Assert.Contains(result, i => i.JobId == "3");
    }

    [Fact]
    public void DateRangeFilter_DateTo_ExcludesJobsAfterTo()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", CreatedAt = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "2", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "3", CreatedAt = new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" }
        };
        var request = new SearchRequest { DateTo = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc) };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.JobId == "1");
        Assert.Contains(result, i => i.JobId == "2"); // Inclusive
        Assert.DoesNotContain(result, i => i.JobId == "3");
    }

    [Fact]
    public void DateRangeFilter_BothBounds_FiltersCorrectly()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", CreatedAt = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "2", CreatedAt = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "3", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "4", CreatedAt = new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" }
        };
        var request = new SearchRequest
        {
            DateFrom = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            DateTo = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.JobId == "2");
        Assert.Contains(result, i => i.JobId == "3");
    }

    [Fact]
    public void DateRangeFilter_NoBounds_ReturnsAll()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", CreatedAt = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" },
            new() { JobId = "2", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), State = "Succeeded" }
        };
        var request = new SearchRequest(); // No date filters

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region State Filter Tests

    [Fact]
    public void StateFilter_WithSelectedStates_FiltersCorrectly()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Failed", CreatedAt = DateTime.UtcNow },
            new() { JobId = "3", State = "Processing", CreatedAt = DateTime.UtcNow }
        };
        var request = new SearchRequest { States = new List<string> { "Failed", "Processing" } };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, i => i.JobId == "1");
        Assert.Contains(result, i => i.JobId == "2");
        Assert.Contains(result, i => i.JobId == "3");
    }

    [Fact]
    public void StateFilter_CaseInsensitive()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "FAILED", CreatedAt = DateTime.UtcNow }
        };
        var request = new SearchRequest { States = new List<string> { "Succeeded" } };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("1", result[0].JobId);
    }

    [Fact]
    public void StateFilter_EmptyStates_ReturnsAll()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Failed", CreatedAt = DateTime.UtcNow }
        };
        var request = new SearchRequest { States = new List<string>() };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region Server Filter Tests

    [Fact]
    public void ServerFilter_MatchesProcessingStateServerId()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };

        // Job 1 was processed by "server-1"
        _mockMonitoringApi.Setup(m => m.JobDetails("1"))
            .Returns(new JobDetailsDto
            {
                History = new List<StateHistoryDto>
                {
                    new() { StateName = "Processing", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string> { { "ServerId", "server-1" } } }
                }
            });

        // Job 2 was processed by "server-2"
        _mockMonitoringApi.Setup(m => m.JobDetails("2"))
            .Returns(new JobDetailsDto
            {
                History = new List<StateHistoryDto>
                {
                    new() { StateName = "Processing", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string> { { "ServerId", "server-2" } } }
                }
            });

        var request = new SearchRequest { Server = "server-1" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("1", result[0].JobId);
    }

    [Fact]
    public void ServerFilter_CaseInsensitiveMatch()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };

        _mockMonitoringApi.Setup(m => m.JobDetails("1"))
            .Returns(new JobDetailsDto
            {
                History = new List<StateHistoryDto>
                {
                    new() { StateName = "Processing", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string> { { "ServerId", "Server-1" } } }
                }
            });

        var request = new SearchRequest { Server = "server-1" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public void ServerFilter_NoProcessingState_ExcludesJob()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Enqueued", CreatedAt = DateTime.UtcNow }
        };

        _mockMonitoringApi.Setup(m => m.JobDetails("1"))
            .Returns(new JobDetailsDto
            {
                History = new List<StateHistoryDto>
                {
                    new() { StateName = "Enqueued", CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string> { { "Queue", "default" } } }
                }
            });

        var request = new SearchRequest { Server = "server-1" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ServerFilter_NullServer_NoFiltering()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };
        var request = new SearchRequest { Server = null };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region Duration Filter Tests

    [Fact]
    public void DurationFilter_MinOnly_FiltersCorrectly()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 500 },   // 0.5s
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 5000 },  // 5s
            new() { JobId = "3", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 15000 }  // 15s
        };
        var request = new SearchRequest { MinDurationSeconds = 5 }; // 5000ms

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.JobId == "2"); // Exactly 5s (inclusive)
        Assert.Contains(result, i => i.JobId == "3");
    }

    [Fact]
    public void DurationFilter_MaxOnly_FiltersCorrectly()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 500 },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 5000 },
            new() { JobId = "3", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 15000 }
        };
        var request = new SearchRequest { MaxDurationSeconds = 5 }; // 5000ms

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.JobId == "1");
        Assert.Contains(result, i => i.JobId == "2"); // Exactly 5s (inclusive)
    }

    [Fact]
    public void DurationFilter_BothBounds_FiltersCorrectly()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 500 },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 5000 },
            new() { JobId = "3", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 15000 }
        };
        var request = new SearchRequest { MinDurationSeconds = 1, MaxDurationSeconds = 10 };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("2", result[0].JobId);
    }

    [Fact]
    public void DurationFilter_NullDuration_ExcludesJob()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Processing", CreatedAt = DateTime.UtcNow, DurationMs = null },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 5000 }
        };
        var request = new SearchRequest { MinDurationSeconds = 0 };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("2", result[0].JobId);
    }

    [Fact]
    public void DurationFilter_NoBounds_ReturnsAll()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, DurationMs = 500 },
            new() { JobId = "2", State = "Processing", CreatedAt = DateTime.UtcNow, DurationMs = null }
        };
        var request = new SearchRequest(); // No duration filter

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region Tags Filter Tests

    [Fact]
    public void TagsFilter_ORLogic_MatchesAnySelectedTag()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, Tags = new[] { "urgent", "billing" } },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow, Tags = new[] { "report" } },
            new() { JobId = "3", State = "Succeeded", CreatedAt = DateTime.UtcNow, Tags = new[] { "billing", "monthly" } }
        };
        var request = new SearchRequest { Tags = new List<string> { "urgent", "monthly" } };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.JobId == "1"); // Has "urgent"
        Assert.Contains(result, i => i.JobId == "3"); // Has "monthly"
    }

    [Fact]
    public void TagsFilter_LooksUpTagsWhenNotPopulated()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, Tags = null },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow, Tags = null }
        };

        // Setup tag lookup
        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags:1"))
            .Returns(new HashSet<string> { "urgent" });
        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags:2"))
            .Returns(new HashSet<string> { "report" });

        var request = new SearchRequest { Tags = new List<string> { "urgent" } };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("1", result[0].JobId);
        // Tags should be cached on the item
        Assert.NotNull(result[0].Tags);
        Assert.Contains("urgent", result[0].Tags);
    }

    [Fact]
    public void TagsFilter_CaseInsensitive()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, Tags = new[] { "Urgent" } }
        };
        var request = new SearchRequest { Tags = new List<string> { "urgent" } };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public void TagsFilter_EmptyTags_ReturnsAll()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };
        var request = new SearchRequest { Tags = new List<string>() };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region Queue Dropdown Filter Tests

    [Fact]
    public void QueueFilter_CaseInsensitiveMatch()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Enqueued", CreatedAt = DateTime.UtcNow, Queue = "Default" },
            new() { JobId = "2", State = "Enqueued", CreatedAt = DateTime.UtcNow, Queue = "critical" },
            new() { JobId = "3", State = "Enqueued", CreatedAt = DateTime.UtcNow, Queue = "DEFAULT" }
        };
        var request = new SearchRequest { Queue = "default" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.JobId == "1");
        Assert.Contains(result, i => i.JobId == "3");
    }

    [Fact]
    public void QueueFilter_NullQueue_NoFiltering()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Enqueued", CreatedAt = DateTime.UtcNow, Queue = "default" },
            new() { JobId = "2", State = "Enqueued", CreatedAt = DateTime.UtcNow, Queue = "critical" }
        };
        var request = new SearchRequest { Queue = null };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void QueueFilter_NullQueueOnItem_ExcludesItem()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow, Queue = null },
            new() { JobId = "2", State = "Enqueued", CreatedAt = DateTime.UtcNow, Queue = "default" }
        };
        var request = new SearchRequest { Queue = "default" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("2", result[0].JobId);
    }

    #endregion

    #region Recurring Job ID Filter Tests

    [Fact]
    public void RecurringJobIdFilter_MatchesJobParameter()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "3", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };

        _mockConnection.Setup(c => c.GetJobParameter("1", "RecurringJobId")).Returns("daily-report");
        _mockConnection.Setup(c => c.GetJobParameter("2", "RecurringJobId")).Returns("hourly-cleanup");
        _mockConnection.Setup(c => c.GetJobParameter("3", "RecurringJobId")).Returns("daily-report");

        var request = new SearchRequest { RecurringJobId = "daily-report" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, i => i.JobId == "1");
        Assert.Contains(result, i => i.JobId == "3");
    }

    [Fact]
    public void RecurringJobIdFilter_CaseInsensitiveMatch()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };

        _mockConnection.Setup(c => c.GetJobParameter("1", "RecurringJobId")).Returns("Daily-Report");

        var request = new SearchRequest { RecurringJobId = "daily-report" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public void RecurringJobIdFilter_NullRecurringJobId_NoFiltering()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };
        var request = new SearchRequest { RecurringJobId = null };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void RecurringJobIdFilter_NoMatchingParameter_ExcludesJob()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow }
        };

        _mockConnection.Setup(c => c.GetJobParameter("1", "RecurringJobId")).Returns((string)null);

        var request = new SearchRequest { RecurringJobId = "daily-report" };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    #endregion

    #region Combined Filter Tests (AND logic)

    [Fact]
    public void CombinedFilters_ANDLogic_AllFiltersMustPass()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), DurationMs = 5000, Queue = "default" },
            new() { JobId = "2", State = "Failed", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), DurationMs = 5000, Queue = "default" },
            new() { JobId = "3", State = "Succeeded", CreatedAt = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), DurationMs = 5000, Queue = "default" },
            new() { JobId = "4", State = "Succeeded", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), DurationMs = 500, Queue = "default" },
            new() { JobId = "5", State = "Succeeded", CreatedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), DurationMs = 5000, Queue = "critical" }
        };

        var request = new SearchRequest
        {
            States = new List<string> { "Succeeded" },
            DateFrom = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            MinDurationSeconds = 1,
            Queue = "default"
        };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert - Only job 1 passes all filters
        Assert.Single(result);
        Assert.Equal("1", result[0].JobId);
    }

    [Fact]
    public void EmptyCandidates_ReturnsEmpty()
    {
        // Arrange
        var candidates = new List<SearchResultItem>();
        var request = new SearchRequest
        {
            States = new List<string> { "Succeeded" },
            DateFrom = DateTime.UtcNow
        };

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void NoFiltersActive_ReturnsAllCandidates()
    {
        // Arrange
        var candidates = new List<SearchResultItem>
        {
            new() { JobId = "1", State = "Succeeded", CreatedAt = DateTime.UtcNow },
            new() { JobId = "2", State = "Failed", CreatedAt = DateTime.UtcNow },
            new() { JobId = "3", State = "Processing", CreatedAt = DateTime.UtcNow }
        };
        var request = new SearchRequest(); // No filters

        // Act
        var result = _service.ApplySecondaryFilters(candidates, request, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.Count);
    }

    #endregion
}
