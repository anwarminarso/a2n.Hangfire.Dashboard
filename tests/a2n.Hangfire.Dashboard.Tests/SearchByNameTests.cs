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
/// Unit tests for SearchService job name search (scan-and-filter).
/// Validates: Requirements 3.1, 3.2, 3.3, 3.4
/// </summary>
public class SearchByNameTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;
    private readonly SearchService _service;

    public SearchByNameTests()
    {
        _mockStorage = new Mock<JobStorage>();
        _mockMonitoringApi = new Mock<IMonitoringApi>();
        _mockConnection = new Mock<JobStorageConnection>();

        _mockStorage.Setup(s => s.GetMonitoringApi()).Returns(_mockMonitoringApi.Object);
        _mockStorage.Setup(s => s.GetReadOnlyConnection()).Returns(_mockConnection.Object);

        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Returns(new HashSet<string>());

        // Default: no queues (so Enqueued scan is skipped unless set up)
        _mockMonitoringApi.Setup(m => m.Queues())
            .Returns(new List<QueueWithTopEnqueuedJobsDto>());

        _tagsReader = new TagsDataReader(_mockStorage.Object);
        _service = new SearchService(_mockStorage.Object, _tagsReader);
    }

    [Fact]
    public async Task SearchByName_MatchesTypeName_CaseInsensitive()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("1", new ProcessingJobDto
            {
                Job = job,
                StartedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InProcessingState = true
            })
        });

        var request = new SearchRequest { Query = "samplejobs" }; // lowercase

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("1", item.JobId);
        Assert.Equal("SampleJobs.FireAndForget", item.JobName);
        Assert.Equal("Processing", item.State);
        Assert.Equal(SearchMatchSource.Name, item.MatchSource);
    }

    [Fact]
    public async Task SearchByName_MatchesMethodName_CaseInsensitive()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupSucceededJobs(new List<KeyValuePair<string, SucceededJobDto>>
        {
            new("2", new SucceededJobDto
            {
                Job = job,
                SucceededAt = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc),
                InSucceededState = true
            })
        });

        var request = new SearchRequest { Query = "FIREANDFORGET" }; // uppercase

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("SampleJobs.FireAndForget", result.Items[0].JobName);
    }

    [Fact]
    public async Task SearchByName_SubstringMatch_ReturnsResults()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("3", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                InFailedState = true
            })
        });

        var request = new SearchRequest { Query = "Fire" }; // substring of "FireAndForget"

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchByName_NoMatch_ReturnsEmptyResult()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("1", new ProcessingJobDto
            {
                Job = job,
                StartedAt = DateTime.UtcNow,
                InProcessingState = true
            })
        });

        var request = new SearchRequest { Query = "NonExistentJob" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
        Assert.False(result.HasError);
    }

    [Fact]
    public async Task SearchByName_SortsResultsByCreatedAtDescending()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("1", new ProcessingJobDto
            {
                Job = job,
                StartedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                InProcessingState = true
            }),
            new("2", new ProcessingJobDto
            {
                Job = job,
                StartedAt = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc),
                InProcessingState = true
            }),
            new("3", new ProcessingJobDto
            {
                Job = job,
                StartedAt = new DateTime(2024, 3, 10, 10, 0, 0, DateTimeKind.Utc),
                InProcessingState = true
            })
        });

        var request = new SearchRequest { Query = "Sample" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.TotalCount);
        Assert.Equal("2", result.Items[0].JobId); // June 15 (most recent)
        Assert.Equal("3", result.Items[1].JobId); // March 10
        Assert.Equal("1", result.Items[2].JobId); // January 1 (oldest)
    }

    [Fact]
    public async Task SearchByName_RespectsStateFilter()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("1", new ProcessingJobDto
            {
                Job = job,
                StartedAt = DateTime.UtcNow,
                InProcessingState = true
            })
        });
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("2", new FailedJobDto
            {
                Job = job,
                FailedAt = DateTime.UtcNow,
                InFailedState = true
            })
        });

        // Only search in Failed state
        var request = new SearchRequest
        {
            Query = "Sample",
            States = new List<string> { "Failed" }
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("2", result.Items[0].JobId);
        Assert.Equal("Failed", result.Items[0].State);
    }

    [Fact]
    public async Task SearchByName_ScansEnqueuedJobsAcrossQueues()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());

        _mockMonitoringApi.Setup(m => m.Queues())
            .Returns(new List<QueueWithTopEnqueuedJobsDto>
            {
                new() { Name = "default" },
                new() { Name = "critical" }
            });

        _mockMonitoringApi.Setup(m => m.EnqueuedJobs("default", 0, 100))
            .Returns(new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>
            {
                new("10", new EnqueuedJobDto
                {
                    Job = job,
                    EnqueuedAt = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc),
                    InEnqueuedState = true
                })
            }));
        _mockMonitoringApi.Setup(m => m.EnqueuedJobs("default", 100, 100))
            .Returns(new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>()));

        _mockMonitoringApi.Setup(m => m.EnqueuedJobs("critical", 0, 100))
            .Returns(new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>
            {
                new("11", new EnqueuedJobDto
                {
                    Job = job,
                    EnqueuedAt = new DateTime(2024, 5, 2, 10, 0, 0, DateTimeKind.Utc),
                    InEnqueuedState = true
                })
            }));
        _mockMonitoringApi.Setup(m => m.EnqueuedJobs("critical", 100, 100))
            .Returns(new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>()));

        var request = new SearchRequest
        {
            Query = "Sample",
            States = new List<string> { "Enqueued" }
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.Equal("11", result.Items[0].JobId); // May 2 (more recent)
        Assert.Equal("default", result.Items[1].Queue);
        Assert.Equal("critical", result.Items[0].Queue);
    }

    [Fact]
    public async Task SearchByName_RespectsCancellationToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        var request = new SearchRequest { Query = "Sample" };

        // Act
        var result = await _service.SearchAsync(request, cts.Token);

        // Assert
        Assert.True(result.TimedOut);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchByName_AppliesPagination()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        var jobs = new List<KeyValuePair<string, ProcessingJobDto>>();
        for (int i = 0; i < 30; i++)
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

        var request = new SearchRequest
        {
            Query = "Sample",
            From = 5,
            PageSize = 10
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(30, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
    }

    [Fact]
    public async Task SearchByName_PageSizeCappedAt50()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        var jobs = new List<KeyValuePair<string, ProcessingJobDto>>();
        for (int i = 0; i < 100; i++)
        {
            jobs.Add(new KeyValuePair<string, ProcessingJobDto>(
                i.ToString(),
                new ProcessingJobDto
                {
                    Job = job,
                    StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                    InProcessingState = true
                }));
        }
        SetupProcessingJobs(jobs);

        var request = new SearchRequest
        {
            Query = "Sample",
            From = 0,
            PageSize = 100 // Request 100, but should be capped at 50
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(100, result.TotalCount);
        Assert.Equal(50, result.Items.Count);
    }

    [Fact]
    public async Task SearchByName_SkipsJobsWithNullJob()
    {
        // Arrange
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("1", new ProcessingJobDto
            {
                Job = null, // Job deserialization failed
                StartedAt = DateTime.UtcNow,
                InProcessingState = true
            })
        });

        var request = new SearchRequest { Query = "Sample" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchByName_ScansMultipleStates()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());

        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("1", new ProcessingJobDto
            {
                Job = job,
                StartedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InProcessingState = true
            })
        });
        SetupSucceededJobs(new List<KeyValuePair<string, SucceededJobDto>>
        {
            new("2", new SucceededJobDto
            {
                Job = job,
                SucceededAt = new DateTime(2024, 6, 2, 10, 0, 0, DateTimeKind.Utc),
                InSucceededState = true
            })
        });
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("3", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true
            })
        });

        var request = new SearchRequest { Query = "Sample" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.TotalCount);
        // Sorted by CreatedAt descending
        Assert.Equal("3", result.Items[0].JobId); // Failed - June 3
        Assert.Equal("2", result.Items[1].JobId); // Succeeded - June 2
        Assert.Equal("1", result.Items[2].JobId); // Processing - June 1
    }

    [Fact]
    public async Task SearchByName_StorageError_ReturnsErrorResult()
    {
        // Arrange
        _mockMonitoringApi.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new InvalidOperationException("Storage unavailable"));

        var request = new SearchRequest
        {
            Query = "Sample",
            States = new List<string> { "Processing" }
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.HasError);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task SearchByName_TracksElapsedTime()
    {
        // Arrange
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>());

        var request = new SearchRequest { Query = "Sample" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Elapsed.TotalMilliseconds >= 0);
    }

    #region Helper Methods

    private void SetupProcessingJobs(List<KeyValuePair<string, ProcessingJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.ProcessingJobs(0, 100))
            .Returns(new JobList<ProcessingJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.ProcessingJobs(It.Is<int>(i => i >= jobs.Count), 100))
            .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
    }

    private void SetupSucceededJobs(List<KeyValuePair<string, SucceededJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.SucceededJobs(0, 100))
            .Returns(new JobList<SucceededJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.SucceededJobs(It.Is<int>(i => i >= jobs.Count), 100))
            .Returns(new JobList<SucceededJobDto>(new List<KeyValuePair<string, SucceededJobDto>>()));
    }

    private void SetupFailedJobs(List<KeyValuePair<string, FailedJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.FailedJobs(0, 100))
            .Returns(new JobList<FailedJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.FailedJobs(It.Is<int>(i => i >= jobs.Count), 100))
            .Returns(new JobList<FailedJobDto>(new List<KeyValuePair<string, FailedJobDto>>()));
    }

    private void SetupScheduledJobs(List<KeyValuePair<string, ScheduledJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.ScheduledJobs(0, 100))
            .Returns(new JobList<ScheduledJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.ScheduledJobs(It.Is<int>(i => i >= jobs.Count), 100))
            .Returns(new JobList<ScheduledJobDto>(new List<KeyValuePair<string, ScheduledJobDto>>()));
    }

    private void SetupDeletedJobs(List<KeyValuePair<string, DeletedJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.DeletedJobs(0, 100))
            .Returns(new JobList<DeletedJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.DeletedJobs(It.Is<int>(i => i >= jobs.Count), 100))
            .Returns(new JobList<DeletedJobDto>(new List<KeyValuePair<string, DeletedJobDto>>()));
    }

    #endregion

    // Sample job class for creating Job instances in tests
    public static class SampleJobs
    {
        public static void FireAndForget() { }
        public static void ProcessReport() { }
    }
}
