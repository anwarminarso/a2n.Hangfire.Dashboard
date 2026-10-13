using System;
using System.Collections.Generic;
using System.Linq;
using a2n.Hangfire.Dashboard.Interfaces;
using a2n.Hangfire.Dashboard.Models;
using a2n.Hangfire.Dashboard.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

public class FailedJobPageSourceTests
{
    [Fact]
    public void GetFailedJobs_UsesPageSource_InsteadOfMonitoringApi()
    {
        var expected = new JobList<FailedJobDto>(new[]
        {
            new KeyValuePair<string, FailedJobDto>("42", new FailedJobDto
            {
                ExceptionMessage = "boom",
            }),
        });

        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi())
            .Throws(new System.InvalidOperationException("monitoring api must not be called"));

        var service = new HangfireMonitorService(storage.Object, failedJobs: new StubSource(expected));

        var page = service.GetFailedJobs(0, 20);

        Assert.Same(expected, page);
    }

    // ─── Generic query provider (#69) ───────────────────────────────────────

    public static class SampleJobs
    {
        public static void Run() { }
    }

    private static (GenericQueryProvider Provider, Mock<IMonitoringApi> MonitoringApi, Mock<IFailedJobPageSource> Source)
        GenericProviderWithPageSource()
    {
        var monitoringApi = new Mock<IMonitoringApi>();
        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetMonitoringApi()).Returns(monitoringApi.Object);

        var source = new Mock<IFailedJobPageSource>();
        source.Setup(s => s.GetFailedJobs(0, It.IsAny<int>())).Returns(new JobList<FailedJobDto>(new[]
        {
            new KeyValuePair<string, FailedJobDto>("42", new FailedJobDto
            {
                Job = Job.FromExpression(() => SampleJobs.Run()),
                FailedAt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                ExceptionType = "System.InvalidOperationException",
                ExceptionMessage = "boom",
                InFailedState = true,
            }),
        }));
        source.Setup(s => s.GetFailedJobs(It.Is<int>(from => from > 0), It.IsAny<int>()))
            .Returns(new JobList<FailedJobDto>(Array.Empty<KeyValuePair<string, FailedJobDto>>()));

        var provider = new GenericQueryProvider(storage.Object, new TagsDataReader(storage.Object), source.Object);
        return (provider, monitoringApi, source);
    }

    [Fact]
    public async Task GenericProvider_SearchOverFailed_ReadsThePageSource_NotTheMonitoringApi()
    {
        var (provider, monitoringApi, source) = GenericProviderWithPageSource();

        var result = await provider.GetJobsWithFilterAsync(
            new JobFilterCriteria { JobNamePattern = nameof(SampleJobs), States = new List<string> { "Failed" } },
            page: 1, pageSize: 20, CancellationToken.None);

        var job = Assert.Single(result.Items);
        Assert.Equal("42", job.JobId);
        Assert.Equal("Failed", job.State);
        Assert.Equal("boom", job.ExceptionMessage);
        source.Verify(s => s.GetFailedJobs(0, It.IsAny<int>()), Times.Once);
        monitoringApi.Verify(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GenericProvider_JobsByFailedState_ReadsThePageSource_NotTheMonitoringApi()
    {
        var (provider, monitoringApi, _) = GenericProviderWithPageSource();

        var result = await provider.GetJobsByStateAsync("Failed", page: 1, pageSize: 20, CancellationToken.None);

        Assert.Equal(new[] { "42" }, result.Items.Select(i => i.JobId));
        monitoringApi.Verify(m => m.FailedJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    private sealed class StubSource : IFailedJobPageSource
    {
        private readonly JobList<FailedJobDto> _page;
        public StubSource(JobList<FailedJobDto> page) => _page = page;
        public JobList<FailedJobDto> GetFailedJobs(int from, int count) => _page;
    }
}
