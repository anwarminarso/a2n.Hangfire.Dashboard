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
/// Unit tests for searching jobs by their serialized arguments ("args:" prefix and the
/// Arguments filter field), executed through the generic scan-and-filter provider.
/// </summary>
public class SearchByArgumentsTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;
    private readonly SearchService _service;

    public SearchByArgumentsTests()
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
        var queryProvider = new GenericQueryProvider(_mockStorage.Object, _tagsReader);
        _service = new SearchService(_mockStorage.Object, _tagsReader, queryProvider);
    }

    [Fact]
    public async Task SearchByArguments_MatchesStringArgumentValue()
    {
        // Arrange
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", SendEmailJob("user@example.com", 4242)),
            Processing("2", SendEmailJob("other@example.com", 7))
        });

        var request = new SearchRequest { Query = "args:user@example.com" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("1", result.Items[0].JobId);
        Assert.Equal(SearchMatchSource.Arguments, result.Items[0].MatchSource);
    }

    [Fact]
    public async Task SearchByArguments_MatchesNumericArgumentValue()
    {
        // Arrange
        SetupSucceededJobs(new List<KeyValuePair<string, SucceededJobDto>>
        {
            Succeeded("1", SendEmailJob("user@example.com", 4242)),
            Succeeded("2", SendEmailJob("user@example.com", 7))
        });

        // "4242" alone would be an ID lookup — the args: prefix keeps it an argument search
        var request = new SearchRequest { Query = "args:4242" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("1", result.Items[0].JobId);
    }

    [Fact]
    public async Task SearchByArguments_IsCaseInsensitive()
    {
        // Arrange
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", SendEmailJob("User@Example.com", 1))
        });

        var request = new SearchRequest { Query = "args:USER@EXAMPLE.COM" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SearchByArguments_DoesNotMatchTypeOrMethodName()
    {
        // Arrange — the method name contains "SendEmail" but no argument does
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", SendEmailJob("user@example.com", 1))
        });

        // Act
        var byArguments = await _service.SearchAsync(
            new SearchRequest { Query = "args:SendEmail" }, CancellationToken.None);
        var byName = await _service.SearchAsync(
            new SearchRequest { Query = "SendEmail" }, CancellationToken.None);

        // Assert — an argument search never falls back to the job name
        Assert.Equal(0, byArguments.TotalCount);
        Assert.Empty(byArguments.Items);
        Assert.Equal(1, byName.TotalCount);
    }

    [Fact]
    public async Task SearchByArguments_NoMatch_ReturnsEmptyResult()
    {
        // Arrange
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", SendEmailJob("user@example.com", 1))
        });

        var request = new SearchRequest { Query = "args:nobody@example.com" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
        Assert.False(result.HasError);
    }

    [Fact]
    public async Task SearchByArguments_FallsBackToJobArguments_WhenInvocationDataMissing()
    {
        // Arrange — some storages/DTOs do not carry the raw payload; the resolved job still has it
        var job = Job.FromExpression(() => SampleArgJobs.SendEmail("user@example.com", 1));
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            new("1", new ProcessingJobDto
            {
                Job = job,
                InvocationData = null,
                StartedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InProcessingState = true
            })
        });

        var request = new SearchRequest { Query = "args:user@example.com" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SearchByArguments_ScansAllStates()
    {
        // Arrange
        var match = SendEmailJob("user@example.com", 1);
        var noMatch = SendEmailJob("other@example.com", 1);

        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", match, new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc))
        });
        SetupSucceededJobs(new List<KeyValuePair<string, SucceededJobDto>>
        {
            Succeeded("2", match, new DateTime(2024, 6, 2, 10, 0, 0, DateTimeKind.Utc)),
            Succeeded("3", noMatch, new DateTime(2024, 6, 4, 10, 0, 0, DateTimeKind.Utc))
        });
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            Failed("4", match, new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc))
        });

        var request = new SearchRequest { Query = "args:user@example.com" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert — sorted by CreatedAt descending, non-matching job excluded
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(new[] { "4", "2", "1" }, result.Items.Select(i => i.JobId).ToArray());
    }

    [Fact]
    public async Task SearchByArguments_FilterField_WorksWithoutPrefix()
    {
        // Arrange — the filter panel sets ArgumentsQuery directly, with no search query at all
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", SendEmailJob("user@example.com", 1)),
            Processing("2", SendEmailJob("other@example.com", 1))
        });

        var request = new SearchRequest { ArgumentsQuery = "user@example.com" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("1", result.Items[0].JobId);
    }

    [Fact]
    public async Task SearchByArguments_FilterField_CombinesWithNameQuery()
    {
        // Arrange — same argument value, different methods
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", SendEmailJob("user@example.com", 1)),
            new("2", new ProcessingJobDto
            {
                Job = Job.FromExpression(() => SampleArgJobs.ProcessOrder("user@example.com")),
                InvocationData = InvocationData.SerializeJob(
                    Job.FromExpression(() => SampleArgJobs.ProcessOrder("user@example.com"))),
                StartedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InProcessingState = true
            })
        });

        var request = new SearchRequest
        {
            Query = "ProcessOrder",
            ArgumentsQuery = "user@example.com"
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert — both filters apply
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("2", result.Items[0].JobId);
    }

    [Fact]
    public async Task SearchByArguments_FilterField_TakesPrecedenceOverPrefix()
    {
        // Arrange — an explicit filter value wins over the "args:" prefix, matching how the
        // queue filter behaves, so the panel is never silently overridden by the search box
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>>
        {
            Processing("1", SendEmailJob("user@example.com", 1))
        });

        var request = new SearchRequest
        {
            Query = "args:nobody@example.com",
            ArgumentsQuery = "user@example.com"
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SearchByArguments_RespectsStateFilter()
    {
        // Arrange
        var job = SendEmailJob("user@example.com", 1);
        SetupProcessingJobs(new List<KeyValuePair<string, ProcessingJobDto>> { Processing("1", job) });
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>> { Failed("2", job) });

        var request = new SearchRequest
        {
            Query = "args:user@example.com",
            States = new List<string> { "Failed" }
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("2", result.Items[0].JobId);
        Assert.Equal("Failed", result.Items[0].State);
    }

    #region Helper Methods

    private static Job SendEmailJob(string recipient, int retryCount)
        => Job.FromExpression(() => SampleArgJobs.SendEmail(recipient, retryCount));

    private static KeyValuePair<string, ProcessingJobDto> Processing(string id, Job job, DateTime? startedAt = null)
        => new(id, new ProcessingJobDto
        {
            Job = job,
            InvocationData = InvocationData.SerializeJob(job),
            StartedAt = startedAt ?? new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            InProcessingState = true
        });

    private static KeyValuePair<string, SucceededJobDto> Succeeded(string id, Job job, DateTime? succeededAt = null)
        => new(id, new SucceededJobDto
        {
            Job = job,
            InvocationData = InvocationData.SerializeJob(job),
            SucceededAt = succeededAt ?? new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc),
            InSucceededState = true
        });

    private static KeyValuePair<string, FailedJobDto> Failed(string id, Job job, DateTime? failedAt = null)
        => new(id, new FailedJobDto
        {
            Job = job,
            InvocationData = InvocationData.SerializeJob(job),
            FailedAt = failedAt ?? new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            InFailedState = true
        });

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

    #endregion

    // Sample job class for creating Job instances with arguments in tests
    public static class SampleArgJobs
    {
        public static void SendEmail(string recipient, int retryCount) { }
        public static void ProcessOrder(string orderId) { }
    }
}
