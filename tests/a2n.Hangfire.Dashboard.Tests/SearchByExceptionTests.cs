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
/// Unit tests for SearchService exception text search.
/// Validates: Requirements 6.1, 6.2, 6.3, 6.4, 6.5
/// </summary>
public class SearchByExceptionTests
{
    private readonly Mock<JobStorage> _mockStorage;
    private readonly Mock<IMonitoringApi> _mockMonitoringApi;
    private readonly Mock<JobStorageConnection> _mockConnection;
    private readonly TagsDataReader _tagsReader;
    private readonly SearchService _service;

    public SearchByExceptionTests()
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

    [Fact]
    public async Task SearchByException_MatchesExceptionMessage_CaseInsensitive()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true,
                ExceptionMessage = "NullReferenceException: Object reference not set",
                ExceptionDetails = "at MyApp.Service.DoWork()"
            })
        });

        var request = new SearchRequest { Query = "exception:nullreference" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("1", item.JobId);
        Assert.Equal("Failed", item.State);
        Assert.Equal(SearchMatchSource.Exception, item.MatchSource);
    }

    [Fact]
    public async Task SearchByException_MatchesExceptionDetails_WhenMessageDoesNotMatch()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true,
                ExceptionMessage = "Something went wrong",
                ExceptionDetails = "at MyApp.Controllers.OrderController.Submit() in OrderController.cs:line 42"
            })
        });

        var request = new SearchRequest { Query = "exception:OrderController" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("1", result.Items[0].JobId);
    }

    [Fact]
    public async Task SearchByException_OnlySearchesFailedState()
    {
        // Arrange - all results should have State = "Failed"
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true,
                ExceptionMessage = "TimeoutException occurred",
                ExceptionDetails = ""
            }),
            new("2", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 6, 2, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true,
                ExceptionMessage = "TimeoutException in batch processing",
                ExceptionDetails = ""
            })
        });

        var request = new SearchRequest { Query = "exception:TimeoutException" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, item => Assert.Equal("Failed", item.State));
    }

    [Fact]
    public async Task SearchByException_EmptyQueryAfterPrefix_ReturnsError()
    {
        // Arrange
        var request = new SearchRequest { Query = "exception:   " };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert - DetectSearchMode returns Auto for empty value after prefix
        // The search should not execute and return empty
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchByException_ExplicitModeWithEmptyQuery_ReturnsError()
    {
        // Arrange - explicitly set Exception mode with empty query
        var request = new SearchRequest
        {
            Query = "   ",
            Mode = SearchMode.Exception
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.HasError);
        Assert.Contains("search term is required", result.ErrorMessage);
    }

    [Fact]
    public async Task SearchByException_NoMatch_ReturnsEmptyResult()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = DateTime.UtcNow,
                InFailedState = true,
                ExceptionMessage = "ArgumentException: value cannot be null",
                ExceptionDetails = "at MyApp.Service.Process()"
            })
        });

        var request = new SearchRequest { Query = "exception:TimeoutException" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
        Assert.False(result.HasError);
    }

    [Fact]
    public async Task SearchByException_GeneratesExcerpt_MaxLength200()
    {
        // Arrange - create a long exception message
        var longMessage = new string('x', 100) + "TARGET_ERROR" + new string('y', 200);
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = DateTime.UtcNow,
                InFailedState = true,
                ExceptionMessage = longMessage,
                ExceptionDetails = ""
            })
        });

        var request = new SearchRequest { Query = "exception:TARGET_ERROR" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        var excerpt = result.Items[0].ExceptionExcerpt;
        Assert.NotNull(excerpt);
        Assert.True(excerpt.Length <= 200);
        Assert.Contains("TARGET_ERROR", excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchByException_ShortMessage_ReturnsFullText()
    {
        // Arrange
        var shortMessage = "NullReferenceException: Object reference not set";
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = DateTime.UtcNow,
                InFailedState = true,
                ExceptionMessage = shortMessage,
                ExceptionDetails = ""
            })
        });

        var request = new SearchRequest { Query = "exception:NullReference" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        Assert.Equal(shortMessage, result.Items[0].ExceptionExcerpt);
    }

    [Fact]
    public async Task SearchByException_SortsResultsByCreatedAtDescending()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true,
                ExceptionMessage = "Error: connection timeout",
                ExceptionDetails = ""
            }),
            new("2", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true,
                ExceptionMessage = "Error: connection timeout",
                ExceptionDetails = ""
            }),
            new("3", new FailedJobDto
            {
                Job = job,
                FailedAt = new DateTime(2024, 3, 10, 10, 0, 0, DateTimeKind.Utc),
                InFailedState = true,
                ExceptionMessage = "Error: connection timeout",
                ExceptionDetails = ""
            })
        });

        var request = new SearchRequest { Query = "exception:connection timeout" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.TotalCount);
        Assert.Equal("2", result.Items[0].JobId); // June 15 (most recent)
        Assert.Equal("3", result.Items[1].JobId); // March 10
        Assert.Equal("1", result.Items[2].JobId); // January 1 (oldest)
    }

    [Fact]
    public async Task SearchByException_AppliesPagination()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        var jobs = new List<KeyValuePair<string, FailedJobDto>>();
        for (int i = 0; i < 30; i++)
        {
            jobs.Add(new KeyValuePair<string, FailedJobDto>(
                i.ToString(),
                new FailedJobDto
                {
                    Job = job,
                    FailedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                    InFailedState = true,
                    ExceptionMessage = "Common error message",
                    ExceptionDetails = ""
                }));
        }
        SetupFailedJobs(jobs);

        var request = new SearchRequest
        {
            Query = "exception:Common error",
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
    public async Task SearchByException_PageSizeCappedAt50()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        var jobs = new List<KeyValuePair<string, FailedJobDto>>();
        for (int i = 0; i < 80; i++)
        {
            jobs.Add(new KeyValuePair<string, FailedJobDto>(
                i.ToString(),
                new FailedJobDto
                {
                    Job = job,
                    FailedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                    InFailedState = true,
                    ExceptionMessage = "Repeated error",
                    ExceptionDetails = ""
                }));
        }
        SetupFailedJobs(jobs);

        var request = new SearchRequest
        {
            Query = "exception:Repeated error",
            From = 0,
            PageSize = 100
        };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(80, result.TotalCount);
        Assert.Equal(50, result.Items.Count);
    }

    [Fact]
    public async Task SearchByException_RespectsCancellationToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new SearchRequest { Query = "exception:error" };

        // Act
        var result = await _service.SearchAsync(request, cts.Token);

        // Assert
        Assert.True(result.TimedOut);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchByException_StorageError_ReturnsErrorResult()
    {
        // Arrange
        _mockMonitoringApi.Setup(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new InvalidOperationException("Storage unavailable"));

        var request = new SearchRequest { Query = "exception:error" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.HasError);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task SearchByException_ExtractsJobNameFromJob()
    {
        // Arrange
        var job = Job.FromExpression(() => SampleJobs.FireAndForget());
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = job,
                FailedAt = DateTime.UtcNow,
                InFailedState = true,
                ExceptionMessage = "Some error occurred",
                ExceptionDetails = ""
            })
        });

        var request = new SearchRequest { Query = "exception:Some error" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("SampleJobs.FireAndForget", result.Items[0].JobName);
    }

    [Fact]
    public async Task SearchByException_NullJob_SetsJobNameToUnknown()
    {
        // Arrange
        SetupFailedJobs(new List<KeyValuePair<string, FailedJobDto>>
        {
            new("1", new FailedJobDto
            {
                Job = null,
                FailedAt = DateTime.UtcNow,
                InFailedState = true,
                ExceptionMessage = "Deserialization error",
                ExceptionDetails = ""
            })
        });

        var request = new SearchRequest { Query = "exception:Deserialization" };

        // Act
        var result = await _service.SearchAsync(request, CancellationToken.None);

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("Unknown", result.Items[0].JobName);
    }

    [Fact]
    public void GenerateExcerpt_ShortText_ReturnsFullText()
    {
        var text = "Short error message";
        var result = SearchService.GenerateExcerpt(text, "error", 200);
        Assert.Equal(text, result);
    }

    [Fact]
    public void GenerateExcerpt_LongText_ReturnsMaxLengthWithSearchTerm()
    {
        var text = new string('a', 100) + "SEARCH_TERM" + new string('b', 200);
        var result = SearchService.GenerateExcerpt(text, "SEARCH_TERM", 200);

        Assert.True(result.Length <= 200);
        Assert.Contains("SEARCH_TERM", result);
    }

    [Fact]
    public void GenerateExcerpt_MatchAtStart_ReturnsFromBeginning()
    {
        var text = "ERROR_HERE" + new string('x', 300);
        var result = SearchService.GenerateExcerpt(text, "ERROR_HERE", 200);

        Assert.True(result.Length <= 200);
        Assert.StartsWith("ERROR_HERE", result);
    }

    [Fact]
    public void GenerateExcerpt_MatchAtEnd_ReturnsEndPortion()
    {
        var text = new string('x', 300) + "ERROR_HERE";
        var result = SearchService.GenerateExcerpt(text, "ERROR_HERE", 200);

        Assert.True(result.Length <= 200);
        Assert.EndsWith("ERROR_HERE", result);
    }

    [Fact]
    public void GenerateExcerpt_EmptyText_ReturnsEmpty()
    {
        var result = SearchService.GenerateExcerpt("", "search", 200);
        Assert.Equal("", result);
    }

    [Fact]
    public void GenerateExcerpt_NullText_ReturnsEmpty()
    {
        var result = SearchService.GenerateExcerpt(null, "search", 200);
        Assert.Equal("", result);
    }

    [Fact]
    public void GenerateExcerpt_CaseInsensitiveMatch()
    {
        var text = new string('a', 150) + "NullReferenceException" + new string('b', 150);
        var result = SearchService.GenerateExcerpt(text, "nullreferenceexception", 200);

        Assert.True(result.Length <= 200);
        Assert.Contains("NullReferenceException", result);
    }

    #region Helper Methods

    private void SetupFailedJobs(List<KeyValuePair<string, FailedJobDto>> jobs)
    {
        _mockMonitoringApi.Setup(m => m.FailedJobs(0, 100))
            .Returns(new JobList<FailedJobDto>(jobs));
        _mockMonitoringApi.Setup(m => m.FailedJobs(It.Is<int>(i => i >= jobs.Count), 100))
            .Returns(new JobList<FailedJobDto>(new List<KeyValuePair<string, FailedJobDto>>()));
    }

    #endregion

    public static class SampleJobs
    {
        public static void FireAndForget() { }
    }
}
