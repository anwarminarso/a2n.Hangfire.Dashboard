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
/// Unit tests for SearchService queue search (prefix and substring).
/// Validates: Requirements 4.1, 4.2, 4.3, 4.4
/// </summary>
public class SearchByQueueTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;
    private readonly SearchService _service;

    public SearchByQueueTests()
    {
        _mockStorage = new Mock<JobStorage>();
        _mockMonitoringApi = new Mock<IMonitoringApi>();
        _mockConnection = new Mock<JobStorageConnection>();

        _mockStorage.Setup(s => s.GetMonitoringApi()).Returns(_mockMonitoringApi.Object);
        _mockStorage.Setup(s => s.GetReadOnlyConnection()).Returns(_mockConnection.Object);

        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Returns(new HashSet<string>());

        _tagsReader = new TagsDataReader(_mockStorage.Object);
        _service = new SearchService(_mockStorage.Object, _tagsReader);
    }

    [Fact]
    public async Task SearchByQueue_PrefixExactMatch_CaseInsensitive()
    {
        // Arrange: queue "Default" exists, search with "queue:default" (lowercase)
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());

        SetupQueues("Default");
        SetupEnqueuedJobs("Default", new List<KeyValuePair<string, EnqueuedJobDto>>
        {
            new("1", new EnqueuedJobDto
            {
                Job = job,
                EnqueuedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InEnqueuedState = true
            })
        });

        var request = new SearchRequest { Query = "queue:default" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("1", item.JobId);
        Assert.Equal("SampleJobs.FireAndForget", item.JobName);
        Assert.Equal("Enqueued", item.State);
        Assert.Equal("Default", item.Queue);
        Assert.Equal(SearchMatchSource.Queue, item.MatchSource);
    }

    [Fact]
    public async Task SearchByQueue_PrefixExactMatch_UpperCase()
    {
        // Arrange: queue "critical" exists, search with "queue:CRITICAL"
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());

        SetupQueues("critical");
        SetupEnqueuedJobs("critical", new List<KeyValuePair<string, EnqueuedJobDto>>
        {
            new("5", new EnqueuedJobDto
            {
                Job = job,
                EnqueuedAt = new DateTime(2024, 6, 5, 10, 0, 0, DateTimeKind.Utc),
                InEnqueuedState = true
            })
        });

        var request = new SearchRequest { Query = "queue:CRITICAL" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("critical", result.Items[0].Queue);
    }

    [Fact]
    public async Task SearchByQueue_NoMatchingQueue_ReturnsEmpty()
    {
        // Arrange: only "default" queue exists, search for "queue:nonexistent"
        SetupQueues("default");

        var request = new SearchRequest { Query = "queue:nonexistent" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
        Assert.False(result.HasError);
    }

    [Fact]
    public async Task SearchByQueue_EmptyQueue_ReturnsEmpty()
    {
        // Arrange: queue exists but has no jobs
        SetupQueues("default");
        SetupEnqueuedJobs("default", new List<KeyValuePair<string, EnqueuedJobDto>>());

        var request = new SearchRequest { Query = "queue:default" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchByQueue_MultipleJobs_SortedByCreatedAtDescending()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());

        SetupQueues("default");
        SetupEnqueuedJobs("default", new List<KeyValuePair<string, EnqueuedJobDto>>
        {
            new("1", new EnqueuedJobDto
            {
                Job = job,
                EnqueuedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                InEnqueuedState = true
            }),
            new("2", new EnqueuedJobDto
            {
                Job = job,
                EnqueuedAt = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc),
                InEnqueuedState = true
            }),
            new("3", new EnqueuedJobDto
            {
                Job = job,
                EnqueuedAt = new DateTime(2024, 3, 10, 10, 0, 0, DateTimeKind.Utc),
                InEnqueuedState = true
            })
        });

        var request = new SearchRequest { Query = "queue:default" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.TotalCount);
        Assert.Equal("2", result.Items[0].JobId); // June 15 (most recent)
        Assert.Equal("3", result.Items[1].JobId); // March 10
        Assert.Equal("1", result.Items[2].JobId); // January 1 (oldest)
    }

    [Fact]
    public async Task SearchByQueue_AppliesPagination()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        var jobs = new List<KeyValuePair<string, EnqueuedJobDto>>();
        for (int i = 0; i < 30; i++)
        {
            jobs.Add(new KeyValuePair<string, EnqueuedJobDto>(
                i.ToString(),
                new EnqueuedJobDto
                {
                    Job = job,
                    EnqueuedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                    InEnqueuedState = true
                }));
        }

        SetupQueues("default");
        SetupEnqueuedJobs("default", jobs);

        var request = new SearchRequest
        {
            Query = "queue:default",
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
    public async Task SearchByQueue_PageSizeCappedAt50()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        var jobs = new List<KeyValuePair<string, EnqueuedJobDto>>();
        for (int i = 0; i < 100; i++)
        {
            jobs.Add(new KeyValuePair<string, EnqueuedJobDto>(
                i.ToString(),
                new EnqueuedJobDto
                {
                    Job = job,
                    EnqueuedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                    InEnqueuedState = true
                }));
        }

        SetupQueues("default");
        SetupEnqueuedJobs("default", jobs);

        var request = new SearchRequest
        {
            Query = "queue:default",
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
    public async Task SearchByQueue_NullJobInEntry_ShowsUnknownName()
    {
        // Arrange: job deserialization failed (null Job)
        SetupQueues("default");
        SetupEnqueuedJobs("default", new List<KeyValuePair<string, EnqueuedJobDto>>
        {
            new("1", new EnqueuedJobDto
            {
                Job = null,
                EnqueuedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InEnqueuedState = true
            })
        });

        var request = new SearchRequest { Query = "queue:default" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Unknown", result.Items[0].JobName);
    }

    [Fact]
    public async Task SearchByQueue_RespectsCancellationToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        SetupQueues("default");

        var request = new SearchRequest { Query = "queue:default" };

        // Act
        var result = await _service.SearchAsync(request, cts.Token);

        // Assert
        Assert.True(result.TimedOut);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchByQueue_StorageError_ReturnsErrorResult()
    {
        // Arrange
        _mockMonitoringApi.Setup(m => m.Queues())
            .Throws(new InvalidOperationException("Storage unavailable"));

        var request = new SearchRequest { Query = "queue:default" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.HasError);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task SearchByQueue_TracksElapsedTime()
    {
        // Arrange
        SetupQueues("default");
        SetupEnqueuedJobs("default", new List<KeyValuePair<string, EnqueuedJobDto>>());

        var request = new SearchRequest { Query = "queue:default" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Elapsed.TotalMilliseconds >= 0);
    }

    [Fact]
    public async Task SearchByQueue_SafetyCap1000()
    {
        // Arrange: create more than 1000 jobs to verify safety cap
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());

        SetupQueues("default");

        // Setup batches that total more than 1000 jobs
        // We'll set up 11 batches of 100 = 1100 jobs, but only 1000 should be collected
        for (int batchIdx = 0; batchIdx <= 10; batchIdx++)
        {
            var batchJobs = new List<KeyValuePair<string, EnqueuedJobDto>>();
            for (int i = 0; i < 100; i++)
            {
                int jobNum = batchIdx * 100 + i;
                batchJobs.Add(new KeyValuePair<string, EnqueuedJobDto>(
                    jobNum.ToString(),
                    new EnqueuedJobDto
                    {
                        Job = job,
                        EnqueuedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(jobNum),
                        InEnqueuedState = true
                    }));
            }

            _mockMonitoringApi.Setup(m => m.EnqueuedJobs("default", batchIdx * 100, 100))
                .Returns(new JobList<EnqueuedJobDto>(batchJobs));
        }

        // Empty batch after 1100
        _mockMonitoringApi.Setup(m => m.EnqueuedJobs("default", 1100, 100))
            .Returns(new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>()));

        var request = new SearchRequest
        {
            Query = "queue:default",
            From = 0,
            PageSize = 50
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1000, result.TotalCount); // Safety cap at 1000
        Assert.Equal(50, result.Items.Count);
    }

    [Fact]
    public async Task SearchByQueue_MultipleQueuesExist_MatchesCorrectOne()
    {
        // Arrange: multiple queues exist, only the matching one is searched
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());

        _mockMonitoringApi.Setup(m => m.Queues())
            .Returns(new List<QueueWithTopEnqueuedJobsDto>
            {
                new() { Name = "default" },
                new() { Name = "critical" },
                new() { Name = "background" }
            });

        SetupEnqueuedJobs("critical", new List<KeyValuePair<string, EnqueuedJobDto>>
        {
            new("10", new EnqueuedJobDto
            {
                Job = job,
                EnqueuedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InEnqueuedState = true
            })
        });

        var request = new SearchRequest { Query = "queue:Critical" }; // Mixed case

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("10", result.Items[0].JobId);
        Assert.Equal("critical", result.Items[0].Queue);
    }

    #region Helper Methods

    private void SetupQueues(params string[] queueNames)
    {
        var queues = queueNames.Select(name => new QueueWithTopEnqueuedJobsDto { Name = name }).ToList();
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(queues);
    }

    private void SetupEnqueuedJobs(string queueName, List<KeyValuePair<string, EnqueuedJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.EnqueuedJobs(queueName, 0, 100))
            .Returns(new JobList<EnqueuedJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.EnqueuedJobs(queueName, It.Is<int>(i => i >= Math.Max(jobs.Count, 100)), 100))
            .Returns(new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>()));
    }

    #endregion

    public static class SampleJobs
    {
        public static void FireAndForget() { }
    }
}
