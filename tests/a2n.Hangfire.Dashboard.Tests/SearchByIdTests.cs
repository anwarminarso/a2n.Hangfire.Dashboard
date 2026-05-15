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
/// Unit tests for SearchService job ID lookup via SearchByIdAsync.
/// Validates: Requirements 2.2, 2.3, 2.4, 2.5
/// </summary>
public class SearchByIdTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;
    private readonly SearchService _service;

    public SearchByIdTests()
    {
        _mockStorage = new Mock<JobStorage>();
        _mockMonitoringApi = new Mock<IMonitoringApi>();
        _mockConnection = new Mock<JobStorageConnection>();

        _mockStorage.Setup(s => s.GetMonitoringApi()).Returns(_mockMonitoringApi.Object);
        _mockStorage.Setup(s => s.GetReadOnlyConnection()).Returns(_mockConnection.Object);

        // Setup tags to avoid null reference
        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Returns(new HashSet<string>());

        _tagsReader = new TagsDataReader(_mockStorage.Object);
        _service = new SearchService(_mockStorage.Object, _tagsReader);
    }

    [Fact]
    public async Task SearchAsync_JobIdExists_ReturnsSingleResult()
    {
        // Arrange
        var jobId = "42";
        var job = Job.FromExpression(() => SampleJob.Execute());
        var jobData = new JobData
        {
            State = "Succeeded",
            Job = job,
            CreatedAt = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };

        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns(jobData);
        _mockMonitoringApi.Setup(m => m.JobDetails(jobId)).Returns((JobDetailsDto)null);

        var request = new SearchRequest { Query = "42" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.False(result.HasError);
        Assert.False(result.TimedOut);

        var item = result.Items[0];
        Assert.Equal("42", item.JobId);
        Assert.Equal("Succeeded", item.State);
        Assert.Equal("SampleJob.Execute", item.JobName);
        Assert.Equal(SearchMatchSource.Id, item.MatchSource);
    }

    [Fact]
    public async Task SearchAsync_JobIdDoesNotExist_ReturnsEmptyResult()
    {
        // Arrange
        var jobId = "99999";
        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns((JobData)null);

        var request = new SearchRequest { Query = "99999" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
        Assert.False(result.HasError);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task SearchAsync_StorageError_ReturnsErrorResult()
    {
        // Arrange
        var jobId = "123";
        _mockConnection.Setup(c => c.GetJobData(jobId))
            .Throws(new InvalidOperationException("Storage connection failed"));

        var request = new SearchRequest { Query = "123" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.HasError);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchAsync_JobIdWithNullJob_ReturnsUnknownJobName()
    {
        // Arrange
        var jobId = "7";
        var jobData = new JobData
        {
            State = "Failed",
            Job = null, // Job deserialization failed
            CreatedAt = new DateTime(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc)
        };

        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns(jobData);
        _mockMonitoringApi.Setup(m => m.JobDetails(jobId)).Returns((JobDetailsDto)null);

        var request = new SearchRequest { Query = "7" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("Unknown", item.JobName);
        Assert.Equal("Failed", item.State);
        Assert.Equal("7", item.JobId);
    }

    [Fact]
    public async Task SearchAsync_JobIdWithStateHistory_ExtractsTimestamps()
    {
        // Arrange
        var jobId = "10";
        var job = Job.FromExpression(() => SampleJob.Execute());
        var jobData = new JobData
        {
            State = "Succeeded",
            Job = job,
            CreatedAt = new DateTime(2024, 2, 20, 12, 0, 0, DateTimeKind.Utc)
        };

        var details = new JobDetailsDto
        {
            Job = job,
            History = new List<StateHistoryDto>
            {
                new() { StateName = "Succeeded", CreatedAt = new DateTime(2024, 2, 20, 12, 5, 0, DateTimeKind.Utc), Data = new Dictionary<string, string>() },
                new() { StateName = "Processing", CreatedAt = new DateTime(2024, 2, 20, 12, 4, 0, DateTimeKind.Utc), Data = new Dictionary<string, string>() },
                new() { StateName = "Enqueued", CreatedAt = new DateTime(2024, 2, 20, 12, 0, 1, DateTimeKind.Utc), Data = new Dictionary<string, string> { ["Queue"] = "default" } },
                new() { StateName = "Created", CreatedAt = new DateTime(2024, 2, 20, 12, 0, 0, DateTimeKind.Utc), Data = new Dictionary<string, string>() }
            }
        };

        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns(jobData);
        _mockMonitoringApi.Setup(m => m.JobDetails(jobId)).Returns(details);

        var request = new SearchRequest { Query = "10" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        var item = result.Items[0];
        // LastStateChange should be the most recent state entry (first in history)
        Assert.Equal(new DateTime(2024, 2, 20, 12, 5, 0, DateTimeKind.Utc), item.LastStateChange);
        // CreatedAt should come from the "Created" state
        Assert.Equal(new DateTime(2024, 2, 20, 12, 0, 0, DateTimeKind.Utc), item.CreatedAt);
        // Queue should be extracted from Enqueued state data
        Assert.Equal("default", item.Queue);
    }

    [Fact]
    public async Task SearchAsync_JobIdWithCancellation_ReturnsTimedOut()
    {
        // Arrange
        var jobId = "5";
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns(new JobData
        {
            State = "Processing",
            Job = Job.FromExpression(() => SampleJob.Execute()),
            CreatedAt = DateTime.UtcNow
        });

        var request = new SearchRequest { Query = "5" };

        // Act
        var result = await _service.SearchAsync(request, cts.Token);

        // Assert
        Assert.True(result.TimedOut);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchAsync_JobIdWithJobDetailsFailure_StillReturnsBasicInfo()
    {
        // Arrange
        var jobId = "15";
        var job = Job.FromExpression(() => SampleJob.Execute());
        var jobData = new JobData
        {
            State = "Enqueued",
            Job = job,
            CreatedAt = new DateTime(2024, 4, 1, 9, 0, 0, DateTimeKind.Utc)
        };

        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns(jobData);
        _mockMonitoringApi.Setup(m => m.JobDetails(jobId))
            .Throws(new InvalidOperationException("Details unavailable"));

        var request = new SearchRequest { Query = "15" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        Assert.False(result.HasError); // Basic lookup succeeded
        var item = result.Items[0];
        Assert.Equal("15", item.JobId);
        Assert.Equal("Enqueued", item.State);
        Assert.Equal("SampleJob.Execute", item.JobName);
        // LastStateChange will be null since details failed
        Assert.Null(item.LastStateChange);
    }

    [Fact]
    public async Task SearchAsync_JobIdWithQueueFromJob_UsesJobQueue()
    {
        // Arrange
        var jobId = "20";
        var method = typeof(SampleJob).GetMethod(nameof(SampleJob.Execute));
        var job = new Job(typeof(SampleJob), method, Array.Empty<object>());

        var jobData = new JobData
        {
            State = "Enqueued",
            Job = job,
            CreatedAt = DateTime.UtcNow
        };

        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns(jobData);
        _mockMonitoringApi.Setup(m => m.JobDetails(jobId)).Returns((JobDetailsDto)null);

        var request = new SearchRequest { Query = "20" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("SampleJob.Execute", item.JobName);
    }

    [Fact]
    public async Task SearchAsync_ElapsedTimeIsTracked()
    {
        // Arrange
        var jobId = "1";
        _mockConnection.Setup(c => c.GetJobData(jobId)).Returns((JobData)null);

        var request = new SearchRequest { Query = "1" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Elapsed.TotalMilliseconds >= 0);
    }

    // Sample job class for creating Job instances in tests
    public static class SampleJob
    {
        public static void Execute() { }
    }
}
