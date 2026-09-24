using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using a2n.Hangfire.Dashboard.Components.Pages;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Search totals stop at <see cref="JobFilterCriteria.CountLimit"/> and are then shown as a lower
/// bound (Issue #45).
/// </summary>
public class SearchCountLimitTests
{
    [Fact]
    public async Task GenericProvider_ScanReachingTheCap_ReportsALowerBound()
    {
        var (provider, _) = GenericProviderWithProcessingJobs(JobFilterCriteria.CountLimit + 200);

        var result = await provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { JobNamePattern = "SampleJobs", States = new List<string> { "Processing" } },
            page: 1, pageSize: 20, CancellationToken.None);

        Assert.True(result.TotalCountIsLowerBound);
        Assert.Equal(JobFilterCriteria.CountLimit, result.TotalCount);
    }

    [Fact]
    public async Task GenericProvider_FewerJobsThanTheCap_CountIsExact()
    {
        var (provider, _) = GenericProviderWithProcessingJobs(30);

        var result = await provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { JobNamePattern = "SampleJobs", States = new List<string> { "Processing" } },
            page: 1, pageSize: 20, CancellationToken.None);

        Assert.False(result.TotalCountIsLowerBound);
        Assert.Equal(30, result.TotalCount);
    }

    [Fact]
    public async Task SearchService_PassesTheLowerBoundThrough()
    {
        JobStorage storage = new InMemoryStorage();
        var provider = new Mock<IStorageQueryProvider>();
        provider
            .Setup(p => p.GetJobsWithFilterAsync(
                It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<JobSummaryDto>
            {
                TotalCount = JobFilterCriteria.CountLimit, TotalCountIsLowerBound = true, Page = 1, PageSize = 20
            });
        var service = new SearchService(storage, new TagsDataReader(storage), provider.Object);

        var result = await service.SearchAsync(new SearchRequest { Query = "SampleJobs" }, CancellationToken.None);

        Assert.True(result.TotalCountIsLowerBound);
        Assert.Equal(JobFilterCriteria.CountLimit, result.TotalCount);
    }

    [Fact]
    public void SearchPage_ShowsTheTotalAsALowerBound()
    {
        JobStorage storage = new InMemoryStorage();
        var provider = new Mock<IStorageQueryProvider>();
        provider
            .Setup(p => p.GetJobsWithFilterAsync(
                It.IsAny<JobFilterCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<JobSummaryDto>
            {
                Items = Enumerable.Range(1, 20)
                    .Select(i => new JobSummaryDto { JobId = i.ToString(), JobName = "SampleJobs.Run", State = "Succeeded" })
                    .ToList(),
                TotalCount = JobFilterCriteria.CountLimit,
                TotalCountIsLowerBound = true,
                Page = 1,
                PageSize = 20
            });

        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new DashboardUIOptions());
        ctx.Services.AddSingleton(new SearchService(storage, new TagsDataReader(storage), provider.Object));

        var cut = ctx.Render<SearchResults>();
        cut.Find("input[placeholder^='Search by ID']").Input("SampleJobs");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Search Jobs")).Click();

        var label = JobFilterCriteria.CountLimit.ToString("N0") + "+";
        cut.WaitForAssertion(() =>
        {
            Assert.Contains($"<strong>{label}</strong>", cut.Markup);
            Assert.Contains($"of {label}", cut.Markup);
            Assert.Contains("can be paged through", cut.Markup);
        }, TestTimeouts.RenderWait);
    }

    private static (GenericQueryProvider Provider, Mock<IMonitoringApi> Monitoring) GenericProviderWithProcessingJobs(int count)
    {
        var jobs = Enumerable.Range(1, count)
            .Select(i => new KeyValuePair<string, ProcessingJobDto>(i.ToString(), new ProcessingJobDto
            {
                Job = Job.FromExpression(() => SearchByNameTests.SampleJobs.FireAndForget()),
                StartedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                InProcessingState = true
            }))
            .ToList();

        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns<int, int>((from, take) => new JobList<ProcessingJobDto>(jobs.Skip(from).Take(take)));
        monitoring.Setup(m => m.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());

        var connection = new Mock<JobStorageConnection>();
        connection.Setup(c => c.GetAllItemsFromSet("tags")).Returns(new HashSet<string>());

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoring.Object);
        storage.Setup(s => s.GetReadOnlyConnection()).Returns(connection.Object);

        return (new GenericQueryProvider(storage.Object, new TagsDataReader(storage.Object)), monitoring);
    }
}
