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
/// Unit tests for SearchService tag search via TagsDataReader.
/// Validates: Requirements 5.1, 5.2, 5.3, 5.4
/// </summary>
public class SearchByTagTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;
    private readonly SearchService _service;

    public SearchByTagTests()
    {
        _mockStorage = new Mock<JobStorage>();
        _mockMonitoringApi = new Mock<IMonitoringApi>();
        _mockConnection = new Mock<JobStorageConnection>();

        _mockStorage.Setup(s => s.GetMonitoringApi()).Returns(_mockMonitoringApi.Object);
        _mockStorage.Setup(s => s.GetReadOnlyConnection()).Returns(_mockConnection.Object);

        _tagsReader = new TagsDataReader(_mockStorage.Object);
        _service = new SearchService(_mockStorage.Object, _tagsReader);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_ExactMatch_ReturnsMatchingJobs()
    {
        // Arrange
        SetupAllTags("urgent", "batch", "api");
        SetupJobsByTag("urgent", new[] { "101", "102" });
        SetupJobData("101", "Processing", new DateTime(2024, 1, 15, 10, 0, 0));
        SetupJobData("102", "Succeeded", new DateTime(2024, 1, 14, 9, 0, 0));
        SetupJobTags("101", new[] { "urgent", "api" });
        SetupJobTags("102", new[] { "urgent" });

        var request = new SearchRequest { Query = "tag:urgent" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(SearchMatchSource.Tag, item.MatchSource));
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_CaseInsensitive_ReturnsMatchingJobs()
    {
        // Arrange
        SetupAllTags("Urgent");
        SetupJobsByTag("Urgent", new[] { "201" });
        SetupJobData("201", "Failed", new DateTime(2024, 1, 10, 8, 0, 0));
        SetupJobTags("201", new[] { "Urgent" });

        var request = new SearchRequest { Query = "tag:URGENT" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("201", result.Items[0].JobId);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_NoMatchingTag_ReturnsEmpty()
    {
        // Arrange
        SetupAllTags("urgent", "batch");

        var request = new SearchRequest { Query = "tag:nonexistent" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_OrdersByCreatedAtDescending()
    {
        // Arrange
        SetupAllTags("batch");
        SetupJobsByTag("batch", new[] { "301", "302", "303" });
        SetupJobData("301", "Succeeded", new DateTime(2024, 1, 10, 8, 0, 0));
        SetupJobData("302", "Succeeded", new DateTime(2024, 1, 15, 12, 0, 0));
        SetupJobData("303", "Succeeded", new DateTime(2024, 1, 12, 6, 0, 0));
        SetupJobTags("301", new[] { "batch" });
        SetupJobTags("302", new[] { "batch" });
        SetupJobTags("303", new[] { "batch" });

        var request = new SearchRequest { Query = "tag:batch" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.TotalCount);
        Assert.Equal("302", result.Items[0].JobId); // Jan 15 (most recent)
        Assert.Equal("303", result.Items[1].JobId); // Jan 12
        Assert.Equal("301", result.Items[2].JobId); // Jan 10 (oldest)
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_PaginatesWithDefaultPageSize20()
    {
        // Arrange
        SetupAllTags("large");
        var jobIds = Enumerable.Range(1, 30).Select(i => i.ToString()).ToArray();
        SetupJobsByTag("large", jobIds);

        for (int i = 1; i <= 30; i++)
        {
            SetupJobData(i.ToString(), "Succeeded", new DateTime(2024, 1, 1).AddHours(i));
            SetupJobTags(i.ToString(), new[] { "large" });
        }

        var request = new SearchRequest { Query = "tag:large", PageSize = 20 };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(30, result.TotalCount);
        Assert.Equal(20, result.Items.Count);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_PaginatesWithFrom()
    {
        // Arrange
        SetupAllTags("paged");
        var jobIds = Enumerable.Range(1, 10).Select(i => i.ToString()).ToArray();
        SetupJobsByTag("paged", jobIds);

        for (int i = 1; i <= 10; i++)
        {
            SetupJobData(i.ToString(), "Succeeded", new DateTime(2024, 1, 1).AddHours(i));
            SetupJobTags(i.ToString(), new[] { "paged" });
        }

        var request = new SearchRequest { Query = "tag:paged", From = 5, PageSize = 20 };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(10, result.TotalCount);
        Assert.Equal(5, result.Items.Count); // 10 total - 5 skipped = 5 remaining
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_PageSizeCappedAt50()
    {
        // Arrange
        SetupAllTags("capped");
        var jobIds = Enumerable.Range(1, 60).Select(i => i.ToString()).ToArray();
        SetupJobsByTag("capped", jobIds);

        for (int i = 1; i <= 60; i++)
        {
            SetupJobData(i.ToString(), "Succeeded", new DateTime(2024, 1, 1).AddHours(i));
            SetupJobTags(i.ToString(), new[] { "capped" });
        }

        var request = new SearchRequest { Query = "tag:capped", PageSize = 100 };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(60, result.TotalCount);
        Assert.Equal(50, result.Items.Count); // Capped at 50
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_IncludesTagsInResult()
    {
        // Arrange
        SetupAllTags("tagged");
        SetupJobsByTag("tagged", new[] { "501" });
        SetupJobData("501", "Processing", new DateTime(2024, 1, 20, 14, 0, 0));
        SetupJobTags("501", new[] { "tagged", "extra-tag", "another" });

        var request = new SearchRequest { Query = "tag:tagged" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        Assert.NotNull(result.Items[0].Tags);
        Assert.Contains("tagged", result.Items[0].Tags);
        Assert.Contains("extra-tag", result.Items[0].Tags);
        Assert.Contains("another", result.Items[0].Tags);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_SetsMatchSourceToTag()
    {
        // Arrange
        SetupAllTags("source-test");
        SetupJobsByTag("source-test", new[] { "601" });
        SetupJobData("601", "Enqueued", new DateTime(2024, 1, 5, 7, 0, 0));
        SetupJobTags("601", new[] { "source-test" });

        var request = new SearchRequest { Query = "tag:source-test" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        Assert.Equal(SearchMatchSource.Tag, result.Items[0].MatchSource);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_HandlesStorageError()
    {
        // Arrange - setup GetAllItemsFromSet to throw
        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Throws(new InvalidOperationException("Storage unavailable"));

        var request = new SearchRequest { Query = "tag:anything" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.HasError);
        Assert.Equal("The search could not be completed due to a storage error.", result.ErrorMessage);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_HandlesCancellation()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        SetupAllTags("cancel-test");

        var request = new SearchRequest { Query = "tag:cancel-test" };

        // Act
        var result = await _service.SearchAsync(request, cts.Token);

        // Assert
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_SkipsDeletedJobs()
    {
        // Arrange - job ID exists in tag set but GetJobData returns null (job was deleted from storage)
        SetupAllTags("with-deleted");
        SetupJobsByTag("with-deleted", new[] { "701", "702" });
        SetupJobData("701", "Succeeded", new DateTime(2024, 1, 10, 8, 0, 0));
        SetupJobTags("701", new[] { "with-deleted" });
        // Job 702 returns null (deleted from storage)
        _mockConnection.Setup(c => c.GetJobData("702")).Returns((JobData)null!);

        var request = new SearchRequest { Query = "tag:with-deleted" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("701", result.Items[0].JobId);
    }

    #region Helper Methods

    private void SetupAllTags(params string[] tags)
    {
        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Returns(new HashSet<string>(tags));
    }

    private void SetupJobsByTag(string tag, string[] jobIds)
    {
        _mockConnection.Setup(c => c.GetSetCount($"tags:{tag}"))
            .Returns(jobIds.Length);
        _mockConnection.Setup(c => c.GetRangeFromSet($"tags:{tag}", 0, jobIds.Length - 1))
            .Returns(jobIds.ToList());
    }

    private void SetupJobData(string jobId, string state, DateTime createdAt)
    {
        var job = Job.FromExpression(() => SampleJob.Execute());
        var jobData = new JobData
        {
            State = state,
            Job = job,
            CreatedAt = createdAt
        };
        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns(jobData);
    }

    private void SetupJobTags(string jobId, string[] tags)
    {
        _mockConnection.Setup(c => c.GetAllItemsFromSet($"tags:{jobId}"))
            .Returns(new HashSet<string>(tags));
    }

    #endregion

    // Sample job class for creating Job instances
    public static class SampleJob
    {
        public static void Execute() { }
    }
}
