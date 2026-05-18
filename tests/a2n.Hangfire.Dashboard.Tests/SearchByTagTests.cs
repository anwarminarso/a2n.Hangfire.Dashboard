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
/// Unit tests for SearchService tag search via GenericQueryProvider.
/// The GenericQueryProvider scans states, collects jobs, then filters by tag client-side.
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

        // Default: no queues
        _mockMonitoringApi.Setup(m => m.Queues())
            .Returns(new List<QueueWithTopEnqueuedJobsDto>());

        // Default: empty state lists
        _mockMonitoringApi.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
        _mockMonitoringApi.Setup(m => m.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<SucceededJobDto>(new List<KeyValuePair<string, SucceededJobDto>>()));
        _mockMonitoringApi.Setup(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<FailedJobDto>(new List<KeyValuePair<string, FailedJobDto>>()));
        _mockMonitoringApi.Setup(m => m.ScheduledJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ScheduledJobDto>(new List<KeyValuePair<string, ScheduledJobDto>>()));
        _mockMonitoringApi.Setup(m => m.DeletedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<DeletedJobDto>(new List<KeyValuePair<string, DeletedJobDto>>()));

        _tagsReader = new TagsDataReader(_mockStorage.Object);
        var queryProvider = new GenericQueryProvider(_mockStorage.Object, _tagsReader);
        _service = new SearchService(_mockStorage.Object, _tagsReader, queryProvider);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_ExactMatch_ReturnsMatchingJobs()
    {
        // Arrange — jobs in Processing state, tagged with "urgent"
        var job = Job.FromExpression(() => SampleJob.Execute());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("101", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc), InProcessingState = true }),
            new("102", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 14, 9, 0, 0, DateTimeKind.Utc), InProcessingState = true }),
            new("103", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 13, 8, 0, 0, DateTimeKind.Utc), InProcessingState = true })
        });

        // Only 101 and 102 have the "urgent" tag
        SetupJobTags("101", new[] { "urgent", "api" });
        SetupJobTags("102", new[] { "urgent" });
        SetupJobTags("103", new[] { "batch" });

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
        var job = Job.FromExpression(() => SampleJob.Execute());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("201", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc), InProcessingState = true })
        });

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
        // Arrange — jobs exist but none have the searched tag
        var job = Job.FromExpression(() => SampleJob.Execute());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("301", new ProcessingJobDto { Job = job, StartedAt = DateTime.UtcNow, InProcessingState = true })
        });

        SetupJobTags("301", new[] { "batch" });

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
        var job = Job.FromExpression(() => SampleJob.Execute());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("301", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc), InProcessingState = true }),
            new("302", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc), InProcessingState = true }),
            new("303", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 12, 6, 0, 0, DateTimeKind.Utc), InProcessingState = true })
        });

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
    public async Task SearchAsync_TagPrefix_PaginatesCorrectly()
    {
        // Arrange — 30 jobs all tagged
        var job = Job.FromExpression(() => SampleJob.Execute());
        var jobs = new List<KeyValuePair<string, ProcessingJobDto>>();
        for (int i = 1; i <= 30; i++)
        {
            jobs.Add(new KeyValuePair<string, ProcessingJobDto>(
                i.ToString(),
                new ProcessingJobDto
                {
                    Job = job,
                    StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                    InProcessingState = true
                }));
        }
        SetupProcessingJobs(jobs);

        for (int i = 1; i <= 30; i++)
            SetupJobTags(i.ToString(), new[] { "large" });

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
        // Arrange — 10 jobs all tagged
        var job = Job.FromExpression(() => SampleJob.Execute());
        var jobs = new List<KeyValuePair<string, ProcessingJobDto>>();
        for (int i = 1; i <= 10; i++)
        {
            jobs.Add(new KeyValuePair<string, ProcessingJobDto>(
                i.ToString(),
                new ProcessingJobDto
                {
                    Job = job,
                    StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                    InProcessingState = true
                }));
        }
        SetupProcessingJobs(jobs);

        for (int i = 1; i <= 10; i++)
            SetupJobTags(i.ToString(), new[] { "paged" });

        // From=5, PageSize=5 → page = 5/5 + 1 = 2 → skip 5, take 5
        var request = new SearchRequest { Query = "tag:paged", From = 5, PageSize = 5 };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(10, result.TotalCount);
        Assert.Equal(5, result.Items.Count); // page 2: 10 total - 5 skipped = 5 remaining
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_PageSizeCappedAt50()
    {
        // Arrange — 60 jobs all tagged
        var job = Job.FromExpression(() => SampleJob.Execute());
        var jobs = new List<KeyValuePair<string, ProcessingJobDto>>();
        for (int i = 1; i <= 60; i++)
        {
            jobs.Add(new KeyValuePair<string, ProcessingJobDto>(
                i.ToString(),
                new ProcessingJobDto
                {
                    Job = job,
                    StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                    InProcessingState = true
                }));
        }
        SetupProcessingJobs(jobs);

        for (int i = 1; i <= 60; i++)
            SetupJobTags(i.ToString(), new[] { "capped" });

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
        var job = Job.FromExpression(() => SampleJob.Execute());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("501", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 20, 14, 0, 0, DateTimeKind.Utc), InProcessingState = true })
        });

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
        var job = Job.FromExpression(() => SampleJob.Execute());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("601", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 5, 7, 0, 0, DateTimeKind.Utc), InProcessingState = true })
        });

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
        // Arrange — monitoring API throws when scanning states
        _mockMonitoringApi.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
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

        var request = new SearchRequest { Query = "tag:cancel-test" };

        // Act
        var result = await _service.SearchAsync(request, cts.Token);

        // Assert
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task SearchAsync_TagPrefix_SkipsJobsWithoutMatchingTag()
    {
        // Arrange — two jobs, only one has the searched tag
        var job = Job.FromExpression(() => SampleJob.Execute());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("701", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc), InProcessingState = true }),
            new("702", new ProcessingJobDto { Job = job, StartedAt = new DateTime(2024, 1, 11, 8, 0, 0, DateTimeKind.Utc), InProcessingState = true })
        });

        SetupJobTags("701", new[] { "with-deleted" });
        SetupJobTags("702", new[] { "other-tag" });

        var request = new SearchRequest { Query = "tag:with-deleted" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("701", result.Items[0].JobId);
    }

    #region Helper Methods

    private void SetupProcessingJobs(List<KeyValuePair<string, ProcessingJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.ProcessingJobs(0, 100))
            .Returns(new JobList<ProcessingJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.ProcessingJobs(It.Is<int>(i => i >= Math.Max(jobs.Count, 100)), 100))
            .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
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
