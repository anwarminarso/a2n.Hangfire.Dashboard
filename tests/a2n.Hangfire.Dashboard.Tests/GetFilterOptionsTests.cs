using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

public class GetFilterOptionsTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;

    public GetFilterOptionsTests()
    {
        _mockStorage = new Mock<JobStorage>();
        _mockMonitoringApi = new Mock<IMonitoringApi>();
        _mockConnection = new Mock<JobStorageConnection>();

        _mockStorage.Setup(s => s.GetMonitoringApi()).Returns(_mockMonitoringApi.Object);
        _mockStorage.Setup(s => s.GetReadOnlyConnection()).Returns(_mockConnection.Object);

        _tagsReader = new TagsDataReader(_mockStorage.Object);
    }

    /// <summary>
    /// Sets up mock for recurring jobs by mocking the underlying storage calls
    /// that the GetRecurringJobs() extension method uses internally.
    /// </summary>
    private void SetupRecurringJobs(params string[] jobIds)
    {
        _mockConnection.Setup(c => c.GetAllItemsFromSet("recurring-jobs"))
            .Returns(new HashSet<string>(jobIds));

        foreach (var id in jobIds)
        {
            _mockConnection.Setup(c => c.GetAllEntriesFromHash($"recurring-job:{id}"))
                .Returns(new Dictionary<string, string>
                {
                    ["Cron"] = "* * * * *",
                    ["Queue"] = "default",
                    ["TimeZoneId"] = "UTC"
                });
        }
    }

    /// <summary>
    /// Sets up mock for tags by mocking GetAllItemsFromSet("tags").
    /// </summary>
    private void SetupTags(params string[] tags)
    {
        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Returns(new HashSet<string>(tags));
    }

    [Fact]
    public void GetFilterOptions_ReturnsQueues_SortedAlphabetically()
    {
        // Arrange
        var queues = new List<QueueWithTopEnqueuedJobsDto>
        {
            new() { Name = "default" },
            new() { Name = "critical" },
            new() { Name = "background" }
        };
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(queues);
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(new List<ServerDto>());
        SetupRecurringJobs();
        SetupTags();

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.Equal(3, result.Queues.Count);
        Assert.Equal("background", result.Queues[0]);
        Assert.Equal("critical", result.Queues[1]);
        Assert.Equal("default", result.Queues[2]);
    }

    [Fact]
    public void GetFilterOptions_ReturnsServers_SortedAlphabetically()
    {
        // Arrange
        var servers = new List<ServerDto>
        {
            new() { Name = "server-3" },
            new() { Name = "server-1" },
            new() { Name = "server-2" }
        };
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(servers);
        SetupRecurringJobs();
        SetupTags();

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.Equal(3, result.Servers.Count);
        Assert.Equal("server-1", result.Servers[0]);
        Assert.Equal("server-2", result.Servers[1]);
        Assert.Equal("server-3", result.Servers[2]);
    }

    [Fact]
    public void GetFilterOptions_ReturnsRecurringJobIds_SortedAlphabetically()
    {
        // Arrange
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(new List<ServerDto>());
        SetupRecurringJobs("weekly-report", "daily-cleanup", "hourly-sync");
        SetupTags();

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.Equal(3, result.RecurringJobIds.Count);
        Assert.Equal("daily-cleanup", result.RecurringJobIds[0]);
        Assert.Equal("hourly-sync", result.RecurringJobIds[1]);
        Assert.Equal("weekly-report", result.RecurringJobIds[2]);
    }

    [Fact]
    public void GetFilterOptions_ReturnsTags_WhenFeatureAvailable()
    {
        // Arrange
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(new List<ServerDto>());
        SetupRecurringJobs();
        SetupTags("urgent", "batch", "api");

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.True(result.TagsFeatureAvailable);
        Assert.Equal(3, result.Tags.Count);
        Assert.Equal("api", result.Tags[0]);
        Assert.Equal("batch", result.Tags[1]);
        Assert.Equal("urgent", result.Tags[2]);
    }

    [Fact]
    public void GetFilterOptions_SetsTagsFeatureUnavailable_WhenTagsReaderThrows()
    {
        // Arrange
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(new List<ServerDto>());
        SetupRecurringJobs();

        // Simulate TagsDataReader throwing (feature not registered)
        _mockConnection.Setup(c => c.GetAllItemsFromSet("tags"))
            .Throws(new InvalidOperationException("Tags feature not available"));

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.False(result.TagsFeatureAvailable);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void GetFilterOptions_FiltersOutNullAndEmptyNames()
    {
        // Arrange
        var queues = new List<QueueWithTopEnqueuedJobsDto>
        {
            new() { Name = "default" },
            new() { Name = "" },
            new() { Name = null }
        };
        var servers = new List<ServerDto>
        {
            new() { Name = "server-1" },
            new() { Name = "" },
            new() { Name = null }
        };
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(queues);
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(servers);
        SetupRecurringJobs();
        SetupTags();

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.Single(result.Queues);
        Assert.Equal("default", result.Queues[0]);
        Assert.Single(result.Servers);
        Assert.Equal("server-1", result.Servers[0]);
    }

    [Fact]
    public void GetFilterOptions_ReturnsEmptyLists_WhenNoDataExists()
    {
        // Arrange
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(new List<ServerDto>());
        SetupRecurringJobs();
        SetupTags();

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.Empty(result.Queues);
        Assert.Empty(result.Servers);
        Assert.Empty(result.RecurringJobIds);
        Assert.Empty(result.Tags);
        Assert.True(result.TagsFeatureAvailable);
    }

    [Fact]
    public void GetFilterOptions_SortsCaseInsensitively()
    {
        // Arrange
        var queues = new List<QueueWithTopEnqueuedJobsDto>
        {
            new() { Name = "Zebra" },
            new() { Name = "alpha" },
            new() { Name = "Beta" }
        };
        _mockMonitoringApi.Setup(m => m.Queues()).Returns(queues);
        _mockMonitoringApi.Setup(m => m.Servers()).Returns(new List<ServerDto>());
        SetupRecurringJobs();
        SetupTags();

        var service = new SearchService(_mockStorage.Object, _tagsReader);

        // Act
        var result = service.GetFilterOptions();

        // Assert
        Assert.Equal("alpha", result.Queues[0]);
        Assert.Equal("Beta", result.Queues[1]);
        Assert.Equal("Zebra", result.Queues[2]);
    }
}
